using System;
using System.IO;
using System.Text;
using System.Threading;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Data;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Owns everything about the bus that needs a frame: the debounced snapshot writer and the
    /// late subscription to the game's own observables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All of it lives on a hidden <c>DontDestroyOnLoad</c> GameObject rather than on the
    /// BepInEx plugin component, because in this game that component receives <c>OnDestroy</c>
    /// about 0.2 s after startup, before the boot scene has finished loading (observed during an
    /// early trace run). Static Harmony patches survive that; coroutines, <c>Update</c> and timers
    /// attached to the plugin do not. Same pattern as <c>CwyLs.ModLoader.Smoke.SmokeHost</c>.
    /// </para>
    /// <para>
    /// The bus is never torn down on plugin destruction - only <see cref="Application.quitting"/>
    /// ends it, and it uses that moment to write one last snapshot, which is when the player's data
    /// is most complete (the game calls <c>SaveAll</c> on the way out).
    /// </para>
    /// </remarks>
    public static class DataHost
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.DataRunner";

        /// <summary>The scene whose load proves the game finished loading save data.</summary>
        public const string SceneName = "RoomScene";

        /// <summary>File name of the snapshot inside <see cref="ModLoaderPaths.DataRoot"/>.</summary>
        public const string SnapshotFileName = "snapshot.json";

        private static readonly object Gate = new object();

        private static DataRunner? _runner;
        private static long _dirtyTicks;
        private static int _writing;
        private static string? _pendingLog;
        private static bool _readyEmitted;

        /// <summary>
        /// Where the snapshot is written: <see cref="ModLoaderPaths.DataRoot"/><c>/snapshot.json</c>,
        /// i.e. <c>&lt;BepInEx&gt;/data/snapshot.json</c>. The folder is the framework's, named in
        /// one place, and <c>ModLoaderPlugin.Awake</c> has already created it.
        /// </summary>
        public static string SnapshotPath => Path.Combine(ModLoaderPaths.DataRoot, SnapshotFileName);

        /// <summary>How many change notifications the bus has published this session.</summary>
        public static int ChangeCount { get; private set; }

        /// <summary>The most recent change, or <c>null</c> if none has happened yet.</summary>
        public static GameDataChanged? LastChange { get; private set; }

        /// <summary>How many snapshots have been written this session.</summary>
        public static int SnapshotCount { get; private set; }

        /// <summary>Summary of the last snapshot written, e.g. <c>7 todos, 3 note pages, 2 habits</c>.</summary>
        public static string LastSnapshotSummary { get; private set; } = string.Empty;

        /// <summary>
        /// Creates the hidden runner once and returns it; later calls return the same instance.
        /// Returns null only if the GameObject could not be created, which is logged - the bus's
        /// read API and change events still work in that case, only the snapshot file stops.
        /// </summary>
        public static DataRunner? EnsureRunner()
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost
                // runner is rebuilt rather than handed back dead.
                if (_runner != null)
                {
                    return _runner;
                }

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _runner = host.AddComponent<DataRunner>();

                    GameData.Changed += OnChanged;
                    Application.quitting += OnQuitting;

                    CwyLsModLoader.Log.LogDebug(
                        $"DATA runner armed; snapshot -> {SnapshotPath} " +
                        $"(write={GameData.Options.WriteSnapshot}, debounce={GameData.Options.DebounceMs}ms)");
                    return _runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        $"DATA runner could not be created; no snapshot will be written: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>Records that RoomScene is up, which is the fallback readiness signal.</summary>
        internal static void MarkSceneLoaded() => SaveDataAccess.SceneObserved = true;

        /// <summary>
        /// True when a change is waiting and its debounce window has elapsed. The window is set
        /// when the change arrives, so it always reflects the config value at that moment. Reading
        /// this clears the pending mark, so a true result obliges the caller to take a snapshot.
        /// </summary>
        internal static bool TryConsumeDue()
        {
            var due = Interlocked.Read(ref _dirtyTicks);
            if (due == 0L || DateTime.UtcNow.Ticks < due)
            {
                return false;
            }

            return Interlocked.CompareExchange(ref _dirtyTicks, 0L, due) == due;
        }

        /// <summary>Takes the queued log line, if any. Called from the main thread each frame.</summary>
        internal static string? TakePendingLog() => Interlocked.Exchange(ref _pendingLog, null);

        /// <summary>
        /// Serialises and writes the snapshot on a worker thread. The capture itself must already
        /// have happened on the main thread; only JSON and file I/O happen here.
        /// </summary>
        internal static void WriteAsync(GameDataSnapshot snapshot)
        {
            if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            {
                // A write is still in flight. Re-arm instead of queueing: the next snapshot would
                // supersede this one anyway, and unbounded queueing is how a debounce turns into
                // a backlog.
                MarkDirty();
                return;
            }

            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    try
                    {
                        WriteNow(snapshot);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(
                            ref _pendingLog,
                            $"!DATA snapshot write failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _writing, 0);
                    }
                });
        }

        /// <summary>
        /// Serialises and writes the snapshot on the calling thread, atomically: a temp file in the
        /// same directory, then a rename. A reader can still catch the brief gap between the delete
        /// and the rename on a platform where rename-over is not atomic, so a sidecar should retry
        /// a missing file rather than treat it as "no data".
        /// </summary>
        internal static void WriteNow(GameDataSnapshot snapshot)
        {
            var json = SnapshotSerializer.ToJson(snapshot, message => CwyLsModLoader.Log.LogWarning("DATA " + message));

            var path = SnapshotPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            var temp = path + ".tmp";

            // UTF-8 without a BOM: a BOM is legal in a file but trips a surprising number of
            // JSON readers, and this file exists to be read by other people's tools.
            File.WriteAllText(temp, json, new UTF8Encoding(false));

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);

            var todos = TodoItemCount(snapshot);
            var summary =
                $"{todos} todos, {snapshot.Notes.Count} note pages, {snapshot.Habits.Count} habits";
            LastSnapshotSummary = summary;
            SnapshotCount++;
            // Emitted from whichever thread wrote the file; TraceBus is thread-safe by contract.
            // The byte count is computed from the text rather than stat-ing the file we just
            // renamed, so the number always matches the document that was serialised. The
            // console line is this one publish - the deferred _pendingLog copy the runner used to
            // print is gone.
            ConsoleEvents.Emit(
                GameData.TraceSys,
                "snapshot",
                ConsoleTags.Data,
                "snapshot written",
                "todos,notes:notePages,habits,bytes",
                ("todos", todos),
                ("notePages", snapshot.Notes.Count),
                ("habits", snapshot.Habits.Count),
                ("bytes", new UTF8Encoding(false).GetByteCount(json)),
                ("path", path));
        }

        /// <summary>
        /// Emits the readiness event exactly once, the first time the bus is ready. Called from
        /// the runner's Update rather than from the readiness postfix, because the resolution path
        /// is only known after the first successful resolve.
        /// </summary>
        internal static void EmitReadyOnce()
        {
            if (_readyEmitted)
            {
                return;
            }

            _readyEmitted = true;
            ConsoleEvents.Emit(
                GameData.TraceSys,
                "ready",
                ConsoleTags.Data,
                "bus ready",
                "via,patched",
                ("via", SaveDataAccess.ResolutionPath),
                ("patched", SaveDataPatches.PatchedCount));
        }

        private static int TodoItemCount(GameDataSnapshot snapshot)
        {
            var todos = 0;
            foreach (var list in snapshot.Todos)
            {
                todos += list.Items.Count;
            }

            return todos;
        }

        private static void MarkDirty() =>
            Interlocked.Exchange(
                ref _dirtyTicks,
                DateTime.UtcNow.AddMilliseconds(Math.Max(0, GameData.Options.DebounceMs)).Ticks);

        private static void OnChanged(GameDataChanged change)
        {
            ChangeCount++;
            LastChange = change;

            if (GameData.Options.WriteSnapshot)
            {
                MarkDirty();
            }
        }

        private static void OnQuitting()
        {
            if (!GameData.Options.WriteSnapshot)
            {
                return;
            }

            try
            {
                // Synchronous on purpose: the process is going away and a worker thread would not
                // be given the chance to finish. The game has just run SaveAll, so this is the
                // most complete snapshot of the session.
                WriteNow(GameData.Snapshot());
                ConsoleEvents.Setup("data", "final snapshot written on quit", ("summary", LastSnapshotSummary));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA final snapshot failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
