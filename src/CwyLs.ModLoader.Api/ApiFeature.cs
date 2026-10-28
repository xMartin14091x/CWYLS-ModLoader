using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Input;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Api
{
    /// <summary>
    /// The local game API's lifecycle in the game: <c>[Api]</c> config, start and stop, the kill
    /// switch, key rotation, the wiring of game events and data into the Unity-free
    /// <see cref="ApiRuntime"/>, and the once-a-frame state refresh. Everything here runs on the
    /// main thread (plugin Awake, console commands, hotkeys, the dispatcher ticker,
    /// <c>Application.quitting</c>); request threads only ever see the runtime.
    /// </summary>
    /// <remarks>
    /// <c>[Api] Enabled=false</c> (the default) means: no listener, no open port, no key file and no
    /// event subscriptions. The console command <c>api on</c> starts it for the session on the
    /// player's explicit request.
    /// </remarks>
    internal static class ApiFeature
    {
        /// <summary>Config section.</summary>
        internal const string Section = "Api";

        /// <summary>Hotkey id of the kill switch.</summary>
        internal const string KillKeyId = "api.kill";

        /// <summary>Dispatcher owner of the state ticker (framework lane).</summary>
        internal const string Owner = "api";

        /// <summary>Feature name for setup lines.</summary>
        internal const string Feature = "api";

        /// <summary>The kill switch's default chord, Ctrl+Shift+K.</summary>
        internal static readonly KeyboardShortcut KillKeyDefault = new KeyboardShortcut(KeyCode.K, KeyCode.LeftControl, KeyCode.LeftShift);

        private static readonly ApiInfoFacts Facts = new ApiInfoFacts();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<int>? _port;
        private static ConfigEntry<ApiTransportMode>? _transport;
        private static ConfigEntry<bool>? _allowActions;
        private static ConfigEntry<bool>? _consoleReads;
        private static volatile Session? _session;
        private static volatile string _offReason = "not initialised";
        private static bool _initialised;

        /// <summary>True while the API listens.</summary>
        internal static bool IsRunning => _session != null;

        /// <summary>The base address while listening; empty otherwise.</summary>
        internal static string Endpoint => _session is Session s ? ApiDefaults.BaseUrl(s.Runtime.Router.Port) : string.Empty;

        /// <summary><c>[Api] Enabled</c>.</summary>
        internal static bool EnabledInConfig => _enabled?.Value ?? false;

        /// <summary>Why the API is off (empty while on).</summary>
        internal static string OffReason => _session is null ? _offReason : string.Empty;

        /// <summary>The key file: <c>BepInEx/data/api/token.json</c>.</summary>
        internal static string KeyFilePath => Path.Combine(ModLoaderPaths.DataRoot, ApiDefaults.TokenFolderName, ApiDefaults.TokenFileName);

        /// <summary>The running session, or null (smoke checks).</summary>
        internal static Session? Current => _session;

        /// <summary>The facet's one-line status.</summary>
        internal static string FacetStatus =>
            _session is Session s
                ? "the local game API listens on " + ApiDefaults.BaseUrl(s.Runtime.Router.Port) + " (" + (s.Transport?.Name ?? "-") + ")"
                : "the local game API is off: " + _offReason;

        /// <summary>Binds the config and registers the command, key, facet and smoke check; starts the API when enabled. Called once from the plugin's Awake.</summary>
        /// <param name="config">The plugin's config file.</param>
        internal static void Initialize(ConfigFile config)
        {
            if (_initialised)
            {
                return;
            }

            _initialised = true;
            _enabled = config.Bind(
                Section,
                "Enabled",
                false,
                "Turns on the local game API for programs on this computer (tools, overlays, companion apps). Off by default: " +
                "when false there is no listener, no open port and no key file. When true it listens only on 127.0.0.1 " +
                "and writes a fresh access key at each start to BepInEx/data/api/token.json, readable only by your user account.");
            _port = config.Bind(
                Section,
                "Port",
                ApiDefaults.DefaultPort,
                new ConfigDescription(
                    "The port on 127.0.0.1. If another program holds it, the API stays off with one warning; it never moves to another port.",
                    new AcceptableValueRange<int>(ApiDefaults.MinPort, ApiDefaults.MaxPort)));
            _transport = config.Bind(
                Section,
                "Transport",
                ApiTransportMode.Auto,
                "Auto: HttpListener, and the built-in TCP server if HttpListener cannot start (for any reason but a taken port). " +
                "HttpListener or Tcp: that one only.");
            _allowActions = config.Bind(
                Section,
                "AllowActions",
                false,
                "Lets outside programs run actions that change the game (play music, start a timer). Reads are always allowed " +
                "once the API is on. Writes to todos/notes also need [Data] AllowWrites and your Yes in the game's own confirmation.");
            _consoleReads = config.Bind(
                Section,
                "ConsoleAuditReads",
                false,
                "Print an API console line for every successful read (state, events). Off: those go to the trace file only; " +
                "actions and refused requests always print.");

            ApiSwitches.AllowActions = _allowActions.Value;
            _allowActions.SettingChanged += (_, __) => ApiSwitches.AllowActions = _allowActions.Value;

            // The action runner lets API act/write actions through only while [Api] AllowActions is on.
            Core.Actions.ActionServices.Shared.UseApiActionsSwitch(() => ApiSwitches.AllowActions);
            Facts.LoaderVersion = CwyLsModLoader.PluginVersion;
            Facts.ModApiVersion = CwyApi.VersionText;
            Facts.GameVersion = Application.version;

            ApiCommands.Register();
            Hotkeys.Register(
                KillKeyId,
                "Kill switch of the local game API: closes the listener at once and deletes the key file. " +
                "Type 'api on' in the console to start it again.",
                KillKeyDefault,
                KeyboardShortcut.Empty,
                Kill,
                config);
            ModFacetBackends.Use<ICwyModApi>("api", new ApiFacetBackend());
            ApiSmokeChecks.Register();
            Application.quitting += () => Stop("the game is quitting", announce: false);

            // A key file left behind by a crash describes a listener that no longer exists.
            RemoveKeyFile("startup", quietWhenAbsent: true);

            if (_enabled.Value)
            {
                Start("config");
            }
            else
            {
                _offReason = "[Api] Enabled=false";
                ConsoleEvents.Setup(
                    Feature,
                    "local API off ([Api] Enabled=false): no listener, no port, no key file",
                    ("port", _port.Value),
                    ("killKey", Hotkeys.Chord(KillKeyDefault).Format()));
            }
        }

        /// <summary>Starts the API (config at boot, or <c>api on</c>). Main thread.</summary>
        /// <param name="trigger">What asked, for the line.</param>
        /// <returns>One line for the console.</returns>
        internal static string Start(string trigger)
        {
            if (_session is Session running)
            {
                return "the API is already on: " + ApiDefaults.BaseUrl(running.Runtime.Router.Port) + " (" + (running.Transport?.Name ?? "-") + ")";
            }

            var port = _port?.Value ?? ApiDefaults.DefaultPort;
            var mode = _transport?.Value ?? ApiTransportMode.Auto;
            var key = ApiToken.Create();
            var runtime = new ApiRuntime(key, new ApiRuntimeOptions
            {
                Port = port,
                AllowActions = () => ApiSwitches.AllowActions,
                ConsoleAuditReads = () => _consoleReads?.Value ?? false,
                Facts = Facts,
                GameData = () => ModBackends.GameData?.Snapshot(),
                Language = () => ModBackends.Language is ICwyModLanguage language && language.IsAvailable ? language.CurrentCode : null,
                Diagnostics = Diagnose,
            });
            SeedLive(runtime);

            var session = new Session(runtime, key);
            session.EnsureSubscriptions();
            session.Ticker = MainThread.AddTicker("api.state", () => session.Tick(), Owner);
            session.Tick();

            var started = ApiServer.Start(port, mode, runtime.Router.Handle, Diagnose);
            if (!started.IsRunning || started.Transport is null)
            {
                session.Close();
                _offReason = started.Problem == ApiStartProblem.PortTaken
                    ? "port " + port + " is taken by another program (set [Api] Port)"
                    : started.Detail;
                Warn("off", "local API off: " + _offReason, ("port", port), ("problem", started.Problem.ToString()), ("trigger", trigger));
                return "the API did not start: " + _offReason;
            }

            session.Transport = started.Transport;
            runtime.Router.TransportName = started.Transport.Name;
            runtime.Router.FallbackReason = started.FallbackReason;

            var file = ApiKeyFile.Write(KeyFilePath, ApiKeyFile.Content(key, port, started.Transport.Name, DateTimeOffset.Now));
            if (!file.Ok)
            {
                runtime.Router.BeginStop();
                started.Transport.Stop();
                session.Close();
                _offReason = "the key file could not be made owner-only: " + file.Problem;
                Warn("off", "local API off: " + _offReason, ("keyFile", file.Path), ("trigger", trigger));
                return "the API did not start: " + _offReason;
            }

            _session = session;
            _offReason = string.Empty;
            if (started.FallbackReason != null)
            {
                Warn("fallback", "local API uses the TCP fallback: " + started.FallbackReason, ("port", port));
            }

            ConsoleEvents.Emit(
                ApiAuditLog.Sys,
                "on",
                ConsoleTags.Api,
                "on " + ApiDefaults.BaseUrl(port),
                "transport,keyFile,allowActions,keyId,trigger",
                ("transport", started.Transport.Name),
                ("keyFile", file.Path),
                ("allowActions", ApiSwitches.AllowActions),
                ("keyId", key.Id),
                ("trigger", trigger));
            return "API on " + ApiDefaults.BaseUrl(port) + " (" + started.Transport.Name + "); key file " + file.Path +
                   (EnabledInConfig ? string.Empty : " (for this session; [Api] Enabled stays false)");
        }

        /// <summary>Stops the API: listener closed, long-polls woken, key file deleted. Main thread; idempotent.</summary>
        /// <param name="reason">Why, for the line.</param>
        /// <param name="announce">False at quit (no console line while the game closes; the file record stays).</param>
        /// <returns>One line for the console.</returns>
        internal static string Stop(string reason, bool announce = true)
        {
            var session = _session;
            if (session is null)
            {
                return "the API is already off: " + _offReason;
            }

            _session = null;
            _offReason = reason;
            session.Runtime.Router.BeginStop();
            session.Transport?.Stop();
            session.Close();
            var removed = RemoveKeyFile(reason, quietWhenAbsent: true);
            if (announce)
            {
                ConsoleEvents.Emit(
                    ApiAuditLog.Sys,
                    "off",
                    ConsoleTags.Api,
                    "off (" + reason + ")",
                    "requests,keyFile",
                    ("requests", session.Runtime.Router.Counters.Total),
                    ("keyFile", removed ? "deleted" : "NOT deleted"));
            }
            else
            {
                ConsoleEvents.File(ApiAuditLog.Sys, "off", ("reason", reason), ("requests", session.Runtime.Router.Counters.Total), ("keyFileDeleted", removed));
            }

            return "API off (" + reason + "); key file " + (removed ? "deleted" : "could NOT be deleted: " + KeyFilePath);
        }

        /// <summary>Mints a new access key; clients holding the old one get 401 and must re-read the key file. Main thread.</summary>
        /// <returns>One line for the console.</returns>
        internal static string Rotate()
        {
            var session = _session;
            if (session is null || session.Transport is null)
            {
                return "the API is off; nothing to rotate";
            }

            var key = ApiToken.Create();
            var file = ApiKeyFile.Write(KeyFilePath, ApiKeyFile.Content(key, session.Runtime.Router.Port, session.Transport.Name, DateTimeOffset.Now));
            if (!file.Ok)
            {
                // Fail closed: a key that cannot be stored privately is not used; the old one is dropped too.
                Warn("rotateFailed", "local API key rotation failed; stopping the API: " + file.Problem, ("keyFile", file.Path));
                return Stop("the key file could not be rewritten owner-only");
            }

            session.Runtime.Router.UseKey(key);
            session.Key = key;
            ConsoleEvents.Emit(ApiAuditLog.Sys, "rotated", ConsoleTags.Api, "access key rotated", "keyId,keyFile", ("keyId", key.Id), ("keyFile", file.Path));
            return "access key rotated (id " + key.Id + "); clients must re-read " + file.Path;
        }

        /// <summary>The kill switch (Ctrl+Shift+K).</summary>
        internal static void Kill()
        {
            if (_session is null)
            {
                ConsoleEvents.Emit(ApiAuditLog.Sys, "kill", ConsoleTags.Api, "kill switch: the API is already off", null);
                return;
            }

            Stop("kill switch " + (Hotkeys.Find(KillKeyId)?.Chords() ?? Hotkeys.Chord(KillKeyDefault).Format()));
        }

        /// <summary>The <c>api status</c> text.</summary>
        internal static string Status()
        {
            var config = "[Api] Enabled=" + Bool(EnabledInConfig) + ", Port=" + (_port?.Value ?? ApiDefaults.DefaultPort).ToString(CultureInfo.InvariantCulture) +
                         ", Transport=" + (_transport?.Value ?? ApiTransportMode.Auto) + ", AllowActions=" + Bool(ApiSwitches.AllowActions) +
                         ", ConsoleAuditReads=" + Bool(_consoleReads?.Value ?? false);
            var session = _session;
            if (session is null || session.Transport is null)
            {
                return "API off: " + _offReason + "\n" + config + "\nkey file: " + (File.Exists(KeyFilePath) ? "PRESENT (stale) " : "absent ") + KeyFilePath;
            }

            var router = session.Runtime.Router;
            var services = Core.Actions.ActionServices.Shared;
            return "API on " + ApiDefaults.BaseUrl(router.Port) + " (" + session.Transport.Name +
                   (router.FallbackReason != null ? ", fallback: " + router.FallbackReason : string.Empty) + ") since " +
                   router.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "; key id " + router.KeyId + "; key file " + KeyFilePath +
                   "\n" + config +
                   "\nrequests " + router.Counters.Total.ToString(CultureInfo.InvariantCulture) + ": " + router.Counters.Describe() +
                   "; actions in flight " + router.Handles.InFlight.ToString(CultureInfo.InvariantCulture) +
                   "; runner " + (services.HasRunner ? "installed" : "not installed") +
                   "; in-game confirmation " + (services.HasConfirmationGate ? "installed" : "not installed (writes refused)") +
                   "\n" + session.Runtime.Describe() +
                   "; subscriptions events=" + Bool(session.HasEvents) + " data=" + Bool(session.HasData);
        }

        private static void SeedLive(ApiRuntime runtime)
        {
            try
            {
                var satone = ModBackends.Satone;
                if (satone != null && satone.IsAvailable)
                {
                    runtime.Live.Seed(satone.State, DateTimeOffset.Now);
                }
            }
            catch (Exception ex)
            {
                Diagnose("reading Satone's state at start failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool RemoveKeyFile(string reason, bool quietWhenAbsent)
        {
            var path = KeyFilePath;
            if (quietWhenAbsent && !File.Exists(path))
            {
                return true;
            }

            var result = ApiKeyFile.Delete(path);
            if (!result.Ok)
            {
                Warn("keyFile", "local API key file could not be deleted (" + reason + "): " + result.Problem, ("keyFile", path));
                return false;
            }

            ConsoleEvents.File(ApiAuditLog.Sys, "keyFileDeleted", ("reason", reason), ("path", path));
            return true;
        }

        private static void Warn(string name, string primary, params (string Key, object? Value)[] fields)
        {
            var shown = new List<string>(fields.Length);
            foreach (var (key, _) in fields)
            {
                shown.Add(key);
            }

            ConsoleEvents.EmitAt(ApiAuditLog.Sys, name, ConsoleTags.Api, ConsoleLevel.Warn, primary, string.Join(",", shown), fields);
        }

        private static void Diagnose(string message) =>
            ConsoleEvents.File(ApiAuditLog.Sys, "diagnostic", ("message", message));

        private static string Bool(bool value) => value ? "true" : "false";

        /// <summary>One running API: the runtime, its transport and its game subscriptions.</summary>
        internal sealed class Session
        {
            private IDisposable? _events;
            private IDisposable? _data;
            private int _tickFailures;

            internal Session(ApiRuntime runtime, ApiToken key)
            {
                Runtime = runtime;
                Key = key;
            }

            internal ApiRuntime Runtime { get; }

            /// <summary>The current access key (self-test only; never logged).</summary>
            internal ApiToken Key { get; set; }

            internal IApiTransport? Transport { get; set; }

            internal IDisposable? Ticker { get; set; }

            internal bool HasEvents => _events != null;

            internal bool HasData => _data != null;

            /// <summary>
            /// Subscribes to the game's events and data changes once their backends exist (the
            /// adapters register them in their Awake; checked again every frame in case they came late).
            /// </summary>
            internal void EnsureSubscriptions()
            {
                if (_events is null && ModBackends.Events is IModEventSource events)
                {
                    try
                    {
                        _events = events.SubscribeAny(e => Runtime.OnEvent(e.Name, e.Args));
                    }
                    catch (Exception ex)
                    {
                        Diagnose("subscribing to game events failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }

                if (_data is null && ModBackends.GameData is IModGameDataSource data)
                {
                    try
                    {
                        _data = data.Subscribe(Runtime.OnDataChanged);
                    }
                    catch (Exception ex)
                    {
                        Diagnose("subscribing to data changes failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            /// <summary>Once a frame (dispatcher ticker).</summary>
            internal void Tick()
            {
                try
                {
                    EnsureSubscriptions();
                    Runtime.Tick(MainThread.Frame);
                }
                catch (Exception ex)
                {
                    if (++_tickFailures == 1 || _tickFailures % 100 == 0)
                    {
                        Warn("tickFailed", "local API state refresh failed (" + _tickFailures + " so far): " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            /// <summary>Drops the ticker and the subscriptions.</summary>
            internal void Close()
            {
                Dispose(ref _events, "events");
                Dispose(ref _data, "data");
                var ticker = Ticker;
                Ticker = null;
                if (ticker != null)
                {
                    try
                    {
                        ticker.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Diagnose("removing the state ticker failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            private static void Dispose(ref IDisposable? subscription, string what)
            {
                var s = subscription;
                subscription = null;
                if (s is null)
                {
                    return;
                }

                try
                {
                    s.Dispose();
                }
                catch (Exception ex)
                {
                    Diagnose("unsubscribing from " + what + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }

    /// <summary>The <c>helper.Api</c> facet backend: whether the API listens and where. Never the key.</summary>
    internal sealed class ApiFacetBackend : ICwyModApi
    {
        public bool IsAvailable => ApiFeature.IsRunning;

        public string Status => ApiFeature.FacetStatus;

        public bool IsRunning => ApiFeature.IsRunning;

        public string Endpoint => ApiFeature.Endpoint;
    }
}
