using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwyfCloudTts.Plugin
{
    /// Edits the same JSON file as the worker. It deliberately works on a JObject so
    /// hand-edited and future fields survive a round trip through the in-game UI.
    internal sealed class CloudTtsConfig
    {
        public static readonly string[] Providers =
        {
            "googleai", "elevenlabs", "openai", "qwenremote", "mock", "silent", "passthrough",
        };

        public static readonly string[] OpenAiVoices =
        {
            "alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse",
        };

        public static readonly string[] GoogleVoices =
        {
            "Kore", "Puck", "Charon", "Aoede", "Leda", "Fenrir", "Orus", "Zephyr", "Callirrhoe", "Autonoe",
            "Enceladus", "Iapetus", "Umbriel", "Algieba", "Despina", "Erinome", "Algenib", "Rasalgethi",
            "Laomedeia", "Achernar", "Alnilam", "Schedar", "Gacrux", "Pulcherrima", "Achird",
            "Zubenelgenubi", "Vindemiatrix", "Sadachbia", "Sadaltager", "Sulafat",
        };

        /// ElevenLabs default voices: id, name, then traits that have a translation ("voice.male"...).
        /// They take no voice slots and work on every plan, Free included.
        public static readonly string[][] ElevenLabsVoices =
        {
            new[] { "JBFqnCBsd6RMkjVDRZzb", "George", "male", "british" },
            new[] { "onwK4e9ZLuTAKqWW03F9", "Daniel", "male", "british" },
            new[] { "pNInz6obpgDQGcFmaJgB", "Adam", "male" },
            new[] { "nPczCjzI2devNBz1zQrb", "Brian", "male" },
            new[] { "CwhRBWXzGAHq8TQ4Fs17", "Roger", "male" },
            new[] { "N2lVS1w4EtoT3dr4eOWO", "Callum", "male" },
            new[] { "cjVigY5qzO86Huf0OWal", "Eric", "male" },
            new[] { "iP95p4xoKVk53GoZ742B", "Chris", "male" },
            new[] { "pqHfZKP75CvOlQylNhV4", "Bill", "male", "old" },
            new[] { "IKne3meq5aSn9XLyUdCD", "Charlie", "male", "young" },
            new[] { "SOYHLrjzK2X1ezoPC6cr", "Harry", "male", "young" },
            new[] { "TX3LPaxmHKxFdv7VOQHJ", "Liam", "male", "young" },
            new[] { "bIHbv24MWmeRgasZH58o", "Will", "male", "young" },
            new[] { "Xb7hH8MSUJpSbSDYk0k2", "Alice", "female", "british" },
            new[] { "pFZP5JQG7iQjIQuC4Bku", "Lily", "female", "british" },
            new[] { "XrExE9yKIg1WjnnlVkGX", "Matilda", "female" },
            new[] { "hpp4J3VqNfWAUOO0d1Us", "Bella", "female" },
            new[] { "EXAVITQu4vr4xnSDxMaL", "Sarah", "female", "young" },
            new[] { "FGY2WhTYpPnrIDTdsKH5", "Laura", "female", "young" },
            new[] { "cgSgspJ2msm6clMCkdW9", "Jessica", "female", "young" },
            new[] { "SAz9YHcvj6GT2YYXdXww", "River", "neutral" },
        };

        /// The 1.2 default ("Rachel"); the worker treats it as no choice, and so does the card.
        public const string LegacyElevenLabsDefault = "21m00Tcm4TlvDq8ikWAM";

        /// "George, male, British" in the card language, or null for an unknown id.
        public static string ElevenLabsDefaultLabel(string id)
        {
            foreach (string[] voice in ElevenLabsVoices)
            {
                if (voice[0] != id)
                {
                    continue;
                }

                string label = voice[1];
                for (int i = 2; i < voice.Length; i++)
                {
                    label += ", " + CloudTtsText.T("voice." + voice[i]);
                }

                return label;
            }

            return null;
        }

        private const string DefaultsJson = @"{
  ""provider"": ""googleai"",
  ""openai"": {
    ""apiKey"": """",
    ""baseUrl"": ""https://api.openai.com/v1"",
    ""model"": ""gpt-4o-mini-tts"",
    ""voice"": null,
    ""instructions"": null,
    ""speed"": null
  },
  ""elevenlabs"": {
    ""apiKey"": """",
    ""baseUrl"": ""https://api.elevenlabs.io/v1"",
    ""modelId"": ""eleven_v4_turbo"",
    ""autoClone"": true,
    ""defaultVoiceId"": """",
    ""reserveVoiceEdits"": 5,
    ""stability"": 0.5,
    ""similarityBoost"": 0.8
  },
  ""googleai"": {
    ""apiKey"": """",
    ""baseUrl"": ""https://generativelanguage.googleapis.com/v1beta"",
    ""model"": ""gemini-3.8-flash-tts"",
    ""voiceId"": """",
    ""style"": ""natural, conversational""
  },
  ""qwenremote"": {
    ""apiKey"": """",
    ""baseUrl"": ""http://127.0.0.1:8000"",
    ""mode"": ""clone"",
    ""model"": ""tts-1"",
    ""voice"": """",
    ""language"": ""auto"",
    ""instruct"": null
  },
  ""cache"": {
    ""enabled"": true,
    ""maxMegabytes"": 512,
    ""clearOnVoiceChange"": true
  },
  ""fallback"": {
    ""passthrough"": false,
    ""originalExe"": ""QwenTtsWorker.orig.exe""
  },
  ""network"": {
    ""timeoutSeconds"": 12,
    ""maxAttempts"": 2
  },
  ""logging"": {
    ""verbose"": false
  }
}";

        private CloudTtsConfig(JObject root)
        {
            Root = root;
        }

        public JObject Root { get; private set; }

        public bool LoadError { get; private set; }

        public string LoadErrorText { get; private set; } = "";

        public string Provider => NormalizeProvider(String(Root, null, "provider", "googleai"));

        /// Card language setting: auto, en, uk or ru. Only the card reads it.
        public string UiLanguage => String(Root, "ui", "language", "auto");

        public static CloudTtsConfig Load()
        {
            if (!File.Exists(CloudTtsPaths.ConfigPath))
            {
                return new CloudTtsConfig(ParseDefaults());
            }

            try
            {
                string text = File.ReadAllText(CloudTtsPaths.ConfigPath);
                JObject root = JObject.Parse(text);
                return new CloudTtsConfig(root);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("config could not be parsed: " + ex.Message);
                return new CloudTtsConfig(ParseDefaults())
                {
                    LoadError = true,
                    LoadErrorText = ex.Message,
                };
            }
        }

        public static JObject ParseDefaults() => JObject.Parse(DefaultsJson);

        public JObject CloneRoot() => (JObject)Root.DeepClone();

        /// Writes atomically and replaces the in-memory root only after success.
        public void Save(JObject root)
        {
            Directory.CreateDirectory(CloudTtsPaths.StateDirectory);

            string temp = CloudTtsPaths.ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = root.ToString(Formatting.Indented);
            File.WriteAllText(temp, json, new UTF8Encoding(false));

            try
            {
                if (File.Exists(CloudTtsPaths.ConfigPath))
                {
                    try
                    {
                        File.Replace(temp, CloudTtsPaths.ConfigPath, null);
                    }
                    catch
                    {
                        // Some file systems do not support Replace; fall back to a safe copy.
                        File.Copy(temp, CloudTtsPaths.ConfigPath, true);
                        File.Delete(temp);
                    }
                }
                else
                {
                    File.Move(temp, CloudTtsPaths.ConfigPath);
                }
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }

            Root = root;
            LoadError = false;
            LoadErrorText = "";
        }

        public static string NormalizeUiLanguage(string value)
        {
            string language = (value ?? "").Trim().ToLowerInvariant();
            return Array.IndexOf(CloudTtsText.Settings, language) >= 0 ? language : "auto";
        }

        public static string NormalizeProvider(string value)
        {
            string provider = (value ?? "").Trim().ToLowerInvariant();
            foreach (string known in Providers)
            {
                if (string.Equals(known, provider, StringComparison.Ordinal))
                {
                    return provider;
                }
            }

            return "googleai";
        }

        public static string ProviderLabel(string provider)
        {
            switch (provider)
            {
                case "googleai": return "Google AI (Gemini)";
                case "elevenlabs": return "ElevenLabs";
                case "openai": return "OpenAI";
                case "qwenremote": return CloudTtsText.T("provider.qwenremote");
                case "mock": return CloudTtsText.T("provider.mock");
                case "silent": return CloudTtsText.T("provider.silent");
                case "passthrough": return CloudTtsText.T("provider.passthrough");
                default: return provider ?? "";
            }
        }

        public static bool NeedsKey(string provider)
            => provider == "googleai" || provider == "elevenlabs" || provider == "openai";

        /// Services that speak through the network and have models and voices to choose.
        public static bool IsCloud(string provider) => NeedsKey(provider) || provider == "qwenremote";

        /// qwenremote accepts an optional bearer token, so its key row is shown but optional.
        public static bool UsesKey(string provider) => NeedsKey(provider) || provider == "qwenremote";

        public static string ModelKey(string provider)
        {
            switch (provider)
            {
                case "elevenlabs": return "modelId";
                case "googleai": return "model";
                case "openai": return "model";
                default: return "model";
            }
        }

        public static string VoiceKey(string provider)
        {
            switch (provider)
            {
                case "elevenlabs": return "defaultVoiceId";
                case "googleai": return "voiceId";
                case "openai": return "voice";
                default: return "voice";
            }
        }

        public static string DefaultModel(string provider)
        {
            switch (provider)
            {
                case "elevenlabs": return "eleven_v4_turbo";
                case "googleai": return "gemini-3.8-flash-tts";
                case "openai": return "gpt-4o-mini-tts";
                case "qwenremote": return "tts-1";
                default: return "";
            }
        }

        public static string ConfigKey(JObject root, string provider)
            => String(root, provider, "apiKey", "");

        public static string ConfigKeyTail(string key)
            => key.Length <= 4 ? "" : key.Substring(key.Length - 4);

        public static bool HasEnvironmentKey(string provider)
        {
            switch (provider)
            {
                case "googleai":
                    return Any(Environment.GetEnvironmentVariable("SWYF_TTS_GOOGLEAI_KEY"),
                        Environment.GetEnvironmentVariable("GOOGLE_API_KEY"),
                        Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
                case "elevenlabs":
                    return Any(Environment.GetEnvironmentVariable("SWYF_TTS_ELEVENLABS_KEY"));
                case "openai":
                    return Any(Environment.GetEnvironmentVariable("SWYF_TTS_OPENAI_KEY"));
                case "qwenremote":
                    return Any(Environment.GetEnvironmentVariable("SWYF_TTS_QWENREMOTE_KEY"),
                        Environment.GetEnvironmentVariable("QWEN_TTS_API_KEY"));
                default:
                    return false;
            }
        }

        /// Only the fields that actually affect which voice is spoken.
        public static string VoiceFingerprint(JObject root)
        {
            string provider = NormalizeProvider(String(root, null, "provider", "googleai"));
            return string.Join("|", new[]
            {
                provider,
                String(root, "openai", "model", ""),
                String(root, "openai", "voice", ""),
                String(root, "openai", "instructions", ""),
                Num(root, "openai", "speed", 1.0).ToString("0.###"),
                String(root, "elevenlabs", "modelId", ""),
                Bool(root, "elevenlabs", "autoClone", true).ToString(),
                String(root, "elevenlabs", "defaultVoiceId", ""),
                String(root, "googleai", "model", ""),
                String(root, "googleai", "voiceId", ""),
                String(root, "googleai", "style", ""),
                String(root, "qwenremote", "baseUrl", ""),
                String(root, "qwenremote", "mode", "clone"),
                String(root, "qwenremote", "model", ""),
                String(root, "qwenremote", "voice", ""),
                String(root, "qwenremote", "language", "auto"),
                String(root, "qwenremote", "instruct", ""),
            });
        }

        public static string String(JObject root, string section, string key, string fallback = "")
        {
            JToken token = Token(root, section, key);
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.String)
            {
                return (string)token ?? fallback;
            }

            return token.ToString();
        }

        public static bool Bool(JObject root, string section, string key, bool fallback = false)
        {
            JToken token = Token(root, section, key);
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            try
            {
                return token.Value<bool>();
            }
            catch
            {
                return fallback;
            }
        }

        public static int Int(JObject root, string section, string key, int fallback = 0)
        {
            JToken token = Token(root, section, key);
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            try
            {
                return token.Value<int>();
            }
            catch
            {
                return fallback;
            }
        }

        public static double Num(JObject root, string section, string key, double fallback = 0)
        {
            JToken token = Token(root, section, key);
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }

            try
            {
                return token.Value<double>();
            }
            catch
            {
                return fallback;
            }
        }

        public static JObject Section(JObject root, string name)
        {
            if (root[name] is JObject existing)
            {
                return existing;
            }

            var created = new JObject();
            root[name] = created;
            return created;
        }

        public static void Set(JObject root, string section, string key, JToken value)
        {
            if (string.IsNullOrEmpty(section))
            {
                root[key] = value;
            }
            else
            {
                Section(root, section)[key] = value;
            }
        }

        public static void SetString(JObject root, string section, string key, string value)
            => Set(root, section, key, new JValue(value ?? ""));

        private static JToken Token(JObject root, string section, string key)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(section))
            {
                return root[key];
            }

            return root[section] is JObject obj ? obj[key] : null;
        }

        private static bool Any(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
