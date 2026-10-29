using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using static SwyfCloudTts.Plugin.CloudTtsText;
using static SwyfCloudTts.Plugin.CloudTtsTheme;

namespace SwyfCloudTts.Plugin
{
    /// The CLOUD TTS card in Settings > Speech, built from the game's own setting rows. Provider
    /// settings go to the worker's config file on Apply; character voices and the panel language
    /// are saved right away.
    internal sealed class CloudTtsSection : IDisposable
    {
        /// Dropdown entry that reveals a text field for a value that is not in the list.
        private const string Custom = "\u0001custom";

        private static readonly string[] GoogleModels =
        {
            "gemini-3.8-flash-tts", "gemini-2.5-flash-preview-tts", "gemini-2.5-pro-preview-tts",
        };

        private static readonly string[] ElevenLabsModels =
        {
            "eleven_v4_turbo", "eleven_flash_v2_5", "eleven_v4", "eleven_v3", "eleven_multilingual_v2",
        };

        private static readonly string[] OpenAiModels = { "gpt-4o-mini-tts", "tts-1", "tts-1-hd" };

        private static readonly string[] QwenLanguages =
        {
            "auto", "English", "Russian", "German", "French", "Spanish", "Italian",
            "Portuguese", "Chinese", "Japanese", "Korean",
        };

        private static readonly string[] QwenModes = { "clone", "voice", "design" };

        private readonly VisualElement _parent;
        private readonly VisualElement _gameRoot;
        private readonly VisualElement _host;
        private readonly VisualElement _card;
        private readonly CloudTtsConfig _doc;
        private readonly List<VisualElement> _inputs = new List<VisualElement>();
        private readonly Delegate _languageHandler;
        private readonly Delegate _qwenHandler;
        private readonly IVisualElementScheduledItem _gameWatch;

        private DropdownField _language;
        private VisualElement _callsRow;
        private DropdownField _calls;
        private Label _gameStatus;
        private VisualElement _gameQwenRow;

        private DropdownField _provider;
        private VisualElement _keyRow;
        private TextField _key;
        private Label _keyState;
        private Button _clearKey;

        private VisualElement _modelRow;
        private DropdownField _modelPreset;
        private TextField _model;

        private VisualElement _voiceRow;
        private DropdownField _voicePreset;
        private TextField _voice;
        private Button _loadVoices;
        private Label _voiceHelp;

        private VisualElement _googleParams;
        private TextField _googleStyle;
        private VisualElement _elevenParams;
        private DropdownField _autoClone;
        private Label _elevenAccount;
        private Slider _stability;
        private Label _stabilityValue;
        private Slider _similarity;
        private Label _similarityValue;
        private VisualElement _openAiParams;
        private TextField _openAiInstructions;
        private Slider _openAiSpeed;
        private Label _openAiSpeedValue;
        private VisualElement _qwenParams;
        private TextField _qwenBaseUrl;
        private DropdownField _qwenMode;
        private DropdownField _qwenLanguage;
        private TextField _qwenInstruct;

        private SliderInt _timeout;
        private Label _timeoutValue;
        private SliderInt _attempts;
        private Label _attemptsValue;
        private DropdownField _fallback;
        private DropdownField _cacheEnabled;
        private IntegerField _cacheMax;
        private DropdownField _clearOnVoiceChange;
        private Button _clearCache;

        private TextField _testText;
        private Button _apply;
        private Button _test;
        private Label _status;
        private Label _testStatus;
        private CloudTtsCharacters _characters;

        private CloudTtsElevenLabsState _eleven = CloudTtsElevenLabsState.Load();
        private bool _setting;
        private bool _busy;
        private bool _disposed;
        private int _runGeneration;
        private CancellationTokenSource _runCts;
        private IVisualElementScheduledItem _runPoll;

        public CloudTtsSection(VisualElement parent, VisualElement gameRoot, CloudTtsConfig doc)
        {
            _parent = parent;
            _gameRoot = gameRoot;
            _doc = doc;
            Use(doc.UiLanguage);
            UseFontFor(Current);

            _host = new VisualElement { name = "swyf-cloud-tts-section" };
            _host.userData = this;

            // The game translates texts it knows; ours are translated already.
            _host.AddToClassList("kolkata-unlocalized");
            _host.style.marginTop = 26;

            var header = new Label(T("title"));
            header.AddToClassList("section-label");
            header.AddToClassList("section-label--spaced");
            _host.Add(header);

            Label intro = Help(_host, T("intro"));
            intro.style.marginLeft = 0;

            if (doc.LoadError)
            {
                var error = new Label(T("configError", CloudTtsPaths.ConfigPath, doc.LoadErrorText));
                error.style.color = ErrorColor;
                error.style.whiteSpace = WhiteSpace.Normal;
                error.style.marginBottom = 10;
                _host.Add(error);
            }

            _card = new VisualElement();
            _card.AddToClassList("settings-card");
            _host.Add(_card);

            BuildLanguageRow();
            BuildCallsRow();
            BuildProviderRow();
            BuildKeyRow();
            BuildModelRow();
            BuildVoiceRow();
            BuildGoogleParams();
            BuildElevenLabsParams();
            BuildOpenAiParams();
            BuildQwenParams();
            BuildNetworkRows();
            BuildTestRows();

            try
            {
                _characters = new CloudTtsCharacters(_host);
            }
            catch (Exception ex)
            {
                PluginLog.Error("the character list could not be built: " + ex);
                Help(_host, T("chars.error", ex.Message));
            }

            RefreshFromDoc();
            ApplyText(_host);

            _languageHandler = CloudTtsGame.OnLanguageChanged(OnGameLanguageChanged);
            _qwenHandler = CloudTtsGame.OnQwenStateChanged(RefreshGame);
            _gameWatch = _host.schedule.Execute(RefreshGame);
            _gameWatch.Every(1000);
        }

