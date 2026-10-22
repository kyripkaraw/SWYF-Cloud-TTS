using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace SwyfCloudTts.Plugin
{
    /// <summary>
    /// Makes the card look like the game's own settings. The game styles its controls through
    /// USS classes, so every control the card adds takes the classes of a matching control the
    /// game already shows (the Qwen dropdown, the FPS slider, the Deepgram key field...). Only
    /// when such a control cannot be found does the card fall back to its own colours.
    /// </summary>
    internal static class CloudTtsTheme
    {
        internal static readonly Color InfoColor = new Color(0.78f, 0.80f, 0.86f);
        internal static readonly Color OkColor = new Color(0.55f, 0.86f, 0.58f);
        internal static readonly Color ErrorColor = new Color(1f, 0.47f, 0.47f);
        internal static readonly Color WarningColor = new Color(1f, 0.77f, 0.42f);

        private static readonly Color InputBg = new Color(0.09f, 0.10f, 0.13f, 1f);
        private static readonly Color InputBorder = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color FieldTextColor = new Color(0.93f, 0.94f, 0.97f);
        private static readonly Color MutedTextColor = new Color(0.65f, 0.68f, 0.75f);
        private static readonly Color ButtonBg = new Color(0.17f, 0.19f, 0.24f, 1f);
        private static readonly Color AccentColor = new Color(0.36f, 0.60f, 1f, 1f);
        private static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.12f);

        private static string[] _dropdown;
        private static string[] _slider;
        private static string[] _value;
        private static string[] _textField;
        private static string[] _button;
        private static string[] _primaryButton;
        private static string[] _resetButton;
        private static Font _font;

        /// <summary>Not public in the game's Unity version.</summary>
        private static readonly MethodInfo ClassList =
            typeof(VisualElement).GetMethod("GetClasses", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null) ??
            typeof(VisualElement).GetMethod("GetClassesForIteration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);

        internal enum ButtonKind
        {
            Normal,
            Primary,
            Small,
        }

        /// <summary>Remembers the classes of the game's own controls. Called before the card is built.</summary>
        public static void Capture(VisualElement root)
        {
            _dropdown = ClassesOf(First<DropdownField>(root, "qwen-tts-dropdown", "speech-recognition-dropdown", "vsync-dropdown"));
            _slider = ClassesOf(First<SliderInt>(root, "max-fps-slider", "render-scale-slider", "computer-volume-slider"));
            _value = ClassesOf(First<Label>(root, "max-fps-value", "render-scale-value", "computer-volume-value"));
            _textField = ClassesOf(First<TextField>(root, "deepgram-key"));
            _button = ClassesOf(First<Button>(root, "deepgram-website", "deepgram-clear"));
            _primaryButton = ClassesOf(First<Button>(root, "deepgram-save"));
            _resetButton = ClassesOf(First<Button>(root, "qwen-tts-reset", "speech-recognition-reset"));

            PluginLog.Info("native look: dropdown=" + Describe(_dropdown) + " slider=" + Describe(_slider) +
                           " value=" + Describe(_value) + " text=" + Describe(_textField) +
                           " button=" + Describe(_button) + " primary=" + Describe(_primaryButton) +
                           " reset=" + Describe(_resetButton));
        }

        /// <summary>
        /// The game's default font has no Cyrillic; it switches fonts only for its own Russian.
        /// A Ukrainian or Russian card over an English game borrows that font.
        /// </summary>
        public static void UseFontFor(string cardLanguage)
        {
            _font = null;
            if (cardLanguage != "uk" && cardLanguage != "ru")
            {
                return;
            }

            if (CloudTtsGame.UiFont(CloudTtsGame.LanguageCode) == null)
            {
                _font = CloudTtsGame.UiFont("ru");
            }
        }

        /// <summary>Applies the borrowed font and the game's casing rules to every text in the card.</summary>
        public static void ApplyText(VisualElement root)
        {
            foreach (TextElement text in root.Query<TextElement>().ToList())
            {
                if (_font != null)
                {
                    text.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(_font));
                }

                if (text.ClassListContains("setting-label") || text.ClassListContains("section-label") || text is Button)
                {
                    text.text = CloudTtsGame.FormatSettingsText(text, text.text);
                }
            }
        }

        public static void StyleDropdown(DropdownField field)
        {
            if (field == null)
            {
                return;
            }

            if (Adopt(field, _dropdown))
            {
                HookPopupFont(field);
                return;
            }

            foreach (VisualElement element in field.Query<VisualElement>().ToList())
            {
                if (element == field)
                {
                    continue;
                }

                if (IsInputBox(element))
                {
                    StyleBox(element);
                }

                if (element.ClassListContains("unity-base-popup-field__arrow") ||
                    element.ClassListContains("unity-popup-field__arrow"))
                {
                    element.style.color = MutedTextColor;
                }
                else if (element is TextElement text)
                {
                    text.style.color = FieldTextColor;
                }
            }

            HookPopup(field);
        }

        public static void StyleTextField(VisualElement field)
        {
            if (field == null || Adopt(field, _textField))
            {
                return;
            }

            foreach (VisualElement element in field.Query<VisualElement>().ToList())
            {
                if (element == field)
                {
                    continue;
                }

                if (IsInputBox(element))
                {
                    StyleBox(element);
                }

                if (element is TextElement text)
                {
                    text.style.color = FieldTextColor;
                }
            }
        }

        public static void StyleSlider(VisualElement slider)
        {
            if (slider == null)
            {
                return;
            }

            slider.AddToClassList("setting-slider");
            if (Adopt(slider, _slider))
            {
                return;
            }

            foreach (VisualElement element in slider.Query<VisualElement>().ToList())
            {
                if (element.ClassListContains("unity-base-slider__drag-container"))
                {
                    element.style.backgroundColor = TrackColor;
                    element.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                    SetRadius(element, 3f);
                }
                else if (element.ClassListContains("unity-base-slider__tracker"))
                {
                    element.style.backgroundColor = AccentColor;
                    element.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                    SetRadius(element, 3f);
                }
                else if (element.ClassListContains("unity-base-slider__dragger"))
                {
                    element.style.backgroundColor = FieldTextColor;
                    element.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                    SetRadius(element, 6f);
                }
            }
        }

        public static void StyleValue(Label label)
        {
            label.AddToClassList("setting-value");
            if (!Adopt(label, _value))
            {
                label.style.color = FieldTextColor;
            }
        }

        public static void StyleButton(Button button, ButtonKind kind)
        {
            if (button == null)
            {
                return;
            }

            string[] native = kind == ButtonKind.Primary ? _primaryButton : kind == ButtonKind.Small ? _resetButton : _button;
            button.AddToClassList(kind == ButtonKind.Primary ? "reset-save-button" : "reset-button");
            if (Adopt(button, native ?? _button))
            {
                return;
            }

            bool primary = kind == ButtonKind.Primary;
            button.style.backgroundColor = primary ? AccentColor : ButtonBg;
            button.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            button.style.color = primary ? Color.white : FieldTextColor;
            SetBorder(button, primary ? new Color(1f, 1f, 1f, 0f) : InputBorder);
            SetRadius(button, 4f);
            button.style.paddingLeft = 14f;
            button.style.paddingRight = 14f;
        }

        /// <summary>Help lines use the game's class; without it they would inherit the label colour.</summary>
        public static void StyleHelp(Label label)
        {
            label.AddToClassList("setting-help");
            if (_dropdown == null)
            {
                label.style.color = MutedTextColor;
            }
        }

        private static bool Adopt(VisualElement element, string[] classes)
        {
            if (classes == null)
            {
                return false;
            }

            foreach (string name in classes)
            {
                element.AddToClassList(name);
            }

            return true;
        }

        private static T First<T>(VisualElement root, params string[] names) where T : VisualElement
        {
            foreach (string name in names)
            {
                T found = root.Q<T>(name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static string[] ClassesOf(VisualElement element)
        {
            if (element == null)
            {
                return null;
            }

            var classes = new List<string>();
            if (ClassList == null || !(ClassList.Invoke(element, null) is IEnumerable names))
            {
                return null;
            }

            foreach (object name in names)
            {
                if (name is string text && !text.StartsWith("unity-"))
                {
                    classes.Add(text);
                }
            }

            return classes.ToArray();
        }

        private static string Describe(string[] classes)
            => classes == null ? "(missing)" : "[" + string.Join(" ", classes) + "]";

        private static bool IsInputBox(VisualElement element)
            => element.ClassListContains("unity-base-text-field__input")
               || element.ClassListContains("unity-text-field__input")
               || element.ClassListContains("unity-base-popup-field__input")
               || element.ClassListContains("unity-popup-field__input")
               || element.ClassListContains("unity-base-field__input");

        private static void StyleBox(VisualElement box)
        {
            box.style.backgroundColor = InputBg;
            box.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            SetBorder(box, InputBorder);
            SetRadius(box, 4f);
            box.style.paddingLeft = 6f;
            box.style.paddingRight = 6f;
        }

        /// <summary>The dropdown list opens in a popup outside the card, so it is styled when it opens.</summary>
        private static void HookPopup(DropdownField field)
        {
            field.RegisterCallback<PointerDownEvent>(_ => field.schedule.Execute(() =>
            {
                foreach (VisualElement menu in Menus(field))
                {
                    foreach (VisualElement element in menu.Query<VisualElement>().ToList())
                    {
                        if (element.ClassListContains("unity-dropdown-menu__background") ||
                            element.ClassListContains("unity-dropdown-menu__container"))
                        {
                            element.style.backgroundColor = new Color(0.10f, 0.11f, 0.14f, 0.99f);
                            element.style.backgroundImage = new StyleBackground(StyleKeyword.None);
                        }

                        if (element is TextElement text)
                        {
                            text.style.color = FieldTextColor;
                            SetFont(text);
                        }
                    }
                }
            }).ExecuteLater(1));
        }

        private static void HookPopupFont(DropdownField field)
        {
            if (_font == null)
            {
                return;
            }

            field.RegisterCallback<PointerDownEvent>(_ => field.schedule.Execute(() =>
            {
                foreach (VisualElement menu in Menus(field))
                {
                    foreach (TextElement text in menu.Query<TextElement>().ToList())
                    {
                        SetFont(text);
                    }
                }
            }).ExecuteLater(1));
        }

        private static List<VisualElement> Menus(DropdownField field)
            => field.panel != null
                ? field.panel.visualTree.Query<VisualElement>(className: "unity-dropdown-menu").ToList()
                : new List<VisualElement>();

        private static void SetFont(TextElement text)
        {
            if (_font != null)
            {
                text.style.unityFontDefinition = new StyleFontDefinition(FontDefinition.FromFont(_font));
            }
        }

        private static void SetBorder(VisualElement element, Color color)
        {
            element.style.borderTopWidth = 1f;
            element.style.borderBottomWidth = 1f;
            element.style.borderLeftWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }
    }
}
