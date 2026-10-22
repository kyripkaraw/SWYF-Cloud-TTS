using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;
using static SwyfCloudTts.Plugin.CloudTtsText;
using static SwyfCloudTts.Plugin.CloudTtsTheme;

namespace SwyfCloudTts.Plugin
{
    /// <summary>
    /// CHARACTERS block of the card: one row per speaker from voices.json with the game's own
    /// clip, an editable name and the voice for the selected service. Edits are saved at once.
    /// </summary>
    internal sealed class CloudTtsCharacters : IDisposable
    {
        private const long WatchIntervalMs = 2000;
        private const long SaveDelayMs = 700;

        /// <summary>A rebuild would drop the field being typed in, so wait until edits settle.</summary>
        private const float EditQuietSeconds = 5f;

        private readonly VisualElement _root;
        private readonly Label _header;
        private readonly Label _help;
        private readonly Label _account;
        private readonly VisualElement _actions;
        private readonly Button _toggle;
        private readonly Button _resetAll;
        private readonly Label _status;
        private readonly VisualElement _list;
        private readonly IVisualElementScheduledItem _watch;

        private string _provider = "googleai";
        private bool _autoClone = true;
        private string _bookText = "";
        private List<CloudTtsSpeaker> _speakers = new List<CloudTtsSpeaker>();
        private CloudTtsElevenLabsState _eleven = new CloudTtsElevenLabsState();
        private bool _expanded;
        private bool _confirmReset;
        private float _lastEdit = -100f;
        private bool _disposed;

        public CloudTtsCharacters(VisualElement parent)
        {
            _root = new VisualElement();
            _root.style.marginTop = 18;
            parent.Add(_root);

            _header = new Label(T("chars"));
            _header.AddToClassList("section-label");
            _header.AddToClassList("section-label--spaced");
            _root.Add(_header);

            _help = Muted();
            _account = Muted();
            _account.style.color = InfoColor;

            _actions = new VisualElement();
            _actions.style.flexDirection = FlexDirection.Row;
            _actions.style.flexWrap = Wrap.Wrap;
            _actions.style.marginBottom = 4;
            _root.Add(_actions);

            _toggle = AddButton(_actions, T("chars.show"), ToggleClicked);
            AddButton(_actions, T("chars.refresh"), Reload);
            _resetAll = AddButton(_actions, T("chars.resetAll"), ResetAllClicked);

            _status = Muted();

            _list = new VisualElement();
            _list.style.display = DisplayStyle.None;
            _root.Add(_list);

            _watch = _root.schedule.Execute(Watch);
            _watch.Every(WatchIntervalMs);
            Reload();
        }

        public void SetProvider(string provider, bool autoClone)
        {
            provider = CloudTtsConfig.NormalizeProvider(provider);
            if (provider == _provider && autoClone == _autoClone)
            {
                return;
            }

            _provider = provider;
            _autoClone = autoClone;
            Rebuild();
        }

        public void Reload()
        {
            _bookText = CloudTtsVoices.ReadText();
            _speakers = CloudTtsVoices.Parse(_bookText);
            _eleven = CloudTtsElevenLabsState.Load();
            Rebuild();
        }

        public void Dispose()
        {
            _disposed = true;
            try
            {
                _watch.Pause();
            }
            catch
            {
                // The scheduler is gone together with the UI.
            }
        }

        private bool UsesCharacterVoices => CloudTtsConfig.IsCloud(_provider);

        private bool HasPresetVoices => _provider == "googleai" || _provider == "openai" || _provider == "elevenlabs";

        private void Watch()
        {
            // A closed pause menu keeps the card attached but lays it out at zero size.
            if (_disposed || _root.panel == null || !(_root.worldBound.width > 0) ||
                Time.unscaledTime - _lastEdit < EditQuietSeconds)
            {
                return;
            }

            if (CloudTtsVoices.ReadText() != _bookText)
            {
                Reload();
            }
        }

        private void Rebuild()
        {
            _list.Clear();
            _header.text = _speakers.Count > 0 ? T("chars.count", _speakers.Count) : T("chars");
            _help.text = HelpText();
            _account.text = _provider == "elevenlabs" ? _eleven.Describe() : "";
            _account.style.display = _account.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;

            bool show = UsesCharacterVoices && _speakers.Count > 0;
            _actions.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            _list.style.display = show && _expanded ? DisplayStyle.Flex : DisplayStyle.None;
            _toggle.text = _expanded ? T("chars.hide") : T("chars.show");

            if (show && _expanded)
            {
                foreach (CloudTtsSpeaker speaker in _speakers)
                {
                    _list.Add(BuildRow(speaker));
                }
            }

            ApplyText(_root);
        }

