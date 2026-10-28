using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Console;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// The per-mod helper. Keeps every registration the mod made through it (subscriptions,
    /// tickers, commands, hotkeys, actions, script commands, tokens, windows) so
    /// <see cref="Deactivate"/> can take them back, together with the mod's Harmony patches
    /// (<see cref="ModHarmony.TakeBack"/>), and routes every call into the mod through
    /// <see cref="Guard"/> so a failure is attributed to the mod. What the mod did outside the
    /// helper - its own threads, objects, files - is not taken back.
    /// </summary>
    internal sealed class ModHelper : ICwyModHelper
    {
        /// <summary>File name pattern of a mod's own config: <c>cwy.mod.&lt;id&gt;.cfg</c>.</summary>
        internal const string ConfigFilePrefix = "cwy.mod.";

        private readonly ModEntry _entry;
        private readonly object _gate = new object();
        private readonly List<IDisposable> _owned = new List<IDisposable>();
        private readonly List<string> _commands = new List<string>();
        private readonly List<Action> _modsReady = new List<Action>();
        private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.Ordinal);
        private readonly ModHarmony _harmony;
        private ConfigFile? _config;
        private bool _modsReadyRaised;

        internal ModHelper(ModEntry entry)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            Log = new ModLog(entry.Id);
            Data = new ModData(this);
            Events = new ModEvents(this);
            Satone = new ModSatone(this);
            GameData = new ModGameData(this);
            Language = new ModLanguage();
            Content = new ModContent(this, entry.Pack);
            Input = new ModInput(this);
            Console = new ModConsole(this);
            Ui = new ModUi(this);
            MainThread = new ModMainThread(this);
            ModRegistry = new ModRegistryView(this);
            _harmony = new ModHarmony(this, () => _entry.Assembly);
            EditData = new ModEditData(this);
            Audio = new ModAudio(this);
            Grading = new ModGrading(this);
            UiSkin = new ModUiSkin(this);
            Conditions = new ModConditions(this);
            Actions = new ModActions(this);
            Scenes = new ModScenes(this);
            Camera = new ModCamera(this);
            VoiceStream = new ModVoiceStream(this);
            Mic = new ModMic(this);
            Api = new ModApi(this);
        }

        public string ModId => _entry.Id;

        public ICwyModManifest Manifest => _entry.ManifestView;

        public string DirectoryPath => _entry.Pack.RootPath;

        public ICwyModLog Log { get; }

        public ConfigFile Config
        {
            get
            {
                lock (_gate)
                {
                    return _config ??= new ConfigFile(ConfigPath, saveOnInit: true);
                }
            }
        }

        public ICwyModData Data { get; }

        public ICwyModEvents Events { get; }

        public ICwyModSatone Satone { get; }

        public ICwyModGameData GameData { get; }

        public ICwyModLanguage Language { get; }

        public ICwyModContent Content { get; }

        public IReadOnlyList<ICwyModInfo> ContentPacks
        {
            get
            {
                var packs = new List<ICwyModInfo>();
                foreach (var entry in ModRuntime.All)
                {
                    var target = entry.Pack.Manifest.ContentPackFor;
                    if (target != null && string.Equals(target.Id, ModId, StringComparison.OrdinalIgnoreCase))
                    {
                        packs.Add(entry);
                    }
                }

                return packs;
            }
        }

        public ICwyModInput Input { get; }

        public ICwyModConsole Console { get; }

        public ICwyModUi Ui { get; }

        public ICwyModMainThread MainThread { get; }

        public ICwyModRegistry ModRegistry { get; }

        public ICwyModHarmony Harmony => _harmony;

        public ICwyModEditData EditData { get; }

        public ICwyModAudio Audio { get; }

        /// <inheritdoc/>
        public ICwyModGrading Grading { get; }

        public ICwyModUiSkin UiSkin { get; }

        public ICwyModConditions Conditions { get; }

        public ICwyModActions Actions { get; }

        public ICwyModScenes Scenes { get; }

        public ICwyModCamera Camera { get; }

        public ICwyModVoiceStream VoiceStream { get; }

        public ICwyModMic Mic { get; }

        public ICwyModApi Api { get; }

        /// <summary>Absolute path of the mod's config file.</summary>
        internal string ConfigPath => Path.Combine(BepInEx.Paths.ConfigPath, ConfigFilePrefix + ModId + ".cfg");

        /// <summary>The dispatcher owner name of this mod's work.</summary>
        internal string Owner => Threading.MainThread.ModOwner(ModId);

        /// <summary>False once the mod failed or shut down: nothing of it runs any more.</summary>
        internal bool IsActive { get; private set; } = true;

        /// <summary>
        /// Runs mod code with attribution. Does nothing (and returns false) once the mod is
        /// inactive, so a hotkey or a late callback of a failed mod cannot run its code.
        /// </summary>
        internal bool Guard(ModStage stage, string? label, Action action)
        {
            if (!IsActive)
            {
                return false;
            }

            return ModFailures.Guard(ModId, stage, label, action);
        }

        /// <summary>
        /// Keeps a registration so it can be taken back when the mod stops. A registration made
        /// after that is disposed at once.
        /// </summary>
        /// <returns>A handle the mod may dispose early; disposing twice is harmless.</returns>
        internal IDisposable Own(IDisposable registration)
        {
            var owned = new OwnedRegistration(this, registration);
            lock (_gate)
            {
                if (IsActive)
                {
                    _owned.Add(owned);
                    return owned;
                }
            }

            owned.Dispose();
            return owned;
        }

        /// <summary>
        /// Wraps a registration in the handle mods get back (<see cref="CwyRegistration"/>) and keeps
        /// it so it is taken back when the mod stops. A registration made after that is disposed at
        /// once and comes back inactive.
        /// </summary>
        /// <param name="name">What was registered, for the handle.</param>
        /// <param name="inner">The underlying registration.</param>
        internal CwyRegistration Track(string name, IDisposable inner)
        {
            var registration = new CwyRegistration(name, inner, Forget);
            lock (_gate)
            {
                if (IsActive)
                {
                    _owned.Add(registration);
                    return registration;
                }
            }

            registration.Dispose();
            return registration;
        }

        /// <summary>Records a console command this mod registered.</summary>
        internal void OwnCommand(string name)
        {
            lock (_gate)
            {
                _commands.Add(name);
            }
        }

        /// <summary>Forgets one record of a console command this mod unregistered itself (each registration added one).</summary>
        internal void ForgetCommand(string name)
        {
            lock (_gate)
            {
                var index = _commands.FindIndex(command => string.Equals(command, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _commands.RemoveAt(index);
                }
            }
        }

        /// <summary>True when this mod registered a command of this name.</summary>
        internal bool OwnsCommand(string name)
        {
            lock (_gate)
            {
                foreach (var command in _commands)
                {
                    if (string.Equals(command, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Queues a mods-ready handler, or schedules it for the next frame when this helper has
        /// already raised the signal. The check and the add happen under one lock with
        /// <see cref="RaiseModsReady"/>, so a handler added from another thread while the loader
        /// raises is never lost.
        /// </summary>
        internal IDisposable AddModsReady(Action handler)
        {
            lock (_gate)
            {
                if (!_modsReadyRaised)
                {
                    _modsReady.Add(handler);
                    return new ActionDisposable(() =>
                    {
                        lock (_gate)
                        {
                            _modsReady.Remove(handler);
                        }
                    });
                }
            }

            var cancelled = 0;
            Threading.MainThread.Post(
                "OnModsReady",
                () =>
                {
                    if (System.Threading.Volatile.Read(ref cancelled) == 0)
                    {
                        Guard(ModStage.ModsReady, null, handler);
                    }
                },
                Owner);
            return new ActionDisposable(() => System.Threading.Interlocked.Exchange(ref cancelled, 1));
        }

        /// <summary>Runs the queued mods-ready handlers. Called once by the loader.</summary>
        internal void RaiseModsReady()
        {
            Action[] handlers;
            lock (_gate)
            {
                _modsReadyRaised = true;
                handlers = _modsReady.ToArray();
                _modsReady.Clear();
            }

            foreach (var handler in handlers)
            {
                Guard(ModStage.ModsReady, null, handler);
            }
        }

        /// <summary>Logs a warning once per key for this mod (an unavailable facet, a refused call).</summary>
        internal void WarnOnce(string key, string message)
        {
            lock (_gate)
            {
                if (!_warnedOnce.Add(key))
                {
                    return;
                }
            }

            ConsoleEvents.EmitAt(
                ModConsoleLines.LoaderSystem, "warning", ModConsoleLines.ModsTag, ConsoleLevel.Warn,
                ModId + ": " + message, null, ("mod", ModId), ("key", key));
        }

        /// <summary>
        /// Takes back what the mod registered through the helper - event subscriptions, tickers,
        /// queued work, console commands, windows, actions, script commands, condition tokens - and
        /// its Harmony patches (under its id, and any patch method declared in its assemblies).
        /// Hotkeys stay bound (the registry has no removal) but their callbacks stop running. Its own
        /// threads, objects and files are not touched. Main thread; idempotent; never throws.
        /// </summary>
        /// <param name="reason">Why, for the trace file.</param>
        internal void Deactivate(string reason)
        {
            IDisposable[] owned;
            string[] commands;
            lock (_gate)
            {
                if (!IsActive)
                {
                    return;
                }

                IsActive = false;
                owned = _owned.ToArray();
                _owned.Clear();
                commands = _commands.ToArray();
                _commands.Clear();
                _modsReady.Clear();
            }

            var failures = 0;
            foreach (var registration in owned)
            {
                try
                {
                    registration.Dispose();
                }
                catch (Exception ex)
                {
                    failures++;
                    CwyLsModLoader.Log.LogWarning(
                        "MODS " + ModId + ": a registration could not be removed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            foreach (var name in commands)
            {
                ConsoleFeature.Commands.Unregister(name);
            }

            var dropped = Threading.MainThread.Cancel(Owner);
            var tickers = Threading.MainThread.RemoveTickers(Owner);
            var patches = _harmony.TakeBack(reason);

            ConsoleEvents.File(
                ModConsoleLines.LoaderSystem, "deactivated",
                ("mod", ModId),
                ("reason", reason),
                ("registrations", owned.Length),
                ("commands", commands.Length),
                ("droppedWork", dropped),
                ("tickers", tickers),
                ("harmonyPatches", patches),
                ("failures", failures));
        }

        private void Forget(IDisposable registration)
        {
            lock (_gate)
            {
                _owned.Remove(registration);
            }
        }

        /// <summary>A registration the helper keeps; disposing it early also forgets it.</summary>
        private sealed class OwnedRegistration : IDisposable
        {
            private readonly ModHelper _helper;
            private IDisposable? _inner;

            internal OwnedRegistration(ModHelper helper, IDisposable inner)
            {
                _helper = helper;
                _inner = inner;
            }

            public void Dispose()
            {
                var inner = System.Threading.Interlocked.Exchange(ref _inner, null);
                if (inner is null)
                {
                    return;
                }

                _helper.Forget(this);
                inner.Dispose();
            }
        }
    }

    /// <summary>A handle that runs an action once when disposed.</summary>
    internal sealed class ActionDisposable : IDisposable
    {
        private Action? _action;

        internal ActionDisposable(Action action) => _action = action;

        /// <summary>A handle that does nothing.</summary>
        internal static IDisposable None { get; } = new ActionDisposable(() => { });

        public void Dispose() => System.Threading.Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
