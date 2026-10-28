using System;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// Which BepInEx log lines the framework's two console sinks keep: <c>cwy-console.log</c>
    /// (<c>CwyConsoleListener</c>) and the in-game console's scrollback (<c>ConsoleLogListener</c>).
    /// One rule for both, so the file and the overlay can never disagree about what was logged.
    /// </summary>
    /// <remarks>
    /// The rule: an Error or Fatal from any source is kept - a Unity exception from the game
    /// matters even though its source is not ours. Otherwise the source must be one of ours
    /// (its name starts with <see cref="CwySourcePrefix"/>) and the level must not be Debug; the
    /// trace file is where that level of detail belongs. Core cannot reference BepInEx, so the
    /// caller translates its <c>LogLevel</c> into the two flags.
    /// </remarks>
    public static class ConsoleLogFilter
    {
        /// <summary>Source-name prefix that marks a log source as the framework's own.</summary>
        public const string CwySourcePrefix = "CWY";

        /// <summary>True when a console sink should keep the line.</summary>
        /// <param name="sourceName">BepInEx source name, e.g. <c>CWYLS Game Adapter</c>; null when unknown.</param>
        /// <param name="isErrorOrFatal">The line was logged at Error or Fatal.</param>
        /// <param name="isDebug">The line was logged at Debug (or at the catch-all level).</param>
        public static bool Admits(string? sourceName, bool isErrorOrFatal, bool isDebug)
        {
            if (isErrorOrFatal)
            {
                return true;
            }

            return !isDebug
                && sourceName != null
                && sourceName.StartsWith(CwySourcePrefix, StringComparison.Ordinal);
        }
    }
}
