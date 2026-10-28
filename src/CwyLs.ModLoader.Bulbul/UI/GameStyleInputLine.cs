using System;
using BepInEx;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>How a mod window's text box behaves. The console's input line uses none of this.</summary>
    public sealed class GameStyleInputOptions
    {
        /// <summary>Visible lines; more than one wraps and scrolls inside the box.</summary>
        public int Lines { get; set; } = 1;

        /// <summary>Enter sends and Shift+Enter breaks the line (true), or the other way round with Ctrl/Command+Enter sending.</summary>
        public bool SubmitOnEnter { get; set; } = true;

        /// <summary>Most characters; 0 for no limit.</summary>
        public int MaxLength { get; set; }
    }

    /// <summary>
    /// A one-line text input in the game's style: a TextMeshPro input field (the same kind the
    /// game's note and to-do fields are, so OS input-method composition works exactly as it does
    /// there) on the game's outlined dark panel, in the game's font, with its 61 % placeholder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Return.</b> The field is configured multi-line and every newline it is about to insert
    /// goes through <see cref="ConsoleKeyRouter.ClassifyTypedChar"/>: Return is turned into
    /// <see cref="Submitted"/> and never inserted, so the field behaves as one line. Configured as
    /// a true single line, TextMeshPro would end editing on Return and report it through
    /// <c>SendOnSubmit</c>/<c>SendOnEndEdit</c> - which the trace plugin mirrors to the console
    /// as a <c>TYPED</c> line for every command typed into the console itself (TMP_InputField
    /// in the game's TextMeshPro: KeyPressed returns Finish for Return only when the line type is
    /// not MultiLineNewline, and hands the character to Append otherwise).
    /// </para>
    /// <para>
    /// <b>Chords.</b> A character typed with Ctrl (not AltGr) or Command held is dropped, so the
    /// console's own Ctrl+Shift+C never leaves a "C" - or, on macOS, where Ctrl+C arrives as
    /// U+0003 and the field turns it into a newline, a spurious Return - behind. The exception is a
    /// paste (Command/Ctrl+V down): the field appends the clipboard with the chord held, so its
    /// printable characters are kept.
    /// </para>
    /// <para>
    /// While the field is selected, the framework's hotkey registry ignores every chord
    /// (<c>Hotkeys.IsTyping</c> sees a TMP_InputField), which is what keeps typed keys from
    /// reaching our hotkeys. The game's own only key in the room (Escape, windowed mode) is read
    /// from the device, so no field can hide it; the console keeps it from acting on an Escape the
    /// console used by skipping the game's window switch instead (<c>ConsoleEscapePatch</c>), and
    /// mod windows do the same (<see cref="ModWindows"/>).
    /// </para>
    /// <para>
    /// <b>Mod text boxes</b> (built with <see cref="GameStyleInputOptions"/>): the characters go
    /// through <see cref="TextBoxKeys"/> instead - Enter sends, Shift+Enter breaks the line, and an
    /// Enter that commits an input-method composition (this frame or the last) does neither. Several
    /// lines are drawn top-aligned with 4 units of room above the first line for Thai tone marks
    /// stacked over an upper vowel.
    /// </para>
    /// </remarks>
    public sealed class GameStyleInputLine
    {
        /// <summary>Room above the first line of a multi-line box for stacked Thai marks.</summary>
        private const float MarkHeadroom = 4f;

        private readonly TMP_InputField _field;
        private readonly TextMeshProUGUI _hint;
        private readonly InputDriver _driver;
        private readonly GameStyleInputOptions? _options;
        private string? _pendingText;
        private int _lastComposingFrame = int.MinValue;

        private GameStyleInputLine(RectTransform rect, TMP_InputField field, TextMeshProUGUI hint, InputDriver driver, GameStyleInputOptions? options)
        {
            Rect = rect;
            _field = field;
            _hint = hint;
            _driver = driver;
            _options = options;
            _driver.Owner = this;
            _field.onValidateInput = Validate;
        }

        /// <summary>Raised on Return, after the frame's input was processed, with the line as typed.</summary>
        public event Action<string>? Submitted;

        /// <summary>The field's rect; the caller places and sizes it.</summary>
        public RectTransform Rect { get; }

        /// <summary>The text in the field.</summary>
        public string Text
        {
            get => _field.text ?? string.Empty;
            set => SetText(value);
        }

        /// <summary>True while the field is the UI's selected object (keyboard focus is ours).</summary>
        public bool IsSelected
        {
            get
            {
                var system = EventSystem.current;
                return system != null && system.currentSelectedGameObject == _field.gameObject;
            }
        }

        /// <summary>True while the field is accepting typing.</summary>
        public bool IsFocused => _field.isFocused;

        /// <summary>
        /// True while the OS input method holds an unfinished composition (a Japanese conversion,
        /// for example). Keys pressed then belong to the input method.
        /// </summary>
        public bool IsComposing
        {
            get
            {
                var module = EventSystem.current == null ? null : EventSystem.current.currentInputModule;
                var input = module == null ? null : module.input;
                return input != null && !string.IsNullOrEmpty(input.compositionString);
            }
        }

        /// <summary>
        /// True while this field has the keyboard and its input method is composing, or was on this
        /// frame or the last (the commit's frame): Escape and Enter then belong to the input method.
        /// </summary>
        public bool ComposingRecently =>
            IsSelected && TextBoxKeys.ComposingRecently(IsComposing, Time.frameCount, _lastComposingFrame);

        /// <summary>The grey text shown while the field is empty.</summary>
        public string Placeholder
        {
            get => _hint.text;
            set
            {
                var text = value ?? string.Empty;
                if (!string.Equals(_hint.text, text, StringComparison.Ordinal))
                {
                    _hint.text = text;
                }
            }
        }

        /// <summary>False greys the field out and takes no typing; it gives the keyboard back.</summary>
        public bool Interactable
        {
            get => _field.interactable;
            set
            {
                if (_field.interactable == value)
                {
                    return;
                }

                if (!value)
                {
                    Release();
                }

                _field.interactable = value;
                _field.textComponent.alpha = value ? 1f : 0.5f;
            }
        }

        /// <summary>Shows or hides the field.</summary>
        public bool Visible
        {
            get => Rect.gameObject.activeSelf;
            set
            {
                if (Rect.gameObject.activeSelf == value)
                {
                    return;
                }

                if (!value)
                {
                    Release();
                }

                Rect.gameObject.SetActive(value);
            }
        }

        /// <summary>Gives the field the keyboard. Does nothing when there is no EventSystem.</summary>
        public void Focus()
        {
            var system = EventSystem.current;
            if (system == null || !_field.isActiveAndEnabled)
            {
                return;
            }

            if (system.currentSelectedGameObject != _field.gameObject)
            {
                system.SetSelectedGameObject(_field.gameObject);
            }

            if (!_field.isFocused)
            {
                _field.ActivateInputField();
            }
        }

        /// <summary>Takes the keyboard away from the field, so typing goes back to the game.</summary>
        public void Release()
        {
            var system = EventSystem.current;
            if (system != null && system.currentSelectedGameObject == _field.gameObject)
            {
                system.SetSelectedGameObject(null);
            }
        }

        /// <summary>
        /// Replaces the text and puts the caret at its end. Applied after the field has processed
        /// this frame's keys, so an arrow key the field also saw cannot move the caret back.
        /// </summary>
        /// <param name="text">The new text; null is empty.</param>
        public void SetText(string? text) => _pendingText = text ?? string.Empty;

        /// <summary>Builds a field under <paramref name="parent"/>. The caller sets its anchors and size.</summary>
        /// <param name="parent">Where the field goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        /// <param name="placeholder">Grey text shown while the field is empty.</param>
        internal static GameStyleInputLine Create(RectTransform parent, GameStyleAssets assets, string placeholder) =>
            Create(parent, assets, placeholder, null);

        /// <summary>Builds a field; with <paramref name="options"/> it is a mod window's text box.</summary>
        /// <param name="parent">Where the field goes.</param>
        /// <param name="assets">The game's UI assets.</param>
        /// <param name="placeholder">Grey text shown while the field is empty.</param>
        /// <param name="options">The mod text box rules, or null for the console's one-line rules.</param>
        internal static GameStyleInputLine Create(RectTransform parent, GameStyleAssets assets, string placeholder, GameStyleInputOptions? options)
        {
            var multiLine = options != null && options.Lines > 1;
            var alignment = multiLine ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            var rect = GameStyleUi.Child(parent, "Input");
            rect.gameObject.SetActive(false);

            var background = GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.Panel), GameStyleUi.PanelFill, raycast: true);

            var area = GameStyleUi.Child(rect, "Text Area");
            GameStyleUi.Stretch(area, 16f, multiLine ? 6f : 4f, 16f, multiLine ? 6f : 4f);
            area.gameObject.AddComponent<RectMask2D>();

            var hintRect = GameStyleUi.Child(area, "Placeholder");
            GameStyleUi.Stretch(hintRect);
            var hint = GameStyleUi.AddLabel(hintRect, assets.Font, GameStyleUi.BodySize, GameStyleUi.Placeholder, alignment);
            hint.text = placeholder;

            var textRect = GameStyleUi.Child(area, "Text");
            GameStyleUi.Stretch(textRect);
            var text = GameStyleUi.AddLabel(textRect, assets.Font, GameStyleUi.BodySize, GameStyleUi.Text, alignment);
            text.overflowMode = TextOverflowModes.Overflow;
            if (multiLine)
            {
                // Thai tone marks over an upper vowel reach above the line box; keep them inside the mask.
                var margin = new Vector4(0f, MarkHeadroom, 0f, 0f);
                text.margin = margin;
                hint.margin = margin;
                hint.enableWordWrapping = true;
            }

            var field = rect.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = area;
            field.textComponent = text;
            field.placeholder = hint;
            field.fontAsset = assets.Font;
            field.pointSize = GameStyleUi.BodySize;
            field.targetGraphic = background;
            field.transition = Selectable.Transition.None;
            field.navigation = new Navigation { mode = Navigation.Mode.None };

            // See the class remarks: Return reaches Validate as a newline instead of ending the edit.
            field.lineType = TMP_InputField.LineType.MultiLineNewline;
            field.richText = false;
            field.onFocusSelectAll = false;
            field.resetOnDeActivation = false;
            field.restoreOriginalTextOnEscape = false;
            field.customCaretColor = true;
            field.caretColor = GameStyleUi.Text;
            field.caretWidth = 2;
            field.selectionColor = new Color(GameStyleUi.Cyan.r, GameStyleUi.Cyan.g, GameStyleUi.Cyan.b, 0.35f);
            if (options != null && options.MaxLength > 0)
            {
                field.characterLimit = options.MaxLength;
            }

            var driver = rect.gameObject.AddComponent<InputDriver>();

            rect.gameObject.SetActive(true);
            return new GameStyleInputLine(rect, field, hint, driver, options);
        }

        private char Validate(string text, int charIndex, char addedChar)
        {
            var input = UnityInput.Current;
            var control = input.GetKey(KeyCode.LeftControl) || input.GetKey(KeyCode.RightControl);
            var command = input.GetKey(KeyCode.LeftCommand) || input.GetKey(KeyCode.RightCommand);
            var alt = input.GetKey(KeyCode.LeftAlt) || input.GetKey(KeyCode.RightAlt);

            // TMP pastes on Command+V (macOS) / Ctrl+V and appends the clipboard through this
            // validator with the chord still down; without this every pasted character was dropped.
            var pasting = (command || control) && !alt && input.GetKey(KeyCode.V);

            if (_options != null)
            {
                var composing = TextBoxKeys.ComposingRecently(IsComposing, Time.frameCount, _lastComposingFrame);
                switch (TextBoxKeys.Classify(addedChar, control, command, alt, _options.Lines > 1, _options.SubmitOnEnter, composing, pasting))
                {
                    case TextBoxChar.Accept:
                        return addedChar;
                    case TextBoxChar.Newline:
                        return '\n';
                    case TextBoxChar.Submit:
                        _driver.SubmitPending = true;
                        return '\0';
                    default:
                        return '\0';
                }
            }

            switch (ConsoleKeyRouter.ClassifyTypedChar(addedChar, control, command, alt, pasting))
            {
                case ConsoleTypedChar.Accept:
                    return addedChar;
                case ConsoleTypedChar.Submit:
                    // Deferred to LateUpdate: the field is in the middle of processing this key.
                    _driver.SubmitPending = true;
                    return '\0';
                default:
                    return '\0';
            }
        }

        private void AfterInput()
        {
            if (_options != null && IsSelected && IsComposing)
            {
                _lastComposingFrame = Time.frameCount;
            }

            if (_driver.SubmitPending)
            {
                _driver.SubmitPending = false;
                var line = Text;
                try
                {
                    Submitted?.Invoke(line);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"{GameStyleUi.LogPrefix} a submit handler threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (_pendingText != null)
            {
                var text = _pendingText;
                _pendingText = null;
                _field.text = text;

                // A string index (not a caret index, which counts rendered characters): the field
                // recomputes the caret from it on its next update.
                _field.stringPosition = text.Length;
            }
        }

        /// <summary>Runs the deferred submit and text change once the frame's input is processed.</summary>
        private sealed class InputDriver : MonoBehaviour
        {
            internal GameStyleInputLine? Owner { get; set; }

            internal bool SubmitPending { get; set; }

            private void LateUpdate() => Owner?.AfterInput();
        }
    }
}