        public void Attach()
        {
            if (_parent != null && _host.parent == null)
            {
                _parent.Add(_host);
            }
        }

        /// True while the card is still part of a live UI panel.
        public bool IsAttached => !_disposed && _host != null && _host.panel != null;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloudTtsGame.Unsubscribe(_languageHandler, _qwenHandler);
            _characters?.Dispose();
            try
            {
                _gameWatch?.Pause();
                _runPoll?.Pause();
                _runCts?.Cancel();
                _runCts?.Dispose();
            }
            catch
            {
                // The scheduler is gone together with the UI.
            }

            if (_gameQwenRow != null)
            {
                _gameQwenRow.style.display = CloudTtsGame.HardwareSupported ? DisplayStyle.Flex : DisplayStyle.None;
            }

            _host.RemoveFromHierarchy();
        }

        private bool Writable => !_doc.LoadError;

        private string SelectedProvider
            => CloudTtsConfig.NormalizeProvider(_provider != null ? _provider.value : _doc.Provider);

        // ---------------------------------------------------------------- building

        private void BuildLanguageRow()
        {
            _language = Dropdown(Row(_card, T("language")), new List<string>(Settings), SettingLabel, 220);
            _language.RegisterValueChangedCallback(evt =>
            {
                if (_setting || _disposed || !Writable)
                {
                    return;
                }

                try
                {
                    JObject root = _doc.CloneRoot();
                    CloudTtsConfig.SetString(root, "ui", "language", evt.newValue);
                    _doc.Save(root);
                    CloudTtsPlugin.RequestRebuild();
                }
                catch (Exception ex)
                {
                    SetStatus(T("save.failed", ex.Message), true);
                }
            });
        }

        /// The game decides which calls reach the worker at all ("Qwen" for non-English calls by
        /// default). This row edits that same setting, and the game's own row is hidden meanwhile.
        private void BuildCallsRow()
        {
            VisualElement control = Row(_card, T("calls"));
            _callsRow = control.parent;
            var choices = new List<string>
            {
                CloudTtsGame.PreferNonEnglish.ToString(CultureInfo.InvariantCulture),
                CloudTtsGame.AlwaysPrefer.ToString(CultureInfo.InvariantCulture),
                CloudTtsGame.Never.ToString(CultureInfo.InvariantCulture),
            };
            _calls = Dropdown(control, choices, CallsLabel, 320);
            _calls.RegisterValueChangedCallback(evt =>
            {
                if (!_setting && !_disposed)
                {
                    CloudTtsGame.QwenPreference = int.Parse(evt.newValue, CultureInfo.InvariantCulture);
                }
            });
            Label help = Help(_card, T("calls.help"));

            _gameStatus = Help(_card, "");

            if (CloudTtsGame.CanChangeQwenPreference && CloudTtsGame.QwenPreference >= 0)
            {
                DropdownField gameDropdown = _gameRoot != null ? _gameRoot.Q<DropdownField>("qwen-tts-dropdown") : null;
                _gameQwenRow = gameDropdown != null ? RowOf(gameDropdown) : null;
            }
            else
            {
                // Since 0.1.95 a per-language model list in the game's own settings replaces the preference.
                _callsRow.style.display = DisplayStyle.None;
                help.text = T("calls.order");
                Show(help, CloudTtsPlugin.FindType(CloudTtsModelOrder.PreferencesType) != null);
            }
        }

        private void BuildProviderRow()
        {
            _provider = Dropdown(Row(_card, T("provider")), new List<string>(CloudTtsConfig.Providers),
                CloudTtsConfig.ProviderLabel, 320);
            _provider.RegisterValueChangedCallback(OnProviderChanged);
            Help(_card, T("provider.help"));
        }

        private void BuildKeyRow()
        {
            VisualElement control = Row(_card, T("key"));
            _keyRow = control.parent;

            _key = Text(control, 320);
            _key.isPasswordField = true;
            _key.maxLength = 512;
            _key.RegisterValueChangedCallback(evt =>
            {
                if (!_setting && !_disposed)
                {
                    RefreshKeyState();
                }
            });

            _clearKey = AddButton(control, T("key.clear"), ClearKeyClicked, ButtonKind.Small);

            _keyState = new Label();
            _keyState.style.whiteSpace = WhiteSpace.Normal;

            // A line of its own under the field, rather than a narrow column beside the button.
            _keyState.style.width = Length.Percent(100);
            _keyState.style.marginTop = 4;
            _keyState.style.marginLeft = 4;
            control.Add(_keyState);

            Help(_card, T("key.help"));
        }

        private void BuildModelRow()
        {
            VisualElement control = Row(_card, T("model"));
            _modelRow = control.parent;
            _modelPreset = Dropdown(control, new List<string>(), ModelLabel, 320);
            _modelPreset.RegisterValueChangedCallback(evt =>
            {
                if (!_setting)
                {
                    ShowCustom(_model, evt.newValue);
                }
            });
            _model = Text(control, 260);
        }

