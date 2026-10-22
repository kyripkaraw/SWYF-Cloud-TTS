using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwyfCloudTts.Providers;

/// Keeps an instant clone of every character's game voice within the account limits. Starter has
/// 10 voice slots and a monthly budget of voice additions, while the game has dozens of characters:
/// when the slots are full, the clone heard least recently is deleted to make room. Clones made
/// earlier are reused rather than created twice. Only voices named "swyf-..." are ever deleted.
/// The game restarts the worker for every line, so all of this state lives in a file.
internal sealed class ElevenLabsClones
{
    public const string NamePrefix = "swyf-";

    /// The game never sends less; shorter clips would not clone well anyway.
    public const int MinReferenceSamples = 2400;

    private const string Provider = "elevenlabs";

    private static readonly TimeSpan SpeakerRetry = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AccountRetry = TimeSpan.FromMinutes(30);

    private readonly Config.ElevenLabsConfig _config;
    private readonly VoiceResolver _voices;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly string _statePath;
    private readonly CloneState _state;

    public ElevenLabsClones(Config config, VoiceResolver voices,
                            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _config = config.ElevenLabs;
        _voices = voices;
        _send = send;
        _statePath = Path.Combine(config.StateDirectory, "elevenlabs-clones.json");
        _state = ReadState(_statePath);
    }

    public bool IsOwnClone(string voiceId) => _state.Clones.ContainsKey(voiceId);

    /// False while the account or this character is in a cool-down after a failure.
    public bool CanTry(string speakerId)
    {
        DateTime now = DateTime.UtcNow;
        return !(_state.BlockedUntil > now) &&
               !(_state.RetryAfter.TryGetValue(speakerId, out DateTime until) && until > now);
    }

    public void Touch(string voiceId)
    {
        if (_state.Clones.TryGetValue(voiceId, out CloneRecord? record))
        {
            record.LastUsed = DateTime.UtcNow;
            Save();
        }
    }

    /// Drops a clone that no longer exists in the account.
    public void Forget(string voiceId, VoiceEntry? speaker = null)
    {
        _voices.UnmapVoice(Provider, voiceId);
        if (speaker != null && speaker.Voices.TryGetValue(Provider, out string? mapped) && mapped == voiceId)
        {
            speaker.Voices.Remove(Provider);
        }

        _state.Clones.Remove(voiceId);
        Save();
    }

