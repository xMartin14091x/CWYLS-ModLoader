using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Input;
using CwyLs.ModLoader.Input;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The in-game console. One key opens an overlay that shows what the framework is doing
    /// and lets the user drive its APIs without restarting the game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is the framework half: config, the toggle key, the scrollback, the command
    /// registry's error sink, the built-in commands and the choice of surface. What the console
    /// does lives in <see cref="ConsoleSession"/>; drawing is a surface's job
    /// (<see cref="IConsoleSurface"/>).
    /// </para>
    /// <para>
    /// A game adapter registers a surface that looks like the game's own windows
    /// (<see cref="RegisterSurface"/>). It is used whenever it can be built; when it cannot - the
    /// room is not loaded yet, or a game update renamed the game's UI assets - the IMGUI overlay
    /// (<see cref="ImguiConsoleSurface"/>) is shown instead and the fallback is logged once. The
    /// console therefore never becomes unusable.
    /// </para>
    /// <para>
    /// Game-specific commands do not belong here. The adapter registers its own through
    /// <see cref="Register"/> and adds tabs through <see cref="ConsolePanels"/>, which is why the
    /// core stays free of any game type.
    /// </para>
    /// </remarks>
    public static class ConsoleFeature
    {
        /// <summary>Config section holding the console settings.</summary>
        public const string ConfigSection = "Console";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the background opacity.</summary>
        public const string ConfigKeyOpacity = "Opacity";

        /// <summary>Config key of the text size multiplier.</summary>
        public const string ConfigKeyFontScale = "FontScale";

        /// <summary>Config key of the look: the game's window style or the IMGUI overlay.</summary>
        public const string ConfigKeyStyle = "Style";

        /// <summary>Config key of "fade with the game's Hide UI".</summary>
        public const string ConfigKeyFollowHideUi = "FollowHideUI";

        /// <summary>Registry id of the toggle key.</summary>
        public const string HotkeyId = "console.toggle";

        /// <summary>Prefix of every console line this feature writes to the BepInEx log.</summary>
        public const string LogPrefix = "CONSOLE";

        /// <summary>Group the framework's own commands are listed under by <c>help</c>.</summary>
        public const string FrameworkGroup = "framework";

        /// <summary>
        /// Default primary chord: Ctrl+Shift+C. The chord is primary on macOS and Windows,
        /// because on macOS F10 is a media key unless Fn is held and never reaches the game.
        /// </summary>
        public static readonly KeyboardShortcut ToggleKeyDefault =
            new KeyboardShortcut(KeyCode.C, KeyCode.LeftControl, KeyCode.LeftShift);

        /// <summary>Default alternate chord: F10, which works on Windows.</summary>
        public static readonly KeyboardShortcut ToggleKeyAlternateDefault =
            new KeyboardShortcut(KeyCode.F10);

        /// <summary>The primary default older builds wrote; see <see cref="Core.Input.HotkeyDefaults"/>.</summary>
        public static readonly KeyboardShortcut ToggleKeyLegacyDefault = new KeyboardShortcut(KeyCode.F10);

        /// <summary>The alternate default older builds wrote.</summary>
        public static readonly KeyboardShortcut ToggleKeyLegacyAlternateDefault =
            new KeyboardShortcut(KeyCode.C, KeyCode.LeftControl, KeyCode.LeftShift);

        private static readonly object Gate = new object();
        private static readonly ConsoleBuffer Buffer = new ConsoleBuffer(ConsoleBuffer.DefaultCapacity);
        private static readonly CommandHistory HistoryRing = new CommandHistory();
        private static readonly ConsoleSurfacePolicy SurfacePolicy = new ConsoleSurfacePolicy();
        private static readonly ConsoleVisibility VisibilityRule = new ConsoleVisibility();
        private static readonly HotkeyDebounce ToggleDebounce = new HotkeyDebounce();
        private static readonly ConsoleEscapeGuard EscapeRule = new ConsoleEscapeGuard();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<float>? _opacity;
        private static ConfigEntry<float>? _fontScale;
        private static ConfigEntry<ConsoleStyle>? _style;
        private static ConfigEntry<bool>? _followHideUi;
        private static ConfigEntry<KeyboardShortcut>? _togglePrimary;
        private static ConfigEntry<KeyboardShortcut>? _toggleAlternate;
        private static ConfigFile? _ownConfig;
        private static ConsoleLogListener? _listener;
        private static IConsoleSurface? _gameSurface;
        private static IConsoleSurface? _active;
        private static bool _initialized;

        /// <summary>True when the console may be opened (<c>Console.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>Background opacity, 0.2 to 1 (<c>Console.Opacity</c>, default 0.85).</summary>
        public static float Opacity => Mathf.Clamp(_opacity?.Value ?? 0.85f, 0.2f, 1f);

        /// <summary>Text size multiplier, 0.5 to 3 (<c>Console.FontScale</c>, default 1).</summary>
        public static float FontScale => Mathf.Clamp(_fontScale?.Value ?? 1f, 0.5f, 3f);

        /// <summary>The configured look (<c>Console.Style</c>, default <see cref="ConsoleStyle.Game"/>).</summary>
        public static ConsoleStyle Style => _style?.Value ?? ConsoleStyle.Game;

        /// <summary>
        /// The rule that fades an open console with the game's Hide UI (<c>Console.FollowHideUI</c>)
        /// and lets the toggle reveal it. Surfaces that track the game's UI read it once a frame.
        /// </summary>
        public static ConsoleVisibility Visibility => VisibilityRule;

        /// <summary>
        /// Who owns this frame's Escape. A surface reports every frame it is on screen
        /// (<see cref="ConsoleEscapeGuard.Observe"/>) and claims an Escape it closes on
        /// (<see cref="ConsoleEscapeGuard.Claim"/>); the game adapter asks
        /// <see cref="ConsoleEscapeGuard.ShouldBlockWindowedSwitch"/> before the game's own Escape
        /// handling switches a fullscreen window to windowed mode. The core names no game type: the
        /// adapter owns that patch.
        /// </summary>
        public static ConsoleEscapeGuard EscapeGuard => EscapeRule;

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool IsReady { get; private set; }

        /// <summary>True while the console is open, whichever surface draws it.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>True while the console is open and the IMGUI overlay is the surface drawing it.</summary>
        public static bool IsImguiShowing => IsOpen && ReferenceEquals(_active, ImguiConsoleSurface.Instance);

        /// <summary>Name of the surface drawing the open console, or empty when closed.</summary>
        public static string ActiveSurfaceName => IsOpen && _active != null ? _active.Name : string.Empty;

        /// <summary>Name of the registered game-style surface, or empty when none is registered.</summary>
        public static string RegisteredSurfaceName => _gameSurface?.Name ?? string.Empty;

        /// <summary>Why the game-style surface is off for this session, or empty.</summary>
        public static string SurfaceBrokenReason => SurfacePolicy.BrokenReason;

        /// <summary>The scrollback: command output plus the mirrored CWY log lines.</summary>
        public static ConsoleBuffer Output => Buffer;

        /// <summary>The Up/Down history of submitted lines.</summary>
        public static CommandHistory History => HistoryRing;

        /// <summary>The commands the console knows - the process-wide registry any mod can add to.</summary>
        public static CommandRegistry Commands => CommandRegistry.Shared;

        /// <summary>Every registered command name, sorted. What Tab offers.</summary>
        public static IReadOnlyList<string> CommandNames => CommandRegistry.Shared.Names;

        /// <summary>
        /// Binds the config, registers the toggle key and the built-in commands, and starts
        /// mirroring the CWY log into the scrollback. Called once from
        /// <see cref="ModLoaderPlugin"/>'s Awake; calling it again does nothing.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Console.*</c> entries and the <c>[Keys] console.toggle.*</c> chords
        /// are bound in. Pass the calling plugin's <c>Config</c>; when omitted the framework binds
        /// them in its own .cfg.
        /// </param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
            }

            var owner = config ?? OwnConfig();
            BindConfig(owner);

            CommandRegistry.Shared.ErrorSink = OnCommandError;
            ConsolePanels.ErrorSink = OnPanelError;

            BuiltInCommands.RegisterAll();
            ConsoleSmokeChecks.RegisterAll();

            if (!Enabled)
            {
                IsReady = true;
                ConsoleEvents.Emit(
                    "console", "ready", ConsoleTags.Console,
                    $"off ({ConfigSection}.{ConfigKeyEnabled}=false); no overlay",
                    "commands",
                    ("commands", CommandRegistry.Shared.Count));
                return;
            }

            StartLogMirror();

            Hotkeys.Register(
                HotkeyId,
                "Opens the in-game console overlay: framework state, the pack/smoke/target/key tabs, "
                + "and a command line for the framework APIs.",
                ToggleKeyDefault,
                ToggleKeyAlternateDefault,
                Toggle,
                owner,
                ToggleKeyLegacyDefault,
                ToggleKeyLegacyAlternateDefault);

            // The same two entries the registry polls. A surface whose text field has the keyboard
            // needs them: the registry ignores every chord while a text field is selected.
            owner.TryGetEntry(Hotkeys.ConfigSection, HotkeyId + Hotkeys.PrimarySuffix, out _togglePrimary);
            owner.TryGetEntry(Hotkeys.ConfigSection, HotkeyId + Hotkeys.AlternateSuffix, out _toggleAlternate);

            // Armed now rather than on the first keypress, so a failure to create the host is in
            // the startup log instead of appearing the first time the user presses the key.
            var host = ConsoleHost.Ensure();
            IsReady = true;

            Write(
                $"CWY console ready. {CommandRegistry.Shared.Count} command(s); type 'help'. "
                + "Tab completes, Up/Down repeats, Esc closes.",
                ConsoleLineKind.Info);

            // HotkeyChord.Format(), never BepInEx's serialised "C + LeftShift + LeftControl",
            // and the chords the user actually has bound, not the defaults.
            var chords = Hotkeys.Find(HotkeyId)?.Chords()
                ?? Hotkeys.Chord(ToggleKeyDefault).Format() + " | "
                + Hotkeys.Chord(ToggleKeyAlternateDefault).Format();

            ConsoleEvents.Emit(
                "console",
                "ready",
                ConsoleTags.Console,
                CommandRegistry.Shared.Count + " command(s)"
                + (host == null ? " (no overlay: the host could not be created)" : string.Empty),
                "toggle,commands,style",
                ("toggle", chords),
                ("commands", CommandRegistry.Shared.Count),
                ("style", Style.ToString().ToLowerInvariant()));
        }

        /// <summary>
        /// Registers the surface that draws the console in the game's own style. The game adapter
        /// calls this once; a second registration replaces the first. The IMGUI overlay stays the
        /// fallback whatever is registered.
        /// </summary>
        /// <param name="surface">The surface.</param>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        public static void RegisterSurface(IConsoleSurface surface)
        {
            if (surface is null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            var previous = _gameSurface;
            _gameSurface = surface;

            ConsoleEvents.File(
                "console", "surface",
                ("surface", surface.Name),
                ("replaced", previous?.Name));
        }

        /// <summary>
        /// Called by a surface that failed while running (an exception in its per-frame work). The
        /// game-style surface is switched off for the session and, if the console is open, the
        /// IMGUI overlay takes over at once. Never throws.
        /// </summary>
        /// <param name="surface">The surface that failed.</param>
        /// <param name="error">What it threw.</param>
        public static void ReportSurfaceFailure(IConsoleSurface? surface, Exception? error)
        {
            var reason = error is null ? "unknown failure" : error.GetType().Name + ": " + error.Message;
            var line = SurfacePolicy.RecordFailure((surface?.Name ?? "?") + " " + reason);
            if (line != null)
            {
                CwyLsModLoader.Log.LogError(LogPrefix + " " + line);
            }

            if (surface is null || !ReferenceEquals(_active, surface))
            {
                return;
            }

            HideQuietly(surface);
            _active = null;

            if (IsOpen)
            {
                ShowSurface();
            }
        }

        /// <summary>
        /// True on the frame either <c>console.toggle</c> chord went down. For a surface whose own
        /// text field has the keyboard: the hotkey registry is silent then, so the surface asks here
        /// and calls <see cref="Toggle"/> itself.
        /// </summary>
        public static bool ToggleChordDown()
        {
            try
            {
                return (_togglePrimary != null && _togglePrimary.Value.IsDown())
                    || (_toggleAlternate != null && _toggleAlternate.Value.IsDown());
            }
            catch (Exception ex)
            {
                // A shortcut BepInEx cannot evaluate: the registry path still works when the
                // text field is not selected, so this is a notice.
                CwyLsModLoader.Log.LogWarning($"{LogPrefix} could not read the toggle chord: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Opens the console. Does nothing when it is switched off or already open.</summary>
        public static void Open()
        {
            if (!Enabled || IsOpen)
            {
                return;
            }

            IsOpen = true;
            ShowSurface();

            ConsoleEvents.File("console", "open", ("surface", ActiveSurfaceName));
        }

        /// <summary>Closes the console.</summary>
        public static void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            IsOpen = false;
            VisibilityRule.Reset();

            var active = _active;
            _active = null;
            if (active != null)
            {
                HideQuietly(active);
            }

            ConsoleEvents.File("console", "close", ("surface", active?.Name));
        }

        /// <summary>
        /// The toggle key's action. Opens a closed console and closes an open one - except that a
        /// console faded out with the game's hidden UI is revealed rather than closed, so a press
        /// always ends with the console visible or gone, never with an invisible open console.
        /// Runs at most once per frame: two paths can fire on the same press (the hotkey registry
        /// and a surface whose text field has the keyboard), and the second must not undo the first.
        /// </summary>
        public static void Toggle()
        {
            if (!ToggleDebounce.TryFire(Time.frameCount))
            {
                return;
            }

            // Before opening, the registered game-style surface still knows whether the game's
            // UI is hidden, which is what makes "open while hidden" show the console at once.
            var probe = IsOpen ? _active : _gameSurface;
            var gameUiAlpha = probe != null ? SafeGameUiAlpha(probe) : 1f;
            switch (VisibilityRule.OnToggle(IsOpen, gameUiAlpha))
            {
                case ConsoleToggleIntent.Open:
                    Open();
                    break;
                case ConsoleToggleIntent.Reveal:
                    ConsoleEvents.File("console", "reveal", ("surface", ActiveSurfaceName));
                    break;
                default:
                    Close();
                    break;
            }
        }

        /// <summary>
        /// Appends a line to the scrollback. Safe from any thread; multi-line text is split.
        /// </summary>
        /// <param name="text">The text to show.</param>
        /// <param name="kind">How to colour it.</param>
        public static void Write(string? text, ConsoleLineKind kind = ConsoleLineKind.Output) =>
            Buffer.Add(text, kind);

        /// <summary>
        /// Runs a typed line: echoes it, executes it, shows the result and records it in the
        /// history. Never throws.
        /// </summary>
        /// <param name="line">The line as typed.</param>
        /// <returns>The command's output, for a caller that wants it (the smoke checks do).</returns>
        public static string Submit(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            Buffer.Add("> " + line, ConsoleLineKind.Echo);
            HistoryRing.Add(line);

            var output = CommandRegistry.Shared.Execute(line);
            if (!string.IsNullOrEmpty(output))
            {
                Buffer.Add(output, ConsoleLineKind.Output);
            }

            return output;
        }

        /// <summary>
        /// Registers a command and logs that it arrived. This is the call a mod makes; the
        /// registry itself (<see cref="Commands"/>) is public too, for anything more elaborate.
        /// </summary>
        /// <param name="name">What the user types, e.g. <c>voice</c>.</param>
        /// <param name="description">One short sentence for <c>help</c>.</param>
        /// <param name="usage">The argument shape, e.g. <c>voice list|play &lt;name&gt;</c>.</param>
        /// <param name="group">Heading <c>help</c> lists it under, e.g. <c>game</c>.</param>
        /// <param name="handler">The work; receives the arguments after the name.</param>
        public static void Register(
            string name,
            string? description,
            string? usage,
            string? group,
            Func<IReadOnlyList<string>, string?> handler) =>
            CommandRegistry.Shared.Register(name, description, usage, group, handler);

        /// <summary>
        /// Puts the open console on screen: the game-style surface when it is registered, wanted
        /// and buildable, the IMGUI overlay otherwise.
        /// </summary>
        private static void ShowSurface()
        {
            VisibilityRule.FollowGameUi = _followHideUi?.Value ?? true;

            var configured = SurfacePolicy.RecordConfiguredStyle(Style);
            if (configured != null)
            {
                ConsoleEvents.Emit("console", "style", ConsoleTags.Console, configured, null);
            }

            var game = _gameSurface;
            if (game != null && SurfacePolicy.Preferred(Style, true) == ConsoleSurfaceKind.GameStyle)
            {
                bool shown;
                string detail;
                try
                {
                    shown = game.TryShow(out detail);
                }
                catch (Exception ex)
                {
                    // TryShow is documented not to throw; one that does is broken, not unavailable.
                    ReportSurfaceFailure(game, ex);
                    shown = false;
                    detail = string.Empty;
                }

                if (shown)
                {
                    _active = game;
                    var line = SurfacePolicy.RecordAttempt(true, detail);
                    if (line != null)
                    {
                        ConsoleEvents.Emit("console", "surface", ConsoleTags.Console, line, "surface", ("surface", game.Name));
                    }

                    return;
                }

                if (!SurfacePolicy.Broken)
                {
                    var line = SurfacePolicy.RecordAttempt(false, detail);
                    if (line != null)
                    {
                        ConsoleEvents.EmitAt(
                            "console", "fallback", ConsoleTags.Console, ConsoleLevel.Warn, line, "surface",
                            ("surface", ImguiConsoleSurface.SurfaceName));
                    }
                }
            }

            if (ImguiConsoleSurface.Instance.TryShow(out var problem))
            {
                _active = ImguiConsoleSurface.Instance;
                return;
            }

            // Both surfaces failed: nothing can be drawn, so the console is not really open.
            _active = null;
            IsOpen = false;
            CwyLsModLoader.Log.LogError($"{LogPrefix} the console cannot be shown: {problem}");
        }

        private static float SafeGameUiAlpha(IConsoleSurface surface)
        {
            try
            {
                return surface.GameUiAlpha;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} {surface.Name} could not read the game's UI state: {ex.GetType().Name}: {ex.Message}");
                return 1f;
            }
        }

        private static void HideQuietly(IConsoleSurface surface)
        {
            try
            {
                surface.Hide();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix} {surface.Name} threw while hiding: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void BindConfig(ConfigFile file)
        {
            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Allow the in-game console overlay. Off registers the commands but never draws "
                + "anything and binds no key.");

            _opacity = file.Bind(
                ConfigSection,
                ConfigKeyOpacity,
                0.85f,
                new ConfigDescription(
                    "How opaque the overlay's background is. 1 hides the game behind it completely "
                    + "(the game-style window stops at the game's own 0.9).",
                    new AcceptableValueRange<float>(0.2f, 1f)));

            _fontScale = file.Bind(
                ConfigSection,
                ConfigKeyFontScale,
                1f,
                new ConfigDescription(
                    "Text size multiplier on top of the size the overlay picks from the window "
                    + "height. Raise it on a 4K display.",
                    new AcceptableValueRange<float>(0.5f, 3f)));

            _style = file.Bind(
                ConfigSection,
                ConfigKeyStyle,
                ConsoleStyle.Game,
                "Game: draw the console as one of the game's own windows (its frame, font, colours "
                + "and buttons) whenever the game's UI assets can be found, IMGUI otherwise. "
                + "Imgui: always the plain overlay.");

            _followHideUi = file.Bind(
                ConfigSection,
                ConfigKeyFollowHideUi,
                true,
                "Fade an open game-style console out while the game's Hide UI is on, and back in "
                + "with the game's UI. The toggle key always shows it anyway.");

            VisibilityRule.FollowGameUi = _followHideUi.Value;
        }

        private static void StartLogMirror()
        {
            try
            {
                _listener = new ConsoleLogListener(Buffer);
                BepInEx.Logging.Logger.Listeners.Add(_listener);
            }
            catch (Exception ex)
            {
                // The overlay still works; it just will not show what the rest of the framework
                // is logging, which is worth saying out loud once.
                CwyLsModLoader.Log.LogWarning(
                    $"{LogPrefix}: the log mirror could not be started, so the console shows only "
                    + $"command output: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void OnCommandError(string message) =>
            CwyLsModLoader.Log.LogError(LogPrefix + " " + message);

        private static void OnPanelError(string message) =>
            CwyLsModLoader.Log.LogWarning(LogPrefix + " " + message);

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                System.IO.Path.Combine(BepInEx.Paths.ConfigPath, CwyLsModLoader.PluginGuid + ".console.cfg"),
                saveOnInit: true);
    }
}
