using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Core.Ui;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The mod-window manager: the <c>helper.Ui</c> backend, the registry of every mod window
    /// (order, the window the player is in), the game's Hide UI, remembered places, and focus
    /// isolation - who an Escape belongs to (<see cref="EscapeRouting"/>), with the game's own
    /// fullscreen-to-windowed switch skipped for an Escape a mod window used (<see cref="EscapeGuard"/>,
    /// read by <c>ConsoleEscapePatch</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per frame, in the overlay canvas's <c>LateUpdate</c> (<see cref="GameStyleCanvas.LateTick"/>):
    /// a mouse press over a CWY window brings it to the front (a mod window also becomes the active
    /// one), a press anywhere else makes no mod window active; windows fade with the game's Hide UI
    /// and a faded window gives the keyboard back; then Escape is routed and the owner of the next
    /// Escape recorded. <c>LateUpdate</c> runs after every <c>Update</c>, so the console's and the
    /// game's handling of the same key has already happened and is read, never raced.
    /// </para>
    /// <para>
    /// Clicks outside a window's rectangle are never taken: the overlay canvas has no full-screen
    /// graphic, and a hidden or faded window stops catching the pointer. A click inside a window does
    /// not reach the room - the game ignores a click on Satone while any UI is under the pointer
    /// (<c>FacilityClickHeroine</c>, <c>InputController</c>).
    /// </para>
    /// <para>Main thread only, except <see cref="Status"/>.</para>
    /// </remarks>
    public static class ModWindows
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Ui";

        /// <summary>Config key of the feature switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>The file the remembered places are kept in, under <c>BepInEx/data/ui/</c>.</summary>
        public const string RectFileName = "windows.json";

        /// <summary>
        /// Trace <c>sys</c> of every record this feature writes (file only). Not <c>ui</c>: that family
        /// is the trace plugin's Unity control events (BUTTON, TYPED).
        /// </summary>
        internal const string TraceSys = "windows";

        /// <summary>Seconds between a moved window and the write of the places file.</summary>
        private const double SaveDelaySeconds = 1.0;

        private static readonly List<ModWindow> Windows = new List<ModWindow>();
        private static readonly List<ModWindow> Ordered = new List<ModWindow>();
        private static readonly WindowStack Stack = new WindowStack();
        private static readonly object Gate = new object();
        private static WindowRectStore _rects = new WindowRectStore();
        private static string _rectProblem = string.Empty;
        private static ConfigEntry<bool>? _enabled;
        private static DelayedWork? _pendingSave;
        private static bool _initialised;
        private static bool _ticking;
        private static int _shownCount;
        private static long _escapes;

        /// <summary>Records who owns the next Escape; the console's Escape patch reads it.</summary>
        public static WindowEscapeGuard EscapeGuard { get; } = new WindowEscapeGuard();

        /// <summary>True when mod windows are switched on (<c>[Ui] Enabled</c>).</summary>
        public static bool Enabled => _enabled?.Value ?? false;

        /// <summary>Mod windows alive.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Windows.Count;
                }
            }
        }

        /// <summary>One line: on/off, windows, shown, remembered places. Any thread.</summary>
        public static string Status
        {
            get
            {
                if (!_initialised)
                {
                    return "mod windows are not initialised";
                }

                if (!Enabled)
                {
                    return "mod windows are off (" + ConfigSection + "." + ConfigKeyEnabled + "=false)";
                }

                lock (Gate)
                {
                    return "mod windows on: " + Windows.Count + " window(s), " + _shownCount + " shown, "
                        + _rects.Count + " remembered place(s)";
                }
            }
        }

        /// <summary>Absolute path of the places file.</summary>
        public static string RectFilePath => Path.Combine(Path.Combine(ModLoaderPaths.DataRoot, "ui"), RectFileName);

        /// <summary>
        /// Binds <c>[Ui] Enabled</c>, loads the remembered places, installs the <c>helper.Ui</c> backend,
        /// makes sure the Escape patch is in place, and registers the <c>windows</c> command and the smoke
        /// checks. Call once from <c>BulbulPlugin.Awake</c>, after <c>BulbulModBackends.Register()</c>.
        /// </summary>
        /// <param name="config">The plugin's config file.</param>
        public static void Initialize(ConfigFile config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (Gate)
            {
                if (_initialised)
                {
                    return;
                }

                _initialised = true;
            }

            _enabled = config.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Windows for code mods (helper.Ui.CreateWindow): game-style windows with text, lists, chat bubbles, " +
                "text boxes, buttons and tabs. Off: no mod can open a window, and open ones are hidden.");
            _enabled.SettingChanged += (_, __) => OnEnabledChanged();

            LoadRects();
            ModFacetBackends.Use<IModWindowBackend>(ModUi.FacetName, new Backend());

            // The Escape patch is the console's, generalised here; Apply is idempotent.
            global::CwyLs.ModLoader.Bulbul.Console.ConsoleEscapePatch.Apply();

            ModWindowCommands.Register();
            ModWindowSmokeChecks.Register();
            Application.quitting += SaveNow;

            ConsoleEvents.Setup(
                TraceSys,
                Enabled ? "mod windows armed" : "mod windows off",
                ("enabled", Enabled),
                ("remembered", _rects.Count),
                ("escapePatch", global::CwyLs.ModLoader.Bulbul.Console.ConsoleEscapePatch.Applied),
                ("command", ModWindowCommands.Name));

            if (_rectProblem.Length > 0)
            {
                CwyLsModLoader.Log.LogWarning(GameStyleUi.LogPrefix + " " + RectFilePath + ": " + _rectProblem + " (those windows open at their default place)");
            }
        }

        /// <summary>Builds a hidden window for an owner (the backend's work). Main thread.</summary>
        /// <param name="owner">Who owns it.</param>
        /// <param name="spec">What to build.</param>
        internal static CwyWindowResult Create(ModWindowOwner owner, CwyWindowSpec spec)
        {
            if (!MainThread.IsMainThread)
            {
                return CwyWindowResult.Refused("windows are built on the main thread");
            }

            if (!Enabled)
            {
                return CwyWindowResult.Refused(Status);
            }

            var key = CwyWindowSpecRules.Key(owner.ModId, spec.Id);
            if (Find(key) != null)
            {
                return CwyWindowResult.Refused("a window '" + key + "' is already open; destroy it first");
            }

            WindowRect? remembered = null;
            if (spec.RememberRect && _rects.TryGet(key, out var rect))
            {
                remembered = rect;
            }

            var result = ModWindow.Build(owner, spec, remembered);
            if (!(result.Window is ModWindow window))
            {
                ConsoleEvents.File(TraceSys, "refused", ("window", key), ("problem", result.Problem));
                return result;
            }

            lock (Gate)
            {
                Windows.Add(window);
            }

            Stack.Add(key);
            if (!_ticking)
            {
                _ticking = true;
                GameStyleCanvas.LateTick += LateTick;
            }

            ConsoleEvents.File(
                TraceSys, "created", ("window", key), ("object", window.ObjectName), ("parts", spec.Parts.Count),
                ("remembered", remembered.HasValue), ("rect", window.Rect.ToString()));
            return result;
        }

        /// <summary>A window was shown: it goes on top and the player is in it.</summary>
        internal static void Shown(ModWindow window)
        {
            Stack.Shown(window.Key);
            window.Frame.BringToFront();
        }

        /// <summary>A window was hidden.</summary>
        internal static void Hidden(ModWindow window) => Stack.Hidden(window.Key);

        /// <summary>Brings a window to the front and makes it the active one.</summary>
        internal static void Activate(ModWindow window)
        {
            Stack.Activate(window.Key);
            window.Frame.BringToFront();
        }

        /// <summary>A window was destroyed.</summary>
        internal static void Unregister(ModWindow window)
        {
            lock (Gate)
            {
                Windows.Remove(window);
            }

            Stack.Remove(window.Key);
            ConsoleEvents.File(TraceSys, "destroyed", ("window", window.Key));
        }

        /// <summary>The player finished moving or resizing a window: remember it (written a second later).</summary>
        internal static void RememberPlace(ModWindow window)
        {
            if (!window.Model.RememberRect)
            {
                return;
            }

            bool changed;
            lock (Gate)
            {
                changed = _rects.Set(window.Key, window.Frame.Place) && _rects.IsDirty;
            }

            if (changed)
            {
                ScheduleSave();
            }
        }

        /// <summary>Forgets a remembered place.</summary>
        /// <param name="key">The window's key.</param>
        /// <returns>True when one was remembered.</returns>
        internal static bool Forget(string key)
        {
            bool removed;
            lock (Gate)
            {
                removed = _rects.Remove(key);
            }

            if (removed)
            {
                ScheduleSave();
            }

            return removed;
        }

        /// <summary>Forgets every remembered place.</summary>
        /// <returns>How many were forgotten.</returns>
        internal static int ForgetAll()
        {
            int count;
            lock (Gate)
            {
                count = _rects.Clear();
            }

            if (count > 0)
            {
                ScheduleSave();
            }

            return count;
        }

        /// <summary>The live windows, bottom first, for the command and the smoke checks.</summary>
        internal static IReadOnlyList<ModWindow> Snapshot()
        {
            var ordered = new List<ModWindow>();
            FillOrdered(ordered);
            return ordered;
        }

        /// <summary>Fills <paramref name="ordered"/> with the live windows, bottom first (no allocation).</summary>
        private static void FillOrdered(List<ModWindow> ordered)
        {
            ordered.Clear();
            lock (Gate)
            {
                foreach (var key in Stack.Order)
                {
                    foreach (var window in Windows)
                    {
                        if (window.Key == key)
                        {
                            ordered.Add(window);
                        }
                    }
                }
            }
        }

        /// <summary>The window the player is in, or null.</summary>
        internal static string? ActiveKey => Stack.Active;

        /// <summary>Escapes routed to a mod window since start.</summary>
        internal static long EscapesTaken => System.Threading.Interlocked.Read(ref _escapes);

        /// <summary>The live window with this key, or null.</summary>
        /// <param name="key"><c>&lt;modId&gt;/&lt;windowId&gt;</c>.</param>
        internal static ModWindow? Find(string key)
        {
            lock (Gate)
            {
                foreach (var window in Windows)
                {
                    if (string.Equals(window.Key, key, StringComparison.OrdinalIgnoreCase) && window.IsAlive)
                    {
                        return window;
                    }
                }
            }

            return null;
        }

        private static void LateTick()
        {
            var frame = Time.frameCount;
            if (Count == 0)
            {
                // Nothing to order, fade or route: an Escape is never a mod window's.
                EscapeGuard.Observe(frame, false);
                return;
            }

            Prune();
            Pointer();
            FillOrdered(Ordered);
            FollowHideUi(Ordered);
            RouteEscape(frame, Ordered);
        }

        private static void Prune()
        {
            List<ModWindow>? dead = null;
            lock (Gate)
            {
                foreach (var window in Windows)
                {
                    if (!window.CheckAlive())
                    {
                        (dead ??= new List<ModWindow>()).Add(window);
                    }
                }
            }

            if (dead == null)
            {
                return;
            }

            foreach (var window in dead)
            {
                Unregister(window);
            }
        }

        /// <summary>A mouse press over a CWY window brings it to the front; anywhere else no mod window is active.</summary>
        private static void Pointer()
        {
            var input = UnityInput.Current;
            if (!input.GetMouseButtonDown(0) && !input.GetMouseButtonDown(1) && !input.GetMouseButtonDown(2))
            {
                return;
            }

            var canvas = GameStyleCanvas.Root;
            if (canvas == null)
            {
                return;
            }

            Vector2 point = input.mousePosition;
            for (var i = canvas.childCount - 1; i >= 0; i--)
            {
                var child = canvas.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var frame = GameStyleWindow.Of(child);
                if (frame == null || !frame.IsInteractable || !RectTransformUtility.RectangleContainsScreenPoint(child, point, null))
                {
                    continue;
                }

                var window = WindowOf(frame);
                if (window != null)
                {
                    Activate(window);
                }
                else
                {
                    // The console (or another framework window): it comes up, and no mod window is active.
                    Stack.PointerDown(null);
                    frame.BringToFront();
                }

                return;
            }

            Stack.PointerDown(null);
        }

        private static void FollowHideUi(List<ModWindow> windows)
        {
            var shown = 0;
            var alpha = -1f;
            foreach (var window in windows)
            {
                if (!window.IsShown)
                {
                    continue;
                }

                shown++;
                if (window.Model.FollowHideUi)
                {
                    if (alpha < 0f)
                    {
                        alpha = GameUiBridge.GameUiAlpha();
                    }

                    window.Frame.ExternalAlpha = alpha;
                }
                else
                {
                    window.Frame.ExternalAlpha = 1f;
                }

                if (!window.IsInteractable)
                {
                    // Faded out with the game's hidden UI: typing must go back to the game.
                    window.ReleaseKeyboard();
                }
            }

            lock (Gate)
            {
                _shownCount = shown;
            }
        }

        private static void RouteEscape(int frame, List<ModWindow> windows)
        {
            var escapeDown = UnityInput.Current.GetKeyDown(KeyCode.Escape);
            if (escapeDown)
            {
                var decision = EscapeRouting.Decide(Inputs(frame, windows));
                Act(decision, frame);
            }

            // Who owns an Escape pressed next frame: the game's own handling reads this before our LateUpdate.
            var next = EscapeRouting.Decide(Inputs(frame, windows).WithoutClaim());
            EscapeGuard.Observe(frame, next.ModWindowOwns);
        }

        private static void Act(EscapeDecision decision, int frame)
        {
            if (decision.Window is null || !decision.ModWindowOwns)
            {
                return;
            }

            var window = Find(decision.Window);
            if (window == null)
            {
                return;
            }

            System.Threading.Interlocked.Increment(ref _escapes);
            ConsoleEvents.File(TraceSys, "escape", ("window", decision.Window), ("action", decision.Action), ("frame", frame));
            switch (decision.Action)
            {
                case EscapeAction.CloseWindow:
                    window.ReleaseKeyboard();
                    window.RequestClose(CwyCloseReason.Escape);
                    break;
                case EscapeAction.ReleaseKeyboard:
                    window.ReleaseKeyboard();
                    break;
            }
        }

        /// <summary>Who has the keyboard now, for <see cref="EscapeRouting"/>.</summary>
        private static EscapeInputs Inputs(int frame, List<ModWindow> windows)
        {
            var console = Framework.ConsoleFeature.EscapeGuard;
            var claimed = console.ClaimedFrame != ConsoleEscapeGuard.NoFrame && console.ClaimedFrame == frame;
            var observed = console.ObservedFrame;
            var consoleOwns = Framework.ConsoleFeature.IsOpen
                && console.ObservedOwner == EscapeOwner.Console
                && observed != ConsoleEscapeGuard.NoFrame
                && observed <= frame
                && (long)frame - observed <= 1L;

            ModWindow? keyboard = null;
            InputPartView? keyboardInput = null;
            ModWindow? active = null;
            var activeKey = Stack.Active;
            for (var i = windows.Count - 1; i >= 0; i--)
            {
                var window = windows[i];
                if (!window.IsAlive || !window.IsInteractable)
                {
                    continue;
                }

                if (keyboard == null)
                {
                    var input = window.KeyboardInput;
                    if (input != null)
                    {
                        keyboard = window;
                        keyboardInput = input;
                    }
                }

                if (active == null && activeKey != null && window.Key == activeKey)
                {
                    active = window;
                }
            }

            return new EscapeInputs(
                claimed,
                consoleOwns,
                keyboard?.Key,
                keyboardInput != null && keyboardInput.ComposingRecently,
                keyboard != null && keyboard.Model.CloseOnEscape,
                GameTextFieldSelected(),
                active?.Key,
                active != null && active.Model.CloseOnEscape);
        }

        /// <summary>True when one of the game's own text fields (not one on the CWY canvas) has the keyboard.</summary>
        private static bool GameTextFieldSelected()
        {
            var system = EventSystem.current;
            var selected = system == null ? null : system.currentSelectedGameObject;
            if (selected == null)
            {
                return false;
            }

            if (selected.GetComponent<TMP_InputField>() == null && selected.GetComponent<UnityEngine.UI.InputField>() == null)
            {
                return false;
            }

            var canvas = GameStyleCanvas.Root;
            return canvas == null || !selected.transform.IsChildOf(canvas);
        }

        private static ModWindow? WindowOf(GameStyleWindow frame)
        {
            lock (Gate)
            {
                foreach (var window in Windows)
                {
                    if (ReferenceEquals(window.Frame, frame))
                    {
                        return window;
                    }
                }
            }

            return null;
        }

        private static void OnEnabledChanged()
        {
            ConsoleEvents.File(TraceSys, "enabled", ("enabled", Enabled));
            if (Enabled)
            {
                return;
            }

            MainThread.RunOrPost("ui.disable", () =>
            {
                foreach (var window in Snapshot())
                {
                    if (window.IsShown)
                    {
                        window.Hide();
                    }
                }
            });
        }

        private static void LoadRects()
        {
            var path = RectFilePath;
            try
            {
                var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
                var store = WindowRectStore.Parse(text, out var problem);
                lock (Gate)
                {
                    _rects = store;
                    _rectProblem = problem;
                }
            }
            catch (Exception ex)
            {
                lock (Gate)
                {
                    _rects = new WindowRectStore();
                    _rectProblem = "could not be read: " + ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        private static void ScheduleSave()
        {
            try
            {
                _pendingSave?.Dispose();
                _pendingSave = MainThread.PostAfter(SaveDelaySeconds, "ui.rects.save", SaveNow);
            }
            catch (Exception ex)
            {
                // The dispatcher refused (shutting down): write now instead.
                ConsoleEvents.File(TraceSys, "saveDeferredFailed", ("error", ex.GetType().Name + ": " + ex.Message));
                SaveNow();
            }
        }

        /// <summary>Writes the places file when something changed: temporary file, then replace.</summary>
        private static void SaveNow()
        {
            string json;
            lock (Gate)
            {
                if (!_rects.IsDirty)
                {
                    return;
                }

                json = _rects.ToJson();
            }

            var path = RectFilePath;
            var temp = path + ".tmp~";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }

                ConsoleEvents.File(TraceSys, "rectsSaved", ("path", path), ("count", _rects.Count));
            }
            catch (Exception ex)
            {
                GameStyleUi.WarnOnce("could not save window places to " + path + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The <c>helper.Ui</c> backend.</summary>
        private sealed class Backend : IModWindowBackend
        {
            public bool IsAvailable => Enabled;

            public string Status => ModWindows.Status;

            public CwyWindowResult CreateWindow(ModWindowOwner owner, CwyWindowSpec spec) => Create(owner, spec);
        }
    }

    /// <summary>
    /// The <c>windows</c> console command. Not <c>ui</c>: that name is kept free for a possible sprite reskin feature (<c>ui scan|restore</c>),
    /// and a name another owner holds is refused.
    /// </summary>
    internal static class ModWindowCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "windows";

        /// <summary>The usage line.</summary>
        internal const string Usage = "windows [status]|list|demo|forget <modId/windowId>|forget all";

        /// <summary>Registers <c>windows</c>.</summary>
        internal static void Register() =>
            Framework.ConsoleFeature.Register(
                Name,
                "Mod windows: what is open, a demo window (Thai, chat, list, tabs), forget remembered places.",
                Usage,
                "ui",
                Run);

        /// <summary>Runs one <c>windows</c> line.</summary>
        /// <param name="args">Arguments after the name.</param>
        internal static string Run(IReadOnlyList<string> args)
        {
            var verb = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
            switch (verb)
            {
                case "status":
                    return "WINDOWS: " + ModWindows.Status + "; escapes taken " + ModWindows.EscapesTaken.ToString(CultureInfo.InvariantCulture)
                        + ", game window switches skipped " + ModWindows.EscapeGuard.BlockedCount.ToString(CultureInfo.InvariantCulture);
                case "list":
                    return List();
                case "demo":
                    return ModWindowDemo.Open();
                case "forget":
                    if (args.Count < 2)
                    {
                        return Usage;
                    }

                    if (string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase))
                    {
                        return "forgot " + ModWindows.ForgetAll() + " remembered place(s)";
                    }

                    return ModWindows.Forget(args[1]) ? "forgot " + args[1] : "no remembered place for " + args[1];
                default:
                    return Usage;
            }
        }

        private static string List()
        {
            var windows = ModWindows.Snapshot();
            if (windows.Count == 0)
            {
                return "no mod windows";
            }

            var active = ModWindows.ActiveKey;
            var sb = new StringBuilder();
            sb.Append(windows.Count).Append(" mod window(s), bottom first:");
            foreach (var window in windows)
            {
                sb.Append("\n  ").Append(window.Key)
                    .Append(window.IsShown ? (window.IsInteractable ? " shown" : " faded") : " hidden")
                    .Append(window.Key == active ? " active" : string.Empty)
                    .Append(window.HasKeyboard ? " keyboard" : string.Empty)
                    .Append(" rect=").Append(window.Rect.ToString())
                    .Append(" object=").Append(window.ObjectName);
            }

            return sb.ToString();
        }
    }
}
