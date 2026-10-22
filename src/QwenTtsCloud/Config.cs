using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwyfCloudTts;

internal sealed class Config
{
    public const string FileName = "cloud-tts.config.json";

    public static readonly string Version =
        typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = "googleai";

    [JsonPropertyName("openai")]
    public OpenAiConfig OpenAi { get; set; } = new();

    [JsonPropertyName("elevenlabs")]
    public ElevenLabsConfig ElevenLabs { get; set; } = new();

    [JsonPropertyName("googleai")]
    public GoogleAiConfig GoogleAi { get; set; } = new();

    [JsonPropertyName("qwenremote")]
    public QwenRemoteConfig QwenRemote { get; set; } = new();

    [JsonPropertyName("cache")]
    public CacheConfig Cache { get; set; } = new();

    [JsonPropertyName("fallback")]
    public FallbackConfig Fallback { get; set; } = new();

    [JsonPropertyName("network")]
    public NetworkConfig Network { get; set; } = new();

    [JsonPropertyName("logging")]
    public LoggingConfig Logging { get; set; } = new();

    /// Path this configuration was read from (not serialized).
    [JsonIgnore]
    public string Path { get; private set; } = "";

    [JsonIgnore]
    public string StateDirectory { get; private set; } = "";

    /// SWYF_TTS_STATE_DIR moves all state (config, voices, cache, logs) elsewhere, for tests.
    public static string StateRoot =>
        Environment.GetEnvironmentVariable("SWYF_TTS_STATE_DIR") is { Length: > 0 } custom
            ? System.IO.Path.GetFullPath(custom)
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWYF-CloudTTS");

    /// Shared with the in-game settings card, which edits the same file.
    public static string UserConfigPath => System.IO.Path.Combine(StateRoot, FileName);

