using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwyfCloudTts;

/// One speaker, identified by the hash of the voice reference the game sends with every
/// request. The mapping lives on disk so a character keeps the same cloud voice forever.
internal sealed class VoiceEntry
{
    /// Speaker hash; the dictionary key in voices.json.
    [JsonIgnore]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")] public string? Label { get; set; }

    [JsonPropertyName("gender")] public string Gender { get; set; } = "unknown";

    [JsonPropertyName("pitchHz")] public double PitchHz { get; set; }

    [JsonPropertyName("firstSeen")] public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    /// Explicit choices only: provider name -> provider specific voice id.
    [JsonPropertyName("voices")] public Dictionary<string, string> Voices { get; set; } = new();

    /// Set in the settings card: the ElevenLabs clone of this character is never rotated out.
    [JsonPropertyName("keepClone")] public bool? KeepClone { get; set; }
}

internal sealed class VoiceBook
{
    [JsonPropertyName("speakers")] public Dictionary<string, VoiceEntry> Speakers { get; set; } = new();
}

/// Turns the per-request reference audio into a stable speaker id and a cloud voice.
/// The game never says who is talking; it only sends that character's reference clip,
/// which works as a fingerprint. voices.json is shared with the in-game settings card,
/// so it is re-read whenever the card has changed it.
internal sealed class VoiceResolver
{
    private static readonly string[] OpenAiMale = { "onyx", "echo", "ash", "fable" };
    private static readonly string[] OpenAiFemale = { "nova", "shimmer", "sage", "coral" };
    private static readonly string[] OpenAiNeutral = { "alloy", "sage", "verse" };

    private static readonly string[] GoogleMale = { "Puck", "Charon", "Fenrir", "Orus", "Iapetus", "Alnilam" };
    private static readonly string[] GoogleFemale = { "Kore", "Aoede", "Leda", "Callirrhoe", "Autonoe", "Despina" };
    private static readonly string[] GoogleNeutral = { "Charon", "Zephyr", "Schedar" };

    /// ElevenLabs default voices (October 2026). They take no voice slots and cannot be deleted.
    private static readonly string[] ElevenMale =
    {
        "CwhRBWXzGAHq8TQ4Fs17", "IKne3meq5aSn9XLyUdCD", "JBFqnCBsd6RMkjVDRZzb", "N2lVS1w4EtoT3dr4eOWO",
        "SOYHLrjzK2X1ezoPC6cr", "TX3LPaxmHKxFdv7VOQHJ", "bIHbv24MWmeRgasZH58o", "cjVigY5qzO86Huf0OWal",
        "iP95p4xoKVk53GoZ742B", "nPczCjzI2devNBz1zQrb", "onwK4e9ZLuTAKqWW03F9", "pNInz6obpgDQGcFmaJgB",
        "pqHfZKP75CvOlQylNhV4",
    };

    private static readonly string[] ElevenFemale =
    {
        "hpp4J3VqNfWAUOO0d1Us", "EXAVITQu4vr4xnSDxMaL", "FGY2WhTYpPnrIDTdsKH5", "Xb7hH8MSUJpSbSDYk0k2",
        "XrExE9yKIg1WjnnlVkGX", "cgSgspJ2msm6clMCkdW9", "pFZP5JQG7iQjIQuC4Bku",
    };

    private static readonly string[] ElevenNeutral = { "SAz9YHcvj6GT2YYXdXww", "JBFqnCBsd6RMkjVDRZzb", "XrExE9yKIg1WjnnlVkGX" };

    /// The stock voice older configs carried as their default; treated as "not chosen".
    private const string LegacyElevenDefault = "21m00Tcm4TlvDq8ikWAM";

    private readonly object _gate = new();
    private readonly string _stateDirectory;
    private readonly string _bookPath;
    private readonly string _wavDirectory;
    private VoiceBook _book;
    private DateTime _bookStamp;

    public VoiceResolver(Config config)
    {
        _stateDirectory = config.StateDirectory;
        _bookPath = Path.Combine(config.StateDirectory, "voices.json");
        _wavDirectory = Path.Combine(config.StateDirectory, "voices");
        _book = ReadBook(config.StateDirectory);
        _bookStamp = Stamp();
    }

    public VoiceEntry Resolve(float[] reference, string language)
    {
        string id = Pcm.HashReference(reference);

        lock (_gate)
        {
            ReloadIfChanged();
            if (_book.Speakers.TryGetValue(id, out VoiceEntry? existing))
            {
                existing.Id = id;
                return existing;
            }

            double pitch = Pcm.EstimatePitchHz(reference);
            var entry = new VoiceEntry
            {
                Id = id,
                Gender = pitch <= 0 ? "unknown" : (pitch < 165 ? "male" : "female"),
                PitchHz = Math.Round(pitch, 1),
            };

            _book.Speakers[id] = entry;
            Log.Info($"new speaker {id}: pitch={entry.PitchHz:0.0} Hz, gender={entry.Gender}, " +
                     $"language={language}, samples={reference.Length}");

            try
            {
                Directory.CreateDirectory(_wavDirectory);
                File.WriteAllBytes(Path.Combine(_wavDirectory, id + ".wav"), Pcm.ToWav(reference));
            }
            catch (Exception ex)
            {
                Log.Debug("cannot save the reference clip: " + ex.Message);
            }

            Save();
            return entry;
        }
    }

