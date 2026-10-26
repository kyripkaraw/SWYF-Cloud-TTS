using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    private readonly Func<HttpCall, CancellationToken, Task<HttpReply>> _send;
    private readonly string _statePath;
    private readonly CloneState _state;

    public ElevenLabsClones(Config config, VoiceResolver voices,
                            Func<HttpCall, CancellationToken, Task<HttpReply>> send)
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
        catch (HttpFailure ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
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
        => voice.Name.StartsWith(NamePrefix, StringComparison.Ordinal) ? voice.Name.Substring(NamePrefix.Length) : "";

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
            return root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root.Substring(0, root.Length - 3) : root;
        }
    }

    private HttpCall Request(HttpCall call) => call.Header("xi-api-key", _config.ApiKey);

    private async Task<List<RemoteVoice>> ListClonesAsync(CancellationToken token)
    {
        var result = new List<RemoteVoice>();
        string? pageToken = null;
        do
        {
            string path = "/v2/voices?category=cloned&search=" + NamePrefix + "&page_size=100" +
                          (pageToken != null ? "&next_page_token=" + Uri.EscapeDataString(pageToken) : "");
            using HttpReply response = await _send(Request(HttpCall.Get(ApiRoot + path)), token).ConfigureAwait(false);
            JObject json = await response.ReadJsonAsync(token).ConfigureAwait(false);

            if (json["voices"] is JArray voices)
            {
                foreach (JObject voice in voices.OfType<JObject>())
                {
                    string id = Text(voice, "voice_id");
                    string name = Text(voice, "name");
                    if (id.Length == 0 || !name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    long created = voice["created_at_unix"] is JValue { Type: JTokenType.Integer } unix
                        ? unix.Value<long>()
                        : 0;
                    result.Add(new RemoteVoice(id, name, DateTimeOffset.FromUnixTimeSeconds(created).UtcDateTime));
                }
            }

            pageToken = HasMore(json) ? Text(json, "next_page_token") : null;
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
            using HttpReply response = await _send(Request(HttpCall.Get(ApiRoot + path)), token).ConfigureAwait(false);
            JObject json = await response.ReadJsonAsync(token).ConfigureAwait(false);

            if (json["voices"] is JArray list)
            {
                foreach (JObject voice in list.OfType<JObject>())
                {
                    string id = Text(voice, "voice_id");
                    string name = Text(voice, "name");
                    if (id.Length > 0 && !name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    {
                        voices.Add(new AccountVoice { Id = id, Name = name, Category = Text(voice, "category") });
                    }
                }
            }

            pageToken = HasMore(json) ? Text(json, "next_page_token") : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        string target = Path.Combine(Path.GetDirectoryName(_statePath)!, "elevenlabs-voices.json");
        string temp = target + ".tmp";
        var file = new AccountVoices { Checked = DateTime.UtcNow, Voices = voices };
        File.WriteAllText(temp, Config.ToJson(file));
        Compat.MoveOverwrite(temp, target);
        return voices.Count;
    }

    private async Task<Subscription> GetSubscriptionAsync(CancellationToken token)
    {
        using HttpReply response = await _send(Request(HttpCall.Get(ApiRoot + "/v1/user/subscription")), token)
            .ConfigureAwait(false);
        JObject root = await response.ReadJsonAsync(token).ConfigureAwait(false);

        var account = new Subscription(
            Number(root, "voice_slots_used") ?? 0,
            Number(root, "voice_limit") ?? int.MaxValue,
            Number(root, "voice_add_edit_counter") ?? 0,
            Number(root, "max_voice_add_edits"),
            !(root["can_use_instant_voice_cloning"] is JValue { Type: JTokenType.Boolean } can && !can.Value<bool>()),
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
        HttpCall request = Request(HttpCall.Delete(ApiRoot + "/v1/voices/" + Uri.EscapeDataString(voiceId)));
        try
        {
            using HttpReply response = await _send(request, token).ConfigureAwait(false);
        }
        catch (HttpFailure ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already deleted, by hand or by another session.
        }
    }

    private async Task<string> CreateAsync(float[] reference, string speakerId, CancellationToken token)
    {
        var form = new MultipartForm()
            .Text("name", NamePrefix + speakerId)
            .Text("description", "Scam With Your Friends character, cloned by SWYF Cloud TTS")
            .File("files", "reference.wav", "audio/wav", Pcm.ToWav(reference));

        using HttpReply response = await _send(Request(HttpCall.PostForm(ApiRoot + "/v1/voices/add", form)), token)
            .ConfigureAwait(false);
        JObject json = await response.ReadJsonAsync(token).ConfigureAwait(false);

        string id = Text(json, "voice_id");
        return id.Length > 0 ? id : throw new InvalidOperationException("ElevenLabs returned no voice_id");
    }

    private static string Text(JObject element, string name)
        => element[name] is JValue { Type: JTokenType.String } value ? value.Value<string>() ?? "" : "";

    private static int? Number(JObject element, string name)
        => element[name] is JValue { Type: JTokenType.Integer } value
            ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value.Value<long>()))
            : null;

    private static bool HasMore(JObject json)
        => json["has_more"] is JValue { Type: JTokenType.Boolean } more && more.Value<bool>();

    private static CloneState ReadState(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return Config.FromJson<CloneState>(File.ReadAllText(path)) ??
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
            File.WriteAllText(temp, Config.ToJson(_state));
            Compat.MoveOverwrite(temp, _statePath);
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
    [JsonProperty("clones")] public Dictionary<string, CloneRecord> Clones { get; set; } = new();

    [JsonProperty("blockedUntil")] public DateTime? BlockedUntil { get; set; }

    [JsonProperty("blockedCode")] public string? BlockedCode { get; set; }

    [JsonProperty("blockedReason")] public string? BlockedReason { get; set; }

    [JsonProperty("retryAfter")] public Dictionary<string, DateTime> RetryAfter { get; set; } = new();

    [JsonProperty("account")] public AccountSnapshot? Account { get; set; }
}

internal sealed class AccountSnapshot
{
    [JsonProperty("slotsUsed")] public int SlotsUsed { get; set; }

    [JsonProperty("slotLimit")] public int SlotLimit { get; set; }

    [JsonProperty("addEdits")] public int AddEdits { get; set; }

    [JsonProperty("maxAddEdits")] public int? MaxAddEdits { get; set; }

    [JsonProperty("checked")] public DateTime Checked { get; set; }
}

internal sealed class CloneRecord
{
    [JsonProperty("speaker")] public string Speaker { get; set; } = "";

    [JsonProperty("lastUsed")] public DateTime LastUsed { get; set; }
}

internal sealed class AccountVoices
{
    [JsonProperty("checked")] public DateTime Checked { get; set; }

    [JsonProperty("voices")] public List<AccountVoice> Voices { get; set; } = new();
}

internal sealed class AccountVoice
{
    [JsonProperty("id")] public string Id { get; set; } = "";

    [JsonProperty("name")] public string Name { get; set; } = "";

    [JsonProperty("category")] public string Category { get; set; } = "";
}
