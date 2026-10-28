using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Events;
using Framework = CwyLs.ModLoader.Console;
using GameEvents = CwyLs.ModLoader.Bulbul.Events.CWYEvents;
using Panels = CwyLs.ModLoader.Core.Console.ConsolePanels;
using Seam = CwyLs.ModLoader.Bulbul.VoiceSeam;
using Tex = CwyLs.ModLoader.Bulbul.Textures.TextureFeature;
using Voices = CwyLs.ModLoader.Bulbul.VoiceFeature;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The game half of the in-game console (P2-5): the commands and tabs that only make
    /// sense for this game - Satone, voices, textures, player data, master data and the event
    /// stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// They live here rather than in the framework because <c>CwyLs.ModLoader</c> may not know a
    /// single game type. The framework offers two extension points - a command
    /// registry and a panel registry - and this class is the adapter's use of them.
    /// </para>
    /// <para>
    /// Call <see cref="Register"/> once from the adapter plugin's Awake, after the features it
    /// drives have been initialised. Everything is aliased at the top of this file on purpose:
    /// our namespace ends in <c>Bulbul</c> and the game's is <c>Bulbul</c>, and a method called
    /// <c>Events</c> would otherwise hide the namespace of the same name.
    /// </para>
    /// </remarks>
    public static class BulbulConsoleCommands
    {
        /// <summary>Group these commands are listed under by <c>help</c>.</summary>
        public const string Group = "game";

        /// <summary>Name of the tab showing the recent event stream.</summary>
        public const string StreamPanel = "Stream";

        /// <summary>Name of the tab showing the current game state.</summary>
        public const string StatePanel = "State";

        /// <summary>How many commands this class registers.</summary>
        public const int CommandCount = 6;

        /// <summary>Events the Stream tab shows.</summary>
        public const int StreamLines = 60;

        /// <summary>Events <c>events last</c> shows when no count is given.</summary>
        public const int DefaultEventLines = 20;

        private static readonly object Gate = new object();
        private static readonly EventRing Ring = new EventRing();

        private static IDisposable? _subscription;
        private static bool _registered;

        private static SatoneStateChanged? _lastState;
        private static MusicChanged? _lastMusic;
        private static PomodoroEvent? _lastPomodoro;
        private static WindowChanged? _lastWindow;
        private static DateTime _lastStateAt;

        /// <summary>True once <see cref="Register"/> has run.</summary>
        public static bool IsRegistered
        {
            get { lock (Gate) { return _registered; } }
        }

        /// <summary>How many events the ring is holding.</summary>
        public static int HeldEvents => Ring.Count;

        /// <summary>
        /// Registers the game commands and the two game tabs, and starts recording the event
        /// stream. Idempotent: calling it twice subscribes once.
        /// </summary>
        public static void Register()
        {
            lock (Gate)
            {
                if (_registered)
                {
                    return;
                }

                _registered = true;
            }

            Subscribe();

            Framework.ConsoleFeature.Register(
                "voice", "Lists the registered voices, or plays one.",
                "voice list|play <name>|satone <name>", Group, VoiceCommand);

            Framework.ConsoleFeature.Register(
                "tex", "Applies, restores, reloads or lists the pack textures.",
                "tex apply|restore|reload|list|scan [pattern]|export [pattern|all]", Group, TexCommand);

            Framework.ConsoleFeature.Register(
                "events", "The most recent game events.", "events last [n]", Group, EventsCommand);

            Framework.ConsoleFeature.Register(
                "data", "Reads the player's own data through the game data bus.",
                "data snapshot|todos|notes|habits|pomodoro [--full]", Group, DataCommands.Run);

            Framework.ConsoleFeature.Register(
                "satone", "Drives Satone: state, expression, motion, look, speech.",
                "satone state|face <name>|motion <name>|look <scale> <sec>|say <text>",
                Group, SatoneCommands.Satone);

            // B-WORDING: the 'subtitle <text> [lang]' command is retired. It called Subtitles.Show,
            // which has drawn nothing since the overlay panel went (Phase B1), so every use printed
            // a refusal. 'satone say <text>' puts a line in the game's own text box.

            Framework.ConsoleFeature.Register(
                "masterdata", "What the master-data loader merged and injected.",
                "masterdata status", Group, SatoneCommands.MasterData);

            Panels.Register(StreamPanel, () => Ring.Format(StreamLines));
            Panels.Register(StatePanel, State);

            // B-UI: the console in the game's own window style. Built on first open; the framework
            // keeps its IMGUI overlay as the fallback when the game's UI assets are not loaded.
            Framework.ConsoleFeature.RegisterSurface(GameStyleConsoleSurface.Instance);
            GameStyleConsoleSmokeCheck.Register();

            // C5: Escape that closes the console must not also take a fullscreen game to windowed.
            ConsoleEscapePatch.Apply();

            CwyLsModLoader.Log.LogDebug(
                "CONSOLE: " + CommandCount + " game command(s), the "
                + StreamPanel + "/" + StatePanel + " tabs and the "
                + GameStyleConsoleSurface.SurfaceName + " surface registered");
        }

        private static void Subscribe()
        {
            try
            {
                _subscription = GameEvents.SubscribeAny(OnAnyEvent);
            }
            catch (Exception ex)
            {
                // Without the stream the commands still work; the Stream tab would just stay empty,
                // which is worth one line rather than a silently blank tab.
                CwyLsModLoader.Log.LogWarning(
                    $"CONSOLE could not subscribe to the event bus, so the {StreamPanel} tab stays empty: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnAnyEvent(AnyEvent evt)
        {
            Ring.Add(evt);

            switch (evt.Args)
            {
                case SatoneStateChanged state:
                    _lastState = state;
                    _lastStateAt = DateTime.Now;
                    break;
                case MusicChanged music:
                    _lastMusic = music;
                    break;
                case PomodoroEvent pomodoro:
                    _lastPomodoro = pomodoro;
                    break;
                case WindowChanged window:
                    _lastWindow = window;
                    break;
            }
        }

        private static string State()
        {
            var sb = new StringBuilder("STATE");

            sb.Append("\n  satone   ").Append(
                _lastState is null
                    ? "not seen yet"
                    : _lastState.Next + " (from " + _lastState.Previous + ", "
                      + _lastStateAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ")");

            sb.Append("\n  music    ").Append(_lastMusic is null ? "not seen yet" : _lastMusic.Describe());
            sb.Append("\n  pomodoro ").Append(_lastPomodoro is null ? "not seen yet" : _lastPomodoro.Describe());
            sb.Append("\n  window   ").Append(_lastWindow is null ? "not seen yet" : _lastWindow.Describe());

            var line = GameEvents.LastLine;
            sb.Append("\n  line     ").Append(line is null ? "nothing spoken yet" : line.Describe());

            sb.Append("\n  bus      ").Append(GameEvents.IsReady ? "ready" : "not ready")
                .Append(", ").Append(GameEvents.TotalPublished).Append(" published, ")
                .Append(Ring.Count).Append(" held here, stream ")
                .Append(_subscription is null ? "not subscribed" : "subscribed");

            sb.Append("\n  data     ").Append(DataCommands.ReadyLine());
            sb.Append("\n  voices   ").Append(Voices.Count).Append(" registered, patch ")
                .Append(Voices.PatchApplied ? "applied" : "not applied");
            sb.Append("\n  textures ").Append(Tex.AppliedRules.Count)
                .Append(" applied, ").Append(Tex.FailedCount).Append(" failed");

            return sb.ToString();
        }

        private static string VoiceCommand(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "list";
            if (string.Equals(what, "scan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(what, "export", StringComparison.OrdinalIgnoreCase))
            {
                var rest = new List<string>();
                for (var i = 1; i < args.Count; i++) { rest.Add(args[i]); }
                return string.Equals(what, "scan", StringComparison.OrdinalIgnoreCase)
                    ? global::CwyLs.ModLoader.Bulbul.Textures.SceneryFeature.Scan(rest)
                    : global::CwyLs.ModLoader.Bulbul.Textures.SceneryFeature.Export(rest);
            }


            if (string.Equals(what, "list", StringComparison.OrdinalIgnoreCase))
            {
                var entries = Voices.Snapshot();
                var sb = new StringBuilder("VOICES: ").Append(entries.Count).Append(" registered");
                if (Voices.LastSummary.Length > 0)
                {
                    sb.Append("\n  ").Append(Voices.LastSummary);
                }

                sb.Append("\n  ").Append(CwyLs.ModLoader.Bulbul.LanguageVoices.Describe());

                foreach (var entry in entries)
                {
                    sb.Append("\n  ").Append(entry.ToString());
                }

                return sb.ToString();
            }

            if (args.Count < 2)
            {
                return "voice list|play <name>|satone <name>";
            }

            var name = args[1];

            if (string.Equals(what, "play", StringComparison.OrdinalIgnoreCase))
            {
                // Straight through the game's VoiceManager: the clip plays, but Satone's mouth does
                // not move. 'voice satone' is the one that looks like a line she speaks.
                var player = Seam.TryPlay(name, out var error);
                return player != null
                    ? "playing '" + name + "' through VoiceManager"
                    : "could not play '" + name + "': " + error;
            }

            if (string.Equals(what, "satone", StringComparison.OrdinalIgnoreCase))
            {
                return Voices.PlayAsSatone(name)
                    ? "Satone is saying '" + name + "'"
                    : "could not reach HeroineService (is RoomScene up?); see the log";
            }

            return "voice list|play <name>|satone <name>";
        }

        private static string TexCommand(IReadOnlyList<string> args)
        {
            var what = args.Count > 0 ? args[0] : "list";

            if (string.Equals(what, "apply", StringComparison.OrdinalIgnoreCase))
            {
                Tex.Apply();
                return Tex.LastSummary.Length > 0 ? Tex.LastSummary : "apply pass finished";
            }

            if (string.Equals(what, "restore", StringComparison.OrdinalIgnoreCase))
            {
                return "restored " + Tex.RestoreAll() + " texture slot(s) to the game's own";
            }

            if (string.Equals(what, "reload", StringComparison.OrdinalIgnoreCase))
            {
                var reloaded = Tex.ReloadAll();
                return Tex.LastReloadSummary.Length > 0
                    ? Tex.LastReloadSummary
                    : "reloaded " + reloaded + " file(s)";
            }

            if (!string.Equals(what, "list", StringComparison.OrdinalIgnoreCase))
            {
                return "tex apply|restore|reload|list|scan [pattern]|export [pattern|all]";
            }

            var applied = Tex.AppliedRules;
            var rules = Tex.Rules;
            var text = new StringBuilder("TEXTURES: ").Append(applied.Count).Append(" applied, ")
                .Append(Tex.FailedCount).Append(" failed, ")
                .Append(rules.Count).Append(" rule(s) from packs");

            if (Tex.LastSummary.Length > 0)
            {
                text.Append("\n  ").Append(Tex.LastSummary);
            }

            foreach (var rule in applied)
            {
                text.Append("\n  ").Append(rule.ToString());
            }

            foreach (var problem in rules.Problems)
            {
                text.Append("\n  ! ").Append(problem.ToString());
            }

            return text.ToString();
        }

        private static string EventsCommand(IReadOnlyList<string> args)
        {
            var count = DefaultEventLines;

            if (args.Count > 0 && !string.Equals(args[0], "last", StringComparison.OrdinalIgnoreCase))
            {
                return "events last [n]";
            }

            if (args.Count > 1)
            {
                if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                    || count <= 0)
                {
                    return "'" + args[1] + "' is not a line count";
                }

                count = Math.Min(count, EventRing.Capacity);
            }

            return Ring.Format(count);
        }
    }
}