        private string HelpText()
        {
            if (!UsesCharacterVoices)
            {
                return T("chars.notUsed");
            }

            if (_speakers.Count == 0)
            {
                return T("chars.empty");
            }

            switch (_provider)
            {
                case "elevenlabs": return T("chars.help.eleven");
                case "qwenremote": return T("chars.help.qwen");
                default: return T("chars.help");
            }
        }

        private VisualElement BuildRow(CloudTtsSpeaker speaker)
        {
            var row = new VisualElement();
            row.AddToClassList("setting-row");
            row.AddToClassList("setting-row--compact");
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.alignItems = Align.Center;

            AddButton(row, T("chars.play"), () => PlayReference(speaker), ButtonKind.Small);

            var name = new TextField();
            name.maxLength = 40;
            name.SetValueWithoutNotify(speaker.Label);
            name.style.width = 170;
            name.style.marginRight = 8;
            row.Add(name);
            StyleTextField(name);
            Debounce(name, () => SaveLabel(speaker, name.value));

            var meta = new Label(Describe(speaker));
            StyleHelp(meta);
            meta.style.minWidth = 150;
            meta.style.marginRight = 8;
            row.Add(meta);

            if (HasPresetVoices)
            {
                row.Add(BuildVoiceDropdown(speaker));
            }
            else
            {
                var voice = new TextField();
                voice.maxLength = 120;
                voice.SetValueWithoutNotify(speaker.Voice(_provider));
                voice.style.width = 220;
                voice.style.marginRight = 8;
                row.Add(voice);
                StyleTextField(voice);
                Debounce(voice, () => SaveVoice(speaker, voice.value));
            }

            if (_provider == "elevenlabs")
            {
                row.Add(BuildKeepDropdown(speaker));
            }

            return row;
        }

        private DropdownField BuildVoiceDropdown(CloudTtsSpeaker speaker)
        {
            string current = speaker.Voice(_provider);
            var choices = new List<string> { "" };
            if (current.Length > 0)
            {
                choices.Add(current);
            }

            foreach (string voice in PresetVoices())
            {
                if (!choices.Contains(voice))
                {
                    choices.Add(voice);
                }
            }

            var dropdown = new DropdownField();
            dropdown.choices = choices;
            dropdown.formatListItemCallback = VoiceLabel;
            dropdown.formatSelectedValueCallback = VoiceLabel;
            dropdown.SetValueWithoutNotify(current);
            dropdown.style.width = 260;
            dropdown.style.marginRight = 8;
            dropdown.RegisterValueChangedCallback(evt => SaveVoice(speaker, evt.newValue));
            StyleDropdown(dropdown);
            return dropdown;
        }

        /// <summary>Keep protects the character's clone from rotation (for example the boss's).</summary>
        private DropdownField BuildKeepDropdown(CloudTtsSpeaker speaker)
        {
            var dropdown = new DropdownField();
            dropdown.choices = new List<string> { "rotate", "keep" };
            dropdown.formatListItemCallback = value => T(value == "keep" ? "chars.keep" : "chars.rotate");
            dropdown.formatSelectedValueCallback = dropdown.formatListItemCallback;
            dropdown.SetValueWithoutNotify(speaker.KeepClone ? "keep" : "rotate");
            dropdown.style.width = 150;
            dropdown.RegisterValueChangedCallback(evt => SaveKeep(speaker, evt.newValue == "keep"));
            StyleDropdown(dropdown);
            return dropdown;
        }

        private IEnumerable<string> PresetVoices()
        {
            if (_provider == "elevenlabs")
            {
                foreach (KeyValuePair<string, string> voice in _eleven.MyVoices)
                {
                    yield return voice.Key;
                }

                foreach (string[] voice in CloudTtsConfig.ElevenLabsVoices)
                {
                    yield return voice[0];
                }

                yield break;
            }

            foreach (string voice in _provider == "googleai" ? CloudTtsConfig.GoogleVoices : CloudTtsConfig.OpenAiVoices)
            {
                yield return voice;
            }
        }

        private string VoiceLabel(string voice)
        {
            if (string.IsNullOrEmpty(voice))
            {
                return _provider == "elevenlabs" && _autoClone ? T("chars.autoClone") : T("chars.auto");
            }

            if (_provider != "elevenlabs")
            {
                return voice;
            }

            string mine = _eleven.MyVoiceName(voice);
            if (mine != null)
            {
                return T("voice.mineLabel", mine);
            }

            return CloudTtsConfig.ElevenLabsDefaultLabel(voice) ??
                   (_eleven.Clones.Contains(voice) ? T("voice.clone") : T("voice.id", voice));
        }

