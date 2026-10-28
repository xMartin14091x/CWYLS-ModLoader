using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CwyLs.ModLoader.Console;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Input;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Input;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Packs;
using UnityEngine;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>A mod's log: one publish per line through <see cref="ConsoleEvents"/>, tag <c>MOD</c>, primary <c>&lt;id&gt;: message</c>.</summary>
    internal sealed class ModLog : ICwyModLog
    {
        private readonly string _modId;

        internal ModLog(string modId) => _modId = modId;

        public void Trace(string message) =>
            ConsoleEvents.File(ModConsoleLines.ModSystem, _modId, ("mod", _modId), ("level", "trace"), ("message", message));

        public void Info(string message) => Emit(ConsoleLevel.Info, message, null);

        public void Warn(string message) => Emit(ConsoleLevel.Warn, message, null);

        public void Error(string message, Exception? error = null) => Emit(ConsoleLevel.Error, message, error);

        private void Emit(ConsoleLevel level, string? message, Exception? error)
        {
            var text = ModConsoleLines.OneLine(message ?? string.Empty);
            if (error != null)
            {
                text += " (" + error.GetType().Name + ": " + ModConsoleLines.OneLine(error.Message) + ")";
            }

            var primary = _modId + ": " + text;
            if (level == ConsoleLevel.Info)
            {
                ConsoleEvents.Emit(
                    ModConsoleLines.ModSystem, _modId, ModConsoleLines.ModTag, primary, null,
                    ("mod", _modId), ("stack", error?.ToString()));
                return;
            }

            ConsoleEvents.EmitAt(
                ModConsoleLines.ModSystem, _modId, ModConsoleLines.ModTag, level, primary, null,
                ("mod", _modId), ("stack", error?.ToString()));
        }
    }

    /// <summary>
    /// A mod's own data folder, <c>BepInEx/data/mods/&lt;id&gt;/</c>. Keys follow <see cref="ModDataKey"/>
    /// plus <see cref="SafeFileNames"/> (no Windows device names, no trailing dot) and are
    /// lower-cased, so one key is one file on every operating system. A write is
    /// flushed to disk in a temporary file before it replaces the real one.
    /// </summary>
    internal sealed class ModData : ICwyModData
    {
        /// <summary>Folder under <see cref="ModLoaderPaths.DataRoot"/> holding every mod's data.</summary>
        internal const string ModsFolder = "mods";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private readonly ModHelper _helper;
        private readonly object _gate = new object();

        internal ModData(ModHelper helper) => _helper = helper;

        public string DirectoryPath => Path.Combine(ModLoaderPaths.DataRoot, ModsFolder, _helper.ModId);

        public bool Exists(string key) => File.Exists(PathOf(key));

        public string? ReadText(string key)
        {
            var path = PathOf(key);
            lock (_gate)
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
            }
        }

        public void WriteText(string key, string? text)
        {
            var normalised = KeyOf(key);
            var path = Path.Combine(DirectoryPath, normalised);
            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var temp = Path.Combine(DirectoryPath, ModDataKey.TempName(normalised));
                WriteFlushed(temp, text ?? string.Empty);
                Replace(temp, path);
            }
        }

        public object? ReadJson(string key)
        {
            var text = ReadText(key);
            return text is null ? null : JsonReader.Parse(text);
        }

        public void WriteJson(string key, object? value)
        {
            KeyOf(key);
            var json = JsonWriter.Write(value, problem => _helper.Log.Trace("data " + key + ": " + problem));
            WriteText(key, json);
        }

        public bool Delete(string key)
        {
            var path = PathOf(key);
            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                File.Delete(path);
                return true;
            }
        }

        public IReadOnlyList<string> Keys()
        {
            lock (_gate)
            {
                if (!Directory.Exists(DirectoryPath))
                {
                    return Array.Empty<string>();
                }

                var keys = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var file in Directory.GetFiles(DirectoryPath))
                {
                    var name = Path.GetFileName(file);
                    if (IsKey(name))
                    {
                        keys.Add(name.ToLowerInvariant());
                    }
                }

                return new List<string>(keys);
            }
        }

        /// <summary>True when a file name in the folder is a usable key (temporary files are not).</summary>
        private static bool IsKey(string name) => ModDataKey.IsValid(name) && SafeFileNames.ProblemWith(name) is null;

        /// <summary>Checks a key against both rules and returns its lower-case form.</summary>
        /// <exception cref="ArgumentException">The key breaks a rule; the message names it.</exception>
        private static string KeyOf(string key)
        {
            ModDataKey.Require(key);
            var problem = SafeFileNames.ProblemWith(key);
            if (problem != null)
            {
                throw new ArgumentException("\"" + key + "\" is not a data key: " + problem, nameof(key));
            }

            return key.ToLowerInvariant();
        }

        private string PathOf(string key) => Path.Combine(DirectoryPath, KeyOf(key));

        /// <summary>Writes the text and flushes it to the disk before returning, so the rename that follows publishes a complete file.</summary>
        private static void WriteFlushed(string path, string text)
        {
            var bytes = Utf8NoBom.GetBytes(text);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
        }

        /// <summary>
        /// Moves the flushed temporary file over the real one. <c>File.Replace</c> is a rename on
        /// both Mono platforms, so the key always names either the old or the new file. If the
        /// runtime refuses it, the fallback deletes the old file and renames the new one: the key
        /// is still never a half-written file, but it is NOT atomic - if the process dies between the
        /// two calls the key reads as absent and the new content is left in <c>&lt;key&gt;.tmp~</c>.
        /// </summary>
        private void Replace(string temp, string target)
        {
            if (!File.Exists(target))
            {
                File.Move(temp, target);
                return;
            }

            try
            {
                File.Replace(temp, target, null);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException || ex is NotSupportedException || ex is IOException)
            {
                _helper.Log.Trace("data: File.Replace unavailable (" + ex.GetType().Name + ": " + ex.Message + "); delete + rename instead (not atomic)");
                File.Delete(target);
                File.Move(temp, target);
            }
        }
    }

    /// <summary>Files inside the mod's own folder, through the framework loaders.</summary>
    internal sealed class ModContent : ICwyModContent
    {
        private readonly ModHelper _helper;
        private readonly LoadedPack _pack;

        internal ModContent(ModHelper helper, LoadedPack pack)
        {
            _helper = helper;
            _pack = pack;
        }

        public string DirectoryPath => _pack.RootPath;

        public string GetPath(string relativePath) => _pack.FullPath(relativePath);

        public bool Exists(string relativePath)
        {
            try
            {
                return File.Exists(GetPath(relativePath));
            }
            catch (ArgumentException ex)
            {
                _helper.Log.Trace("content: '" + relativePath + "' is not a path inside the mod folder: " + ex.Message);
                return false;
            }
        }

        public string? ReadText(string relativePath)
        {
            var path = GetPath(relativePath);
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        public object? ReadJson(string relativePath)
        {
            var text = ReadText(relativePath);
            return text is null ? null : JsonReader.Parse(text);
        }

        public Texture2D? LoadTexture(string relativePath, TextureLoadOptions? options = null)
        {
            RequireMainThread(nameof(LoadTexture));
            return TextureLoader.Load(GetPath(relativePath), options);
        }

        public AudioClip? LoadWav(string relativePath)
        {
            RequireMainThread(nameof(LoadWav));
            return AudioLoader.LoadWav(GetPath(relativePath));
        }

        public void LoadAudioAsync(string relativePath, Action<AudioClip?> onDone)
        {
            if (onDone is null)
            {
                throw new ArgumentNullException(nameof(onDone));
            }

            var path = GetPath(relativePath);
            Threading.MainThread.RunOrPost(
                "LoadAudioAsync " + relativePath,
                () => AudioLoader.LoadAsync(
                    path,
                    clip => _helper.Guard(ModStage.Event, "LoadAudioAsync " + relativePath, () => onDone(clip))),
                _helper.Owner);
        }

        private static void RequireMainThread(string member)
        {
            if (!Threading.MainThread.IsMainThread)
            {
                throw new InvalidOperationException(
                    member + " uses the Unity API and must run on the main thread; wrap it in helper.MainThread.InvokeAsync");
            }
        }
    }

    /// <summary>
    /// Hotkeys through the framework registry. A mod's key has the
    /// framework id <c>mod:&lt;modId&gt;/&lt;id&gt;</c>: <c>:</c> and <c>/</c> appear in no mod id and no
    /// framework id, so a mod key cannot collide with <c>console.toggle</c>, <c>smoke.run</c> or
    /// another mod's key.
    /// </summary>
    internal sealed class ModInput : ICwyModInput
    {
        /// <summary>The prefix of a mod hotkey's framework id.</summary>
        internal const string IdPrefix = HotkeyClaims.ModIdPrefix;

        private readonly ModHelper _helper;

        internal ModInput(ModHelper helper) => _helper = helper;

        /// <summary>The framework id of a mod's hotkey: <c>mod:&lt;modId&gt;/&lt;id&gt;</c>.</summary>
        internal static string HotkeyId(string modId, string id) => IdPrefix + modId + "/" + id;

        public CwyRegistration Register(string id, string description, string primaryChord, string? alternateChord, Action onPressed)
        {
            if (onPressed is null)
            {
                throw new ArgumentNullException(nameof(onPressed));
            }

            var fullId = HotkeyId(_helper.ModId, id ?? string.Empty);
            if (!_helper.IsActive)
            {
                return CwyRegistration.Refused(fullId, "the mod " + _helper.ModId + " is not running");
            }

            if (string.IsNullOrWhiteSpace(id) || !IsIdText(id!))
            {
                return Refuse(fullId, "hotkey id '" + id + "' must be letters, digits, '.', '_' or '-'");
            }

            if (!TryShortcut(primaryChord, out var primary))
            {
                return Refuse(fullId, "hotkey '" + id + "': '" + primaryChord + "' is not a chord like Ctrl+Shift+H or F7");
            }

            var alternate = KeyboardShortcut.Empty;
            if (!string.IsNullOrWhiteSpace(alternateChord) && !TryShortcut(alternateChord, out alternate))
            {
                _helper.WarnOnce("hotkey:" + fullId + ":alt", "hotkey '" + id + "': alternate '" + alternateChord + "' is not a chord; registered without it");
                alternate = KeyboardShortcut.Empty;
            }

            // The hotkey table has no removal, so the handle switches the callback off instead.
            var live = 1;
            var registered = Hotkeys.Register(
                fullId,
                string.IsNullOrWhiteSpace(description) ? fullId : description,
                primary,
                alternate,
                () =>
                {
                    if (System.Threading.Volatile.Read(ref live) == 1)
                    {
                        _helper.Guard(ModStage.Hotkey, fullId, onPressed);
                    }
                },
                _helper.Config);
            if (!registered)
            {
                // Hotkeys already logged which owner holds the id.
                return CwyRegistration.Refused(fullId, "the hotkey id " + fullId + " belongs to another owner");
            }

            return _helper.Track(fullId, new ActionDisposable(() => System.Threading.Interlocked.Exchange(ref live, 0)));
        }

        private CwyRegistration Refuse(string fullId, string problem)
        {
            _helper.WarnOnce("hotkey:" + fullId, problem + "; not registered");
            return CwyRegistration.Refused(fullId, problem);
        }

        /// <summary>Turns <c>Ctrl+Shift+H</c> into a BepInEx shortcut, via the Core chord parser.</summary>
        internal static bool TryShortcut(string? text, out KeyboardShortcut shortcut)
        {
            shortcut = KeyboardShortcut.Empty;
            if (!HotkeyChord.TryParse(text, out var chord) || chord is null || !chord.IsSet)
            {
                return false;
            }

            if (!TryKey(chord.MainKey, out var main))
            {
                return false;
            }

            var modifiers = new KeyCode[chord.Modifiers.Count];
            for (var i = 0; i < modifiers.Length; i++)
            {
                if (!TryKey(chord.Modifiers[i], out modifiers[i]))
                {
                    return false;
                }
            }

            shortcut = new KeyboardShortcut(main, modifiers);
            return true;
        }

        private static bool TryKey(string name, out KeyCode key) =>
            Enum.TryParse(name, ignoreCase: true, out key) && key != KeyCode.None;

        private static bool IsIdText(string id)
        {
            foreach (var c in id)
            {
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-'))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>Console commands through the shared registry, grouped under the mod id.</summary>
    internal sealed class ModConsole : ICwyModConsole
    {
        private readonly ModHelper _helper;

        internal ModConsole(ModHelper helper) => _helper = helper;

        public CwyRegistration Register(string name, string description, string usage, Func<IReadOnlyList<string>, string?> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!_helper.IsActive)
            {
                return CwyRegistration.Refused(name ?? string.Empty, "the mod " + _helper.ModId + " is not running");
            }

            if (ConsoleFeature.Commands.TryGet(name, out var existing) && existing != null && !_helper.OwnsCommand(existing.Name))
            {
                return Refuse(name!, "console command '" + name + "' already belongs to '" + existing.Group + "'; not replaced");
            }

            ConsoleCommand command;
            try
            {
                command = new ConsoleCommand(
                    name,
                    description,
                    usage,
                    _helper.ModId,
                    args => Run(name!, handler, args));
            }
            catch (ArgumentException ex)
            {
                return Refuse(name ?? string.Empty, "console command '" + name + "' refused: " + ex.Message);
            }

            ConsoleFeature.Commands.Register(command);
            _helper.OwnCommand(command.Name);
            return _helper.Track(command.Name, new ActionDisposable(() => Unregister(command)));
        }

        /// <summary>Removes the command if it is still this registration's (a later re-registration keeps its own).</summary>
        private void Unregister(ConsoleCommand command)
        {
            if (ConsoleFeature.Commands.TryGet(command.Name, out var current) && ReferenceEquals(current, command))
            {
                ConsoleFeature.Commands.Unregister(command.Name);
            }

            _helper.ForgetCommand(command.Name);
        }

        private CwyRegistration Refuse(string name, string problem)
        {
            _helper.WarnOnce("command:" + name, problem);
            return CwyRegistration.Refused(name, problem);
        }

        private string? Run(string name, Func<IReadOnlyList<string>, string?> handler, IReadOnlyList<string> args)
        {
            string? output = null;
            if (_helper.Guard(ModStage.Command, name, () => output = handler(args)))
            {
                return output;
            }

            return _helper.IsActive
                ? name + " failed; the error is in the log (MODS line)"
                : name + ": the mod " + _helper.ModId + " is not running";
        }
    }

    /// <summary>The shared dispatcher (<c>MainThread</c>) with the mod as owner.</summary>
    internal sealed class ModMainThread : ICwyModMainThread
    {
        private readonly ModHelper _helper;

        internal ModMainThread(ModHelper helper) => _helper = helper;

        public bool IsMainThread => Threading.MainThread.IsMainThread;

        public double Now => Threading.MainThread.Now;

        public bool Post(string label, Action work) =>
            _helper.IsActive &&
            Threading.MainThread.Post(label, Wrap(label, work), _helper.Owner);

        public bool RunOrPost(string label, Action work) =>
            _helper.IsActive &&
            Threading.MainThread.RunOrPost(label, Wrap(label, work), _helper.Owner);

        public DelayedWork PostAfter(double seconds, string label, Action work)
        {
            if (!_helper.IsActive)
            {
                throw NotRunning(label);
            }

            return Threading.MainThread.PostAfterFor(_helper.Owner, seconds, label, Wrap(label, work));
        }

        public Task InvokeAsync(string label, Action work, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            _helper.IsActive
                ? Threading.MainThread.InvokeAsync(label, work, timeout, cancellationToken, _helper.Owner)
                : Stopped<bool>(label);

        public Task<T> InvokeAsync<T>(string label, Func<T> work, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            _helper.IsActive
                ? Threading.MainThread.InvokeAsync(label, work, timeout, cancellationToken, _helper.Owner)
                : Stopped<T>(label);

        public T Invoke<T>(string label, Func<T> work, TimeSpan timeout)
        {
            if (!_helper.IsActive)
            {
                throw NotRunning(label);
            }

            return Threading.MainThread.Invoke(label, work, timeout, _helper.Owner);
        }

        public void Invoke(string label, Action work, TimeSpan timeout)
        {
            if (!_helper.IsActive)
            {
                throw NotRunning(label);
            }

            Threading.MainThread.Invoke(label, work, timeout, _helper.Owner);
        }

        private MainThreadRejectedException NotRunning(string label) =>
            new MainThreadRejectedException(MainThreadRejectReason.OwnerNotRunning, _helper.Owner, label);

        private Action Wrap(string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return () => _helper.Guard(ModStage.MainThread, label, work);
        }

        private Task<T> Stopped<T>(string label)
        {
            var completion = new TaskCompletionSource<T>();
            completion.SetException(NotRunning(label));
            return completion.Task;
        }
    }

    /// <summary>The registry as one mod sees it; API lookups are logged against the asking mod.</summary>
    internal sealed class ModRegistryView : ICwyModRegistry
    {
        private readonly ModHelper _helper;

        internal ModRegistryView(ModHelper helper) => _helper = helper;

        public bool IsLoaded(string id) => ModRuntime.Find(id)?.State == ModState.Loaded;

        public ICwyModInfo? Get(string id) => ModRuntime.Find(id);

        public IReadOnlyList<ICwyModInfo> GetAll() => ModRuntime.All;

        public T? GetApi<T>(string modId)
            where T : class
        {
            if (!ModRuntime.ApisReady)
            {
                _helper.WarnOnce(
                    "api-early:" + modId,
                    "GetApi(\"" + modId + "\") was called before every mod entered; call it from Events.OnModsReady. Returned null");
                return null;
            }

            var entry = ModRuntime.Find(modId);
            if (entry is null || entry.State != ModState.Loaded || entry.Api is null)
            {
                return null;
            }

            if (entry.Api is T api)
            {
                return api;
            }

            _helper.WarnOnce(
                "api-type:" + modId + ":" + typeof(T).FullName,
                "the API of " + modId + " (" + entry.Api.GetType().FullName + ") does not implement " + typeof(T).FullName +
                "; returned null. Both mods must reference the same contract assembly");
            return null;
        }
    }
}
