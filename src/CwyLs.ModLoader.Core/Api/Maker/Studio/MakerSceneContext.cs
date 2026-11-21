using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>
    /// Where each line sits in the game's novel scripts (Studio v2 <c>context</c>): every <c>NovelMaster</c> row that speaks
    /// the line in a known scenario group is one occurrence, and its previous and next lines are the neighbours in that
    /// group's own row order. Nothing is inferred: a row without a group is not an occurrence, a neighbour that is not a
    /// line of the game's table is skipped, and mood and pose are never derived from motion identifiers.
    /// </summary>
    public sealed class MakerSceneContext
    {
        private static readonly MakerStudioOccurrence[] None = new MakerStudioOccurrence[0];

        private readonly Dictionary<string, List<MakerStudioOccurrence>> _byLine;

        private MakerSceneContext(Dictionary<string, List<MakerStudioOccurrence>> byLine) => _byLine = byLine;

        /// <summary>A context with no occurrence.</summary>
        public static MakerSceneContext Empty { get; } = new MakerSceneContext(new Dictionary<string, List<MakerStudioOccurrence>>(StringComparer.Ordinal));

        /// <summary>The value of <see cref="MakerStudioContext.Source"/> for a line that has occurrences.</summary>
        public const string NovelSource = "novel";

        /// <summary>Builds the context from the game's novel rows.</summary>
        /// <param name="novel">The rows in the game's order.</param>
        /// <param name="lineExists">True when the id is a line of the game's localisation table.</param>
        public static MakerSceneContext Build(IReadOnlyList<MakerGameNovelRow> novel, Func<string, bool> lineExists)
        {
            if (novel is null) throw new ArgumentNullException(nameof(novel));
            if (lineExists is null) throw new ArgumentNullException(nameof(lineExists));

            // The sequence of verified rows of each group, in the game's order.
            var groups = new Dictionary<string, List<MakerGameNovelRow>>(StringComparer.Ordinal);
            var groupOrder = new List<string>();
            foreach (var row in novel)
            {
                if (row.GroupId is null || row.LineId is null || !lineExists(row.LineId)) continue;
                if (!groups.TryGetValue(row.GroupId, out var sequence))
                {
                    sequence = new List<MakerGameNovelRow>();
                    groups.Add(row.GroupId, sequence);
                    groupOrder.Add(row.GroupId);
                }

                sequence.Add(row);
            }

            var byLine = new Dictionary<string, List<MakerStudioOccurrence>>(StringComparer.Ordinal);
            foreach (var groupId in groupOrder)
            {
                var sequence = groups[groupId];
                for (var i = 0; i < sequence.Count; i++)
                {
                    var row = sequence[i];
                    var occurrence = new MakerStudioOccurrence
                    {
                        OccurrenceId = MakerProjectRevision.StableId("occurrence\n" + groupId + "\n" + row.RowId + "\n" + i),
                        GroupId = groupId,
                        Order = i,
                        PreviousId = i > 0 ? sequence[i - 1].LineId : null,
                        NextId = i + 1 < sequence.Count ? sequence[i + 1].LineId : null,
                        Talker = row.Talker,
                        BodyMotion = row.BodyMotion,
                        FacialMotion = row.FacialMotion,
                    };
                    if (!byLine.TryGetValue(row.LineId!, out var list))
                    {
                        list = new List<MakerStudioOccurrence>();
                        byLine.Add(row.LineId!, list);
                    }

                    list.Add(occurrence);
                }
            }

            return new MakerSceneContext(byLine);
        }

        /// <summary>The occurrences of a line in the game's order; empty when none is verified.</summary>
        /// <param name="lineId">A localisation id.</param>
        public IReadOnlyList<MakerStudioOccurrence> OccurrencesOf(string lineId) =>
            _byLine.TryGetValue(lineId, out var list) ? list : (IReadOnlyList<MakerStudioOccurrence>)None;

        /// <summary>Builds the wire context of a line; <paramref name="occurrenceId"/> null selects the first occurrence.</summary>
        /// <param name="lineId">A localisation id.</param>
        /// <param name="occurrenceId">The occurrence asked for, or null.</param>
        /// <param name="context">The context on success.</param>
        /// <returns>False when <paramref name="occurrenceId"/> is not one of the line's occurrences.</returns>
        public bool TryContextOf(string lineId, string? occurrenceId, out MakerStudioContext context)
        {
            var list = OccurrencesOf(lineId);
            string? selected = null;
            if (occurrenceId != null)
            {
                foreach (var occurrence in list)
                {
                    if (string.Equals(occurrence.OccurrenceId, occurrenceId, StringComparison.Ordinal)) selected = occurrence.OccurrenceId;
                }

                if (selected is null)
                {
                    context = new MakerStudioContext();
                    return false;
                }
            }
            else if (list.Count > 0)
            {
                selected = list[0].OccurrenceId;
            }

            context = new MakerStudioContext
            {
                Occurrences = list,
                SelectedOccurrenceId = selected,
                Mood = null,
                Pose = null,
                Source = list.Count == 0 ? null : NovelSource,
            };
            return true;
        }
    }
}
