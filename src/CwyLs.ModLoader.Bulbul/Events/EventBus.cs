using System;
using BepInEx.Configuration;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core;
using HarmonyLib;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// Starts the typed game event bus. One call from the adapter's <c>Awake</c>
    /// verifies the seams, applies the patches, arms the host and registers the smoke checks.
    /// </summary>
    public static class EventBus
    {
        /// <summary>Harmony id of the bus's own patches, so they are separable in a Harmony dump.</summary>
        public const string HarmonyId = BulbulPlugin.PluginGuid + ".events";

        /// <summary>Name the bus registers its patch targets under.</summary>
        public const string TargetOwner = "CWYLS Game Adapter events";

        private static Harmony? _harmony;

        /// <summary>The seam check made at startup, or null if <see cref="Initialize"/> has not run.</summary>
        public static PatchTargetReport? Targets { get; private set; }

        /// <summary>True when every seam the bus needs resolved and every patch applied.</summary>
        public static bool AllSeamsOk =>
            Targets != null && Targets.AllFound && EventPatches.Failed.Count == 0;

        /// <summary>
        /// Brings the bus up. Call once, from the adapter plugin's <c>Awake</c>, after the main
        /// patch-target verification. Never throws: a failure leaves <see cref="CWYEvents.IsReady"/>
        /// false and is reported through the log and the smoke report.
        /// </summary>
        /// <param name="config">The plugin's config file, where the bus binds its settings.</param>
        public static void Initialize(ConfigFile config)
        {
            try
            {
                // BepInEx runs Awake on the Unity main thread, so this is the thread every
                // subscriber is promised.
                CWYEvents.CaptureMainThread();
                CWYEvents.BindConfig(config);

                // Registered so the framework-wide table owns them too, then checked separately so
                // the bus can report its own seams in the smoke report without re-checking the main table's.
                PatchTargetRegistry.Register(TargetOwner, BulbulEventTargets.All);
                Targets = PatchTargetChecker.Check(BulbulEventTargets.All, new ReflectionMemberResolver());
                CwyLsModLoader.Log.LogDebug("EVENT " + Targets.Summarize());
                foreach (var line in Targets.MissingLines())
                {
                    CwyLsModLoader.Log.LogError("EVENT " + line);
                }

                _harmony = new Harmony(HarmonyId);
                EventPatches.Apply(_harmony);

                if (EventHost.EnsureRunner() == null)
                {
                    CwyLsModLoader.Log.LogWarning(
                        "EVENT no runner: the clock tick, room-ready signal and observable bridge are off");
                }

                EventSmoke.Register();
                CWYEvents.IsReady = true;

                ConsoleEvents.Setup(
                    "events",
                    "bus ready"
                    + (EventPatches.Failed.Count == 0
                        ? string.Empty
                        : $"; {EventPatches.Failed.Count} patch site(s) FAILED: "
                          + string.Join(", ", ToArray(EventPatches.Failed))),
                    ("sites", EventPatches.Applied.Count),
                    ("saves", EventPatches.SaveMethodCount),
                    ("types", CWYEvents.EventTypes.Count),
                    ("targets", Targets.Summarize()));
            }
            catch (Exception ex)
            {
                CWYEvents.IsReady = false;
                CwyLsModLoader.Log.LogError(
                    $"EVENT bus failed to start: {ex.GetType().Name}: {ex.Message}. "
                    + "The game is unaffected; no events will be published.");
            }
        }

        private static string[] ToArray(System.Collections.Generic.IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return copy;
        }
    }
}
