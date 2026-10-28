using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Console;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>The <c>mods</c> console command: what loaded, what did not and why, and the dispatcher's state.</summary>
    internal static class ModCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "mods";

        /// <summary>Registers <c>mods list|status</c> in the framework group.</summary>
        internal static void Register() =>
            ConsoleFeature.Register(
                Name,
                "Code mods: loaded, skipped or failed and why; the API version and the main-thread dispatcher.",
                "mods list|status",
                "framework",
                Run);

        private static string? Run(IReadOnlyList<string> args)
        {
            var sub = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
            switch (sub)
            {
                case "":
                    return List() + "\n" + Status();
                case "list":
                    return List();
                case "status":
                    return Status();
                default:
                    return "usage: mods list|status";
            }
        }

        private static string List()
        {
            if (!ModLoader.HasLoaded)
            {
                return "code mods load on the first frame; nothing yet";
            }

            var sb = new StringBuilder();
            var codeMods = 0;
            var packs = 0;
            foreach (var entry in ModRuntime.All)
            {
                if (!entry.IsCodeMod)
                {
                    packs++;
                    continue;
                }

                codeMods++;
                var status = entry.Status;
                sb.Append("  ").Append(entry.Pack.Manifest).Append("  ").Append(status.State);
                if (status.SkipReason != null || status.Stage != null)
                {
                    sb.Append(" (").Append(status.ReasonText).Append(')');
                }

                if (status.State == ModState.Loaded || status.State == ModState.ShutDown)
                {
                    sb.Append("  ").Append(entry.EntryTypeName)
                      .Append("  ").Append(entry.EntryMs.ToString("0.#", CultureInfo.InvariantCulture)).Append(" ms");
                }
                else if (status.Detail.Length > 0)
                {
                    sb.Append("  ").Append(status.Detail);
                }

                if (entry.Failures > 0)
                {
                    sb.Append("  failures=").Append(entry.Failures.ToString(CultureInfo.InvariantCulture));
                }

                if (entry.Api != null)
                {
                    sb.Append("  api=").Append(entry.Api.GetType().Name);
                }

                sb.Append('\n');
            }

            sb.Insert(0, codeMods + " code mod(s), " + packs + " content pack(s): " + ModLoader.LastSummary + "\n");
            return sb.ToString().TrimEnd('\n');
        }

        private static string Status() =>
            "api " + CwyApi.VersionText + (CwyApi.IsFrozen ? " (frozen)" : " (0.x: may change before " + CwyApi.FreezeVersionText + ")") +
            "; loader " + (ModLoader.Enabled ? "on" : "off") + ", " + ModLoader.AssemblyLoadMode +
            "; backends " + ModBackends.Describe() +
            "; deprecation notices " + CwyDeprecations.Count +
            "\nmain thread: framework work in full each frame, mod work " +
            MainThread.FrameBudgetMs.ToString("0.#", CultureInfo.InvariantCulture) + " ms/frame round-robin; frame " +
            MainThread.Frame + ", " + MainThread.Stats;
    }

    /// <summary>Two quiet smoke checks for the code-mod loader: the dispatcher pumps, and the loader ran without failures.</summary>
    internal static class ModSmokeChecks
    {
        /// <summary>Smoke phase label of the checks.</summary>
        internal const string Phase = "C1";

        /// <summary>Id of the dispatcher check.</summary>
        internal const string PumpId = "c1.mainthread.pump";

        /// <summary>Id of the loader check.</summary>
        internal const string LoaderId = "c1.mods.loaded";

        /// <summary>Registers both checks; idempotent (the registry replaces by id).</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(PumpId, Phase, Pump));
            SmokeRegistry.Register(new SmokeCheck(LoaderId, Phase, Loaded));
        }

        private static SmokeCheckResult Pump()
        {
            var stats = MainThread.Stats;
            if (!MainThread.HasHost)
            {
                return SmokeCheckResult.Fail(PumpId, "the dispatcher host is not alive; " + stats);
            }

            return stats.Pumps > 0 && MainThread.Frame > 0
                ? SmokeCheckResult.Pass(PumpId, "frame " + MainThread.Frame + ", " + stats)
                : SmokeCheckResult.Fail(PumpId, "the host exists but has not pumped; " + stats);
        }

        private static SmokeCheckResult Loaded()
        {
            if (!ModLoader.HasLoaded)
            {
                return SmokeCheckResult.NotYetObserved(LoaderId, "the loader has not run yet (first frame)");
            }

            var failed = new List<string>();
            foreach (var status in ModLoader.Statuses)
            {
                if (status.State == ModState.Failed)
                {
                    failed.Add(status.Id + " (" + status.ReasonText + ")");
                }
            }

            return failed.Count == 0
                ? SmokeCheckResult.Pass(LoaderId, ModLoader.LastSummary)
                : SmokeCheckResult.Fail(LoaderId, "failed: " + string.Join(", ", failed) + "; " + ModLoader.LastSummary);
        }
    }
}