        private void BuildVoiceRow()
        {
            VisualElement control = Row(_card, T("voice"));
            _voiceRow = control.parent;
            _voicePreset = Dropdown(control, new List<string>(), VoiceLabel, 320);
            _voicePreset.RegisterValueChangedCallback(evt =>
            {
                if (!_setting)
                {
                    ShowCustom(_voice, evt.newValue);
                }
            });
            _voice = Text(control, 260);
            _loadVoices = AddButton(control, T("voice.mine"), LoadVoicesClicked, ButtonKind.Normal);
            _voiceHelp = Help(_card, T("voice.help"));
        }

        private void BuildGoogleParams()
        {
            _googleParams = Group();
            _googleStyle = Text(Row(_googleParams, T("google.style")), 420);
            Help(_googleParams, T("google.style.help"));
        }

        private void BuildElevenLabsParams()
        {
            _elevenParams = Group();
            _autoClone = OnOff(Row(_elevenParams, T("eleven.clone")));
            Help(_elevenParams, T("eleven.clone.help"));
            _elevenAccount = Help(_elevenParams, "");
            _stability = SliderRow(_elevenParams, T("stability"), 0f, 1f, out _stabilityValue);
            _stability.RegisterValueChangedCallback(evt => _stabilityValue.text = Decimal(evt.newValue));
            _similarity = SliderRow(_elevenParams, T("similarity"), 0f, 1f, out _similarityValue);
            _similarity.RegisterValueChangedCallback(evt => _similarityValue.text = Decimal(evt.newValue));
        }

        private void BuildOpenAiParams()
        {
            _openAiParams = Group();
            _openAiInstructions = Text(Row(_openAiParams, T("openai.instructions")), 420);
            _openAiInstructions.multiline = true;
            _openAiInstructions.style.height = 58;
            Help(_openAiParams, T("openai.instructions.help"));
            _openAiSpeed = SliderRow(_openAiParams, T("speed"), 0.25f, 4f, out _openAiSpeedValue);
            _openAiSpeed.RegisterValueChangedCallback(evt => _openAiSpeedValue.text = Decimal(evt.newValue));
        }

        private void BuildQwenParams()
        {
            _qwenParams = Group();
            _qwenBaseUrl = Text(Row(_qwenParams, T("qwen.url")), 420);
            Help(_qwenParams, T("qwen.url.help"));

            _qwenMode = Dropdown(Row(_qwenParams, T("qwen.mode")), new List<string>(QwenModes),
                mode => string.IsNullOrEmpty(mode) ? "" : T("qwen.mode." + mode), 320);
            Help(_qwenParams, T("qwen.mode.help"));

            _qwenLanguage = Dropdown(Row(_qwenParams, T("qwen.language")), new List<string>(QwenLanguages),
                language => language == "auto" ? T("qwen.language.auto") : language, 240);

            _qwenInstruct = Text(Row(_qwenParams, T("qwen.instruct")), 420);
            _qwenInstruct.multiline = true;
            _qwenInstruct.style.height = 48;
            Help(_qwenParams, T("qwen.instruct.help"));
        }

        private void BuildNetworkRows()
        {
            SubHeader(T("network"));

            _timeout = IntSliderRow(_card, T("timeout"), 2, 30, out _timeoutValue);
            _timeout.RegisterValueChangedCallback(evt => _timeoutValue.text = T("seconds", evt.newValue));
            Help(_card, T("timeout.help"));

            _attempts = IntSliderRow(_card, T("attempts"), 1, 5, out _attemptsValue);
            _attempts.RegisterValueChangedCallback(evt => _attemptsValue.text = evt.newValue.ToString(CultureInfo.InvariantCulture));

            _fallback = OnOff(Row(_card, T("fallback")));
            Help(_card, T("fallback.help"));

            _cacheEnabled = OnOff(Row(_card, T("cache")));

            _cacheMax = new IntegerField();
            _cacheMax.style.width = 120;
            Row(_card, T("cache.max")).Add(_cacheMax);
            StyleTextField(_cacheMax);
            _inputs.Add(_cacheMax);

            VisualElement clearRow = Row(_card, T("cache.clearOnChange"));
            _clearOnVoiceChange = OnOff(clearRow);
            _clearCache = AddButton(clearRow, T("cache.clear"), ClearCacheClicked, ButtonKind.Normal);
        }

        private void BuildTestRows()
        {
            SubHeader(T("test"));

            _testText = Text(Row(_card, T("test.phrase")), 420);
            Help(_card, T("test.phrase.help"));

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.alignItems = Align.Center;
            actions.style.marginTop = 12;
            _card.Add(actions);

            _apply = AddButton(actions, T("apply"), ApplyClicked, ButtonKind.Primary);
            _test = AddButton(actions, T("test.button"), TestClicked, ButtonKind.Normal);

            _status = StatusLabel(_card);
            _testStatus = StatusLabel(_card);
        }

        // ---------------------------------------------------------------- state

        private void RefreshFromDoc()
        {
            _eleven = CloudTtsElevenLabsState.Load();
            _setting = true;
            try
            {
                string setting = CloudTtsConfig.NormalizeUiLanguage(_doc.UiLanguage);
                _language.SetValueWithoutNotify(setting);

                string provider = _doc.Provider;
                _provider.SetValueWithoutNotify(provider);
                RefreshProviderFields(provider);

                SetSlider(_timeout, _timeoutValue,
                    Mathf.Clamp(CloudTtsConfig.Int(_doc.Root, "network", "timeoutSeconds", 12), 2, 30));
                SetSlider(_attempts, _attemptsValue,
                    Mathf.Clamp(CloudTtsConfig.Int(_doc.Root, "network", "maxAttempts", 2), 1, 5));
                SetOnOff(_fallback, CloudTtsConfig.Bool(_doc.Root, "fallback", "passthrough", false));
                SetOnOff(_cacheEnabled, CloudTtsConfig.Bool(_doc.Root, "cache", "enabled", true));
                _cacheMax.SetValueWithoutNotify(Mathf.Clamp(CloudTtsConfig.Int(_doc.Root, "cache", "maxMegabytes", 512), 0, 8192));
                SetOnOff(_clearOnVoiceChange, CloudTtsConfig.Bool(_doc.Root, "cache", "clearOnVoiceChange", true));
                _key.SetValueWithoutNotify("");
            }
            finally
            {
                _setting = false;
            }

            RefreshVisibility();
            RefreshGame();
        }