    /// A known character by id; saved clips round-trip through 16 bits, so their hash can differ.
    public VoiceEntry? Find(string id)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            if (_book.Speakers.TryGetValue(id, out VoiceEntry? entry))
            {
                entry.Id = id;
                return entry;
            }

            return null;
        }
    }

    public string GetVoiceId(VoiceEntry entry, string provider)
    {
        lock (_gate)
        {
            return entry.Voices.TryGetValue(provider, out string? voiceId) ? voiceId : "";
        }
    }

    public void SetVoiceId(VoiceEntry entry, string provider, string voiceId)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            entry.Voices[provider] = voiceId;
            if (_book.Speakers.TryGetValue(entry.Id, out VoiceEntry? current) && !ReferenceEquals(current, entry))
            {
                current.Voices[provider] = voiceId;
            }

            Save();
        }
    }

    public string PickOpenAiVoice(VoiceEntry entry, string? configured)
        => PickVoice(entry, "openai", configured, OpenAiMale, OpenAiFemale, OpenAiNeutral);

    public string PickGoogleVoice(VoiceEntry entry, string? configured)
        => PickVoice(entry, "googleai", configured, GoogleMale, GoogleFemale, GoogleNeutral);

    /// Voice for a character without a clone: their own choice, the global voice, or a default voice.
    public string PickElevenLabsVoice(VoiceEntry entry, string? configured)
        => PickVoice(entry, "elevenlabs", configured == LegacyElevenDefault ? null : configured,
            ElevenMale, ElevenFemale, ElevenNeutral);

    public bool KeepsClone(string speakerId)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            return _book.Speakers.TryGetValue(speakerId, out VoiceEntry? entry) && entry.KeepClone == true;
        }
    }

    /// Voice ids currently assigned to any character for this provider.
    public HashSet<string> MappedVoiceIds(string provider)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (VoiceEntry entry in _book.Speakers.Values)
            {
                if (entry.Voices.TryGetValue(provider, out string? id) && id.Length > 0)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
    }

    /// Forgets a voice that no longer exists, for every character that used it.
    public void UnmapVoice(string provider, string voiceId)
    {
        lock (_gate)
        {
            ReloadIfChanged();
            bool changed = false;
            foreach (VoiceEntry entry in _book.Speakers.Values)
            {
                if (entry.Voices.TryGetValue(provider, out string? id) && id == voiceId)
                {
                    entry.Voices.Remove(provider);
                    changed = true;
                }
            }

            if (changed)
            {
                Save();
            }
        }
    }

    /// A self-hosted server decides which voices exist, so there is no preset pool.
    public string PickQwenVoice(VoiceEntry entry, string? configured)
    {
        string mapped = GetVoiceId(entry, "qwenremote");
        return mapped.Length > 0 ? mapped : configured ?? "";
    }

    /// Character choice, then the global voice, then a pick from the gender pool. The pool pick
    /// is derived from stored speaker data, so it is stable without being written to disk, and
    /// changing the global voice later still affects every character without an explicit choice.
    private string PickVoice(VoiceEntry entry, string provider, string? configured,
                             string[] male, string[] female, string[] neutral)
    {
        string mapped = GetVoiceId(entry, provider);
        if (mapped.Length > 0)
        {
            return mapped;
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        string[] pool = entry.Gender switch
        {
            "male" => male,
            "female" => female,
            _ => neutral,
        };

        int seed = Math.Abs((int)(entry.PitchHz * 10)) + entry.FirstSeenUtc.Millisecond;
        return pool[seed % pool.Length];
    }

    public static VoiceBook ReadBook(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, "voices.json");
        try
        {
            if (File.Exists(path))
            {
                VoiceBook? book = JsonSerializer.Deserialize<VoiceBook>(
                    File.ReadAllText(path), Config.SerializerOptions);
                if (book != null)
                {
                    foreach ((string id, VoiceEntry entry) in book.Speakers)
                    {
                        entry.Id = id;
                    }

                    return book;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"voices.json is unreadable ({ex.Message}); starting a new speaker book");
        }

        return new VoiceBook();
    }

    private void ReloadIfChanged()
    {
        DateTime stamp = Stamp();
        if (stamp == _bookStamp)
        {
            return;
        }

        _book = ReadBook(_stateDirectory);
        _bookStamp = stamp;
        Log.Debug("voices.json changed on disk; reloaded");
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            string temp = _bookPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_book, Config.SerializerOptions));
            File.Move(temp, _bookPath, overwrite: true);
            _bookStamp = Stamp();
        }
        catch (Exception ex)
        {
            Log.Warn("cannot save voices.json: " + ex.Message);
        }
    }

    private DateTime Stamp()
    {
        try
        {
            return File.Exists(_bookPath) ? File.GetLastWriteTimeUtc(_bookPath) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    public static string Describe(VoiceEntry entry, string id)
        => $"{entry.Label ?? id}({entry.Gender}, {entry.PitchHz.ToString("0", CultureInfo.InvariantCulture)} Hz)";
}
