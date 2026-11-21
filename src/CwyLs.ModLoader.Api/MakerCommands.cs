using System.Collections.Generic;
using CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Api
{
    /// <summary>The <c>maker</c> console command: <c>maker</c> (start and open the page) or <c>maker [open|stop|serve|status|trace on|off]</c>.</summary>
    internal static class MakerCommands
    {
        /// <summary>Registers the command in the framework group.</summary>
        internal static void Register() =>
            ConsoleFeature.Register(
                MakerFeature.CommandName,
                "Pack Maker in your browser (this computer only): subtitles and dubs for content packs.",
                "maker [open|stop|serve|status|trace on|off]",
                "framework",
                Run);

        private static string? Run(IReadOnlyList<string> args)
        {
            var sub = args.Count == 0 ? "open" : args[0].ToLowerInvariant();
            switch (sub)
            {
                case "open":
                    return MakerFeature.Open("console");
                case "serve":
                    return MakerFeature.Open("console", openBrowser: false);
                case "stop":
                case "off":
                    return MakerFeature.Close("console");
                case "status":
                    return MakerFeature.Status();
                case "trace":
                    return MakerFeature.Trace(args.Count > 1 ? args[1] : null);
                default:
                    return "usage: maker (start and open the page) | maker stop | maker serve (no browser) | maker status | maker trace on|off";
            }
        }
    }
}
