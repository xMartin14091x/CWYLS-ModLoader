using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// Which merged pack rows may enter the game's master data, given the DLC gate's verdict over the game's untouched tables.
    /// A row that would override, extend or point at paid DLC content is refused: a localisation row with a protected id or
    /// voice clip, a novel row that speaks a protected line, belongs to a protected group or replaces a game row of one, and a
    /// scenario group that replaces a protected group or titles itself with a protected line. No verdict, or an undecided one,
    /// refuses every row (fail closed).
    /// </summary>
    public static class MasterDataPaidAdmission
    {
        /// <summary>The rows that may enter, and the refused ones (kind and id, never text).</summary>
        public sealed class Result
        {
            internal Result(List<LocalizationRow> localization, List<NovelRow> novel, List<ScenarioGroupRow> groups, List<string> refused)
            {
                Localization = localization;
                Novel = novel;
                ScenarioGroups = groups;
                Refused = refused;
            }

            /// <summary>Admitted localisation rows, in order.</summary>
            public IReadOnlyList<LocalizationRow> Localization { get; }

            /// <summary>Admitted novel rows, in order.</summary>
            public IReadOnlyList<NovelRow> Novel { get; }

            /// <summary>Admitted scenario groups, in order.</summary>
            public IReadOnlyList<ScenarioGroupRow> ScenarioGroups { get; }

            /// <summary>Refused rows as "kind id".</summary>
            public IReadOnlyList<string> Refused { get; }
        }

        /// <summary>Filters <paramref name="set"/>.</summary>
        /// <param name="set">The merged pack rows.</param>
        /// <param name="paid">The verdict over the game's untouched tables; null = none yet (refuse everything).</param>
        /// <param name="baseNovelGroups">The game's own novel row ids and the group each belongs to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
        public static Result Filter(MasterDataSet set, MakerDlcHidden? paid, IReadOnlyDictionary<string, string?>? baseNovelGroups)
        {
            if (set is null) throw new ArgumentNullException(nameof(set));
            var known = paid != null && paid.IsKnown;
            bool Line(string? id) => !string.IsNullOrEmpty(id) && id != "0" && (!known || paid!.IsLine(id));
            bool Group(string? id) => !string.IsNullOrEmpty(id) && (!known || paid!.IsGroup(id));
            // The raw field and its normalised voice key (a path or case variant of a paid clip names the same clip).
            bool PaidClip(string? clip) => !string.IsNullOrEmpty(clip)
                && (!known || paid!.IsVoice(clip) || paid.IsVoice(CwyLs.ModLoader.Core.Voice.VoiceNameRules.KeyFromAudioPath(clip)));

            var refused = new List<string>();
            var localization = new List<LocalizationRow>();
            foreach (var row in set.Localization)
            {
                if (!known || Line(row.Id) || PaidClip(row.Voice)) refused.Add("localization " + row.Id);
                else localization.Add(row);
            }

            var novel = new List<NovelRow>();
            foreach (var row in set.Novel)
            {
                string? baseGroup = null;
                var replacesPaid = (known && paid!.IsNovelRow(row.Id))
                    || (baseNovelGroups != null && baseNovelGroups.TryGetValue(row.Id, out baseGroup) && Group(baseGroup));
                if (!known || Line(row.LocalizationId) || Group(row.ScenarioGroupId) || replacesPaid) refused.Add("novel " + row.Id);
                else novel.Add(row);
            }

            var groups = new List<ScenarioGroupRow>();
            foreach (var row in set.ScenarioGroups)
            {
                if (!known || Group(row.Id) || Line(row.TitleLocalizationId) || Line(row.SubtitleLocalizationId)) refused.Add("scenario group " + row.Id);
                else groups.Add(row);
            }

            return new Result(localization, novel, groups, refused);
        }
    }
}
