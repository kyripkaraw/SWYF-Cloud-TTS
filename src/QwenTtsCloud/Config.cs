using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwyfCloudTts;

internal sealed class Config
{
    public const string FileName = "cloud-tts.config.json";

    public static readonly string Version =
        typeof(Config).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [JsonProperty("provider")]
    public string Provider { get; set; } = "googleai";

    [JsonProperty("openai")]
    public OpenAiConfig OpenAi { get; set; } = new();

    [JsonProperty("elevenlabs")]
    public ElevenLabsConfig ElevenLabs { get; set; } = new();

    [JsonProperty("googleai")]
    public GoogleAiConfig GoogleAi { get; set; } = new();

    [JsonProperty("qwenremote")]
    public QwenRemoteConfig QwenRemote { get; set; } = new();

    [JsonProperty("cache")]
    public CacheConfig Cache { get; set; } = new();

    [JsonProperty("fallback")]
    public FallbackConfig Fallback { get; set; } = new();

    [JsonProperty("network")]
    public NetworkConfig Network { get; set; } = new();

    [JsonProperty("logging")]
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
#if !NETFRAMEWORK
        // The in-game plugin never shipped a config of its own.
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
#endif
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
                config = FromJson<Config>(json) ?? new Config();
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

                File.WriteAllText(path, ToJson(config));
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

    /// Every state file goes through these two. Property names come from the attributes, and
    /// reading is case-insensitive and skips comments, so hand-edited files keep working. Only
    /// the plainest JsonConvert overloads are used: the game's Newtonsoft is stripped of the rest.
    public static string ToJson(object value)
    {
        // Unset optional values are left out, as earlier versions wrote them.
        JToken token = ParseJson(JsonConvert.SerializeObject(value));
        DropNulls(token);
        return token.ToString(Formatting.Indented);
    }

    /// Dates stay strings, exactly as written.
    public static JToken ParseJson(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JToken.ReadFrom(reader);
    }

    private static void DropNulls(JToken token)
    {
        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties().ToList())
            {
                if (property.Value.Type == JTokenType.Null)
                {
                    property.Remove();
                }
                else
                {
                    DropNulls(property.Value);
                }
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                DropNulls(item);
            }
        }
    }

    public static T? FromJson<T>(string json) where T : class => JsonConvert.DeserializeObject<T>(json);

    internal sealed class OpenAiConfig
    {
        [JsonProperty("apiKey")] public string ApiKey { get; set; } = "";
        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "https://api.openai.com/v1";
        [JsonProperty("model")] public string Model { get; set; } = "gpt-4o-mini-tts";
        [JsonProperty("voice")] public string? Voice { get; set; }
        [JsonProperty("instructions")] public string? Instructions { get; set; }
        [JsonProperty("speed")] public double? Speed { get; set; }
    }

    internal sealed class ElevenLabsConfig
    {
        [JsonProperty("apiKey")] public string ApiKey { get; set; } = "";
        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "https://api.elevenlabs.io/v1";
        [JsonProperty("modelId")] public string ModelId { get; set; } = "eleven_v4_turbo";
        [JsonProperty("autoClone")] public bool AutoClone { get; set; } = true;

        /// Global voice; empty picks a default ElevenLabs voice per character.
        [JsonProperty("defaultVoiceId")] public string DefaultVoiceId { get; set; } = "";

        /// Monthly voice additions the mod leaves unused, for the player's own voices.
        [JsonProperty("reserveVoiceEdits")] public int ReserveVoiceEdits { get; set; } = 5;

        [JsonProperty("stability")] public double Stability { get; set; } = 0.5;
        [JsonProperty("similarityBoost")] public double SimilarityBoost { get; set; } = 0.8;
    }

    /// Google AI Studio (Gemini TTS), not Google Cloud TTS.
    internal sealed class GoogleAiConfig
    {
        [JsonProperty("apiKey")] public string ApiKey { get; set; } = "";
        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
        [JsonProperty("model")] public string Model { get; set; } = "gemini-3.8-flash-tts";
        [JsonProperty("voiceId")] public string VoiceId { get; set; } = "";
        [JsonProperty("style")] public string Style { get; set; } = "natural, conversational";
    }

    /// Self-hosted Qwen3-TTS on a rented GPU (or any OpenAI-compatible TTS server such as
    /// k8bit/qwen3-tts). "clone" sends the reference clip the game already provides, so every
    /// character keeps their own voice; "voice" uses a reference clip preloaded on the server;
    /// "design" uses a custom POST /tts VoiceDesign wrapper and builds the voice from Instruct.
    internal sealed class QwenRemoteConfig
    {
        /// Server root, for example http://1.2.3.4:8000. A trailing /v1 is accepted.
        [JsonProperty("baseUrl")] public string BaseUrl { get; set; } = "http://127.0.0.1:8000";

        /// Optional bearer token (RunPod proxy, reverse proxy, or a custom gateway).
        [JsonProperty("apiKey")] public string ApiKey { get; set; } = "";

        /// "clone" (per-character reference audio), "voice" (named voice on the server)
        /// or "design" (POST /tts with a voice description).
        [JsonProperty("mode")] public string Mode { get; set; } = "clone";

        [JsonProperty("model")] public string Model { get; set; } = "tts-1";

        /// Server-side voice name for "voice" mode; also used if cloning is unavailable.
        [JsonProperty("voice")] public string Voice { get; set; } = "";

        /// "auto" follows the language the game asks for; otherwise a Qwen3-TTS language name.
        [JsonProperty("language")] public string Language { get; set; } = "auto";

        /// Optional style instruction (CustomVoice / VoiceDesign models).
        [JsonProperty("instruct")] public string? Instruct { get; set; }
    }

    internal sealed class CacheConfig
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("maxMegabytes")] public int MaxMegabytes { get; set; } = 512;

        /// The settings card drops cached audio when voices or the provider change.
        [JsonProperty("clearOnVoiceChange")] public bool ClearOnVoiceChange { get; set; } = true;
    }

    internal sealed class NetworkConfig
    {
        /// Seconds without audio before a request is retried. The first audio of a line has 15 s in total.
        [JsonProperty("timeoutSeconds")] public int TimeoutSeconds { get; set; } = 12;

        /// Total attempts per request (1 = no retry).
        [JsonProperty("maxAttempts")] public int MaxAttempts { get; set; } = 2;
    }

    internal sealed class FallbackConfig
    {
        /// Off by default: the local model needs most of the game's 20 s first-audio limit just to
        /// load, so it is only tried when the cloud fails within a few seconds.
        [JsonProperty("passthrough")] public bool Passthrough { get; set; }
        [JsonProperty("originalExe")] public string OriginalExe { get; set; } = "QwenTtsWorker.orig.exe";
    }

    internal sealed class LoggingConfig
    {
        [JsonProperty("verbose")] public bool Verbose { get; set; }
    }
}
