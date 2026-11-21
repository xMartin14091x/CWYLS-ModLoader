using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>The pure rules of the Studio catalog: progress counting, group order and the clip revision.</summary>
    internal static class MakerStudioView
    {
        /// <summary>
        /// Adds one line to a progress bucket. A line is dubbable when the game gives it a voice and speaks it; text-only and
        /// omitted lines are not. A bucket sees each line once, because the caller assigns every line one primary group.
        /// </summary>
        internal static void Count(MakerStudioProgress bucket, MakerLineState state, MakerStudioWorkflow workflow)
        {
            bucket.TotalLines++;
            if (state.NeedsDub) bucket.DubbableLines++;
            if (state.HasSubtitle) bucket.SubtitlePresent++;
            if (state.NeedsDub && state.HasDub) bucket.DubPresent++;
            switch (workflow.Status)
            {
                case "draft": bucket.Draft++; break;
                case "done": bucket.Done++; break;
                case "needs-retake": bucket.NeedsRetake++; break;
                default: bucket.Todo++; break;
            }

            if (workflow.Starred) bucket.Starred++;
        }

        /// <summary>
        /// The display order of the groups that occur: the game's scenario groups in the game's order, then every other group
        /// by name, then <c>ungrouped</c>, then <c>(omitted)</c>.
        /// </summary>
        internal static List<string> GroupOrder(IReadOnlyList<string> seen, IReadOnlyList<string> gameOrder)
        {
            var present = new HashSet<string>(seen, StringComparer.Ordinal);
            var ordered = new List<string>(present.Count);
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in gameOrder)
            {
                if (present.Contains(id) && id != MakerStudioCatalog.UngroupedId && id != MakerLineRules.OmittedGroup && placed.Add(id)) ordered.Add(id);
            }

            var rest = new List<string>();
            foreach (var id in seen)
            {
                if (id != MakerStudioCatalog.UngroupedId && id != MakerLineRules.OmittedGroup && !placed.Contains(id)) rest.Add(id);
            }

            rest.Sort(StringComparer.Ordinal);
            ordered.AddRange(rest);
            if (present.Contains(MakerStudioCatalog.UngroupedId)) ordered.Add(MakerStudioCatalog.UngroupedId);
            if (present.Contains(MakerLineRules.OmittedGroup)) ordered.Add(MakerLineRules.OmittedGroup);
            return ordered;
        }

        /// <summary>
        /// The revision of the selected pack's clip for a line. It is derived from the pack's own clip file (size and write time,
        /// or "none"), so it moves when a clip is added, removed or replaced, and every line that shares a clip shares it. This is
        /// the one place to replace when the canonical per-clip store arrives.
        /// </summary>
        /// <param name="lang">The canonical language code.</param>
        /// <param name="lineId">The line id (used only for a line without a voice).</param>
        /// <param name="voice">The line's clip name, or null for a text-only line.</param>
        /// <param name="ownClipPath">The absolute path of the pack's own clip, or null when it has none.</param>
        internal static string ClipRevisionOf(string lang, string lineId, string? voice, string? ownClipPath)
        {
            if (voice is null) return MakerProjectRevision.StableId("noclip\n" + lang + "\n" + lineId);
            var stamp = "none";
            if (ownClipPath != null)
            {
                try
                {
                    var info = new FileInfo(ownClipPath);
                    if (info.Exists) stamp = info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    stamp = "unreadable";
                }
            }

            return MakerProjectRevision.StableId("clip\n" + lang + "\n" + voice + "\n" + stamp);
        }
    }
}
