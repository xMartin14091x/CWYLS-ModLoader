using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using UnityEngine;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The IMGUI overlay: a draggable IMGUI window with a command line and a tab per view. It is
    /// the fallback surface (<see cref="ImguiConsoleSurface"/>); the game adapter
    /// registers a window in the game's own style, which is used whenever it can be built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn from <see cref="ConsoleHost"/>'s <c>OnGUI</c>. It needs no canvas, no prefab and no
    /// font asset from the game, so it works before <c>RoomScene</c> exists and after a game update
    /// that renamed the game's UI assets. Tabs, views, completion and history come from
    /// <see cref="ConsoleSession"/>, shared with every other surface.
    /// </para>
    /// <para>
    /// <b>Keyboard capture is partial, and this is the limit of what IMGUI can do.</b> While the
    /// Console tab is open the input field is focused every frame and the keys the console uses
    /// (Return, Tab, Up, Down, Escape, PageUp/PageDown) are consumed with <c>Event.Use()</c>, so
    /// IMGUI hands them to nobody else. A game that reads <c>UnityEngine.Input</c> directly is
    /// polling the device, not the event stream, and cannot be blocked from here at all - typing
    /// into the console can still trigger such a key. The framework's own hotkeys are safe because
    /// every one of them needs a modifier or an F-key.
    /// </para>
    /// <para>
    /// One such key is handled anyway: the game switches fullscreen to windowed on Escape. The
    /// overlay reports that it owns Escape every frame it is drawn and claims the Escape it closes
    /// on (<see cref="ConsoleFeature.EscapeGuard"/>), and the game adapter skips the switch.
    /// <c>OnGUI</c> runs after every <c>Update</c>, so it is the per-frame report that covers the
    /// game's read in the same frame.
    /// </para>
    /// </remarks>
    internal static class ConsoleWindow
    {
        /// <summary>IMGUI window id. Arbitrary but must not collide with another mod's window.</summary>
        private const int WindowId = 0x43_57_59_05;

        /// <summary>Name the input control is focused by.</summary>
        private const string InputControlName = "cwy.console.input";

        /// <summary>Lines of the scrollback drawn at once.</summary>
        private const int VisibleLines = ConsoleBuffer.DefaultCapacity;

        private static Rect _rect;
        private static bool _placed;
        private static string _input = string.Empty;
        private static Vector2 _outputScroll;
        private static Vector2 _viewScroll;
        private static long _lastSequence = -1;
        private static bool _scrollToBottom;
        private static bool _takeFocus;

        /// <summary>Prepares the window for being shown: focus the input, refresh the tab.</summary>
        internal static void OnOpened()
        {
            _scrollToBottom = true;
            _takeFocus = true;
            ConsoleSession.OnOpened();
        }

        /// <summary>Draws the overlay. Called from <c>OnGUI</c>; assumes the console is open.</summary>
        internal static void Draw()
        {
            // IMGUI closes on any Escape that reaches the window, so while it is drawn the console
            // owns Escape; the game's Escape read in the next frame is judged by this report.
            ConsoleFeature.EscapeGuard.Observe(Time.frameCount, EscapeOwner.Console);

            ConsoleStyles.Ensure();

            var width = Mathf.Clamp(Screen.width * 0.70f, 480f, Mathf.Max(480f, Screen.width - 24f));
            var height = Mathf.Clamp(Screen.height * 0.62f, 300f, Mathf.Max(300f, Screen.height - 24f));

            if (!_placed)
            {
                _rect = new Rect(16f, 16f, width, height);
                _placed = true;
            }
            else
            {
                // The size follows the window; the position is the user's, only clamped back on
                // screen so a resize cannot strand the overlay outside it.
                _rect.width = width;
                _rect.height = height;
                _rect.x = Mathf.Clamp(_rect.x, 8f - width * 0.5f, Screen.width - 80f);
                _rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 40f));
            }

            // Below everything else IMGUI draws means on top of it: GUI.depth sorts descending.
            GUI.depth = -2000;
            _rect = GUI.Window(WindowId, _rect, DrawWindow, GUIContent.none, GUIStyle.none);
            GUI.BringWindowToFront(WindowId);

            // Once, on opening: without keyboard focus on the window itself, a tab with no input
            // field (Packs, Smoke, ...) would never see the Escape that closes the overlay. Doing
            // it every frame would instead hold focus away from any other mod's IMGUI window.
            if (_takeFocus)
            {
                _takeFocus = false;
                GUI.FocusWindow(WindowId);
            }
        }

        private static void DrawWindow(int id)
        {
            // Before any widget: a consumed key never reaches the text field, which is what makes
            // Tab complete instead of moving IMGUI's focus.
            HandleKeys();

            var background = new Rect(0f, 0f, _rect.width, _rect.height);
            Fill(background, ConsoleStyles.WindowColor);

            const float pad = 8f;
            GUILayout.BeginArea(new Rect(pad, pad, _rect.width - pad * 2f, _rect.height - pad * 2f));

            DrawTitleRow();
            DrawTabStrip();

            if (ConsoleSession.OnConsoleTab)
            {
                DrawConsoleTab();
            }
            else
            {
                DrawViewTab();
            }

            GUILayout.EndArea();

            // Only the title row drags, so a click in the output or the input line is not a drag.
            GUI.DragWindow(new Rect(0f, 0f, _rect.width, ConsoleStyles.TitleBarHeight));
        }

        private static void DrawTitleRow()
        {
            GUILayout.BeginHorizontal(GUILayout.Height(ConsoleStyles.TitleBarHeight));
            GUILayout.Label("CWY console", ConsoleStyles.Title);
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                ConsoleSession.StatusLine() + "  " + ConsoleFeature.HotkeyId + " toggles",
                ConsoleStyles.Status);

            if (GUILayout.Button("close", ConsoleStyles.Button))
            {
                ConsoleFeature.Close();
            }

            GUILayout.EndHorizontal();
        }

        private static void DrawTabStrip()
        {
            var tabs = Tabs();
            var current = Mathf.Clamp(ConsoleSession.Tab, 0, tabs.Length - 1);
            var chosen = GUILayout.Toolbar(current, tabs, ConsoleStyles.Tab);
            if (chosen == current)
            {
                return;
            }

            ConsoleSession.SelectTab(chosen);
            _viewScroll = Vector2.zero;
        }

        private static void DrawConsoleTab()
        {
            var lines = ConsoleFeature.Output.Tail(VisibleLines);

            _outputScroll = GUILayout.BeginScrollView(
                _outputScroll, false, true, GUILayout.ExpandHeight(true));

            foreach (var line in lines)
            {
                GUILayout.Label(line.Text, ConsoleStyles.For(line.Kind));
            }

            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label(">", ConsoleStyles.For(ConsoleLineKind.Echo), GUILayout.ExpandWidth(false));
            GUI.SetNextControlName(InputControlName);
            _input = GUILayout.TextField(_input, ConsoleStyles.Input, GUILayout.ExpandWidth(true));

            if (GUILayout.Button("run", ConsoleStyles.Button, GUILayout.ExpandWidth(false)))
            {
                Submit();
            }

            GUILayout.EndHorizontal();

            // Every frame, not once: this is what keeps typed keys inside IMGUI instead of
            // reaching the game's own UI (see the limits on this class).
            GUI.FocusControl(InputControlName);

            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var sequence = ConsoleFeature.Output.LastSequence;
            if (_scrollToBottom || sequence != _lastSequence)
            {
                _lastSequence = sequence;
                _scrollToBottom = false;
                _outputScroll.y = float.MaxValue;
            }
        }

        private static void DrawViewTab()
        {
            GUILayout.BeginHorizontal();

            foreach (var action in ConsoleSession.TabActions())
            {
                if (GUILayout.Button(action, ConsoleStyles.Button, GUILayout.ExpandWidth(false)))
                {
                    ConsoleSession.RunTabAction(action);
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(ConsoleSession.OnPanelTab ? "panel, live" : "snapshot", ConsoleStyles.Status);
            GUILayout.EndHorizontal();

            // A panel belongs to whoever registered it and is expected to be cheap, so it renders
            // live; the framework's own views are snapshots taken on open or on refresh.
            var text = ConsoleSession.ViewText();

            _viewScroll = GUILayout.BeginScrollView(_viewScroll, false, true, GUILayout.ExpandHeight(true));
            GUILayout.Label(text, ConsoleStyles.For(ConsoleLineKind.Output));
            GUILayout.EndScrollView();
        }

        private static void HandleKeys()
        {
            var e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
            {
                return;
            }

            if (e.keyCode == KeyCode.Escape)
            {
                ConsoleFeature.EscapeGuard.Claim(Time.frameCount);
                ConsoleFeature.Close();
                e.Use();
                return;
            }

            if (!ConsoleSession.OnConsoleTab)
            {
                return;
            }

            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Submit();
                    e.Use();
                    return;

                case KeyCode.Tab:
                    CompleteInput();
                    e.Use();
                    return;

                case KeyCode.UpArrow:
                    var previous = ConsoleSession.HistoryPrevious(_input);
                    if (previous != null)
                    {
                        _input = previous;
                    }

                    e.Use();
                    return;

                case KeyCode.DownArrow:
                    var next = ConsoleSession.HistoryNext();
                    if (next != null)
                    {
                        _input = next;
                    }

                    e.Use();
                    return;

                case KeyCode.PageUp:
                    _outputScroll.y = Mathf.Max(0f, _outputScroll.y - _rect.height * 0.5f);
                    e.Use();
                    return;

                case KeyCode.PageDown:
                    _outputScroll.y += _rect.height * 0.5f;
                    e.Use();
                    return;
            }

            // Unity sends the printable character as a second event with no key code. Tab and the
            // newlines would otherwise still reach the text field after the event above was used.
            if (e.keyCode == KeyCode.None
                && (e.character == '\t' || e.character == '\n' || e.character == '\r'))
            {
                e.Use();
            }
        }

        private static void Submit()
        {
            var line = _input;
            _input = string.Empty;
            _scrollToBottom = true;

            // Blank lines are ignored by the session; a command may have added a panel or
            // changed what a tab would say, which the session also takes care of.
            ConsoleSession.Submit(line);
        }

        private static void CompleteInput()
        {
            var before = ConsoleFeature.Output.LastSequence;
            _input = ConsoleSession.Complete(_input);

            // More than one candidate: the session listed them in the scrollback.
            if (ConsoleFeature.Output.LastSequence != before)
            {
                _scrollToBottom = true;
            }
        }

        private static string[] Tabs()
        {
            var tabs = ConsoleSession.Tabs();
            if (tabs is string[] array)
            {
                return array;
            }

            var copy = new string[tabs.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = tabs[i];
            }

            return copy;
        }

        private static void Fill(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, ConsoleStyles.White);
            GUI.color = previous;
        }
    }
}