    /// Returns the clone for this character, creating one if the account allows, or null.
    public async Task<string?> AcquireAsync(VoiceEntry speaker, float[] reference, CancellationToken token)
    {
        if (!CanTry(speaker.Id))
        {
            return null;
        }

        try
        {
            List<RemoteVoice> clones = await ListClonesAsync(token).ConfigureAwait(false);
            RemoteVoice? existing = clones
                .Where(voice => voice.Name == NamePrefix + speaker.Id)
                .OrderByDescending(voice => voice.Created)
                .FirstOrDefault();
            if (existing != null)
            {
                Adopt(speaker, existing.Id);
                Log.Info($"reusing ElevenLabs clone {existing.Id} for speaker {speaker.Id}");
                return existing.Id;
            }

            Subscription account = await GetSubscriptionAsync(token).ConfigureAwait(false);
            if (!account.CanClone)
            {
                Block(AccountRetry, "plan", "this ElevenLabs plan cannot clone voices");
                return null;
            }

            int reserve = Math.Max(0, _config.ReserveVoiceEdits);
            if (account.MaxAddEdits is int max && account.AddEdits >= max - reserve)
            {
                Block(account.ResetsAt ?? DateTime.UtcNow.AddDays(1), "budget",
                    $"{account.AddEdits} of {max} voice additions this month are used ({reserve} kept for you)");
                return null;
            }

            int needed = account.SlotsUsed - account.SlotLimit + 1;
            if (needed > 0 && !await FreeSlotsAsync(clones, needed, token).ConfigureAwait(false))
            {
                Block(AccountRetry, "slots", $"all {account.SlotLimit} voice slots hold voices that are not rotated " +
                                    "(your own voices or characters marked Keep)");
                return null;
            }

            string id = await CreateAsync(reference, speaker.Id, token).ConfigureAwait(false);
            Adopt(speaker, id);
            if (_state.Account != null)
            {
                _state.Account.SlotsUsed = Math.Max(0, account.SlotsUsed - Math.Max(0, needed)) + 1;
                _state.Account.AddEdits = account.AddEdits + 1;
                Save();
            }

            Log.Info($"cloned speaker {speaker.Id} to ElevenLabs voice {id} " +
                     $"({account.AddEdits + 1}/{account.MaxAddEdits?.ToString() ?? "unlimited"} additions this month)");
            return id;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Block(AccountRetry, "key", "the API key lacks the voices_read or voices_write permission " +
                                "(ElevenLabs -> Developers -> API keys)");
            Log.Warn(ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.RetryAfter[speaker.Id] = DateTime.UtcNow + SpeakerRetry;
            Save();
            Log.Warn($"cannot clone speaker {speaker.Id} ({ex.Message}); using a default voice for now");
            return null;
        }
    }

    /// Deletes mod clones that no character uses first (duplicates left by a cancelled line), then
    /// the ones heard least recently. Characters marked Keep are never touched.
    private async Task<bool> FreeSlotsAsync(List<RemoteVoice> clones, int needed, CancellationToken token)
    {
        HashSet<string> mapped = _voices.MappedVoiceIds(Provider);
        var victims = clones
            .Where(voice => !mapped.Contains(voice.Id))
            .OrderBy(voice => voice.Created)
            .Concat(clones
                .Where(voice => mapped.Contains(voice.Id) && !_voices.KeepsClone(SpeakerOf(voice)))
                .OrderBy(LastHeard))
            .Take(needed)
            .ToList();

        if (victims.Count < needed)
        {
            return false;
        }

        foreach (RemoteVoice victim in victims)
        {
            await DeleteAsync(victim.Id, token).ConfigureAwait(false);
            Forget(victim.Id);
            Log.Info($"freed a voice slot: deleted {victim.Name} (last heard {LastHeard(victim):yyyy-MM-dd HH:mm} UTC)");
        }

        return true;
    }

    private DateTime LastHeard(RemoteVoice voice)
        => _state.Clones.TryGetValue(voice.Id, out CloneRecord? record) ? record.LastUsed : voice.Created;

    private static string SpeakerOf(RemoteVoice voice)
        => voice.Name.StartsWith(NamePrefix, StringComparison.Ordinal) ? voice.Name[NamePrefix.Length..] : "";

    private void Adopt(VoiceEntry speaker, string voiceId)
    {
        _voices.SetVoiceId(speaker, Provider, voiceId);
        _state.Clones[voiceId] = new CloneRecord { Speaker = speaker.Id, LastUsed = DateTime.UtcNow };
        _state.RetryAfter.Remove(speaker.Id);
        Save();
    }

    private void Block(TimeSpan duration, string code, string reason) => Block(DateTime.UtcNow + duration, code, reason);

    /// The code lets the settings card explain the pause in the player's language.
    private void Block(DateTime until, string code, string reason)
    {
        _state.BlockedUntil = until;
        _state.BlockedCode = code;
        _state.BlockedReason = reason;
        Save();
        Log.Warn($"ElevenLabs cloning paused until {until.ToLocalTime():yyyy-MM-dd HH:mm}: {reason}; " +
                 "new characters use default voices meanwhile");
    }

    private string ApiRoot
    {
        get
        {
            string root = _config.BaseUrl.TrimEnd('/');
            return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, ApiRoot + path);
        request.Headers.TryAddWithoutValidation("xi-api-key", _config.ApiKey);
        return request;
    }

    private async Task<List<RemoteVoice>> ListClonesAsync(CancellationToken token)
    {
        var result = new List<RemoteVoice>();
        string? pageToken = null;
        do
        {
            string path = "/v2/voices?category=cloned&search=" + NamePrefix + "&page_size=100" +
                          (pageToken != null ? "&next_page_token=" + Uri.EscapeDataString(pageToken) : "");
            using HttpRequestMessage request = Request(HttpMethod.Get, path);
            using HttpResponseMessage response = await _send(request, token).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, token).ConfigureAwait(false);

            if (json.RootElement.TryGetProperty("voices", out JsonElement voices) &&
                voices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement voice in voices.EnumerateArray())
                {
                    string id = Text(voice, "voice_id");
                    string name = Text(voice, "name");
                    if (id.Length == 0 || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    long created = voice.TryGetProperty("created_at_unix", out JsonElement unix) &&
                                   unix.TryGetInt64(out long seconds) ? seconds : 0;
                    result.Add(new RemoteVoice(id, name, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime));
                }
            }

            pageToken = json.RootElement.TryGetProperty("has_more", out JsonElement more) &&
                        more.ValueKind == JsonValueKind.True
                ? Text(json.RootElement, "next_page_token")
                : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    /// Saves the player's own voices (designed, cloned by hand, saved from the library) to
    /// elevenlabs-voices.json so the settings card can offer them by name. Mod clones are left out.
    public async Task<int> SaveAccountVoicesAsync(CancellationToken token)
    {
        var voices = new List<AccountVoice>();
        string? pageToken = null;
        do
        {
            string path = "/v2/voices?voice_type=non-default&page_size=100" +
                          (pageToken != null ? "&next_page_token=" + Uri.EscapeDataString(pageToken) : "");
            using HttpRequestMessage request = Request(HttpMethod.Get, path);
            using HttpResponseMessage response = await _send(request, token).ConfigureAwait(false);
            using JsonDocument json = await ReadJsonAsync(response, token).ConfigureAwait(false);

            if (json.RootElement.TryGetProperty("voices", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement voice in list.EnumerateArray())
                {
                    string id = Text(voice, "voice_id");
                    string name = Text(voice, "name");
                    if (id.Length > 0 && !name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    {
                        voices.Add(new AccountVoice { Id = id, Name = name, Category = Text(voice, "category") });
                    }
                }
            }

            pageToken = json.RootElement.TryGetProperty("has_more", out JsonElement more) &&
                        more.ValueKind == JsonValueKind.True
                ? Text(json.RootElement, "next_page_token")
                : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        string target = Path.Combine(Path.GetDirectoryName(_statePath)!, "elevenlabs-voices.json");
        string temp = target + ".tmp";
        var file = new AccountVoices { Checked = DateTime.UtcNow, Voices = voices };
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Config.SerializerOptions));
        File.Move(temp, target, overwrite: true);
        return voices.Count;
    }

    private async Task<Subscription> GetSubscriptionAsync(CancellationToken token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, "/v1/user/subscription");
        using HttpResponseMessage response = await _send(request, token).ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        JsonElement root = json.RootElement;

        var account = new Subscription(
            Number(root, "voice_slots_used") ?? 0,
            Number(root, "voice_limit") ?? int.MaxValue,
            Number(root, "voice_add_edit_counter") ?? 0,
            Number(root, "max_voice_add_edits"),
            !root.TryGetProperty("can_use_instant_voice_cloning", out JsonElement can) || can.ValueKind != JsonValueKind.False,
            Number(root, "next_character_count_reset_unix") is int reset && reset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime
                : null);

        // The settings card shows this snapshot; it has no way to ask ElevenLabs itself.
        _state.Account = new AccountSnapshot
        {
            SlotsUsed = account.SlotsUsed,
            SlotLimit = account.SlotLimit,
            AddEdits = account.AddEdits,
            MaxAddEdits = account.MaxAddEdits,
            Checked = DateTime.UtcNow,
        };
        Save();
        return account;
    }

    private async Task DeleteAsync(string voiceId, CancellationToken token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Delete, "/v1/voices/" + Uri.EscapeDataString(voiceId));
        try
        {
            using HttpResponseMessage response = await _send(request, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted, by hand or by another session.
        }
    }

    private async Task<string> CreateAsync(float[] reference, string speakerId, CancellationToken token)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(NamePrefix + speakerId), "name");
        form.Add(new StringContent("Scam With Your Friends character, cloned by SWYF Cloud TTS"), "description");

