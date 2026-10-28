using System;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The in-game evidence that the console exists and works: two smoke checks.
    /// </summary>
    internal static class ConsoleSmokeChecks
    {
        /// <summary>Smoke phase label of these checks.</summary>
        internal const string Phase = "P2";

        /// <summary>Id of the "the console is armed" check.</summary>
        internal const string ReadyCheckId = "p2.console.ready";

        /// <summary>Id of the "a command actually runs" check.</summary>
        internal const string ExecCheckId = "p2.console.exec";

        /// <summary>How many commands a working console is expected to have registered.</summary>
        internal const int ExpectedCommands = 15;

        /// <summary>Pack id the <c>packs list</c> check looks for - the reference pack in this repo.</summary>
        internal const string ExamplePackId = "cwy.example";

        /// <summary>Registers both checks. Idempotent.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(ReadyCheckId, Phase, Ready));
            SmokeRegistry.Register(new SmokeCheck(ExecCheckId, Phase, Exec));
        }

        private static SmokeCheckResult Ready()
        {
            var commands = ConsoleFeature.Commands.Count;

            if (!ConsoleFeature.IsReady)
            {
                return SmokeCheckResult.Fail(ReadyCheckId, "ConsoleFeature.Initialize never ran");
            }

            if (!ConsoleFeature.Enabled)
            {
                return SmokeCheckResult.Fail(
                    ReadyCheckId,
                    "the console is switched off (" + ConsoleFeature.ConfigSection + "."
                    + ConsoleFeature.ConfigKeyEnabled + "=false)");
            }

            // After the switch, so a disabled console is not given a host by the check itself.
            var host = ConsoleHost.Ensure();
            // Which surfaces exist. The IMGUI host is the fallback and must be alive; the
            // game-style surface is registered by the game adapter and built on first open.
            var registered = ConsoleFeature.RegisteredSurfaceName;
            var detail =
                commands + " command(s), " + ConsolePanels.Count + " panel(s), host "
                + (host == null ? "missing" : "alive") + ", style "
                + ConsoleFeature.Style.ToString().ToLowerInvariant() + ", game-style surface "
                + (registered.Length == 0 ? "not registered" : registered)
                + (ConsoleFeature.SurfaceBrokenReason.Length == 0 ? string.Empty : " (off: " + ConsoleFeature.SurfaceBrokenReason + ")")
                + ", " + ConsoleFeature.Output.Count + " line(s) buffered";

            if (host == null)
            {
                return SmokeCheckResult.Fail(ReadyCheckId, "no overlay host; " + detail);
            }

            if (commands < ExpectedCommands)
            {
                return SmokeCheckResult.Fail(
                    ReadyCheckId,
                    "only " + commands + " command(s), expected at least " + ExpectedCommands);
            }

            return SmokeCheckResult.Pass(ReadyCheckId, detail);
        }

        private static SmokeCheckResult Exec()
        {
            string help;
            string packs;

            try
            {
                // Through the registry rather than the private methods: this has to prove that the
                // path a typed line takes works, not that a method returns a string.
                help = ConsoleFeature.Commands.Execute("help");
                packs = ConsoleFeature.Commands.Execute("packs list");
            }
            catch (Exception ex)
            {
                // Execute is documented not to throw, so reaching here is itself the finding.
                CwyLsModLoader.Log.LogError(
                    $"{ConsoleFeature.LogPrefix} a smoke command threw: {ex.GetType().Name}: {ex.Message}");
                return SmokeCheckResult.Fail(ExecCheckId, ex.GetType().Name + ": " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(help) || help.StartsWith(CommandRegistry.UnknownPrefix, StringComparison.Ordinal))
            {
                return SmokeCheckResult.Fail(ExecCheckId, "'help' returned: " + Short(help));
            }

            if (packs.IndexOf(ExamplePackId, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return SmokeCheckResult.Fail(
                    ExecCheckId,
                    "'packs list' never mentioned " + ExamplePackId + ": " + Short(packs));
            }

            return SmokeCheckResult.Pass(
                ExecCheckId,
                "'help' returned " + help.Length + " chars, 'packs list' found " + ExamplePackId);
        }

        private static string Short(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "(nothing)";
            }

            var single = text!.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= 160 ? single : single.Substring(0, 160) + "...";
        }
    }
}