        private void RefreshProviderFields(string provider)
        {
            provider = CloudTtsConfig.NormalizeProvider(provider);
            bool wasSetting = _setting;
            _setting = true;
            try
            {
                string model = CloudTtsConfig.String(_doc.Root, provider, CloudTtsConfig.ModelKey(provider), "");
                if (model.Trim().Length == 0)
                {
                    model = CloudTtsConfig.DefaultModel(provider);
                }

                SetPreset(_modelPreset, _model, ModelChoices(provider), model);

                string voice = CloudTtsConfig.String(_doc.Root, provider, CloudTtsConfig.VoiceKey(provider), "");
                if (provider == "elevenlabs" && voice == CloudTtsConfig.LegacyElevenLabsDefault)
                {
                    voice = "";
                }

                SetPreset(_voicePreset, _voice, VoiceChoices(provider), voice);

                _googleStyle.SetValueWithoutNotify(CloudTtsConfig.String(_doc.Root, "googleai", "style", "natural, conversational"));
                SetOnOff(_autoClone, CloudTtsConfig.Bool(_doc.Root, "elevenlabs", "autoClone", true));
                SetSlider(_stability, _stabilityValue, Mathf.Clamp01((float)CloudTtsConfig.Num(_doc.Root, "elevenlabs", "stability", 0.5)));
                SetSlider(_similarity, _similarityValue, Mathf.Clamp01((float)CloudTtsConfig.Num(_doc.Root, "elevenlabs", "similarityBoost", 0.8)));
                _openAiInstructions.SetValueWithoutNotify(CloudTtsConfig.String(_doc.Root, "openai", "instructions", ""));
                SetSlider(_openAiSpeed, _openAiSpeedValue,
                    Mathf.Clamp((float)CloudTtsConfig.Num(_doc.Root, "openai", "speed", 1.0), 0.25f, 4f));
                _qwenBaseUrl.SetValueWithoutNotify(CloudTtsConfig.String(_doc.Root, "qwenremote", "baseUrl", "http://127.0.0.1:8000"));
                _qwenMode.SetValueWithoutNotify(Pick(QwenModes, CloudTtsConfig.String(_doc.Root, "qwenremote", "mode", "clone")));
                _qwenLanguage.SetValueWithoutNotify(Pick(QwenLanguages, CloudTtsConfig.String(_doc.Root, "qwenremote", "language", "auto")));
                _qwenInstruct.SetValueWithoutNotify(CloudTtsConfig.String(_doc.Root, "qwenremote", "instruct", ""));
            }
            finally
            {
                _setting = wasSetting;
            }

            RefreshAccount();
            RefreshKeyState();
        }

