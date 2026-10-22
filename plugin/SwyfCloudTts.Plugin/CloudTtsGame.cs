using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace SwyfCloudTts.Plugin
{
    /// Reads and changes game state through reflection, so a renamed member in a game update
    /// only switches the matching feature off instead of keeping the plugin from loading.
    internal static class CloudTtsGame
    {
        /// Order of Kolkata.Speech.QwenTtsPreference.
        public const int PreferNonEnglish = 0;
        public const int AlwaysPrefer = 1;
        public const int Never = 2;

        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;

        private static bool _resolved;
        private static PropertyInfo _effectiveCode;
        private static MethodInfo _uiFontForCode;
        private static EventInfo _languageChanged;
        private static MethodInfo _formatSettingsText;
        private static PropertyInfo _qwenPreference;
        private static EventInfo _selectionChanged;
        private static PropertyInfo _downloadState;
        private static PropertyInfo _voiceState;
        private static PropertyInfo _hardwareSupported;
        private static EventInfo _hardwareChanged;
        private static PropertyInfo _currentCallLanguage;
        private static FieldInfo _englishName;

        /// Two-letter code of the game's interface language, "en" when unknown.
        public static string LanguageCode
        {
            get
            {
                Resolve();
                return Get(_effectiveCode) as string ?? "en";
            }
        }

        /// English name of the call language ("Russian"), which is what the worker is sent.
        public static string CallLanguageName
        {
            get
            {
                Resolve();
                object current = Get(_currentCallLanguage);
                string name = current != null && _englishName != null ? _englishName.GetValue(current) as string : null;
                return string.IsNullOrEmpty(name) ? "English" : name;
            }
        }

        /// The font the game uses for a language, or null where it keeps its default font.
        public static Font UiFont(string code)
        {
            Resolve();
            try
            {
                return _uiFontForCode != null ? _uiFontForCode.Invoke(null, new object[] { code }) as Font : null;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("GetUiFontForCode failed: " + ex.Message);
                return null;
            }
        }

        /// Applies the game's own casing rules for setting labels, section labels and buttons.
        public static string FormatSettingsText(TextElement element, string text)
        {
            Resolve();
            if (_formatSettingsText == null || element == null || string.IsNullOrEmpty(text))
            {
                return text;
            }

            try
            {
                return _formatSettingsText.Invoke(null, new object[] { element, text }) as string ?? text;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("FormatSettingsText failed: " + ex.Message);
                return text;
            }
        }

        /// The game's "use Qwen for" preference, or -1 when it cannot be read.
        public static int QwenPreference
        {
            get
            {
                Resolve();
                object value = Get(_qwenPreference);
                return value != null ? Convert.ToInt32(value) : -1;
            }
            set
            {
                Resolve();
                if (_qwenPreference == null || !_qwenPreference.CanWrite)
                {
                    return;
                }

                try
                {
                    _qwenPreference.SetValue(null, Enum.ToObject(_qwenPreference.PropertyType, value), null);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning("could not change the game's Qwen preference: " + ex.Message);
                }
            }
        }

        public static bool CanChangeQwenPreference
        {
            get
            {
                Resolve();
                return _qwenPreference != null && _qwenPreference.CanWrite;
            }
        }

        /// False where the game never starts the worker (Windows on ARM, or a hardware check the mod could not redirect). True when unknown.
        public static bool HardwareSupported
        {
            get
            {
                Resolve();
                object value = Get(_hardwareSupported);
                return !(value is bool supported) || supported;
            }
        }

        /// Kolkata.Speech.QwenDownloadState name: Inactive, Checking, Downloading, Ready, Failed, Unavailable.
        public static string DownloadState
        {
            get
            {
                Resolve();
                object value = Get(_downloadState);
                return value != null ? value.ToString() : "";
            }
        }

        /// Kolkata.Speech.QwenVoiceState name: Unloaded, Loading, Ready, Failed.
        public static string VoiceState
        {
            get
            {
                Resolve();
                object value = Get(_voiceState);
                return value != null ? value.ToString() : "";
            }
        }

        public static Delegate OnLanguageChanged(Action<string> handler) => Subscribe(_languageChanged, handler);

        public static Delegate OnQwenStateChanged(Action handler)
        {
            Delegate first = Subscribe(_selectionChanged, handler);
            Subscribe(_hardwareChanged, handler);
            return first ?? handler;
        }

        public static void Unsubscribe(Delegate languageHandler, Delegate qwenHandler)
        {
            Remove(_languageChanged, languageHandler);
            Remove(_selectionChanged, qwenHandler);
            Remove(_hardwareChanged, qwenHandler);
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
                Type language = CloudTtsPlugin.FindType("LanguageManager");
                if (language != null)
                {
                    _effectiveCode = language.GetProperty("EffectiveCode", Static);
                    _uiFontForCode = language.GetMethod("GetUiFontForCode", Static, null, new[] { typeof(string) }, null);
                    _languageChanged = language.GetEvent("OnLanguageChanged", Static);
                }

                Type localizer = CloudTtsPlugin.FindType("Kolkata.UI.KolkataUiTextLocalizer");
                if (localizer != null)
                {
                    _formatSettingsText = localizer.GetMethod(
                        "FormatSettingsText", Static, null, new[] { typeof(TextElement), typeof(string) }, null);
                }

                Type selection = CloudTtsPlugin.FindType("Kolkata.Speech.SpeechProviderSelection");
                if (selection != null)
                {
                    _qwenPreference = selection.GetProperty("Qwen", Static);
                    _selectionChanged = selection.GetEvent("Changed", Static);
                }

                Type progress = CloudTtsPlugin.FindType("Kolkata.Speech.QwenTtsProgress");
                if (progress != null)
                {
                    _downloadState = progress.GetProperty("State", Static);
                    _voiceState = progress.GetProperty("VoiceState", Static);
                    _hardwareSupported = progress.GetProperty("HardwareSupported", Static);
                    _hardwareChanged = progress.GetEvent("HardwareSupportChanged", Static);
                }

                Type callLanguage = CloudTtsPlugin.FindType("Kolkata.Speech.CallLanguage");
                Type option = CloudTtsPlugin.FindType("Kolkata.Speech.CallLanguageOption");
                if (callLanguage != null && option != null)
                {
                    _currentCallLanguage = callLanguage.GetProperty("Current", Static);
                    _englishName = option.GetField("EnglishName", BindingFlags.Public | BindingFlags.Instance);
                }

                PluginLog.Info("game hooks: language=" + (_effectiveCode != null) +
                               ", font=" + (_uiFontForCode != null) +
                               ", casing=" + (_formatSettingsText != null) +
                               ", qwen preference=" + (_qwenPreference != null) +
                               ", qwen state=" + (_downloadState != null));
            }
            catch (Exception ex)
            {
                PluginLog.Warning("game hooks unavailable: " + ex.Message);
            }
        }

        private static object Get(PropertyInfo property)
        {
            try
            {
                return property != null ? property.GetValue(null, null) : null;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("reading " + property.Name + " failed: " + ex.Message);
                return null;
            }
        }

        private static Delegate Subscribe(EventInfo info, Delegate handler)
        {
            Resolve();
            if (info == null || handler == null || info.EventHandlerType != handler.GetType())
            {
                return null;
            }

            try
            {
                MethodInfo add = info.GetAddMethod(true);
                if (add == null)
                {
                    return null;
                }

                add.Invoke(null, new object[] { handler });
                return handler;
            }
            catch (Exception ex)
            {
                PluginLog.Debug("subscribing to " + info.Name + " failed: " + ex.Message);
                return null;
            }
        }

        private static void Remove(EventInfo info, Delegate handler)
        {
            if (info == null || handler == null || info.EventHandlerType != handler.GetType())
            {
                return;
            }

            try
            {
                MethodInfo remove = info.GetRemoveMethod(true);
                if (remove != null)
                {
                    remove.Invoke(null, new object[] { handler });
                }
            }
            catch
            {
                // The game is shutting down.
            }
        }
    }
}
