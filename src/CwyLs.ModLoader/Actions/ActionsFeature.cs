using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Actions
{
    /// <summary>
    /// The action runner in the running game: installs it on
    /// <see cref="ActionServices.Shared"/>, finds the <see cref="ActionFamilyAttribute"/> families
    /// in the framework's assemblies on the first frame, registers the <c>act</c> console command and
    /// the smoke checks, and publishes every finished call as an <c>ACTION</c> audit line.
    /// </summary>
    /// <remarks>
    /// <para>Every caller goes through the same door: <c>ActionServices.Shared.RunAsync(request)</c>
    /// (the console's <c>act run</c>, scripts' <c>cwy.do</c>, the local API, mods through
    /// <c>helper.Actions</c>). The gate's switches come from the plugins that own them
    /// (<see cref="ActionServices.UseWritesSwitch"/>, <see cref="ActionServices.UseApiActionsSwitch"/>);
    /// confirmations from the in-game dialog once one is installed
    /// (<see cref="ActionServices.UseConfirmationGate"/>), until then every Confirm verdict ends
    /// Declined.</para>
    /// <para>Config <c>[Actions] Enabled</c> (true). Off: no runner is installed, so every call
    /// answers NotAvailable; <c>act</c> still explains why. Static and initialised once from the
    /// core plugin's Awake, on the main thread.</para>
    /// </remarks>
    public static class ActionsFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Actions";

        /// <summary>Master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>The trace <c>sys</c> family: <c>action/run</c>, <c>action/families</c>, <c>action/family</c>, <c>action/selftest</c>, <c>action/uiclick</c>.</summary>
        public const string TraceSys = ActionAudit.TraceSys;

        /// <summary>Assemblies searched for families: this name, or names starting with it and a dot.</summary>
        public const string AssemblyPrefix = "CwyLs.ModLoader";

        /// <summary>The self-test action's name; it lives in a private registry, never in the shared one.</summary>
        public const string SelfTestAction = "cwy.selftest.ping";

        private static readonly object Gate = new object();
        private static ConfigEntry<bool>? _enabled;
        private static bool _initialized;
        private static ActionRunner? _runner;
        private static ActionFamilyCatalog? _catalog;
        private static int _discovered;
        private static ActionResult? _selfTest;
        private static int _selfTestStarted;

        /// <summary>True when the action system is on (<c>[Actions] Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>The installed runner, or null when off or not yet initialised.</summary>
        public static ActionRunner? Runner => Volatile.Read(ref _runner);

        /// <summary>The family catalog, or null when off.</summary>
        public static ActionFamilyCatalog? Families => Volatile.Read(ref _catalog);

        /// <summary>True once the first-frame family discovery has run.</summary>
        public static bool HasDiscovered => Volatile.Read(ref _discovered) == 1;

        /// <summary>The self-test's outcome (a private action run through the real dispatcher), or null while pending.</summary>
        public static ActionResult? SelfTestResult => Volatile.Read(ref _selfTest);

        /// <summary>
        /// Binds the config, registers <c>act</c> and the smoke checks, installs the runner and
        /// schedules family discovery for the first frame. Idempotent. Call on the main thread, after
        /// <c>MainThread.Arm</c>.
        /// </summary>
        /// <param name="config">The core plugin's config file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        public static void Initialize(ConfigFile config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("ACTION already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
            }

            _enabled = config.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Run game actions (music, timer, room, ...) for the console's 'act run', pack scripts, the local API and " +
                "code mods, through one tier gate and an ACTION audit line. Off: every action answers NotAvailable. " +
                "Writes to the player's records also need [Data] AllowWrites in the data plugin's config.");

            ActionCommands.Register();
            ActionSmokeChecks.RegisterAll();

            if (!Enabled)
            {
                ConsoleEvents.Setup("actions", "disabled (" + ConfigSection + "." + ConfigKeyEnabled + "=false); every action answers NotAvailable");
                return;
            }

            var runner = new ActionRunner(ActionRegistry.Shared, new MainThreadActionDispatcher(), ActionServices.Shared, ActionAudit.Publish);
            Volatile.Write(ref _runner, runner);
            Volatile.Write(ref _catalog, new ActionFamilyCatalog(ActionRegistry.Shared));
            ActionServices.Shared.UseRunner(runner);

            if (!MainThread.Post("actions: discover families", DiscoverOnFirstFrame, ActionRunner.Owner))
            {
                CwyLsModLoader.Log.LogWarning(
                    "ACTION could not schedule family discovery on the main thread; only actions registered directly (mods) are available");
            }

            ConsoleEvents.Setup(
                "actions",
                "runner installed; families register on the first frame",
                ("timeout", "2s"),
                ("confirm", "in-game dialog or decline"));
        }

        /// <summary>
        /// Finds and registers the families of an assembly that loads after the first frame (a plugin
        /// loaded on demand). Main thread only; each family is registered once however often this runs.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>The outcomes of the families new in it; empty when off or off the main thread.</returns>
        public static IReadOnlyList<ActionFamilyOutcome> DiscoverFamilies(Assembly assembly)
        {
            var catalog = Families;
            if (assembly is null || catalog is null)
            {
                return Array.Empty<ActionFamilyOutcome>();
            }

            if (!MainThread.IsMainThread)
            {
                CwyLsModLoader.Log.LogWarning("ACTION DiscoverFamilies(" + assembly.GetName().Name + ") must run on the main thread; nothing registered");
                return Array.Empty<ActionFamilyOutcome>();
            }

            var outcomes = catalog.Discover(new[] { assembly });
            Report(catalog, outcomes, assembly.GetName().Name ?? "?");
            return outcomes;
        }

        /// <summary>The <c>act status</c> text: runner, switches, confirmation, families, counters, self-test.</summary>
        public static string Status()
        {
            var services = ActionServices.Shared;
            var settings = services.GateSettings;
            var sb = new StringBuilder("ACTIONS  ");
            var runner = Runner;
            if (!Enabled)
            {
                sb.Append("off (").Append(ConfigSection).Append('.').Append(ConfigKeyEnabled).Append("=false): every action answers NotAvailable");
            }
            else if (runner is null || !services.HasRunner)
            {
                sb.Append("no runner installed: every action answers NotAvailable");
            }
            else
            {
                sb.Append("runner on; ").Append(ActionRegistry.Shared.Count).Append(" registered; ").Append(runner.Stats);
            }

            sb.Append("\n  gate     writes ").Append(settings.AllowWrites ? "on" : "off")
                .Append(" (").Append(ActionGateSettings.AllowWritesKey)
                .Append(services.HasWritesSwitch ? string.Empty : ", not connected: off").Append(')')
                .Append("; API actions ").Append(settings.AllowApiActions ? "on" : "off (reads only)")
                .Append(" (").Append(ActionGateSettings.AllowApiActionsKey)
                .Append(services.HasApiActionsSwitch ? string.Empty : ", not connected: off").Append(')');
            sb.Append("\n  confirm  ").Append(
                services.HasConfirmationGate
                    ? services.ConfirmationGate.GetType().Name + " (deletes and API writes ask the player)"
                    : "no in-game dialog installed: deletes and API writes are declined");
            sb.Append("\n  timeout  ").Append(ActionRunner.DefaultTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append(" s default, ")
                .Append(ActionRunner.MaxTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)).Append(" s at most; mods run in their own dispatcher lane");

            var catalog = Families;
            if (catalog is null)
            {
                sb.Append("\n  families -");
            }
            else if (!HasDiscovered)
            {
                sb.Append("\n  families not discovered yet (first frame)");
            }
            else
            {
                sb.Append("\n  families ").Append(catalog.Summary());
                foreach (var outcome in catalog.Outcomes)
                {
                    sb.Append("\n    ").Append(outcome);
                }
            }

            var selfTest = SelfTestResult;
            sb.Append("\n  selftest ").Append(selfTest is null ? "pending" : selfTest.ToString());
            return sb.ToString();
        }

        private static void DiscoverOnFirstFrame()
        {
            var catalog = Families;
            if (catalog is null)
            {
                return;
            }

            var assemblies = FrameworkAssemblies();
            var names = new List<string>(assemblies.Count);
            foreach (var assembly in assemblies)
            {
                names.Add(assembly.GetName().Name ?? "?");
            }

            var outcomes = catalog.Discover(assemblies);
            Volatile.Write(ref _discovered, 1);
            Report(catalog, outcomes, string.Join(",", names));
            StartSelfTest();
        }

        private static List<Assembly> FrameworkAssemblies()
        {
            var list = new List<Assembly>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                var name = assembly.GetName().Name ?? string.Empty;
                if (string.Equals(name, AssemblyPrefix, StringComparison.Ordinal) ||
                    name.StartsWith(AssemblyPrefix + ".", StringComparison.Ordinal))
                {
                    list.Add(assembly);
                }
            }

            list.Sort((a, b) => string.CompareOrdinal(a.GetName().Name, b.GetName().Name));
            return list;
        }

        private static void Report(ActionFamilyCatalog catalog, IReadOnlyList<ActionFamilyOutcome> outcomes, string searched)
        {
            foreach (var problem in catalog.AssemblyProblems)
            {
                ConsoleEvents.EmitAt(TraceSys, "family", ConsoleTags.Action, ConsoleLevel.Warn,
                    "families: " + problem, null, ("problem", problem));
            }

            foreach (var outcome in outcomes)
            {
                var fields = new (string Key, object? Value)[]
                {
                    ("family", outcome.Family),
                    ("type", outcome.TypeName),
                    ("state", outcome.State.ToString()),
                    ("actions", outcome.Actions.Count),
                    ("detail", outcome.Detail.Length == 0 ? null : outcome.Detail),
                };

                switch (outcome.State)
                {
                    case ActionFamilyState.Failed:
                        ConsoleEvents.EmitAt(TraceSys, "family", ConsoleTags.Action, ConsoleLevel.Warn,
                            "family " + outcome.Family + " failed: " + outcome.Detail, "type", fields);
                        break;
                    case ActionFamilyState.Skipped:
                        ConsoleEvents.Emit(TraceSys, "family", ConsoleTags.Action,
                            "family " + outcome.Family + " skipped: " + outcome.Detail, "type", fields);
                        break;
                    default:
                        ConsoleEvents.File(TraceSys, "family", fields);
                        break;
                }

                foreach (var problem in outcome.Problems)
                {
                    ConsoleEvents.EmitAt(TraceSys, "family", ConsoleTags.Action, ConsoleLevel.Warn,
                        "family " + outcome.Family + " refused an action: " + problem, null, ("family", outcome.Family), ("problem", problem));
                }
            }

            ConsoleEvents.Emit(
                TraceSys,
                "families",
                ConsoleTags.Action,
                catalog.Summary(),
                "registered",
                ("registered", ActionRegistry.Shared.Count),
                ("searched", searched));
        }

        private static void StartSelfTest()
        {
            if (Interlocked.Exchange(ref _selfTestStarted, 1) == 1)
            {
                return;
            }

            try
            {
                // A private registry and private services: the real dispatcher and runner code, but
                // nothing the player, a script or the API can reach, and no confirmation dialog.
                var registry = new ActionRegistry();
                registry.Register(
                    ActionRegistry.FrameworkOwner,
                    new ActionDescriptor(SelfTestAction, ActionTier.Act, "Answers from the main thread (E-ACT-CORE self-test)."),
                    _ => MainThread.IsMainThread
                        ? ActionResult.Ok("pong on the main thread, frame " + MainThread.Frame)
                        : ActionResult.Failed("the handler ran off the main thread"));
                var runner = new ActionRunner(registry, new MainThreadActionDispatcher(), new ActionServices());
                runner.RunAsync(new ActionRequest(SelfTestAction, null, ActionCaller.Console, "selftest"), CancellationToken.None)
                    .ContinueWith(OnSelfTest, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("ACTION self-test could not start: " + ex.GetType().Name + ": " + ex.Message);
                Volatile.Write(ref _selfTest, ActionResult.Failed("could not start: " + ex.GetType().Name));
            }
        }

        private static void OnSelfTest(Task<ActionResult> task)
        {
            var result = task.Status == TaskStatus.RanToCompletion && task.Result != null
                ? task.Result
                : ActionResult.Failed("the self-test task ended " + task.Status + (task.Exception is null ? string.Empty : ": " + task.Exception.GetBaseException().Message));
            Volatile.Write(ref _selfTest, result);
            ConsoleEvents.File(TraceSys, "selftest", ("status", ActionAudit.Word(result.Status)), ("path", ActionAudit.Word(result.Path)), ("detail", result.Message));
            if (!result.IsOk)
            {
                CwyLsModLoader.Log.LogWarning("ACTION self-test: " + result);
            }
        }
    }
}
