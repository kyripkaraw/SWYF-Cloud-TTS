using System;
using System.Reflection;

namespace SwyfCloudTts.Plugin
{
    /// Since 0.1.95 the game keeps an ordered list of voice models for every call language
    /// (Kolkata.Speech.SpeechModelPreferences: Pocket TTS, MOSS TTS Nano, Qwen) and asks Qwen, and
    /// so the mod, only when Qwen is the first enabled model. For Russian it puts its built-in
    /// Pocket TTS first. While the mod speaks, Qwen comes first for Russian, and for the local
    /// provider for each language it speaks, unless the player switched Qwen off for that language
    /// in the game's own list. The local provider hands every other language back to the game's
    /// own voices, because it cannot speak them.
    internal static class CloudTtsModelOrder
    {
        public const string PreferencesType = "Kolkata.Speech.SpeechModelPreferences";

        private static bool _resolved;
        private static MethodInfo _get;
        private static FieldInfo _model;
        private static FieldInfo _enabled;
        private static FieldInfo _englishName;

        /// The answer to SpeechModelPreferences.PrefersQwen for one call language. The game's own
        /// answer is kept for "passthrough" and for the languages the provider leaves to the game.
        public static bool PrefersQwen(object language, bool gameAnswer, string provider, string[] localLanguages)
        {
            if (provider == "passthrough" || language == null)
            {
                return gameAnswer;
            }

            string name = EnglishName(language);
            bool local = provider == "local";
            bool speaks = local ? Contains(localLanguages, name) : name == "Russian";
            if (!speaks)
            {
                return !local && gameAnswer;
            }

            Array entries = Entries(language);
            if (entries == null)
            {
                return gameAnswer;
            }

            foreach (object entry in entries)
            {
                if (IsQwen(entry))
                {
                    return IsEnabled(entry);
                }
            }

            return false;
        }

        /// The game's own rule: Qwen is preferred when it is the first enabled model.
        public static bool GameAnswer(object language)
        {
            Array entries = language != null ? Entries(language) : null;
            if (entries == null)
            {
                return false;
            }

            foreach (object entry in entries)
            {
                if (IsEnabled(entry))
                {
                    return IsQwen(entry);
                }
            }

            return false;
        }

        /// English name of a Kolkata.Speech.CallLanguageOption ("Russian"), or "".
        public static string EnglishName(object language)
        {
            Resolve();
            try
            {
                return _englishName != null && language != null ? _englishName.GetValue(language) as string ?? "" : "";
            }
            catch
            {
                return "";
            }
        }

        private static Array Entries(object language)
        {
            Resolve();
            if (_get == null || _model == null || _enabled == null)
            {
                return null;
            }

            try
            {
                return _get.Invoke(null, new[] { language }) as Array;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("SpeechModelPreferences.Get failed: " + ex.Message);
                return null;
            }
        }

        private static bool IsQwen(object entry)
        {
            object model = _model.GetValue(entry);
            return model != null && model.ToString() == "Qwen";
        }

        private static bool IsEnabled(object entry) => _enabled.GetValue(entry) is bool enabled && enabled;

        private static bool Contains(string[] values, string value)
        {
            if (values == null || string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (string candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }

            _resolved = true;
            try
            {
                Type preferences = CloudTtsPlugin.FindType(PreferencesType);
                Type option = CloudTtsPlugin.FindType("Kolkata.Speech.CallLanguageOption");
                Type preference = CloudTtsPlugin.FindType("Kolkata.Speech.SpeechModelPreference");
                if (preferences != null && option != null)
                {
                    _get = preferences.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { option }, null);
                }

                if (preference != null)
                {
                    _model = preference.GetField("Model", BindingFlags.Public | BindingFlags.Instance);
                    _enabled = preference.GetField("Enabled", BindingFlags.Public | BindingFlags.Instance);
                }

                if (option != null)
                {
                    _englishName = option.GetField("EnglishName", BindingFlags.Public | BindingFlags.Instance);
                }

                if (_get == null || _model == null || _enabled == null || _englishName == null)
                {
                    PluginLog.Warning("the game's voice model order could not be read; it stays as the game sets it.");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning("the game's voice model order could not be read: " + ex.Message);
            }
        }
    }
}