        private void RefreshAccount()
        {
            _elevenAccount.text = _eleven.Describe();
            _elevenAccount.style.display = _elevenAccount.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private List<string> ModelChoices(string provider)
        {
            switch (provider)
            {
                case "googleai": return new List<string>(GoogleModels);
                case "elevenlabs": return new List<string>(ElevenLabsModels);
                case "openai": return new List<string>(OpenAiModels);
                default: return new List<string>();
            }
        }

        private List<string> VoiceChoices(string provider)
        {
            var choices = new List<string> { "" };
            switch (provider)
            {
                case "googleai":
                    choices.AddRange(CloudTtsConfig.GoogleVoices);
                    break;
                case "openai":
                    choices.AddRange(CloudTtsConfig.OpenAiVoices);
                    break;
                case "elevenlabs":
                    foreach (KeyValuePair<string, string> voice in _eleven.MyVoices)
                    {
                        choices.Add(voice.Key);
                    }

                    foreach (string[] voice in CloudTtsConfig.ElevenLabsVoices)
                    {
                        choices.Add(voice[0]);
                    }

                    break;
            }

            return choices;
        }

        /// A known value selects its entry; anything else selects "Other" and fills the text field.
        private void SetPreset(DropdownField preset, TextField custom, List<string> choices, string value)
        {
            value = (value ?? "").Trim();
            choices.Add(Custom);
            preset.choices = choices;
            bool known = choices.Contains(value);
            preset.SetValueWithoutNotify(known ? value : Custom);
            custom.SetValueWithoutNotify(known ? "" : value);
            ShowCustom(custom, preset.value);
        }

        private static void ShowCustom(TextField field, string presetValue)
            => field.style.display = presetValue == Custom ? DisplayStyle.Flex : DisplayStyle.None;

        private static string PresetValue(DropdownField preset, TextField custom)
            => (preset.value == Custom ? custom.value ?? "" : preset.value ?? "").Trim();

        private void OnProviderChanged(ChangeEvent<string> evt)
        {
            if (_setting || _disposed)
            {
                return;
            }

            _key.SetValueWithoutNotify("");
            RefreshProviderFields(evt.newValue);
            RefreshVisibility();
            SetStatus(T("provider.changed"), false);
        }

        private void RefreshVisibility()
        {
            string provider = SelectedProvider;
            bool cloud = CloudTtsConfig.IsCloud(provider);
            bool eleven = provider == "elevenlabs";

            Show(_keyRow, CloudTtsConfig.UsesKey(provider));
            Show(_modelRow, cloud && provider != "qwenremote");
            Show(_voiceRow, cloud);
            Show(_voicePreset, provider != "qwenremote");
            Show(_loadVoices, eleven);
            if (provider == "qwenremote")
            {
                // A server decides its own voice names, so there is nothing to pick from.
                _voice.style.display = DisplayStyle.Flex;
            }

            _voiceHelp.text = T("voice.help");
            Show(_googleParams, provider == "googleai");
            Show(_elevenParams, eleven);
            Show(_openAiParams, provider == "openai");
            Show(_qwenParams, provider == "qwenremote");

            foreach (VisualElement input in _inputs)
            {
                input.SetEnabled(Writable && !_busy);
            }

            if (provider == "passthrough")
            {
                SetOnOff(_fallback, true);
                _fallback.SetEnabled(false);
            }

            _clearCache.SetEnabled(!_busy);
            _apply.SetEnabled(Writable && !_busy);
            _loadVoices.SetEnabled(Writable && !_busy);
            _test.SetEnabled(Writable && !_busy && provider != "passthrough");
            _characters?.SetProvider(provider, OnOffValue(_autoClone));
            RefreshKeyState();
        }

        private void RefreshKeyState()
        {
            string provider = SelectedProvider;
            if (!CloudTtsConfig.UsesKey(provider))
            {
                SetKeyState(T("key.notUsed"), InfoColor, false);
                return;
            }

            string key = CloudTtsConfig.ConfigKey(_doc.Root, provider);
            bool env = CloudTtsConfig.HasEnvironmentKey(provider);
            if ((_key.value ?? "").Trim().Length > 0)
            {
                SetKeyState(T("key.new"), OkColor, Writable && !_busy);
            }
            else if (key.Length > 0)
            {
                SetKeyState(T("key.saved", CloudTtsConfig.ConfigKeyTail(key)) + (env ? T("key.savedEnv") : ""),
                    OkColor, Writable && !_busy);
            }
            else if (env)
            {
                SetKeyState(T("key.env"), OkColor, false);
            }
            else if (!CloudTtsConfig.NeedsKey(provider))
            {
                SetKeyState(T("key.optional"), InfoColor, false);
            }
            else
            {
                SetKeyState(T("key.none"), WarningColor, false);
            }
        }

        private void SetKeyState(string text, Color color, bool canClear)
        {
            _keyState.text = text;
            _keyState.style.color = color;
            _clearKey.SetEnabled(canClear);
        }

        /// The game's own state: whether it will call the worker at all.
        private void RefreshGame()
        {
            if (_disposed)
            {
                return;
            }

            bool supported = CloudTtsGame.HardwareSupported;
            if (_callsRow != null && CloudTtsGame.CanChangeQwenPreference && CloudTtsGame.QwenPreference >= 0)
            {
                Show(_callsRow, supported);
                bool was = _setting;
                _setting = true;
                _calls.SetValueWithoutNotify(CloudTtsGame.QwenPreference.ToString(CultureInfo.InvariantCulture));
                _setting = was;
            }

            // The game shows its row again whenever the preference changes, so it is hidden every time.
            if (_gameQwenRow != null)
            {
                _gameQwenRow.style.display = DisplayStyle.None;
            }

            string message = "";
            if (!supported)
            {
                message = T("game.noHardware");
            }
            else if (CloudTtsGame.QwenPreference != CloudTtsGame.Never)
            {
                string voice = CloudTtsGame.VoiceState;
                string download = CloudTtsGame.DownloadState;
                if (voice == "Failed")
                {
                    message = T("game.failed");
                }
                else if (download == "Downloading" || download == "Checking")
                {
                    message = T("game.downloading");
                }
                else if (download == "Failed" || download == "Unavailable")
                {
                    message = T("game.downloadFailed");
                }
            }

            if (_gameStatus.text != message)
            {
                _gameStatus.text = message;
                _gameStatus.style.color = WarningColor;
                Show(_gameStatus, message.Length > 0);
            }
        }

        private void OnGameLanguageChanged(string code)
        {
            if (!_disposed && Resolve(_doc.UiLanguage) != Current)
            {
                CloudTtsPlugin.RequestRebuild();
            }
        }

        // ---------------------------------------------------------------- actions

        private void ApplyClicked()
        {
            if (!Writable || _busy || _disposed)
            {
                return;
            }

            try
            {
                JObject root = _doc.CloneRoot();
                string error = CaptureForm(root);
                if (error != null)
                {
                    SetStatus(error, true);
                    return;
                }

                string before = CloudTtsConfig.VoiceFingerprint(_doc.Root);
                string after = CloudTtsConfig.VoiceFingerprint(root);
                _doc.Save(root);

                int removed = 0;
                if (CloudTtsConfig.Bool(root, "cache", "clearOnVoiceChange", true) && before != after)
                {
                    removed = CloudTtsPaths.ClearCache();
                }

                bool reloaded = CloudTtsPlugin.ReloadWorker();
                RefreshFromDoc();
                ApplyText(_card);

                string message = T("saved");
                if (removed > 0)
                {
                    message += T("saved.cache", removed);
                }

                SetStatus(message + T(reloaded ? "saved.reloaded" : "saved.next"), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("save.failed", ex.Message), true);
            }
        }

        private string CaptureForm(JObject root)
        {
            string provider = SelectedProvider;
            CloudTtsConfig.SetString(root, null, "provider", provider);
            CloudTtsConfig.Set(root, "fallback", "passthrough", new JValue(provider == "passthrough" || OnOffValue(_fallback)));

            if (CloudTtsConfig.UsesKey(provider))
            {
                string key = (_key.value ?? "").Trim();
                if (key.Length > 0)
                {
                    CloudTtsConfig.SetString(root, provider, "apiKey", key);
                }
            }

            if (CloudTtsConfig.IsCloud(provider))
            {
                string model = provider == "qwenremote"
                    ? CloudTtsConfig.String(root, "qwenremote", "model", "tts-1")
                    : PresetValue(_modelPreset, _model);
                if (model.Length == 0)
                {
                    return T("model.empty");
                }

                CloudTtsConfig.SetString(root, provider, CloudTtsConfig.ModelKey(provider), model);

                string voice = provider == "qwenremote" ? (_voice.value ?? "").Trim() : PresetValue(_voicePreset, _voice);
                if (provider == "openai")
                {
                    CloudTtsConfig.Set(root, provider, "voice", voice.Length == 0 ? JValue.CreateNull() : new JValue(voice));
                }
                else
                {
                    CloudTtsConfig.SetString(root, provider, CloudTtsConfig.VoiceKey(provider), voice);
                }
            }

            switch (provider)
            {
                case "googleai":
                    CloudTtsConfig.SetString(root, "googleai", "style", (_googleStyle.value ?? "").Trim());
                    break;
                case "elevenlabs":
                    CloudTtsConfig.Set(root, "elevenlabs", "autoClone", new JValue(OnOffValue(_autoClone)));
                    CloudTtsConfig.Set(root, "elevenlabs", "stability", new JValue(Math.Round(Mathf.Clamp01(_stability.value), 2)));
                    CloudTtsConfig.Set(root, "elevenlabs", "similarityBoost", new JValue(Math.Round(Mathf.Clamp01(_similarity.value), 2)));
                    break;
                case "openai":
                    string instructions = (_openAiInstructions.value ?? "").Trim();
                    CloudTtsConfig.Set(root, "openai", "instructions",
                        instructions.Length == 0 ? JValue.CreateNull() : new JValue(instructions));
                    CloudTtsConfig.Set(root, "openai", "speed", new JValue(Math.Round(Mathf.Clamp(_openAiSpeed.value, 0.25f, 4f), 2)));
                    break;
                case "qwenremote":
                    string baseUrl = (_qwenBaseUrl.value ?? "").Trim().TrimEnd('/');
                    if (baseUrl.Length == 0)
                    {
                        return T("qwen.urlEmpty");
                    }

                    if (!baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return T("qwen.urlScheme");
                    }

                    if (_qwenMode.value == "voice" && (_voice.value ?? "").Trim().Length == 0)
                    {
                        return T("qwen.voiceNeeded");
                    }

                    string instruct = (_qwenInstruct.value ?? "").Trim();
                    CloudTtsConfig.SetString(root, "qwenremote", "baseUrl", baseUrl);
                    CloudTtsConfig.SetString(root, "qwenremote", "mode", _qwenMode.value ?? "clone");
                    CloudTtsConfig.SetString(root, "qwenremote", "language", _qwenLanguage.value ?? "auto");
                    CloudTtsConfig.Set(root, "qwenremote", "instruct", instruct.Length == 0 ? JValue.CreateNull() : new JValue(instruct));
                    break;
            }

            CloudTtsConfig.Set(root, "network", "timeoutSeconds", new JValue(Mathf.Clamp(_timeout.value, 2, 30)));
            CloudTtsConfig.Set(root, "network", "maxAttempts", new JValue(Mathf.Clamp(_attempts.value, 1, 5)));
            CloudTtsConfig.Set(root, "cache", "enabled", new JValue(OnOffValue(_cacheEnabled)));
            CloudTtsConfig.Set(root, "cache", "maxMegabytes", new JValue(Mathf.Clamp(_cacheMax.value, 0, 8192)));
            CloudTtsConfig.Set(root, "cache", "clearOnVoiceChange", new JValue(OnOffValue(_clearOnVoiceChange)));
            return null;
        }

        private void ClearKeyClicked()
        {
            if (!Writable || _busy || _disposed)
            {
                return;
            }

            string provider = SelectedProvider;
            if (!CloudTtsConfig.UsesKey(provider))
            {
                SetStatus(T("key.notService"), false);
                return;
            }

            try
            {
                JObject root = _doc.CloneRoot();
                CloudTtsConfig.SetString(root, provider, "apiKey", "");
                _doc.Save(root);
                _key.SetValueWithoutNotify("");
                RefreshKeyState();
                CloudTtsPlugin.ReloadWorker();
                SetStatus(T("key.cleared", CloudTtsConfig.ProviderLabel(provider)) +
                          (CloudTtsConfig.HasEnvironmentKey(provider) ? T("key.clearedEnv") : ""), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("key.clearFailed", ex.Message), true);
            }
        }

        /// Form values for a one-shot worker run, or null after showing why there is none.
        private string RunConfig(string provider, Label status)
        {
            JObject root = _doc.CloneRoot();
            string error = CaptureForm(root);
            if (error != null)
            {
                SetLabel(status, error, true);
                return null;
            }

            if (CloudTtsConfig.NeedsKey(provider) && CloudTtsConfig.ConfigKey(root, provider).Length == 0 &&
                !CloudTtsConfig.HasEnvironmentKey(provider))
            {
                SetLabel(status, T("test.noKey"), true);
                return null;
            }

            // A preview has no game clip, so it must never clone.
            CloudTtsConfig.Set(root, "elevenlabs", "autoClone", new JValue(false));
            return root.ToString(Formatting.Indented);
        }

        private void TestClicked()
        {
            if (!Writable || _busy || _disposed)
            {
                return;
            }

            string provider = SelectedProvider;
            if (provider == "passthrough")
            {
                SetLabel(_testStatus, T("test.passthrough"), true);
                return;
            }

            string config = RunConfig(provider, _testStatus);
            if (config == null)
            {
                return;
            }

            string language = CloudTtsGame.CallLanguageName;
            PluginLog.Info("test started: provider=" + provider + ", language=" + language);
            SetLabel(_testStatus, T("test.running", CloudTtsConfig.ProviderLabel(provider)), false);
            SetStatus("", false);
            StartRun(token => CloudTtsTester.RunAsync(config, _testText.value ?? "", language,
                CloudTtsPaths.TestWorkerPath, token), OnTestFinished);
        }

        private void OnTestFinished(CloudTtsTestResult result)
        {
            if (!result.Ok)
            {
                PluginLog.Warning("test failed: " + result.Message);
                SetLabel(_testStatus, T("test.failed", result.Message), true);
                return;
            }

            string playbackError = CloudTtsAudio.Play(result.Wav);
            if (playbackError != null)
            {
                PluginLog.Warning("test audio could not be played: " + playbackError);
                SetLabel(_testStatus, T("test.playFailed", playbackError), true);
                return;
            }

            PluginLog.Info("test ok: " + result.Wav.Length + " bytes");
            SetLabel(_testStatus, T("test.ok"), false);
        }

        private void LoadVoicesClicked()
        {
            if (!Writable || _busy || _disposed)
            {
                return;
            }

            string config = RunConfig("elevenlabs", _status);
            if (config == null)
            {
                return;
            }

            SetStatus(T("voice.mineLoading"), false);
            StartRun(token => CloudTtsTester.ListVoicesAsync(config, CloudTtsPaths.TestWorkerPath, token), result =>
            {
                if (!result.Ok)
                {
                    SetStatus(T("voice.mineFailed", result.Message), true);
                    return;
                }

                string voice = PresetValue(_voicePreset, _voice);
                _eleven = CloudTtsElevenLabsState.Load();
                _setting = true;
                SetPreset(_voicePreset, _voice, VoiceChoices("elevenlabs"), voice);
                _setting = false;
                RefreshAccount();
                _characters?.Reload();
                SetStatus(_eleven.MyVoices.Count > 0 ? T("voice.mineLoaded", _eleven.MyVoices.Count) : T("voice.mineNone"),
                    false);
            });
        }

        /// The game's stripped mscorlib has no Task&lt;T&gt;.ContinueWith(Action&lt;Task&lt;T&gt;&gt;),
        /// so the UI scheduler polls the task instead.
        private void StartRun(Func<CancellationToken, Task<CloudTtsTestResult>> start, Action<CloudTtsTestResult> done)
        {
            try
            {
                _runCts?.Cancel();
                _runCts?.Dispose();
                _runCts = new CancellationTokenSource();
                Task<CloudTtsTestResult> task = start(_runCts.Token);
                int generation = ++_runGeneration;

                _busy = true;
                RefreshVisibility();
                _runPoll?.Pause();
                _runPoll = _host.schedule.Execute(() =>
                {
                    if (!task.IsCompleted)
                    {
                        return;
                    }

                    _runPoll.Pause();
                    if (_disposed || generation != _runGeneration)
                    {
                        return;
                    }

                    _busy = false;
                    RefreshVisibility();
                    done(task.IsFaulted || task.IsCanceled
                        ? new CloudTtsTestResult { Message = task.Exception?.GetBaseException().Message ?? T("test.cancelled") }
                        : task.Result ?? new CloudTtsTestResult { Message = "no result" });
                });
                _runPoll.Every(100);
            }
            catch (Exception ex)
            {
                _busy = false;
                RefreshVisibility();
                SetLabel(_testStatus, T("test.startFailed", ex.Message), true);
            }
        }

        private void ClearCacheClicked()
        {
            if (!_disposed)
            {
                SetStatus(T("cache.cleared", CloudTtsPaths.ClearCache()), false);
            }
        }

        // ---------------------------------------------------------------- labels

        private string VoiceLabel(string voice)
        {
            if (voice == Custom)
            {
                return T("custom");
            }

            if (string.IsNullOrEmpty(voice))
            {
                return T("voice.auto");
            }

            if (SelectedProvider != "elevenlabs")
            {
                return voice;
            }

            string mine = _eleven.MyVoiceName(voice);
            return mine != null ? T("voice.mineLabel", mine) : CloudTtsConfig.ElevenLabsDefaultLabel(voice) ?? T("voice.id", voice);
        }

        private static string ModelLabel(string model)
        {
            switch (model)
            {
                case Custom: return T("custom");
                case "eleven_v4_turbo": return model + " · " + T("model.fast");
                case "eleven_flash_v2_5": return model + " · " + T("model.fastest");
                case "eleven_v4": return model + " · " + T("model.best");
                case "eleven_v3": return model + " · " + T("model.expressive");
                case "eleven_multilingual_v2": return model + " · " + T("model.older");
                default: return model ?? "";
            }
        }

        private static string CallsLabel(string value)
        {
            switch (value)
            {
                case "1": return T("calls.all");
                case "2": return T("calls.never");
                default: return T("calls.nonEnglish");
            }
        }

        private void SetStatus(string text, bool error) => SetLabel(_status, text, error);

        private static void SetLabel(Label label, string text, bool error)
        {
            if (label == null)
            {
                return;
            }

            label.text = text ?? "";
            label.style.color = error ? ErrorColor : InfoColor;
            label.style.display = label.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---------------------------------------------------------------- controls

        private VisualElement Group()
        {
            var group = new VisualElement();
            _card.Add(group);
            return group;
        }

        /// A row in the game's layout: label on the left, controls on the right.
        private static VisualElement Row(VisualElement parent, string labelText)
        {
            var row = new VisualElement();
            row.AddToClassList("setting-row");

            var label = new Label(labelText);
            label.AddToClassList("setting-label");
            label.style.minWidth = 200;
            label.style.flexShrink = 0;

            // The game cuts long labels off; ours wrap onto a second line instead.
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.paddingRight = 10;
            row.Add(label);

            var control = new VisualElement();
            control.AddToClassList("setting-control-row");
            control.style.flexDirection = FlexDirection.Row;
            control.style.flexWrap = Wrap.Wrap;
            control.style.alignItems = Align.Center;
            control.style.flexGrow = 1;
            row.Add(control);

            parent.Add(row);
            return control;
        }

        private static Label Help(VisualElement parent, string text)
        {
            var help = new Label(text);
            StyleHelp(help);
            help.style.whiteSpace = WhiteSpace.Normal;
            help.style.marginLeft = 210;
            help.style.marginBottom = 8;
            parent.Add(help);
            return help;
        }

        private static Label StatusLabel(VisualElement parent)
        {
            var label = new Label();
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 6;
            label.style.display = DisplayStyle.None;
            parent.Add(label);
            return label;
        }

        private void SubHeader(string text)
        {
            var label = new Label(text);
            label.AddToClassList("section-label");
            label.AddToClassList("section-label--spaced");
            label.style.marginTop = 18;
            _card.Add(label);
        }

        private DropdownField Dropdown(VisualElement parent, List<string> choices, Func<string, string> format, float width)
        {
            var field = new DropdownField();
            field.choices = choices;

            // Without our own callbacks the game would run every value through its menu dictionary.
            field.formatListItemCallback = value => format(value);
            field.formatSelectedValueCallback = value => format(value);
            field.style.width = width;
            field.style.marginRight = 10;
            parent.Add(field);
            StyleDropdown(field);
            _inputs.Add(field);
            return field;
        }

        private DropdownField OnOff(VisualElement parent)
            => Dropdown(parent, new List<string> { "on", "off" }, value => T(value == "on" ? "on" : "off"), 220);

        private static void SetOnOff(DropdownField field, bool on) => field.SetValueWithoutNotify(on ? "on" : "off");

        private static bool OnOffValue(DropdownField field) => field.value == "on";

        private TextField Text(VisualElement parent, float width)
        {
            var field = new TextField();
            field.style.width = width;
            field.style.marginRight = 10;
            parent.Add(field);
            StyleTextField(field);
            _inputs.Add(field);
            return field;
        }

        private Button AddButton(VisualElement parent, string text, Action clicked, ButtonKind kind)
        {
            var button = new Button(clicked) { text = text };
            button.style.marginRight = 10;
            parent.Add(button);
            StyleButton(button, kind);
            return button;
        }

        private Slider SliderRow(VisualElement parent, string labelText, float min, float max, out Label value)
        {
            VisualElement control = Row(parent, labelText);
            var slider = new Slider(min, max);
            slider.style.width = 260;
            slider.style.marginRight = 10;
            control.Add(slider);
            StyleSlider(slider);
            _inputs.Add(slider);
            value = ValueLabel(control);
            return slider;
        }

        private SliderInt IntSliderRow(VisualElement parent, string labelText, int min, int max, out Label value)
        {
            VisualElement control = Row(parent, labelText);
            var slider = new SliderInt(min, max);
            slider.style.width = 260;
            slider.style.marginRight = 10;
            control.Add(slider);
            StyleSlider(slider);
            _inputs.Add(slider);
            value = ValueLabel(control);
            return slider;
        }

        private static Label ValueLabel(VisualElement control)
        {
            var label = new Label();
            label.style.minWidth = 60;
            control.Add(label);
            StyleValue(label);
            return label;
        }

        /// Change events need a panel, so values set while the card is detached update their labels here.
        private static void SetSlider(Slider slider, Label label, float value)
        {
            slider.SetValueWithoutNotify(value);
            label.text = Decimal(value);
        }

        private void SetSlider(SliderInt slider, Label label, int value)
        {
            slider.SetValueWithoutNotify(value);
            label.text = slider == _timeout ? T("seconds", value) : value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Decimal(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static string Pick(string[] values, string value)
        {
            foreach (string known in values)
            {
                if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return values[0];
        }

        private static void Show(VisualElement element, bool visible)
        {
            if (element != null)
            {
                element.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private static VisualElement RowOf(VisualElement element)
        {
            for (VisualElement current = element.parent; current != null; current = current.parent)
            {
                if (current.ClassListContains("setting-row"))
                {
                    return current;
                }
            }

            return element.parent;
        }
    }
}