        private void SaveKeep(CloudTtsSpeaker speaker, bool keep)
        {
            try
            {
                CloudTtsVoices.SetKeepClone(speaker.Id, keep);
                speaker.KeepClone = keep;
                _bookText = CloudTtsVoices.ReadText();
                SetStatus(T(keep ? "chars.kept" : "chars.rotated", Who(speaker)), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("chars.saveFailed", ex.Message), true);
            }
        }

        private void Debounce(TextField field, Action save)
        {
            IVisualElementScheduledItem pending = null;
            field.RegisterValueChangedCallback(evt =>
            {
                _lastEdit = Time.unscaledTime;
                if (pending == null)
                {
                    pending = field.schedule.Execute(save);
                }

                pending.ExecuteLater(SaveDelayMs);
            });
        }

        private void SaveLabel(CloudTtsSpeaker speaker, string label)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                CloudTtsVoices.SetLabel(speaker.Id, label);
                speaker.Label = (label ?? "").Trim();
                _bookText = CloudTtsVoices.ReadText();
                SetStatus(T("chars.nameSaved", speaker.ShortId), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("chars.saveFailed", ex.Message), true);
            }
        }

        private void SaveVoice(CloudTtsSpeaker speaker, string voice)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                voice = (voice ?? "").Trim();
                CloudTtsVoices.SetVoice(speaker.Id, _provider, voice);
                if (voice.Length > 0)
                {
                    speaker.Voices[_provider] = voice;
                }
                else
                {
                    speaker.Voices.Remove(_provider);
                }

                _bookText = CloudTtsVoices.ReadText();
                SetStatus(voice.Length > 0
                    ? T("chars.voiceSaved", Who(speaker), VoiceLabel(voice))
                    : T("chars.voiceAuto", Who(speaker)), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("chars.saveFailed", ex.Message), true);
            }
        }

        private void PlayReference(CloudTtsSpeaker speaker)
        {
            try
            {
                byte[] wav = CloudTtsVoices.ReadReference(speaker.Id);
                if (wav == null)
                {
                    SetStatus(T("chars.noClip"), true);
                    return;
                }

                string error = CloudTtsAudio.Play(wav);
                SetStatus(error == null ? T("chars.playing") : T("chars.playFailed", error), error != null);
            }
            catch (Exception ex)
            {
                SetStatus(T("chars.playFailed", ex.Message), true);
            }
        }

        private void ToggleClicked()
        {
            _expanded = !_expanded;
            Rebuild();
        }

        private void ResetAllClicked()
        {
            if (!_confirmReset)
            {
                _confirmReset = true;
                _resetAll.text = T("chars.confirm");
                _resetAll.schedule.Execute(() =>
                {
                    _confirmReset = false;
                    _resetAll.text = T("chars.resetAll");
                    ApplyText(_actions);
                }).ExecuteLater(4000);
                ApplyText(_actions);
                return;
            }

            _confirmReset = false;
            _resetAll.text = T("chars.resetAll");
            try
            {
                int changed = CloudTtsVoices.ResetProvider(_provider);
                Reload();
                SetStatus(T("chars.resetDone", changed), false);
            }
            catch (Exception ex)
            {
                SetStatus(T("chars.resetFailed", ex.Message), true);
            }
        }

        private void SetStatus(string text, bool error)
        {
            _status.text = text ?? "";
            _status.style.color = error ? ErrorColor : InfoColor;
            _status.style.display = _status.text.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static string Who(CloudTtsSpeaker speaker) => speaker.Label.Length > 0 ? speaker.Label : speaker.ShortId;

        private static string Describe(CloudTtsSpeaker speaker)
        {
            string gender = speaker.Gender == "male" ? T("chars.male")
                : speaker.Gender == "female" ? T("chars.female")
                : T("chars.unknown");
            string pitch = speaker.PitchHz > 0
                ? " · " + T("chars.hz", Math.Round(speaker.PitchHz).ToString(CultureInfo.InvariantCulture))
                : "";
            return gender + pitch + " · " + speaker.ShortId;
        }

        private Label Muted()
        {
            var label = new Label();
            StyleHelp(label);
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 6;
            _root.Add(label);
            return label;
        }

        private static Button AddButton(VisualElement parent, string text, Action clicked,
                                        ButtonKind kind = ButtonKind.Normal)
        {
            var button = new Button(clicked) { text = text };
            button.style.marginRight = 8;
            button.style.marginBottom = 4;
            parent.Add(button);
            StyleButton(button, kind);
            return button;
        }
    }
}
