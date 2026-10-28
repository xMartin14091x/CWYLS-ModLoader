using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// Everything the console does that is not drawing: which tabs exist and which is showing, the
    /// text behind each tab, the buttons a tab offers, running a typed line, Tab completion and the
    /// history walk. Every <see cref="IConsoleSurface"/> drives the same session, so the IMGUI
    /// overlay and the game-style window behave identically and a fallback mid-session keeps the
    /// selected tab.
    /// </summary>
    /// <remarks>Main thread only, like the surfaces that call it.</remarks>
    public static class ConsoleSession
    {
        /// <summary>Index of the Console tab, the only one with a command line.</summary>
        public const int ConsoleTabIndex = 0;

        /// <summary>Button on every view tab: take a fresh snapshot.</summary>
        public const string RefreshAction = "refresh";

        /// <summary>Packs tab button: scan the pack folder again.</summary>
        public const string RescanAction = "rescan";

        /// <summary>Smoke tab button: run the checks quietly.</summary>
        public const string RunAction = "run";

        /// <summary>Targets tab button: resolve every patch target again.</summary>
        public const string VerifyAction = "verify";

        private static readonly string[] BuiltInTabs = { "Console", "Packs", "Smoke", "Targets", "Keys" };
        private static readonly string[] RefreshOnly = { RefreshAction };
        private static readonly string[] PacksActions = { RefreshAction, RescanAction };
        private static readonly string[] SmokeActions = { RefreshAction, RunAction };
        private static readonly string[] TargetsActions = { RefreshAction, VerifyAction };

        private static string[] _tabs = BuiltInTabs;
        private static int _tabsBuiltFor = -1;
        private static int _tab;
        private static string _viewText = string.Empty;
        private static long _viewVersion;

        /// <summary>The tab showing now; 0 is the Console tab.</summary>
        public static int Tab => _tab;

        /// <summary>True while the Console tab (the one with the command line) is showing.</summary>
        public static bool OnConsoleTab => _tab == ConsoleTabIndex;

        /// <summary>The name of the tab showing now.</summary>
        public static string TabName
        {
            get
            {
                var tabs = Tabs();
                return _tab >= 0 && _tab < tabs.Count ? tabs[_tab] : string.Empty;
            }
        }

        /// <summary>
        /// True when the tab showing now is a panel a mod registered (<see cref="ConsolePanels"/>):
        /// rendered live rather than from a snapshot.
        /// </summary>
        public static bool OnPanelTab => _tab >= BuiltInTabs.Length;

        /// <summary>Grows by one every time the snapshot behind a view tab is retaken.</summary>
        public static long ViewVersion => _viewVersion;

        /// <summary>
        /// The tab labels: the framework's five, then every registered panel in registration order.
        /// Cached until the panel count changes.
        /// </summary>
        public static IReadOnlyList<string> Tabs()
        {
            var panels = ConsolePanels.Names;
            if (_tabsBuiltFor == panels.Count)
            {
                return _tabs;
            }

            var tabs = new string[BuiltInTabs.Length + panels.Count];
            Array.Copy(BuiltInTabs, tabs, BuiltInTabs.Length);
            for (var i = 0; i < panels.Count; i++)
            {
                tabs[BuiltInTabs.Length + i] = panels[i];
            }

            _tabs = tabs;
            _tabsBuiltFor = panels.Count;
            if (_tab >= tabs.Length)
            {
                _tab = ConsoleTabIndex;
            }

            return _tabs;
        }

        /// <summary>Shows another tab and takes its snapshot.</summary>
        /// <param name="index">The tab; out-of-range values are clamped.</param>
        public static void SelectTab(int index)
        {
            var tabs = Tabs();
            var chosen = Math.Max(0, Math.Min(index, tabs.Count - 1));
            if (chosen == _tab)
            {
                return;
            }

            _tab = chosen;
            RefreshView();
        }

        /// <summary>Called by a surface when the console opens: the view tabs are snapshots, so retake them.</summary>
        public static void OnOpened() => RefreshView();

        /// <summary>
        /// The text of the tab showing now (not the Console tab, whose text is the scrollback).
        /// A registered panel is rendered on every call; a built-in view returns its snapshot.
        /// </summary>
        public static string ViewText()
        {
            if (OnPanelTab)
            {
                ConsolePanels.TryRender(TabName, out var live);
                return live;
            }

            return _viewText;
        }

        /// <summary>Retakes the snapshot behind the tab showing now.</summary>
        public static void RefreshView()
        {
            switch (TabName)
            {
                case "Packs":
                    SetView(ConsoleViews.Packs());
                    break;
                case "Smoke":
                    SetView(ConsoleViews.Smoke());
                    break;
                case "Targets":
                    SetView(ConsoleViews.Targets(false));
                    break;
                case "Keys":
                    SetView(ConsoleViews.Keys());
                    break;
                case "Console":
                    break;
                default:
                    ConsolePanels.TryRender(TabName, out var text);
                    SetView(text);
                    break;
            }
        }

        /// <summary>The buttons the tab showing now offers, left to right. Empty on the Console tab.</summary>
        public static IReadOnlyList<string> TabActions()
        {
            switch (TabName)
            {
                case "Console":
                    return Array.Empty<string>();
                case "Packs":
                    return PacksActions;
                case "Smoke":
                    return SmokeActions;
                case "Targets":
                    return TargetsActions;
                default:
                    return RefreshOnly;
            }
        }

        /// <summary>Runs one of <see cref="TabActions"/>. Unknown names do nothing.</summary>
        /// <param name="action">The button's name.</param>
        public static void RunTabAction(string? action)
        {
            switch (action)
            {
                case RefreshAction:
                    RefreshView();
                    break;
                case RescanAction:
                    ConsoleFeature.Submit("packs rescan");
                    RefreshView();
                    break;
                case RunAction:
                    ConsoleFeature.Submit("smoke run");
                    RefreshView();
                    break;
                case VerifyAction:
                    SetView(ConsoleViews.Targets(true));
                    break;
            }
        }

        /// <summary>
        /// Runs a typed line: echo, execute, output, history (<see cref="ConsoleFeature.Submit"/>),
        /// then retakes the view, since a command may have changed what a tab would say.
        /// </summary>
        /// <param name="line">The line as typed; blank lines do nothing.</param>
        public static void Submit(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            ConsoleFeature.Submit(line);
            RefreshView();
        }

        /// <summary>
        /// Tab completion: completes the word at the end of the line (command names first, then the
        /// literal words of that command's usage) and lists the candidates in the scrollback when
        /// there is more than one.
        /// </summary>
        /// <param name="input">The line as typed so far.</param>
        /// <returns>The line to put back in the input.</returns>
        public static string Complete(string? input)
        {
            var line = input ?? string.Empty;
            var caretWord = LastWord(line, out var head);
            var candidates = head.Length == 0
                ? ConsoleFeature.CommandNames
                : ArgumentHints(head);

            var result = Completion.Complete(caretWord, candidates);
            if (result.IsEmpty)
            {
                return line;
            }

            if (result.Matches.Count > 1)
            {
                ConsoleFeature.Write(
                    string.Join("  ", new List<string>(result.Matches).ToArray()),
                    ConsoleLineKind.Info);
            }

            return !result.IsFuzzy || result.IsUnique
                ? head + result.Text + (result.IsUnique ? " " : string.Empty)
                : line;
        }

        /// <summary>The previous history entry, or null at the oldest.</summary>
        /// <param name="current">The half-typed line, remembered for the way back down.</param>
        public static string? HistoryPrevious(string? current) => ConsoleFeature.History.Previous(current);

        /// <summary>The next history entry, the half-typed line past the newest, or null.</summary>
        public static string? HistoryNext() => ConsoleFeature.History.Next();

        /// <summary>The short status for a title bar: commands, lines held, the toggle.</summary>
        public static string StatusLine() =>
            ConsoleFeature.Commands.Count + " cmds  "
            + ConsoleFeature.Output.Count + "/" + ConsoleFeature.Output.Capacity + " lines";

        private static void SetView(string? text)
        {
            _viewText = text ?? string.Empty;
            _viewVersion++;
        }

        /// <summary>
        /// The word Tab is completing and everything before it, so the rest of the line survives.
        /// </summary>
        private static string LastWord(string line, out string head)
        {
            if (string.IsNullOrEmpty(line))
            {
                head = string.Empty;
                return string.Empty;
            }

            var at = line.LastIndexOf(' ');
            if (at < 0)
            {
                head = string.Empty;
                return line;
            }

            head = line.Substring(0, at + 1);
            return line.Substring(at + 1);
        }

        /// <summary>What the command being typed allows as its next word, taken from its own usage line.</summary>
        private static IReadOnlyList<string> ArgumentHints(string head)
        {
            var name = CommandLineParser.Parse(head)?.Name;
            return ConsoleFeature.Commands.TryGet(name, out var command) && command != null
                ? UsageHints.Parse(command.Usage)
                : Array.Empty<string>();
        }
    }
}