    public static string ResolvePath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return System.IO.Path.GetFullPath(explicitPath!);
        }

        string? fromEnv = Environment.GetEnvironmentVariable("SWYF_TTS_CONFIG");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return System.IO.Path.GetFullPath(fromEnv!);
        }

        MigrateLegacyConfig();
        return UserConfigPath;
    }

    /// Versions before 1.2 kept the config next to the worker inside the game folder.
    private static void MigrateLegacyConfig()
    {
        string legacy = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        try
        {
            if (!File.Exists(UserConfigPath) && File.Exists(legacy))
            {
                Directory.CreateDirectory(StateRoot);
                File.Copy(legacy, UserConfigPath);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot migrate {legacy}: {ex.Message}");
        }
    }

    public static Config Load(string? explicitPath = null)
    {
        string path = ResolvePath(explicitPath);

        Config config;
        if (File.Exists(path))
        {
            try
            {
                string json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<Config>(json, SerializerOptions) ?? new Config();
            }
            catch (Exception ex)
            {
                Log.Error($"config parse failed ({path}): {ex.Message}; using defaults");
                config = new Config();
            }
        }
        else
        {
            config = new Config();
            try
            {
                string? directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(config, SerializerOptions));
                Log.Info($"wrote default config to {path}");
            }
            catch (Exception ex)
            {
                Log.Warn("cannot write default config: " + ex.Message);
            }
        }

        // API keys can be supplied through the environment instead of a file.
        config.OpenAi.ApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SWYF_TTS_OPENAI_KEY"), config.OpenAi.ApiKey);
        config.ElevenLabs.ApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SWYF_TTS_ELEVENLABS_KEY"), config.ElevenLabs.ApiKey);
        config.GoogleAi.ApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SWYF_TTS_GOOGLEAI_KEY"),
            FirstNonEmpty(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                FirstNonEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"),
                    config.GoogleAi.ApiKey)));
        config.QwenRemote.ApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SWYF_TTS_QWENREMOTE_KEY"),
            FirstNonEmpty(Environment.GetEnvironmentVariable("QWEN_TTS_API_KEY"), config.QwenRemote.ApiKey));
        config.QwenRemote.BaseUrl = FirstNonEmpty(
            Environment.GetEnvironmentVariable("SWYF_TTS_QWENREMOTE_URL"), config.QwenRemote.BaseUrl);

        config.Path = path;
        config.StateDirectory = StateRoot;
        Directory.CreateDirectory(config.StateDirectory);

        return config;
    }

    private static string FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first! : (second ?? "");

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    internal sealed class OpenAiConfig
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-mini-tts";
        [JsonPropertyName("voice")] public string? Voice { get; set; }
        [JsonPropertyName("instructions")] public string? Instructions { get; set; }
        [JsonPropertyName("speed")] public double? Speed { get; set; }
    }

    internal sealed class ElevenLabsConfig
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://api.elevenlabs.io/v1";
        [JsonPropertyName("modelId")] public string ModelId { get; set; } = "eleven_v4_turbo";
        [JsonPropertyName("autoClone")] public bool AutoClone { get; set; } = true;

        /// Global voice; empty picks a default ElevenLabs voice per character.
        [JsonPropertyName("defaultVoiceId")] public string DefaultVoiceId { get; set; } = "";

        /// Monthly voice additions the mod leaves unused, for the player's own voices.
        [JsonPropertyName("reserveVoiceEdits")] public int ReserveVoiceEdits { get; set; } = 5;

        [JsonPropertyName("stability")] public double Stability { get; set; } = 0.5;
        [JsonPropertyName("similarityBoost")] public double SimilarityBoost { get; set; } = 0.8;
    }

    /// Google AI Studio (Gemini TTS), not Google Cloud TTS.
    internal sealed class GoogleAiConfig
    {
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        [JsonPropertyName("model")] public string Model { get; set; } = "gemini-3.8-flash-tts";
        [JsonPropertyName("voiceId")] public string VoiceId { get; set; } = "";
        [JsonPropertyName("style")] public string Style { get; set; } = "natural, conversational";
    }

    /// Self-hosted Qwen3-TTS on a rented GPU (or any OpenAI-compatible TTS server such as
    /// k8bit/qwen3-tts). "clone" sends the reference clip the game already provides, so every
    /// character keeps their own voice; "voice" uses a reference clip preloaded on the server;
    /// "design" uses a custom POST /tts VoiceDesign wrapper and builds the voice from Instruct.
    internal sealed class QwenRemoteConfig
    {
        /// Server root, for example http://1.2.3.4:8000. A trailing /v1 is accepted.
        [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

        /// Optional bearer token (RunPod proxy, reverse proxy, or a custom gateway).
        [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";

        /// "clone" (per-character reference audio), "voice" (named voice on the server)
        /// or "design" (POST /tts with a voice description).
        [JsonPropertyName("mode")] public string Mode { get; set; } = "clone";

        [JsonPropertyName("model")] public string Model { get; set; } = "tts-1";

        /// Server-side voice name for "voice" mode; also used if cloning is unavailable.
        [JsonPropertyName("voice")] public string Voice { get; set; } = "";

        /// "auto" follows the language the game asks for; otherwise a Qwen3-TTS language name.
        [JsonPropertyName("language")] public string Language { get; set; } = "auto";

        /// Optional style instruction (CustomVoice / VoiceDesign models).
        [JsonPropertyName("instruct")] public string? Instruct { get; set; }
    }

    internal sealed class CacheConfig
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
        [JsonPropertyName("maxMegabytes")] public int MaxMegabytes { get; set; } = 512;

        /// The settings card drops cached audio when voices or the provider change.
        [JsonPropertyName("clearOnVoiceChange")] public bool ClearOnVoiceChange { get; set; } = true;
    }

    internal sealed class NetworkConfig
    {
        /// Seconds without audio before a request is retried. The first audio of a line has 15 s in total.
        [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 12;

        /// Total attempts per request (1 = no retry).
        [JsonPropertyName("maxAttempts")] public int MaxAttempts { get; set; } = 2;
    }

    internal sealed class FallbackConfig
    {
        /// Off by default: the local model needs most of the game's 20 s first-audio limit just to
        /// load, so it is only tried when the cloud fails within a few seconds.
        [JsonPropertyName("passthrough")] public bool Passthrough { get; set; }
        [JsonPropertyName("originalExe")] public string OriginalExe { get; set; } = "QwenTtsWorker.orig.exe";
    }

    internal sealed class LoggingConfig
    {
        [JsonPropertyName("verbose")] public bool Verbose { get; set; }
    }
}
