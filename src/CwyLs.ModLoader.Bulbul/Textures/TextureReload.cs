using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Textures;
using CwyLs.ModLoader.Input;
using CwyLs.ModLoader.Packs;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The live-reload half of <see cref="TextureFeature"/>: the key that re-reads every pack
    /// texture, and the watcher that does it by itself while a pack author has the PNG open in an
    /// image editor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two detectors, on purpose.</b> A <see cref="FileSystemWatcher"/> gives the near-instant
    /// reload a "save and look at the game" loop needs, but on this Mono/macOS build its failure
    /// mode is silence, not an exception: it can simply stop delivering events. So a cheap
    /// last-write poll of the same files runs as well (<c>Textures.WatchPollSeconds</c>, 2 s by
    /// default, a few dozen <c>stat</c> calls) and catches anything the watcher missed. Whichever
    /// notices first reloads; the reload refreshes the file reading both of them compare against,
    /// so the other one does not fire again for the same edit.
    /// </para>
    /// <para>
    /// Watcher callbacks arrive on a thread pool thread. Nothing here touches Unity from one: an
    /// event only records the path and the time, and <see cref="Pump"/> - called from
    /// <see cref="TextureRunner"/>'s <c>Update</c> - does the work on the main thread, after a
    /// short debounce so one save that writes the file twice is one reload.
    /// </para>
    /// </remarks>
    public static class TextureReload
    {
        /// <summary>Id this feature's reload key is registered under in <see cref="Hotkeys"/>.</summary>
        public const string HotkeyId = "textures.reload";

        /// <summary>Config key of the file-watching switch.</summary>
        public const string ConfigKeyWatchFiles = "WatchFiles";

        /// <summary>Config key of the poll interval.</summary>
        public const string ConfigKeyWatchPollSeconds = "WatchPollSeconds";

        /// <summary>The pack subfolder that is watched.</summary>
        public const string WatchedFolder = "textures";

        /// <summary>How long after the last file event the reload runs, in seconds.</summary>
        public const float DebounceSeconds = 0.4f;

        /// <summary>Shortest poll interval that is accepted; below it the poll is the cost.</summary>
        public const float MinPollSeconds = 0.25f;

        private static readonly object Gate = new object();
        private static readonly List<FileSystemWatcher> Watchers = new List<FileSystemWatcher>();

        private static ConfigEntry<bool>? _watchFiles;
        private static ConfigEntry<float>? _watchPollSeconds;

        private static TextureFileSnapshot _snapshot = TextureFileSnapshot.Empty;
        private static string? _pendingPath;
        private static long _pendingAtTicks;
        private static float _nextPollAt;
        private static bool _quitHooked;

        /// <summary>
        /// True when the packs' <c>textures/</c> folders are watched (<c>Textures.WatchFiles</c>,
        /// default true). Off leaves only the reload key.
        /// </summary>
        public static bool WatchFiles => _watchFiles?.Value ?? true;

        /// <summary>
        /// Seconds between last-write polls of the pack texture files
        /// (<c>Textures.WatchPollSeconds</c>, default 2). Clamped to <see cref="MinPollSeconds"/>;
        /// 0 or less turns the poll off and leaves only the watcher.
        /// </summary>
        public static float PollSeconds
        {
            get
            {
                var configured = _watchPollSeconds?.Value ?? 2f;
                if (configured <= 0f)
                {
                    return 0f;
                }

                return configured < MinPollSeconds ? MinPollSeconds : configured;
            }
        }

        /// <summary>How many folders currently have a <see cref="FileSystemWatcher"/> on them.</summary>
        public static int WatchedFolders
        {
            get { lock (Gate) { return Watchers.Count; } }
        }

        /// <summary>
        /// Which detectors are live, for the log and the smoke detail - e.g.
        /// <c>FileSystemWatcher on 1 folder(s) + 2.0s poll</c>, <c>2.0s poll only</c>, or <c>off</c>.
        /// </summary>
        public static string Mode
        {
            get
            {
                if (!WatchFiles)
                {
                    return "off (" + TextureFeature.ConfigSection + "." + ConfigKeyWatchFiles + "=false)";
                }

                var poll = PollSeconds > 0f
                    ? PollSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s poll"
                    : "no poll";

                var watchers = WatchedFolders;
                return watchers > 0
                    ? $"FileSystemWatcher on {watchers} folder(s) + {poll}"
                    : poll + " only (no FileSystemWatcher)";
            }
        }

        /// <summary>The last reading of the packs' texture files. Never null.</summary>
        public static TextureFileSnapshot Snapshot
        {
            get { lock (Gate) { return _snapshot; } }
        }

        /// <summary>
        /// Binds the reload settings. Called from <see cref="TextureFeature.Initialize"/> with the
        /// same config file the other <c>Textures.*</c> entries go in.
        /// </summary>
        /// <param name="file">The config file; must not be null.</param>
        internal static void Configure(ConfigFile file)
        {
            if (file is null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            // One registry owns every key, with a Mac alternate for each: Ctrl+Shift+R is primary because F8
            // reaches the system on macOS unless Fn is held; F8 stays as the alternate. The action runs on the
            // main thread, on the hotkey host's frame tick.
            Hotkeys.Register(
                HotkeyId,
                "Re-reads every texture in the installed packs and applies them again, for editing " +
                "a PNG while the game runs; also picks up files you added or deleted.",
                new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl, KeyCode.LeftShift),   // chord-first (by design)
                new KeyboardShortcut(KeyCode.F8),
                () => TextureFeature.ReloadAll("key"),
                file,
                legacyPrimaryDefault: new KeyboardShortcut(KeyCode.F8),
                legacyAlternateDefault: new KeyboardShortcut(KeyCode.R, KeyCode.LeftControl, KeyCode.LeftShift));

            _watchFiles = file.Bind(
                TextureFeature.ConfigSection,
                ConfigKeyWatchFiles,
                true,
                "Watch each pack's textures/ folder and reload by itself when a PNG or _rules.json " +
                "is saved, added or deleted. Off leaves only the '" + HotkeyId + "' key; turning it " +
                "back on while the game runs takes effect at the next reload or pack rescan.");

            _watchPollSeconds = file.Bind(
                TextureFeature.ConfigSection,
                ConfigKeyWatchPollSeconds,
                2f,
                "Seconds between last-write checks of the pack texture files. This is the backstop " +
                "for the file watcher, which on macOS/Mono can stop delivering events without " +
                "saying so. 0 turns the poll off and trusts the watcher alone.");
        }

        /// <summary>
        /// Points the watchers at the folders of the packs that are loaded now and takes a fresh
        /// reading of their files. Called after every scan and after every reload.
        /// </summary>
        /// <param name="packs">The packs of the current resolution.</param>
        /// <param name="log">True to log which detectors ended up live.</param>
        /// <returns>The reading it took.</returns>
        internal static TextureFileSnapshot Arm(IReadOnlyList<LoadedPack> packs, bool log = false)
        {
            var reading = Refresh(packs);

            if (!WatchFiles)
            {
                StopWatchers();
                if (log)
                {
                    ConsoleEvents.Setup(
                        "textures.reload",
                        $"key '{HotkeyId}' only, file watching is {Mode}");
                }

                return reading;
            }

            StartWatchers(packs);
            HookQuit();

            if (log)
            {
                ConsoleEvents.Setup(
                    "textures.reload",
                    "armed",
                    ("mode", Mode),
                    ("files", reading.Count),
                    ("key", HotkeyId));
            }

            return reading;
        }

        /// <summary>
        /// Re-reads the packs' texture files from disk and stores the result as the reading the
        /// watcher and the poll compare against.
        /// </summary>
        /// <param name="packs">The packs of the current resolution.</param>
        /// <returns>The new reading.</returns>
        internal static TextureFileSnapshot Refresh(IReadOnlyList<LoadedPack> packs)
        {
            var reading = Read(packs);

            lock (Gate)
            {
                _snapshot = reading;
            }

            // A reload that has just happened is the newest state both detectors know about.
            Interlocked.Exchange(ref _pendingPath, null);
            _nextPollAt = SafeRealtime() + PollSeconds;
            return reading;
        }

        /// <summary>
        /// One main-thread tick: the debounced watcher event and the poll. Called from
        /// <see cref="TextureRunner"/>'s <c>Update</c> and nowhere else. (The reload key is polled
        /// by <see cref="Hotkeys"/> on its own host.)
        /// </summary>
        internal static void Pump()
        {
            try
            {
                if (!WatchFiles)
                {
                    return;
                }

                if (PumpWatcherEvent())
                {
                    return;
                }

                PumpPoll();
            }
            catch (Exception ex)
            {
                // This runs every frame; a throw here would fill the log and stop the pump.
                CwyLsModLoader.Log.LogError(
                    $"TEXTURES: live reload tick failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Stops watching. Called on <c>Application.quitting</c>.</summary>
        internal static void StopWatchers()
        {
            lock (Gate)
            {
                foreach (var watcher in Watchers)
                {
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        watcher.Dispose();
                    }
                    catch (Exception ex)
                    {
                        CwyLsModLoader.Log.LogWarning(
                            $"TEXTURES: could not stop a file watcher: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                Watchers.Clear();
            }
        }

        /// <summary>True when a texture reload should care about this file.</summary>
        /// <param name="path">Any path; only the file name is looked at.</param>
        internal static bool IsWatched(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "_rules.json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the last-write stamp of every watched file in these packs.</summary>
        /// <param name="packs">The packs of the current resolution.</param>
        /// <remarks>
        /// Reads the disk rather than the pack index: a file added since the last scan is not in
        /// the index yet, and noticing exactly that is the point.
        /// </remarks>
        private static TextureFileSnapshot Read(IReadOnlyList<LoadedPack> packs)
        {
            var files = new List<KeyValuePair<string, long>>();

            foreach (var pack in packs)
            {
                var folder = FolderOf(pack);
                if (folder is null)
                {
                    continue;
                }

                try
                {
                    foreach (var path in Directory.GetFiles(folder!, "*", SearchOption.AllDirectories))
                    {
                        if (!IsWatched(path))
                        {
                            continue;
                        }

                        files.Add(new KeyValuePair<string, long>(path, StampOf(path)));
                    }
                }
                catch (Exception ex)
                {
                    // A folder that vanished mid-scan is a pack being edited, not a failure.
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: could not list {pack.Id}/{WatchedFolder}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return TextureFileSnapshot.Of(files);
        }

        private static long StampOf(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not read the timestamp of {path}: {ex.GetType().Name}: {ex.Message}");
                return TextureFileSnapshot.MissingStamp;
            }
        }

        /// <summary>The pack's <c>textures/</c> folder, or null when it has none.</summary>
        private static string? FolderOf(LoadedPack pack)
        {
            try
            {
                var folder = Path.Combine(pack.RootPath, WatchedFolder);
                return Directory.Exists(folder) ? folder : null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not reach {pack.Id}/{WatchedFolder}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void StartWatchers(IReadOnlyList<LoadedPack> packs)
        {
            var folders = new List<string>(packs.Count);
            foreach (var pack in packs)
            {
                var folder = FolderOf(pack);
                if (folder != null)
                {
                    folders.Add(folder!);
                }
            }

            // A reload rescans the packs, which lands here again with the same folders. Rebuilding
            // the watchers then would leave a window with nothing watching, for no gain.
            if (AlreadyWatching(folders))
            {
                return;
            }

            StopWatchers();

            foreach (var folder in folders)
            {
                try
                {
                    var watcher = new FileSystemWatcher(folder!)
                    {
                        // One filter only on netstandard2.1, so everything is watched and the
                        // handler decides; a pack's textures/ folder holds nothing else anyway.
                        Filter = "*",
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    };

                    watcher.Changed += OnFileEvent;
                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Renamed += OnFileEvent;
                    watcher.Error += OnWatcherError;
                    watcher.EnableRaisingEvents = true;

                    lock (Gate)
                    {
                        Watchers.Add(watcher);
                    }
                }
                catch (Exception ex)
                {
                    // Mono on macOS can refuse a watcher (descriptor limits, a network volume).
                    // The poll is exactly the fallback for it, so this is a warning.
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: could not watch {folder}, the {ConfigKeyWatchPollSeconds} poll " +
                        $"will have to catch edits: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>True when these exact folders already have a live watcher, in this order.</summary>
        private static bool AlreadyWatching(IReadOnlyList<string> folders)
        {
            lock (Gate)
            {
                if (Watchers.Count == 0 || Watchers.Count != folders.Count)
                {
                    return false;
                }

                for (var i = 0; i < Watchers.Count; i++)
                {
                    if (!string.Equals(Watchers[i].Path, folders[i], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Records a file event. Runs on a thread pool thread - nothing Unity here.</summary>
        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                if (!IsWatched(e.FullPath))
                {
                    return;
                }

                Interlocked.Exchange(ref _pendingPath, e.FullPath);
                Interlocked.Exchange(ref _pendingAtTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception ex)
            {
                // No Unity logging guarantee off the main thread, but BepInEx's logger is
                // thread-safe and a silent watcher failure is what this whole class exists to avoid.
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: a file event could not be recorded: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnWatcherError(object sender, ErrorEventArgs e)
        {
            var error = e.GetException();
            CwyLsModLoader.Log.LogWarning(
                "TEXTURES: the file watcher failed; the " + ConfigKeyWatchPollSeconds +
                " poll keeps live reload working: " + (error is null ? "unknown error" : error.Message));
        }

        /// <summary>Reloads for a watcher event once it has been quiet for <see cref="DebounceSeconds"/>.</summary>
        /// <returns>True when a reload ran.</returns>
        private static bool PumpWatcherEvent()
        {
            var pending = Volatile.Read(ref _pendingPath);
            if (pending is null)
            {
                return false;
            }

            var quietSeconds = (DateTime.UtcNow.Ticks - Interlocked.Read(ref _pendingAtTicks)) / (double)TimeSpan.TicksPerSecond;
            if (quietSeconds < DebounceSeconds)
            {
                // Still being written: an image editor saves in two or three bursts.
                return false;
            }

            Interlocked.Exchange(ref _pendingPath, null);

            var relative = RelativeName(pending!);
            CwyLsModLoader.Log.LogDebug($"TEXTURES: file changed on disk: {relative} -> reloading");
            TextureFeature.ReloadAll("watcher " + relative);
            return true;
        }

        /// <summary>The backstop: compares last-write stamps and reloads when any of them moved.</summary>
        private static void PumpPoll()
        {
            var interval = PollSeconds;
            if (interval <= 0f)
            {
                return;
            }

            var now = SafeRealtime();
            if (now < _nextPollAt)
            {
                return;
            }

            _nextPollAt = now + interval;

            var reading = Read(TextureFeature.PacksNow());
            var diff = reading.DiffFrom(Snapshot);
            if (diff.IsEmpty)
            {
                return;
            }

            CwyLsModLoader.Log.LogDebug($"TEXTURES: file changed on disk: {diff.Summarize()} -> reloading (poll)");
            TextureFeature.ReloadAll("poll");
        }

        private static void HookQuit()
        {
            if (_quitHooked)
            {
                return;
            }

            _quitHooked = true;
            Application.quitting += StopWatchers;
        }

        private static float SafeRealtime()
        {
            try
            {
                return Time.realtimeSinceStartup;
            }
            catch (Exception ex)
            {
                // Time is main-thread only; every caller here is, but a wrong call must not throw
                // from a watcher path that is supposed to be the safe one.
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not read the clock: {ex.GetType().Name}: {ex.Message}");
                return 0f;
            }
        }

        /// <summary><c>&lt;pack&gt;/textures/&lt;file&gt;</c> for a log line, or the file name.</summary>
        private static string RelativeName(string path)
        {
            try
            {
                var marker = Path.DirectorySeparatorChar + WatchedFolder + Path.DirectorySeparatorChar;
                var at = path.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    return Path.GetFileName(path);
                }

                var before = path.Substring(0, at);
                return Path.GetFileName(before) + "/" + WatchedFolder + "/" +
                    path.Substring(at + marker.Length).Replace(Path.DirectorySeparatorChar, '/');
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not shorten '{path}': {ex.GetType().Name}: {ex.Message}");
                return path;
            }
        }
    }
}
