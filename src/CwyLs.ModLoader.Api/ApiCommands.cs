using System.Collections.Generic;
using CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Api
{
    /// <summary>The <c>api</c> console command: <c>api on|off|status|rotate</c>.</summary>
    internal static class ApiCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "api";

        /// <summary>Registers the command in the framework group.</summary>
        internal static void Register() =>
            ConsoleFeature.Register(
                Name,
                "The local game API for programs on this computer (off by default): start, stop, state, new access key.",
                "api on|off|status|rotate",
                "framework",
                Run);

        private static string? Run(IReadOnlyList<string> args)
        {
            var sub = args.Count == 0 ? "status" : args[0].ToLowerInvariant();
            switch (sub)
            {
                case "on":
                    return ApiFeature.Start("console");
                case "off":
                    return ApiFeature.Stop("console");
                case "status":
                    return ApiFeature.Status();
                case "rotate":
                    return ApiFeature.Rotate();
                default:
                    return "usage: api on|off|status|rotate";
            }
        }
    }
}
