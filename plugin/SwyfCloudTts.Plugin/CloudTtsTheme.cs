using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace SwyfCloudTts.Plugin
{
    /// Makes the card look like the game's own settings. The game styles its controls through
    /// USS classes, so every control the card adds takes the classes of a matching control the
    /// game already shows (the Qwen dropdown, the FPS slider, the Deepgram key field...). Only
    /// when such a control cannot be found does the card fall back to its own colours.
    internal static class CloudTtsTheme
    {
        // Colours taken from the game's settings style sheet, so fallbacks blend in with it.
        internal static readonly Color InfoColor = new Color32(232, 210, 172, 255);
        internal static readonly Color OkColor = new Color(0.55f, 0.86f, 0.58f);
        internal static readonly Color ErrorColor = new Color(1f, 0.47f, 0.47f);
        internal static readonly Color WarningColor = new Color(1f, 0.77f, 0.42f);

        private static readonly Color InputBg = new Color32(31, 19, 17, 255);
        private static readonly Color InputBorder = new Color32(147, 97, 48, 255);
        private static readonly Color FieldTextColor = new Color32(255, 225, 171, 255);
        private static readonly Color MutedTextColor = new Color32(204, 177, 139, 255);
        private static readonly Color ButtonBg = new Color32(47, 33, 27, 250);
        private static readonly Color ButtonBorder = new Color32(173, 129, 71, 255);
        private static readonly Color AccentColor = new Color32(222, 156, 47, 255);
        private static readonly Color TrackColor = new Color32(87, 68, 57, 255);
        private static readonly Color MenuBg = new Color32(31, 19, 17, 252);

        private static string[] _dropdown;
        private static string[] _slider;
        private static string[] _value;
        private static string[] _textField;
        private static string[] _button;
        private static string[] _primaryButton;
        private static string[] _resetButton;
        private static Font _font;

        /// Not public in the game's Unity version.
        private static readonly MethodInfo ClassList =
            typeof(VisualElement).GetMethod("GetClasses", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null) ??
            typeof(VisualElement).GetMethod("GetClassesForIteration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, System.Type.EmptyTypes, null);

        internal enum ButtonKind
        {
            Normal,
            Primary,
            Small,
        }

        /// Remembers the classes of the game's own controls. Called before the card is built.
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

        /// The game puts its language's font on every text it localizes, but the card opts out of
        /// that localization, so without this its Latin text would fall back to the game's
        /// default display font. The default font has no Cyrillic either, so a Ukrainian or
        /// Russian card over an English game borrows the Russian font.
        public static void UseFontFor(string cardLanguage)
        {
            string game = CloudTtsGame.LanguageCode;

            // Like the game, keep the default font for Arabic and Farsi; the card is never in those.
            _font = game != "ar" && game != "fa" ? CloudTtsGame.UiFont(game) : null;
            if (_font == null && (cardLanguage == "uk" || cardLanguage == "ru"))
            {
                _font = CloudTtsGame.UiFont("ru");
            }
        }

        /// Applies the borrowed font and the game's casing rules to every text in the card.
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
                if (field.ClassListContains("menu-dropdown"))
                {
                    AddSurface(field);
                }

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

            // Same height as the dropdowns beside it.
            field.style.height = 38f;

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
            button.AddToClassList("reset-button");
            bool primary = kind == ButtonKind.Primary;
            if (kind != ButtonKind.Small)
            {
                // The game's reset button is a fixed 91px; ours carry longer captions.
                button.style.width = StyleKeyword.Auto;
                button.style.minWidth = 91f;
                button.style.paddingLeft = 16f;
                button.style.paddingRight = 16f;
            }

            if (Adopt(button, native ?? _resetButton))
            {
                if (primary)
                {
                    // Only the border, so the game's hover colours still apply.
                    SetBorder(button, AccentColor, 2f);
                }

                return;
            }

            button.style.backgroundColor = primary ? AccentColor : ButtonBg;
            button.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            button.style.color = primary ? (Color)new Color32(34, 21, 17, 255) : FieldTextColor;
            SetBorder(button, primary ? AccentColor : ButtonBorder, 2f);
            SetRadius(button, 6f);
            button.style.height = 36f;
        }

        /// Help lines use the game's class; without it they would inherit the label colour.
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
            SetRadius(box, 6f);
            box.style.paddingLeft = 10f;
            box.style.paddingRight = 10f;
        }

        /// The game's dropdown look is drawn by a surface it wraps around the field's contents
        /// (MainMenuToolkitController.ConfigureDropdownHover); without it the field keeps
        /// Unity's grey box. This repeats that wrapping for the card's dropdowns.
        private static void AddSurface(DropdownField field)
        {
            VisualElement input = field.Q(null, "unity-base-popup-field__input");
            if (input == null || input.ClassListContains("menu-dropdown-anchor"))
            {
                return;
            }

            var surface = new VisualElement { pickingMode = PickingMode.Ignore };
            surface.AddToClassList("menu-dropdown-surface");
            foreach (VisualElement child in new List<VisualElement>(input.Children()))
            {
                surface.Add(child);
            }

            input.Add(surface);
            input.AddToClassList("menu-dropdown-anchor");
        }

        /// The dropdown list opens in a popup outside the card, so it is styled when it opens.
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
                            element.style.backgroundColor = MenuBg;
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

        private static void SetBorder(VisualElement element, Color color, float width = 1f)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
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