        var audio = new ByteArrayContent(Pcm.ToWav(reference));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "files", "reference.wav");

        using HttpRequestMessage request = Request(HttpMethod.Post, "/v1/voices/add");
        request.Content = form;
        using HttpResponseMessage response = await _send(request, token).ConfigureAwait(false);
        using JsonDocument json = await ReadJsonAsync(response, token).ConfigureAwait(false);

        string id = Text(json.RootElement, "voice_id");
        return id.Length > 0 ? id : throw new InvalidOperationException("ElevenLabs returned no voice_id");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number)
            ? (int)Math.Clamp(number, int.MinValue, int.MaxValue)
            : null;

    private static CloneState ReadState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<CloneState>(File.ReadAllText(path), Config.SerializerOptions) ??
                       new CloneState();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{Path.GetFileName(path)} is unreadable ({ex.Message}); starting over");
        }

        return new CloneState();
    }

    private void Save()
    {
        try
        {
            string temp = _statePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Config.SerializerOptions));
            File.Move(temp, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot save {Path.GetFileName(_statePath)}: {ex.Message}");
        }
    }

    private sealed record RemoteVoice(string Id, string Name, DateTime Created);

    private sealed record Subscription(int SlotsUsed, int SlotLimit, int AddEdits, int? MaxAddEdits, bool CanClone,
                                       DateTime? ResetsAt);
}

internal sealed class CloneState
{
    [JsonPropertyName("clones")] public Dictionary<string, CloneRecord> Clones { get; set; } = new();

    [JsonPropertyName("blockedUntil")] public DateTime? BlockedUntil { get; set; }

    [JsonPropertyName("blockedCode")] public string? BlockedCode { get; set; }

    [JsonPropertyName("blockedReason")] public string? BlockedReason { get; set; }

    [JsonPropertyName("retryAfter")] public Dictionary<string, DateTime> RetryAfter { get; set; } = new();

    [JsonPropertyName("account")] public AccountSnapshot? Account { get; set; }
}

internal sealed class AccountSnapshot
{
    [JsonPropertyName("slotsUsed")] public int SlotsUsed { get; set; }

    [JsonPropertyName("slotLimit")] public int SlotLimit { get; set; }

    [JsonPropertyName("addEdits")] public int AddEdits { get; set; }

    [JsonPropertyName("maxAddEdits")] public int? MaxAddEdits { get; set; }

    [JsonPropertyName("checked")] public DateTime Checked { get; set; }
}

internal sealed class CloneRecord
{
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";

    [JsonPropertyName("lastUsed")] public DateTime LastUsed { get; set; }
}

internal sealed class AccountVoices
{
    [JsonPropertyName("checked")] public DateTime Checked { get; set; }

    [JsonPropertyName("voices")] public List<AccountVoice> Voices { get; set; } = new();
}

internal sealed class AccountVoice
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("category")] public string Category { get; set; } = "";
}
