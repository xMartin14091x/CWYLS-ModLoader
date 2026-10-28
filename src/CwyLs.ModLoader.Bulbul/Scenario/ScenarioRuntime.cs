using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Threading;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>How script-command rows reach their handlers.</summary>
    internal enum ScenarioRuntimeMode
    {
        /// <summary>They do not: the feature is off or both seams are missing.</summary>
        Off = 0,

        /// <summary>The prefix on <c>ScenarioReader.ReadSentence</c> runs them in place (the normal path).</summary>
        Seam = 1,

        /// <summary>
        /// The seam is missing: script rows become the game's <c>ReadNextForScript</c> (command 50) and
        /// a per-frame poll runs them and resumes the reader. <c>JumpTo</c> is not available.
        /// </summary>
        Fallback = 2,
    }

    /// <summary>
    /// The game-side half of the script runtime: runs a script-command row when the reader
    /// reaches it, applies the handler's decision through the reader's own <c>ReadNext</c> /
    /// <c>SelectionJump</c>, holds rows that wait, and releases deferred actions when the room is quiet.
    /// Every member runs on the game's main thread and never throws into the game.
    /// </summary>
    internal static class ScenarioRuntime
    {
        /// <summary>The main-thread owner of this feature's work.</summary>
        internal const string Owner = "script";

        /// <summary>Synchronous row-to-row hops before the next one is posted to the next frame (a cwy row looping on itself must not overflow the stack).</summary>
        internal const int MaxInlineDepth = 24;

        /// <summary>How long a <c>cwy.do</c> holds the scene for its action's answer.</summary>
        internal static readonly TimeSpan DoTimeout = TimeSpan.FromSeconds(30);

        private const double RoomProbeSeconds = 0.1;
        private const double FallbackProbeSeconds = 0.25;
        private const int MaxConsoleWarnings = 64;

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static AccessTools.FieldRef<Game.ScenarioReader, List<Master.NovelData>>? _groupNovels;
        private static AccessTools.FieldRef<Game.ScenarioReader, int>? _messageIndex;
        private static AccessTools.FieldRef<Game.ScenarioReader, Master.NovelData>? _currentNovel;
        private static Action<Game.ScenarioReader>? _readNext;
        private static Action<Game.ScenarioReader, Master.NovelData>? _selectionJump;

        private static Dictionary<Master.NovelData, int> _fallbackRows = new Dictionary<Master.NovelData, int>();
        private static Master.NovelData? _fallbackHandled;
        private static double _nextFallbackProbe;
        private static double _nextRoomProbe;
        private static int _depth;
        private static IDisposable? _ticker;

        /// <summary>How rows run now.</summary>
        internal static ScenarioRuntimeMode Mode { get; private set; }

        /// <summary>Rows holding on a check.</summary>
        internal static ScenarioWaitBook Waits { get; } = new ScenarioWaitBook();

        /// <summary>Actions waiting for the scene to end and the room to be quiet.</summary>
        internal static DeferredActionQueue Defers { get; } = new DeferredActionQueue();

        /// <summary>Script-command rows run this session.</summary>
        internal static long RowsRun { get; private set; }

        /// <summary>Rows converted to command 50 by the fallback (0 in seam mode).</summary>
        internal static int FallbackRowCount => _fallbackRows.Count;

        /// <summary>The last row run, for <c>scene list</c> and the smoke detail.</summary>
        internal static string LastRow { get; private set; } = "none";

        /// <summary>Resolves the reader members the seam needs. False leaves the seam unusable (the fallback may still work).</summary>
        /// <param name="problem">Why not.</param>
        internal static bool PrepareSeam(out string problem)
        {
            try
            {
                _groupNovels = AccessTools.FieldRefAccess<Game.ScenarioReader, List<Master.NovelData>>(ScenarioPatchTargets.GroupNovelsField);
                _messageIndex = AccessTools.FieldRefAccess<Game.ScenarioReader, int>(ScenarioPatchTargets.MessageIndexField);
                _currentNovel = AccessTools.FieldRefAccess<Game.ScenarioReader, Master.NovelData>(ScenarioPatchTargets.CurrentNovelField);
                _readNext = Delegate<Action<Game.ScenarioReader>>("ReadNext", Type.EmptyTypes);
                _selectionJump = Delegate<Action<Game.ScenarioReader, Master.NovelData>>("SelectionJump", new[] { typeof(Master.NovelData) });
            }
            catch (Exception ex)
            {
                problem = "the reader's members could not be bound: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            if (_readNext is null || _selectionJump is null)
            {
                problem = "ScenarioReader.ReadNext or SelectionJump is missing";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Resolves the one field the fallback reads.</summary>
        /// <param name="problem">Why not.</param>
        internal static bool PrepareFallback(out string problem)
        {
            try
            {
                _currentNovel ??= AccessTools.FieldRefAccess<Game.ScenarioReader, Master.NovelData>(ScenarioPatchTargets.CurrentNovelField);
            }
            catch (Exception ex)
            {
                problem = "ScenarioReader." + ScenarioPatchTargets.CurrentNovelField + " could not be bound: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>Starts running rows in <paramref name="mode"/> and arms the per-frame ticker.</summary>
        /// <param name="mode">Seam or fallback.</param>
        internal static void Start(ScenarioRuntimeMode mode)
        {
            Mode = mode;
            _ticker ??= MainThread.AddTicker("script waits, defers and fallback", Tick, Owner);
        }

        // ---- the seam: a prefix on ScenarioReader.ReadSentence -----------------------------------

        /// <summary>
        /// The body of the <c>ReadSentence(int)</c> prefix. Mirrors the method's own first lines:
        /// out of range returns; otherwise the index and row are set.
        /// </summary>
        /// <returns>True to let the game's method run (not a script row), false when the row was run here.</returns>
        internal static bool OnReadSentence(Game.ScenarioReader reader, int index)
        {
            var novels = _groupNovels!(reader);
            if (novels is null || index < 0 || index >= novels.Count)
            {
                return true;
            }

            var row = novels[index];
            if (row is null || !ScenarioCommandIds.IsInRange((int)row.Command))
            {
                return true;
            }

            _messageIndex!(reader) = index;
            _currentNovel!(reader) = row;
            var decision = RunRow(reader, row, (int)row.Command, index, id => HasRow(novels, id));
            if (!StillAt(reader, row, index))
            {
                ConsoleEvents.File(Owner, "moved", ("group", row.ScenarioGroupID), ("row", row.ID), ("decision", decision.ToString()));
                return false;
            }

            Apply(new Cursor(reader, row, index), decision);
            return false;
        }

        private static ScenarioCommandDecision RunRow(Game.ScenarioReader reader, Master.NovelData row, int command, int index, Func<string, bool>? hasRow)
        {
            var registry = ScenarioCommandRegistry.Shared;
            registry.TryGetName(command, out var name);
            var groupId = string.IsNullOrEmpty(row.ScenarioGroupID) ? reader.PlayingScenarioGroupID ?? string.Empty : row.ScenarioGroupID;
            var scenarioRow = new ScenarioCommandRow(row.ID, groupId, index, command, name ?? string.Empty, row.Arg1, row.Arg2, row.Arg3, row.Arg4, row.LocalizationID);
            var decision = ScenarioCommandRunner.Run(registry, scenarioRow, reader.PlayingScenarioType.ToString(), hasRow, Warn);
            RowsRun++;
            LastRow = scenarioRow + " -> " + decision;
            ConsoleEvents.File(Owner, "row", ("group", groupId), ("row", row.ID), ("command", name ?? command.ToString()), ("decision", decision.ToString()));
            return decision;
        }

        private static void Apply(Cursor cursor, ScenarioCommandDecision decision)
        {
            switch (decision.Outcome)
            {
                case ScenarioCommandOutcome.Wait:
                    Waits.Add(cursor.Label, decision.WaitCheck!, decision.WaitTimeout, MainThread.Now, cursor);
                    ConsoleEvents.File(Owner, "wait", ("row", cursor.Label), ("timeoutSec", decision.WaitTimeout.TotalSeconds));
                    return;
                case ScenarioCommandOutcome.Jump:
                    if (cursor.Fallback)
                    {
                        Warn(cursor.Label + ": JumpTo(\"" + decision.JumpTarget + "\") is not available in the fallback mode; going on with the next row");
                        Go(cursor, "continue", () => cursor.Reader.ReadNextForScript());
                        return;
                    }

                    var target = decision.JumpTarget;
                    var jump = new Master.NovelData { ID = cursor.Row.ID + "to" + target, Arg1 = target, Command = cursor.Row.Command };
                    Go(cursor, "jump " + target, () => _selectionJump!(cursor.Reader, jump));
                    return;
                default:
                    if (cursor.Fallback)
                    {
                        Go(cursor, "continue", () => cursor.Reader.ReadNextForScript());
                    }
                    else
                    {
                        Go(cursor, "continue", () => _readNext!(cursor.Reader));
                    }

                    return;
            }
        }

        /// <summary>
        /// Runs the reader's next step inline (as the game does between rows), or on the next frame
        /// once <see cref="MaxInlineDepth"/> script rows are nested, so a loop of script rows cannot
        /// overflow the stack.
        /// </summary>
        private static void Go(Cursor cursor, string what, Action step)
        {
            if (_depth >= MaxInlineDepth)
            {
                ConsoleEvents.File(Owner, "nextFrame", ("row", cursor.Label), ("step", what), ("depth", _depth));
                MainThread.Post("script " + what, () =>
                {
                    if (cursor.IsCurrent())
                    {
                        Step(cursor, what, step);
                    }
                }, Owner);
                return;
            }

            _depth++;
            try
            {
                Step(cursor, what, step);
            }
            finally
            {
                _depth--;
            }
        }

        private static void Step(Cursor cursor, string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // The reader's own code threw while going on (its EndStory, a game row after ours):
                // nothing we can redo, but it must be visible.
                WarnAlways(cursor.Label + ": the game's reader threw on '" + what + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool HasRow(List<Master.NovelData> novels, string id)
        {
            foreach (var row in novels)
            {
                if (row != null && string.Equals(row.ID, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StillAt(Game.ScenarioReader reader, Master.NovelData row, int index) =>
            reader.IsPlayingScenario()
            && ReferenceEquals(_currentNovel!(reader), row)
            && (index < 0 || _messageIndex!(reader) == index);

        // ---- the fallback: script rows as command 50 --------------------------------------------------

        /// <summary>
        /// After every master-data injection in fallback mode: turns the loader's script rows (5000-5999)
        /// into <c>ReadNextForScript</c> rows and remembers their command. The rows are the injector's
        /// own new objects, never the game's assets.
        /// </summary>
        /// <param name="novels">The loader's <c>NovelMasterList</c>.</param>
        internal static void ConvertForFallback(IReadOnlyList<Master.NovelData>? novels)
        {
            if (Mode != ScenarioRuntimeMode.Fallback || novels is null)
            {
                return;
            }

            var next = new Dictionary<Master.NovelData, int>();
            foreach (var row in novels)
            {
                if (row is null)
                {
                    continue;
                }

                if (_fallbackRows.TryGetValue(row, out var known))
                {
                    next[row] = known;
                }
                else if (ScenarioCommandIds.IsInRange((int)row.Command))
                {
                    next[row] = (int)row.Command;
                    row.Command = Master.CommandType.ReadNextForScript;
                }
            }

            _fallbackRows = next;
            ConsoleEvents.File(Owner, "fallbackRows", ("rows", next.Count));
        }

        private static void PollFallback(double now)
        {
            if (_fallbackRows.Count == 0 || now < _nextFallbackProbe)
            {
                return;
            }

            _nextFallbackProbe = now + FallbackProbeSeconds;

            // Resolved each probe (4 Hz): the room scope, and with it the reader, changes when the room reloads.
            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null || !reader.IsPlayingScenario())
            {
                _fallbackHandled = null;
                return;
            }

            var row = _currentNovel!(reader);
            if (row is null || !_fallbackRows.TryGetValue(row, out var command))
            {
                _fallbackHandled = null;
                return;
            }

            if (ReferenceEquals(row, _fallbackHandled))
            {
                return;
            }

            _fallbackHandled = row;
            var decision = RunRow(reader, row, command, -1, null);
            if (!StillAt(reader, row, -1))
            {
                return;
            }

            Apply(new Cursor(reader, row, -1), decision);
        }

        // ---- per frame: waits, deferred actions, fallback -------------------------------------------

        private static void Tick()
        {
            var now = MainThread.Now;
            if (Waits.Count > 0)
            {
                foreach (var end in Waits.Poll(now, cursor => cursor is Cursor c && c.IsCurrent()))
                {
                    Finish(end);
                }
            }

            if (Mode == ScenarioRuntimeMode.Fallback)
            {
                PollFallback(now);
            }

            if (Defers.Count > 0 && now >= _nextRoomProbe)
            {
                _nextRoomProbe = now + RoomProbeSeconds;
                var take = Defers.Take(IsRoomQuiet(), now);
                foreach (var expired in take.Expired)
                {
                    WarnAlways("cwy.defer " + expired.Line.Name + " from " + expired.GroupId + " dropped: the room was not idle for " +
                               (Defers.MaxAgeSeconds / 60d).ToString("0.#", CultureInfo.InvariantCulture) + " minutes");
                }

                foreach (var due in take.Due)
                {
                    ScenarioBuiltIns.RunDeferred(due);
                }
            }
        }

        private static void Finish(ScenarioWaitEnd end)
        {
            ConsoleEvents.File(Owner, "waitEnd", ("row", end.Label), ("kind", end.Kind.ToString()), ("sec", Math.Round(end.WaitedSeconds, 3)), ("detail", end.Detail));
            if (!(end.Cursor is Cursor cursor))
            {
                return;
            }

            switch (end.Kind)
            {
                case ScenarioWaitEndKind.Abandoned:
                    return;
                case ScenarioWaitEndKind.TimedOut:
                    Warn(end.Label + ": the wait timed out after " + end.WaitedSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s; going on with the next row");
                    break;
                case ScenarioWaitEndKind.Threw:
                    Warn(end.Label + ": the wait's check threw " + end.Detail + "; going on with the next row");
                    break;
            }

            Apply(cursor, ScenarioCommandDecision.Continue);
        }

        /// <summary>
        /// The room probe for deferred actions: RoomScene is loaded, no scene is playing
        /// (<c>ScenarioReader.IsPlayingScenario</c>) and the room's main state is Idle
        /// (<c>RoomGameManager.CurrentMainState</c>; a long story returns to Idle only after its fade and tidy).
        /// </summary>
        internal static bool IsRoomQuiet()
        {
            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null || reader.IsPlayingScenario())
            {
                return false;
            }

            return GameScopes.TryRoom<Game.RoomGameManager>(out var room) && room != null
                && room.CurrentMainState == Game.RoomGameManager.MainState.Idle;
        }

        /// <summary>Drops every wait and deferred action (feature off, shutdown).</summary>
        internal static void Reset(string why)
        {
            var waits = Waits.Clear();
            var defers = Defers.Clear();
            if (waits + defers > 0)
            {
                ConsoleEvents.File(Owner, "reset", ("why", why), ("waits", waits), ("defers", defers));
            }
        }

        // ---- warnings ------------------------------------------------------------------------------

        /// <summary>A SCRIPT warning, once per distinct text this session (later repeats go to the trace file).</summary>
        internal static void Warn(string message)
        {
            bool first;
            lock (Warned)
            {
                first = Warned.Count < MaxConsoleWarnings && Warned.Add(message);
            }

            if (first)
            {
                ConsoleEvents.EmitAt(Owner, "warn", ConsoleTags.Script, ConsoleLevel.Warn, message, null);
            }
            else
            {
                ConsoleEvents.File(Owner, "warn", ("message", message));
            }
        }

        /// <summary>A SCRIPT warning every time.</summary>
        internal static void WarnAlways(string message) =>
            ConsoleEvents.EmitAt(Owner, "warn", ConsoleTags.Script, ConsoleLevel.Warn, message, null);

        private static T? Delegate<T>(string name, Type[] parameters)
            where T : class
        {
            var method = AccessTools.Method(typeof(Game.ScenarioReader), name, parameters);
            return method is null ? null : (T)(object)System.Delegate.CreateDelegate(typeof(T), method);
        }

        /// <summary>Where a held row is: the reader, its row object and index (-1 in fallback mode).</summary>
        private sealed class Cursor
        {
            internal Cursor(Game.ScenarioReader reader, Master.NovelData row, int index)
            {
                Reader = reader;
                Row = row;
                Index = index;
                Label = row.ID + " in " + row.ScenarioGroupID;
            }

            internal Game.ScenarioReader Reader { get; }

            internal Master.NovelData Row { get; }

            internal int Index { get; }

            internal string Label { get; }

            internal bool Fallback => Index < 0;

            internal bool IsCurrent() => StillAt(Reader, Row, Index);
        }
    }
}
