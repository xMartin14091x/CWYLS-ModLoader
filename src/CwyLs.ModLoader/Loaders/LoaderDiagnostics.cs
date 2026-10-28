using System;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Where every load attempt is recorded and every failure is logged. One place, so no loader
    /// can grow its own quiet error path.
    /// </summary>
    public static class LoaderDiagnostics
    {
        /// <summary>
        /// Everything loaded this session that no caller claimed with its own report. A caller that
        /// passes a <see cref="LoadReport"/> of its own (one per pack, say) owns those entries
        /// instead; they are not duplicated here, so neither report double-counts.
        /// </summary>
        public static LoadReport Session { get; } = new LoadReport();

        /// <summary>
        /// Records one attempt and, when it failed, warns with the path attached. A load failure is
        /// a content problem, not a crash: the game keeps running with the asset it already had.
        /// </summary>
        /// <param name="report">The caller's report, or null for <see cref="Session"/>.</param>
        /// <param name="entry">The attempt.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public static void Record(LoadReport? report, LoadEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            (report ?? Session).Add(entry);

            if (!entry.Ok)
            {
                CwyLsModLoader.Log.LogWarning($"load failed: {entry.Path}: {entry.Detail}");
            }
        }

        /// <summary>Logs the one-line summary of a report, and every failure under it.</summary>
        /// <param name="report">The report to summarise, or null for <see cref="Session"/>.</param>
        public static void LogSummary(LoadReport? report = null)
        {
            var target = report ?? Session;

            ConsoleEvents.Setup("loaders", target.Summarize());

            foreach (var failure in target.Failures)
            {
                CwyLsModLoader.Log.LogWarning("  " + failure);
            }
        }
    }
}
