using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Ui;
using UnityEngine;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>Game events through the adapter's bus; every handler is wrapped and owned by the helper.</summary>
    internal sealed class ModEvents : ICwyModEvents
    {
        private readonly ModHelper _helper;

        internal ModEvents(ModHelper helper) => _helper = helper;

        public bool IsAvailable => ModBackends.Events != null;

        public IDisposable Subscribe<T>(Action<T> handler)
            where T : ICwyEvent
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var source = ModBackends.Events;
            if (source is null)
            {
                _helper.WarnOnce("events", "game events are not available (no game adapter); Subscribe<" + typeof(T).Name + "> does nothing");
                return ActionDisposable.None;
            }

            var label = typeof(T).Name;
            return _helper.Own(source.Subscribe<T>(e => _helper.Guard(ModStage.Event, label, () => handler(e))));
        }

        public IDisposable SubscribeAny(Action<AnyEvent> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var source = ModBackends.Events;
            if (source is null)
            {
                _helper.WarnOnce("events", "game events are not available (no game adapter); SubscribeAny does nothing");
                return ActionDisposable.None;
            }

            return _helper.Own(source.SubscribeAny(e => _helper.Guard(ModStage.Event, "Any", () => handler(e))));
        }

        public IDisposable EveryFrame(Action handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return _helper.Own(Threading.MainThread.AddTicker(
                "EveryFrame",
                () => _helper.Guard(ModStage.Tick, "EveryFrame", handler),
                _helper.Owner));
        }

        public IDisposable OnModsReady(Action handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return _helper.Own(_helper.AddModsReady(handler));
        }
    }

    /// <summary>Satone through the adapter; a file-only trace line records which mod asked.</summary>
    internal sealed class ModSatone : ICwyModSatone
    {
        private readonly ModHelper _helper;

        internal ModSatone(ModHelper helper) => _helper = helper;

        public bool IsAvailable => ModBackends.Satone != null;

        public bool IsSaying => ModBackends.Satone?.IsSaying ?? false;

        public bool CanInteract => ModBackends.Satone?.CanInteract ?? false;

        public string State => ModBackends.Satone?.State ?? "unknown";

        public bool Face(string nameOrId) => Call("face", nameOrId, s => s.Face(nameOrId));

        public bool FaceReset() => Call("faceReset", null, s => s.FaceReset());

        public bool Motion(string nameOrId, bool trigger = false) => Call("motion", nameOrId, s => s.Motion(nameOrId, trigger));

        public bool LookAt(float scale, float seconds) => Call("lookAt", scale.ToString(System.Globalization.CultureInfo.InvariantCulture), s => s.LookAt(scale, seconds));

        public bool LookReset() => Call("lookReset", null, s => s.LookReset());

        public RestoreResult RestoreDefaults(RestoreScope scope = RestoreScope.All)
        {
            var satone = Backend("RestoreDefaults");
            if (satone is null)
            {
                return RestoreResult.Refused(scope, "Satone is not available (no game adapter)");
            }

            Trace("restore", scope.ToString());
            return satone.RestoreDefaults(scope);
        }

        public SayHandle Say(string text) => Say(SayRequest.For(text));

        public SayHandle Say(SayRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var satone = Backend("Say");
            if (satone is null)
            {
                return Unavailable(request);
            }

            Trace("say", request.Text);
            return satone.Say(request);
        }

        public SayHandle SayWithClip(SayRequest request, AudioClip clip)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip));
            }

            var satone = Backend("SayWithClip");
            if (satone is null)
            {
                return Unavailable(request);
            }

            Trace("sayWithClip", request.Text);
            return satone.SayWithClip(request, clip);
        }

        private bool Call(string call, string? argument, Func<ICwyModSatone, bool> action)
        {
            var satone = Backend(call);
            if (satone is null)
            {
                return false;
            }

            Trace(call, argument);
            return action(satone);
        }

        private ICwyModSatone? Backend(string call)
        {
            var satone = ModBackends.Satone;
            if (satone is null)
            {
                _helper.WarnOnce("satone", "Satone is not available (no game adapter); " + call + " and the other Satone calls do nothing");
            }

            return satone;
        }

        private void Trace(string call, string? argument) =>
            ConsoleEvents.File(ModConsoleLines.LoaderSystem, "satone", ("mod", _helper.ModId), ("call", call), ("arg", argument));

        private static SayHandle Unavailable(SayRequest request) =>
            SayHandle.Rejected(
                new SayLine(1, request, null, SayLine.NoMotion, SayLine.NoMotion, Threading.MainThread.Now),
                "Satone is not available (no game adapter)");
    }

    /// <summary>The player-data bus through the data plugin.</summary>
    internal sealed class ModGameData : ICwyModGameData
    {
        private readonly ModHelper _helper;

        internal ModGameData(ModHelper helper) => _helper = helper;

        public bool IsAvailable => ModBackends.GameData != null;

        public bool IsReady => ModBackends.GameData?.IsReady ?? false;

        public GameDataSnapshot? Snapshot()
        {
            var source = ModBackends.GameData;
            if (source is null)
            {
                _helper.WarnOnce("gameData", "the player-data bus is not available (CwyLs.ModLoader.Bulbul.Data is not loaded)");
                return null;
            }

            if (!Threading.MainThread.IsMainThread)
            {
                throw new InvalidOperationException(
                    "GameData.Snapshot reads live game objects and must run on the main thread; " +
                    "use helper.MainThread.InvokeAsync(\"snapshot\", () => helper.GameData.Snapshot())");
            }

            return source.Snapshot();
        }

        public IDisposable OnChanged(Action<GameDataChanged> handler)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var source = ModBackends.GameData;
            if (source is null)
            {
                _helper.WarnOnce("gameData", "the player-data bus is not available (CwyLs.ModLoader.Bulbul.Data is not loaded)");
                return ActionDisposable.None;
            }

            return _helper.Own(source.Subscribe(change => _helper.Guard(ModStage.Event, "GameData.Changed", () => handler(change))));
        }
    }

    /// <summary>The display language through the adapter; empty and false without one.</summary>
    internal sealed class ModLanguage : ICwyModLanguage
    {
        public bool IsAvailable => ModBackends.Language != null;

        public string CurrentCode => ModBackends.Language?.CurrentCode ?? string.Empty;

        public bool IsPackLanguage => ModBackends.Language?.IsPackLanguage ?? false;
    }

    /// <summary>
    /// The adapter's mod-window factory, registered with
    /// <c>ModFacetBackends.Use&lt;IModWindowBackend&gt;("windows", ...)</c>. Internal: only the
    /// framework's assemblies implement or call it.
    /// </summary>
    internal interface IModWindowBackend : ICwyModFacet
    {
        /// <summary>Builds a hidden window. Main thread only; the spec is valid and its id unused by the owner.</summary>
        /// <param name="owner">The owning mod (or framework feature) and how to run its handlers.</param>
        /// <param name="spec">What to build.</param>
        CwyWindowResult CreateWindow(ModWindowOwner owner, CwyWindowSpec spec);
    }

    /// <summary>Who owns a mod window: the id its objects and remembered place are named after, and how its handlers run.</summary>
    internal sealed class ModWindowOwner
    {
        /// <summary>Creates an owner.</summary>
        /// <param name="modId">The mod's (or framework feature's) id.</param>
        /// <param name="run">Runs a handler with the owner's attribution; never throws.</param>
        internal ModWindowOwner(string modId, UiEventRunner run)
        {
            ModId = string.IsNullOrEmpty(modId) ? throw new ArgumentException("an owner id is required", nameof(modId)) : modId;
            Run = run ?? throw new ArgumentNullException(nameof(run));
        }

        /// <summary>The owner's id.</summary>
        internal string ModId { get; }

        /// <summary>Runs a handler with the owner's attribution.</summary>
        internal UiEventRunner Run { get; }

        /// <summary>An owner for the framework's own windows: a handler that throws is logged, never rethrown.</summary>
        /// <param name="id">The feature's id, e.g. <c>cwy.ui</c>.</param>
        internal static ModWindowOwner Framework(string id) =>
            new ModWindowOwner(id, (label, handler) =>
            {
                try
                {
                    handler();
                    return true;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError("GAMEUI " + id + ": " + label + " threw " + ex.GetType().Name + ": " + ex.Message);
                    ConsoleEvents.File("windows", "handlerFailed", ("owner", id), ("label", label), ("error", ex.ToString()));
                    return false;
                }
            });
    }

    /// <summary>
    /// Mod windows through the adapter; each window is owned by the helper and destroyed when
    /// the mod stops. The legacy <see cref="CreateWindow(CwyWindowOptions, out string)"/> builds the same
    /// windows when the window backend is installed, and falls back to the legacy adapter otherwise.
    /// </summary>
    internal sealed class ModUi : ICwyModUi
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "mod windows are not available: no window backend is installed";

        /// <summary>The facet name in <c>ModFacetBackends</c> and <c>mods status</c>.</summary>
        internal const string FacetName = "windows";

        private readonly ModHelper _helper;
        private readonly object _gate = new object();
        private readonly List<ICwyWindow> _windows = new List<ICwyWindow>();
        private int _legacyWindows;

        internal ModUi(ModHelper helper) => _helper = helper;

        public bool IsAvailable => Backend?.IsAvailable ?? false;

        public string Status => Backend?.Status ?? NotAvailable;

        public IReadOnlyList<ICwyWindow> Windows
        {
            get
            {
                lock (_gate)
                {
                    _windows.RemoveAll(window => !window.IsAlive);
                    return _windows.ToArray();
                }
            }
        }

        private static IModWindowBackend? Backend => ModFacetBackends.Get<IModWindowBackend>();

        public ICwyWindow? Find(string windowId)
        {
            lock (_gate)
            {
                foreach (var window in _windows)
                {
                    if (window.IsAlive && string.Equals(window.Id, windowId, StringComparison.OrdinalIgnoreCase))
                    {
                        return window;
                    }
                }
            }

            return null;
        }

        public CwyWindowResult CreateWindow(CwyWindowSpec spec)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            var backend = Backend;
            if (backend is null)
            {
                return Refuse(spec, "unavailable", NotAvailable);
            }

            if (!backend.IsAvailable)
            {
                return Refuse(spec, "off", backend.Status);
            }

            if (!Threading.MainThread.IsMainThread)
            {
                return Refuse(spec, "thread", "windows are built on the main thread; call CreateWindow from Entry, an event or helper.MainThread");
            }

            if (!_helper.IsActive)
            {
                return CwyWindowResult.Refused("the mod is not running");
            }

            var problems = CwyWindowSpecRules.Validate(spec);
            if (problems.Count > 0)
            {
                return Refuse(spec, "spec:" + spec.Id, "the window spec is invalid: " + string.Join("; ", problems));
            }

            if (Find(spec.Id) != null)
            {
                return Refuse(spec, "duplicate:" + spec.Id, "this mod already has a window '" + spec.Id + "'; Destroy it first or use Find");
            }

            var owner = new ModWindowOwner(_helper.ModId, (label, handler) => _helper.Guard(ModStage.Event, "window " + label, handler));
            CwyWindowResult result;
            try
            {
                result = backend.CreateWindow(owner, spec);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("MODS " + _helper.ModId + ": window '" + spec.Id + "' could not be built: " + ex);
                return CwyWindowResult.Refused("the window could not be built: " + ex.GetType().Name + ": " + ex.Message);
            }

            var window = result.Window;
            if (window is null)
            {
                return Refuse(spec, "build:" + result.Problem, result.Problem);
            }

            lock (_gate)
            {
                _windows.Add(window);
            }

            _helper.Own(new ActionDisposable(() => DestroyQuietly(window)));
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ICwyModWindow? CreateWindow(CwyWindowOptions options, out string problem)
        {
            CwyDeprecations.Notify(MethodBase.GetCurrentMethod()!);
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (Backend != null)
            {
                var number = System.Threading.Interlocked.Increment(ref _legacyWindows);
                var result = CreateWindow(LegacyWindow.Spec(options, number));
                problem = result.Problem;
                return result.Window is null ? null : new LegacyWindow(result.Window);
            }

            // No window backend: the legacy adapter (CwyLs.ModLoader.Bulbul/Mods/BulbulModBackends.cs).
            var source = ModBackends.Ui;
            if (source is null)
            {
                problem = "game-style windows are not available (no game adapter)";
                return null;
            }

            if (!Threading.MainThread.IsMainThread)
            {
                problem = "windows are built on the main thread; call CreateWindow from Entry, an event or helper.MainThread";
                return null;
            }

            if (!_helper.IsActive)
            {
                problem = "the mod is not running";
                return null;
            }

            var inner = source.CreateWindow(_helper.ModId, options, out problem);
            if (inner is null)
            {
                return null;
            }

            var window = new ModWindow(_helper, inner);
            _helper.Own(new ActionDisposable(window.DestroyQuietly));
            return window;
        }

        private CwyWindowResult Refuse(CwyWindowSpec spec, string key, string problem)
        {
            _helper.WarnOnce("ui:" + key, "window '" + spec.Id + "' refused: " + problem);
            return CwyWindowResult.Refused(problem);
        }

        /// <summary>Destroys a window when the mod stops; a failure is logged, never thrown.</summary>
        private void DestroyQuietly(ICwyWindow window)
        {
            try
            {
                if (window.IsAlive)
                {
                    window.Destroy();
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "MODS " + _helper.ModId + ": window '" + window.Id + "' could not be destroyed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// A legacy window (<see cref="ICwyModWindow"/>) on top of a spec-built window from
    /// <see cref="Spec"/>: the text part <c>lines</c> and the optional one-line input <c>input</c>.
    /// Handlers already run with the mod's attribution (the spec-built window's owner).
    /// </summary>
    internal sealed class LegacyWindow : ICwyModWindow
    {
        /// <summary>The text part's id.</summary>
        internal const string LinesPart = "lines";

        /// <summary>The input part's id.</summary>
        internal const string InputPart = "input";

        private readonly ICwyWindow _window;
        private readonly ICwyTextView _lines;

        internal LegacyWindow(ICwyWindow window)
        {
            _window = window;
            _lines = window.Text(LinesPart);
            _window.CloseRequested += _ => CloseClicked?.Invoke();
            foreach (var part in window.Parts)
            {
                if (part is ICwyInputBox input)
                {
                    input.Submitted += text => Submitted?.Invoke(text);
                }
            }
        }

        public event Action? CloseClicked;

        public event Action<string>? Submitted;

        public string Title
        {
            get => _window.Title;
            set => _window.Title = value;
        }

        public string Status
        {
            get => _window.Status;
            set => _window.Status = value;
        }

        public bool IsShown => _window.IsShown;

        public bool IsAlive => _window.IsAlive;

        public void Show() => _window.Show();

        public void Hide() => _window.Hide();

        public void Destroy() => _window.Destroy();

        public void SetLines(IReadOnlyList<string>? lines) => _lines.SetLines(lines);

        public void AppendLine(string line) => _lines.AppendLine(line);

        /// <summary>The window spec of a legacy window: never remembered (it has no stable id), not hidden by its close button.</summary>
        /// <param name="options">The legacy options.</param>
        /// <param name="number">The mod's legacy window count, for the id.</param>
        internal static CwyWindowSpec Spec(CwyWindowOptions options, int number)
        {
            var spec = new CwyWindowSpec("legacy-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                Title = options.Title ?? string.Empty,
                MinWidth = options.MinWidth,
                MinHeight = options.MinHeight,
                RememberRect = false,
                HideOnClose = false,
            };

            spec.Parts.Add(new CwyTextSpec(LinesPart));
            if (options.InputPlaceholder != null)
            {
                spec.Parts.Add(new CwyInputSpec(InputPart) { Placeholder = options.InputPlaceholder });
            }

            return spec;
        }
    }

    /// <summary>A window whose event handlers run with the mod's attribution.</summary>
    internal sealed class ModWindow : ICwyModWindow
    {
        private readonly ModHelper _helper;
        private readonly ICwyModWindow _inner;

        internal ModWindow(ModHelper helper, ICwyModWindow inner)
        {
            _helper = helper;
            _inner = inner;
            _inner.CloseClicked += () => Raise(CloseClicked, "CloseClicked");
            _inner.Submitted += text => RaiseSubmitted(text);
        }

        public event Action? CloseClicked;

        public event Action<string>? Submitted;

        public string Title
        {
            get => _inner.Title;
            set => _inner.Title = value;
        }

        public string Status
        {
            get => _inner.Status;
            set => _inner.Status = value;
        }

        public bool IsShown => _inner.IsShown;

        public bool IsAlive => _inner.IsAlive;

        public void Show() => _inner.Show();

        public void Hide() => _inner.Hide();

        public void Destroy() => _inner.Destroy();

        public void SetLines(IReadOnlyList<string>? lines) => _inner.SetLines(lines);

        public void AppendLine(string line) => _inner.AppendLine(line);

        /// <summary>Destroys the window when the mod stops; a failure is logged, never thrown.</summary>
        internal void DestroyQuietly()
        {
            try
            {
                if (_inner.IsAlive)
                {
                    _inner.Destroy();
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    "MODS " + _helper.ModId + ": a window could not be destroyed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void Raise(Action? handlers, string label)
        {
            if (handlers != null)
            {
                _helper.Guard(ModStage.Event, "window " + label, handlers);
            }
        }

        private void RaiseSubmitted(string text)
        {
            var handlers = Submitted;
            if (handlers != null)
            {
                _helper.Guard(ModStage.Event, "window Submitted", () => handlers(text));
            }
        }
    }
}
