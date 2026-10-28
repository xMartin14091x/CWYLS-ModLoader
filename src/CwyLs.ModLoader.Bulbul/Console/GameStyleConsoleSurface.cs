using System;
using System.Collections.Generic;
using BepInEx;
using CwyLs.ModLoader.Bulbul.UI;
using CwyLs.ModLoader.Core.Console;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The console drawn as one of the game's own windows - the facility-window frame, the
    /// settings-window tabs, the game's font and palette - on top of the reusable
    /// <see cref="GameStyleWindow"/>. Registered with the framework by
    /// <see cref="BulbulConsoleCommands.Register"/>; the framework falls back to its IMGUI overlay
    /// whenever this cannot be built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the console does comes from <see cref="Framework.ConsoleSession"/> and
    /// <see cref="Framework.ConsoleFeature"/>; this class only lays the session out and routes keys
    /// (<see cref="ConsoleKeyRouter"/>): Enter runs, Tab completes, Up/Down walk the history,
    /// PageUp/PageDown scroll, Escape closes, and the toggle chord works even while the input has
    /// the keyboard.
    /// </para>
    /// <para>
    /// While the Console tab shows, the input keeps the keyboard unless the player is typing into
    /// one of the game's own text fields - the IMGUI console's "focus every frame", made polite.
    /// </para>
    /// </remarks>
    public sealed class GameStyleConsoleSurface : Framework.IConsoleSurface
    {
        /// <summary>The name the log and the smoke report use.</summary>
        public const string SurfaceName = "game-style";

        /// <summary>Window title.</summary>
        public const string WindowTitle = "CWY console";

        /// <summary>Most buttons a view tab offers (refresh plus one).</summary>
        private const int ActionButtons = 3;

        /// <summary>How often a live panel (Stream, State) is re-rendered.</summary>
        private const float PanelRefreshSeconds = 0.25f;

        private const float RowHeight = ConsoleLayout.RowHeight;
        private const float TabsHeight = ConsoleLayout.TabsHeight;
        private const float Gap = ConsoleLayout.Gap;
        private const float ButtonWidth = ConsoleLayout.ButtonWidth;

        private readonly List<GameStyleButton> _actionButtons = new List<GameStyleButton>(ActionButtons);
        private readonly List<string> _actions = new List<string>(ActionButtons);
        private readonly Func<int, string> _scrollbackLine;
        private readonly Func<int, string> _viewLine;

        private GameStyleWindow? _window;
        private GameStyleTabStrip? _tabs;
        private GameStyleTextView? _view;
        private GameStyleInputLine? _input;
        private GameStyleButton? _runButton;
        private TextMeshProUGUI? _mode;
        private bool? _logAreaHasRow;

        private IReadOnlyList<ConsoleLine> _scrollback = Array.Empty<ConsoleLine>();
        private long _lastSequence = -1;
        private string[] _viewLines = Array.Empty<string>();
        private string _viewSource = string.Empty;
        private long _sessionViewVersion = -1;
        private long _contentVersion;
        private float _nextPanelRender;
        private int _shownTab = -1;
        private long _statusSequence = -1;
        private int _statusCommands = -1;
        private string _chords = string.Empty;

        private GameStyleConsoleSurface()
        {
            _scrollbackLine = ScrollbackLine;
            _viewLine = ViewLine;
        }

        /// <summary>The one instance.</summary>
        public static GameStyleConsoleSurface Instance { get; } = new GameStyleConsoleSurface();

        /// <inheritdoc />
        public string Name => SurfaceName;

        /// <inheritdoc />
        public float GameUiAlpha => GameUiBridge.GameUiAlpha();

        /// <inheritdoc />
        public bool TryShow(out string detail)
        {
            try
            {
                var built = false;
                if (_window != null && _window.IsAlive && !_window.Assets.IsLoaded)
                {
                    // The game unloaded a sprite or the font we were built with (a scene change):
                    // build again from what is loaded now, or fall back if nothing is.
                    _window.Destroy();
                    _window = null;
                }

                if (_window == null || !_window.IsAlive)
                {
                    if (!Build(out detail))
                    {
                        return false;
                    }

                    built = true;
                }

                _chords = global::CwyLs.ModLoader.Input.Hotkeys.Find(Framework.ConsoleFeature.HotkeyId)?.Chords() ?? string.Empty;

                Framework.ConsoleSession.OnOpened();
                _shownTab = -1;
                _contentVersion++;
                _statusSequence = -1;
                _view!.ScrollToEnd();
                _window!.Show();

                detail = built ? _window.Assets.Report.Describe() : string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                detail = "the window could not be shown: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError($"{Framework.ConsoleFeature.LogPrefix} {detail}");
                return false;
            }
        }

        /// <inheritdoc />
        public void Hide()
        {
            _input?.Release();
            _window?.Hide();
        }

        private bool Build(out string detail)
        {
            var options = new GameStyleWindowOptions
            {
                Name = "CWY Console",
                Title = WindowTitle,
                MinWidth = 640f,
                MinHeight = 360f,
                Resizable = true
            };

            if (!GameStyleWindow.TryCreate(options, out var window, out detail) || window == null)
            {
                return false;
            }

            try
            {
                Layout(window);
            }
            catch (Exception ex)
            {
                detail = "the console could not be laid out: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError($"{Framework.ConsoleFeature.LogPrefix} {detail}");
                window.Destroy();
                return false;
            }

            window.CloseClicked += Framework.ConsoleFeature.Close;
            window.Updated += OnUpdate;
            window.Failed += ex => Framework.ConsoleFeature.ReportSurfaceFailure(this, ex);
            _window = window;
            return true;
        }

        private void Layout(GameStyleWindow window)
        {
            var body = window.Body;

            // A rebuild (the game unloaded what the last window was made of) starts from nothing.
            _actionButtons.Clear();
            _actions.Clear();
            _lastSequence = -1;
            _scrollback = Array.Empty<ConsoleLine>();
            _viewLines = Array.Empty<string>();
            _viewSource = string.Empty;
            _logAreaHasRow = null;

            _tabs = window.AddTabStrip(body);
            var tabs = _tabs.Rect;
            tabs.anchorMin = new Vector2(0f, 1f);
            tabs.anchorMax = new Vector2(1f, 1f);
            tabs.pivot = new Vector2(0.5f, 1f);
            tabs.offsetMin = new Vector2(0f, -TabsHeight);
            tabs.offsetMax = Vector2.zero;
            _tabs.TabClicked += Framework.ConsoleSession.SelectTab;

            _view = window.AddTextView(body);
            PlaceLogArea(bottomRow: true);

            var row = GameStyleUi.Child(body, "Row");
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0.5f, 0f);
            row.offsetMin = Vector2.zero;
            row.offsetMax = new Vector2(0f, RowHeight);

            _input = window.AddInputLine(row, "type a command - Tab completes, Up/Down history, Esc closes");
            GameStyleUi.Stretch(_input.Rect, 0f, 0f, ButtonWidth + Gap, 0f);
            _input.Submitted += Run;

            _runButton = window.AddButton(row, "run");
            GameStyleUi.Pin(_runButton.Rect, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(ButtonWidth, RowHeight - 4f));
            _runButton.Clicked += () => Run(_input.Text);

            for (var i = 0; i < ActionButtons; i++)
            {
                var index = i;
                var button = window.AddButton(row, string.Empty);
                GameStyleUi.Pin(button.Rect, new Vector2(0f, 0.5f), new Vector2(i * (ButtonWidth + Gap), 0f), new Vector2(ButtonWidth, RowHeight - 4f));
                button.Clicked += () => RunAction(index);
                button.Visible = false;
                _actionButtons.Add(button);
            }

            var mode = GameStyleUi.Child(row, "Mode");
            mode.anchorMin = new Vector2(1f, 0f);
            mode.anchorMax = new Vector2(1f, 1f);
            mode.pivot = new Vector2(1f, 0.5f);
            mode.anchoredPosition = Vector2.zero;
            mode.sizeDelta = new Vector2(240f, 0f);
            _mode = GameStyleUi.AddLabel(mode, window.Assets.Font, GameStyleUi.SmallSize, GameStyleUi.TabLabel, TextAlignmentOptions.MidlineRight);
        }

        private void OnUpdate()
        {
            var window = _window;
            if (window == null || !Framework.ConsoleFeature.IsOpen)
            {
                return;
            }

            FollowSettings(window);

            if (window.IsInteractable)
            {
                // Who owns Escape, reported every frame: the game reads Escape itself and may
                // do so before this Update in the same frame (ConsoleEscapeGuard).
                var focus = CurrentFocus();
                var escapeOwner = ConsoleEscapeGuard.OwnerOf(true, true, focus);
                Framework.ConsoleFeature.EscapeGuard.Observe(Time.frameCount, escapeOwner);

                RouteKeys(focus, escapeOwner);

                // Escape or the toggle may just have closed the console.
                if (!Framework.ConsoleFeature.IsOpen)
                {
                    return;
                }
            }
            else
            {
                // Faded out with the game's hidden UI: keys, Escape included, are the game's again.
                Framework.ConsoleFeature.EscapeGuard.Observe(Time.frameCount, EscapeOwner.Game);
            }

            SyncTab();
            SyncContent();
            SyncStatus(window);

            if (window.IsInteractable)
            {
                KeepFocus();
            }
            else
            {
                // Faded out with the game's hidden UI: typing must go back to the game.
                _input!.Release();
            }
        }

        private void FollowSettings(GameStyleWindow window)
        {
            var gameAlpha = GameUiBridge.GameUiAlpha();
            var rule = Framework.ConsoleFeature.Visibility;
            rule.Observe(gameAlpha);
            window.ExternalAlpha = rule.TargetAlpha(gameAlpha);

            window.SetBackgroundOpacity(Framework.ConsoleFeature.Opacity);
            _view!.FontSize = GameStyleUi.BodySize * Framework.ConsoleFeature.FontScale;
        }

        /// <summary>Who has the keyboard this frame, as the key router sees it.</summary>
        private ConsoleFocus CurrentFocus()
        {
            var line = _input!;
            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
            return new ConsoleFocus(
                line.IsSelected,
                line.IsFocused,
                selected != null && !line.IsSelected && IsTextEntry(selected),
                line.IsComposing,
                Framework.ConsoleSession.OnConsoleTab);
        }

        private void RouteKeys(ConsoleFocus focus, EscapeOwner escapeOwner)
        {
            var input = UnityInput.Current;
            var keys = ConsoleKeyPress.None;

            if (Framework.ConsoleFeature.ToggleChordDown())
            {
                keys |= ConsoleKeyPress.ToggleChord;
            }

            if (input.GetKeyDown(KeyCode.Escape))
            {
                keys |= ConsoleKeyPress.Escape;
            }

            if (input.GetKeyDown(KeyCode.Tab))
            {
                keys |= ConsoleKeyPress.Tab;
            }

            if (input.GetKeyDown(KeyCode.UpArrow))
            {
                keys |= ConsoleKeyPress.Up;
            }

            if (input.GetKeyDown(KeyCode.DownArrow))
            {
                keys |= ConsoleKeyPress.Down;
            }

            if (input.GetKeyDown(KeyCode.PageUp))
            {
                keys |= ConsoleKeyPress.PageUp;
            }

            if (input.GetKeyDown(KeyCode.PageDown))
            {
                keys |= ConsoleKeyPress.PageDown;
            }

            if (keys == ConsoleKeyPress.None)
            {
                return;
            }

            // Ours: it closes the console (or cancels a composition in our input), so the game's own
            // Escape handling must not also switch fullscreen to windowed this frame.
            if ((keys & ConsoleKeyPress.Escape) != 0 && escapeOwner == EscapeOwner.Console)
            {
                Framework.ConsoleFeature.EscapeGuard.Claim(Time.frameCount);
            }

            var line = _input!;
            switch (ConsoleKeyRouter.Route(keys, focus))
            {
                case ConsoleKeyAction.Toggle:
                    Framework.ConsoleFeature.Toggle();
                    break;
                case ConsoleKeyAction.Close:
                    Framework.ConsoleFeature.Close();
                    break;
                case ConsoleKeyAction.Complete:
                    line.SetText(Framework.ConsoleSession.Complete(line.Text));
                    break;
                case ConsoleKeyAction.HistoryPrevious:
                    line.SetText(Framework.ConsoleSession.HistoryPrevious(line.Text) ?? line.Text);
                    break;
                case ConsoleKeyAction.HistoryNext:
                    line.SetText(Framework.ConsoleSession.HistoryNext() ?? line.Text);
                    break;
                case ConsoleKeyAction.PageUp:
                    _view!.PageUp();
                    break;
                case ConsoleKeyAction.PageDown:
                    _view!.PageDown();
                    break;
            }
        }

        private void SyncTab()
        {
            var tab = Framework.ConsoleSession.Tab;
            _tabs!.SetTabs(Framework.ConsoleSession.Tabs(), tab);

            if (tab == _shownTab)
            {
                return;
            }

            _shownTab = tab;
            _contentVersion++;
            _sessionViewVersion = -1;
            _nextPanelRender = 0f;

            var console = Framework.ConsoleSession.OnConsoleTab;
            _input!.Visible = console;
            _runButton!.Visible = console;

            _actions.Clear();
            _actions.AddRange(Framework.ConsoleSession.TabActions());
            for (var i = 0; i < _actionButtons.Count; i++)
            {
                var button = _actionButtons[i];
                button.Visible = i < _actions.Count;
                if (i < _actions.Count)
                {
                    button.Label = _actions[i];
                }
            }

            _mode!.text = console ? string.Empty : Framework.ConsoleSession.OnPanelTab ? "panel, live" : "snapshot";

            // One rule for every tab's log area; the view re-plans its lines when its size changes.
            PlaceLogArea(ConsoleLayout.NeedsBottomRow(console, _actions.Count, _mode.text.Length > 0));

            // The scrollback follows the newest line; a view reads like a document, top first.
            if (console)
            {
                _view!.ScrollToEnd();
            }
            else
            {
                _view!.ScrollToStart();
            }
        }

        private void SyncContent()
        {
            var view = _view!;

            if (Framework.ConsoleSession.OnConsoleTab)
            {
                var output = Framework.ConsoleFeature.Output;
                var sequence = output.LastSequence;
                var appended = 0;
                if (sequence != _lastSequence)
                {
                    appended = _lastSequence < 0 ? 0 : (int)Math.Min(int.MaxValue, sequence - _lastSequence);
                    _scrollback = output.Snapshot();
                    _lastSequence = sequence;
                }

                view.Render(_scrollback.Count, appended, _contentVersion, _scrollbackLine);
                return;
            }

            var changed = false;
            if (Framework.ConsoleSession.OnPanelTab)
            {
                // A registered panel renders live; a few times a second is live enough to read and
                // keeps a busy panel from rebuilding the text mesh every frame.
                var now = Time.unscaledTime;
                if (now >= _nextPanelRender)
                {
                    _nextPanelRender = now + PanelRefreshSeconds;
                    changed = SetViewSource(Framework.ConsoleSession.ViewText());
                }
            }
            else if (Framework.ConsoleSession.ViewVersion != _sessionViewVersion)
            {
                _sessionViewVersion = Framework.ConsoleSession.ViewVersion;
                changed = SetViewSource(Framework.ConsoleSession.ViewText());
            }

            if (changed)
            {
                _contentVersion++;
            }

            view.Render(_viewLines.Length, 0, _contentVersion, _viewLine);
        }

        /// <summary>
        /// Puts the log area between the tab strip and the bottom row (<see cref="ConsoleLayout"/>).
        /// Only touches the RectTransform when the answer changes.
        /// </summary>
        /// <param name="bottomRow">True when the tab showing has a bottom row.</param>
        private void PlaceLogArea(bool bottomRow)
        {
            if (_logAreaHasRow == bottomRow)
            {
                return;
            }

            _logAreaHasRow = bottomRow;
            ConsoleLayout.LogAreaInsets(bottomRow, out var top, out var bottom);
            GameStyleUi.Stretch(_view!.Rect, 0f, top, 0f, bottom);
        }

        private bool SetViewSource(string? text)
        {
            var source = text ?? string.Empty;
            if (string.Equals(source, _viewSource, StringComparison.Ordinal) && _viewLines.Length > 0)
            {
                return false;
            }

            _viewSource = source;
            _viewLines = source.Replace("\r", string.Empty).Split('\n');
            return true;
        }

        private void SyncStatus(GameStyleWindow window)
        {
            var sequence = Framework.ConsoleFeature.Output.LastSequence;
            var commands = Framework.ConsoleFeature.Commands.Count;
            if (sequence == _statusSequence && commands == _statusCommands)
            {
                return;
            }

            _statusSequence = sequence;
            _statusCommands = commands;
            window.Status = Framework.ConsoleSession.StatusLine()
                + (_chords.Length > 0 ? "  " + _chords : string.Empty);
        }

        private void KeepFocus()
        {
            var line = _input!;
            if (!Framework.ConsoleSession.OnConsoleTab || (line.IsSelected && line.IsFocused))
            {
                return;
            }

            var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;

            // The player is typing into one of the game's fields: leave the keyboard with them.
            if (selected != null && !line.IsSelected && IsTextEntry(selected))
            {
                return;
            }

            line.Focus();
        }

        private void Run(string? line)
        {
            _input!.SetText(string.Empty);
            _view!.ScrollToEnd();
            Framework.ConsoleSession.Submit(line);
        }

        private void RunAction(int index)
        {
            if (index >= 0 && index < _actions.Count)
            {
                Framework.ConsoleSession.RunTabAction(_actions[index]);
            }
        }

        private string ScrollbackLine(int index)
        {
            if (index < 0 || index >= _scrollback.Count)
            {
                return string.Empty;
            }

            var line = _scrollback[index];
            return ConsoleLineColors.ToRichText(line.Kind, line.Text);
        }

        private string ViewLine(int index)
        {
            if (index < 0 || index >= _viewLines.Length)
            {
                return string.Empty;
            }

            // A view line that is a console record gets the scrollback's colours; plain view text
            // (tables, reports) is drawn as command output.
            var text = _viewLines[index];
            var kind = ConsoleLineColors.TrySplit(text, out _, out _, out _) ? ConsoleLineKind.Info : ConsoleLineKind.Output;
            return ConsoleLineColors.ToRichText(kind, text);
        }

        /// <summary>
        /// True when the selected object is a text field. The two field types Unity has (the game
        /// uses the TextMeshPro one); GetComponent rather than the registry's type-name scan
        /// because this runs every frame while a game field has the keyboard.
        /// </summary>
        private static bool IsTextEntry(GameObject selected) =>
            selected.GetComponent<TMP_InputField>() != null
            || selected.GetComponent<UnityEngine.UI.InputField>() != null;
    }
}
