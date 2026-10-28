using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>Why a pack file was not used.</summary>
    public enum AudioSkipKind
    {
        /// <summary>No row of its table has that name or that game clip name.</summary>
        UnknownRow,

        /// <summary>A later file (a later pack, or a later path in the same pack) claims the same row.</summary>
        Overridden,

        /// <summary>The row is paid content (see <see cref="AudioPaidContent"/>).</summary>
        Paid,
    }

    /// <summary>A pack file that was not used, and why.</summary>
    public sealed class AudioSkip
    {
        internal AudioSkip(AudioCandidate candidate, AudioSkipKind kind, string reason, AudioRowDescriptor? row)
        {
            Candidate = candidate;
            Kind = kind;
            Reason = reason;
            Row = row;
        }

        /// <summary>The file.</summary>
        public AudioCandidate Candidate { get; }

        /// <summary>What kind of skip.</summary>
        public AudioSkipKind Kind { get; }

        /// <summary>One line for people.</summary>
        public string Reason { get; }

        /// <summary>The row it claimed, when one matched.</summary>
        public AudioRowDescriptor? Row { get; }

        /// <inheritdoc />
        public override string ToString() => Candidate.Source + ": " + Reason;
    }

    /// <summary>The file that replaces one row's clip.</summary>
    public sealed class AudioAssignment
    {
        internal AudioAssignment(AudioRowDescriptor row, AudioCandidate candidate, bool matchedByClipName)
        {
            Row = row;
            Candidate = candidate;
            MatchedByClipName = matchedByClipName;
        }

        /// <summary>The game's row.</summary>
        public AudioRowDescriptor Row { get; }

        /// <summary>The winning file.</summary>
        public AudioCandidate Candidate { get; }

        /// <summary>True when the file is named after the game's clip (<c>Rainthunder</c>) rather than the row (<c>ThunderRain</c>).</summary>
        public bool MatchedByClipName { get; }

        /// <inheritdoc />
        public override string ToString() => Row.Key + " <- " + Candidate.Source;
    }

    /// <summary>
    /// Which pack file replaces which game row: the pure half of pack audio's row replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file <c>audio/&lt;table folder&gt;/&lt;Name&gt;.ogg|wav</c> claims the row of that table whose
    /// name is <c>Name</c> (the enum value, e.g. <c>LightRain</c>, or a story-music title), or failing
    /// that the row whose game clip is called <c>Name</c> (e.g. <c>Rainthunder</c> for the
    /// <c>ThunderRain</c> row). Matching is case-insensitive.
    /// </para>
    /// <para>
    /// Precedence follows the voice rule: packs in load order, the later claim wins; inside one pack
    /// the later path wins (ordinal, so <c>X.wav</c> beats <c>X.ogg</c>). Paid rows
    /// (<see cref="AudioPaidContent.CheckRowReplacement"/>) are refused, one entry per claiming file.
    /// </para>
    /// </remarks>
    public sealed class AudioReplacementPlan
    {
        private readonly List<AudioAssignment> _assignments;
        private readonly List<AudioSkip> _skipped;
        private readonly Dictionary<string, AudioAssignment> _byKey;

        private AudioReplacementPlan(List<AudioAssignment> assignments, List<AudioSkip> skipped)
        {
            _assignments = assignments;
            _skipped = skipped;
            _byKey = new Dictionary<string, AudioAssignment>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in assignments)
            {
                _byKey[assignment.Row.Key] = assignment;
            }
        }

        /// <summary>A plan with nothing in it.</summary>
        public static AudioReplacementPlan Empty { get; } =
            new AudioReplacementPlan(new List<AudioAssignment>(), new List<AudioSkip>());

        /// <summary>The rows that get a pack clip, in table then row order.</summary>
        public IReadOnlyList<AudioAssignment> Assignments => _assignments;

        /// <summary>The files not used, in candidate order.</summary>
        public IReadOnlyList<AudioSkip> Skipped => _skipped;

        /// <summary>How many claims were refused as paid content.</summary>
        public int RefusedCount
        {
            get
            {
                var count = 0;
                foreach (var skip in _skipped)
                {
                    if (skip.Kind == AudioSkipKind.Paid)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Builds the plan.
        /// </summary>
        /// <param name="candidates">The pack files, in load order (<see cref="AudioCandidate.FromPacks"/>).</param>
        /// <param name="rows">The game's rows.</param>
        /// <param name="soundtrack">The game's music list, for the paid check.</param>
        /// <returns>The plan; never null.</returns>
        public static AudioReplacementPlan Build(
            IReadOnlyList<AudioCandidate>? candidates,
            IReadOnlyList<AudioRowDescriptor>? rows,
            SoundtrackCatalog? soundtrack)
        {
            var assignments = new List<AudioAssignment>();
            var skipped = new List<AudioSkip>();
            if (candidates is null || candidates.Count == 0)
            {
                return new AudioReplacementPlan(assignments, skipped);
            }

            var catalog = soundtrack ?? SoundtrackCatalog.Empty;
            var allRows = rows ?? Array.Empty<AudioRowDescriptor>();
            var winners = new Dictionary<string, AudioAssignment>(StringComparer.OrdinalIgnoreCase);
            var paidChecked = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in candidates)
            {
                if (candidate is null)
                {
                    continue;
                }

                var row = Match(candidate, allRows, out var byClipName);
                if (row is null)
                {
                    skipped.Add(new AudioSkip(
                        candidate,
                        AudioSkipKind.UnknownRow,
                        "no " + AudioTables.Id(candidate.Table) + " row is called '" + candidate.Stem + "' "
                        + "(by row name or game clip name); try 'audio list " + AudioTables.Id(candidate.Table) + "'",
                        null));
                    continue;
                }

                if (!paidChecked.TryGetValue(row.Key, out var refusal))
                {
                    refusal = AudioPaidContent.CheckRowReplacement(row, catalog);
                    paidChecked[row.Key] = refusal;
                }

                if (refusal != null)
                {
                    skipped.Add(new AudioSkip(candidate, AudioSkipKind.Paid, refusal, row));
                    continue;
                }

                if (winners.TryGetValue(row.Key, out var previous))
                {
                    skipped.Add(new AudioSkip(
                        previous.Candidate,
                        AudioSkipKind.Overridden,
                        "overridden by " + candidate.Source + " (the later claim wins)",
                        row));
                }

                winners[row.Key] = new AudioAssignment(row, candidate, byClipName);
            }

            // Table order, then the game's row order: the order 'audio list' prints.
            foreach (var row in allRows)
            {
                if (row != null && winners.TryGetValue(row.Key, out var assignment) && ReferenceEquals(assignment.Row, row))
                {
                    assignments.Add(assignment);
                }
            }

            return new AudioReplacementPlan(assignments, skipped);
        }

        /// <summary>The assignment for a row key, or null.</summary>
        /// <param name="key">A row key, e.g. <c>ambient/LightRain</c>.</param>
        public AudioAssignment? For(string? key) =>
            key != null && _byKey.TryGetValue(key, out var assignment) ? assignment : null;

        /// <summary>One line: <c>2 row(s) replaced from packs; 1 refused (paid); 0 unknown; 1 overridden</c>.</summary>
        public string Summarize()
        {
            int unknown = 0, overridden = 0, paid = 0;
            foreach (var skip in _skipped)
            {
                switch (skip.Kind)
                {
                    case AudioSkipKind.UnknownRow: unknown++; break;
                    case AudioSkipKind.Overridden: overridden++; break;
                    case AudioSkipKind.Paid: paid++; break;
                }
            }

            var sb = new StringBuilder();
            sb.Append(_assignments.Count).Append(" row(s) replaced from packs");
            sb.Append("; ").Append(paid).Append(" refused (paid)");
            sb.Append("; ").Append(unknown).Append(" unknown");
            sb.Append("; ").Append(overridden).Append(" overridden");
            return sb.ToString();
        }

        private static AudioRowDescriptor? Match(AudioCandidate candidate, IReadOnlyList<AudioRowDescriptor> rows, out bool byClipName)
        {
            byClipName = false;
            AudioRowDescriptor? byClip = null;
            foreach (var row in rows)
            {
                if (row is null || row.Table != candidate.Table)
                {
                    continue;
                }

                if (string.Equals(row.Name, candidate.Stem, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }

                if (byClip is null && row.OriginalClipName.Length > 0
                    && string.Equals(row.OriginalClipName, candidate.Stem, StringComparison.OrdinalIgnoreCase))
                {
                    byClip = row;
                }
            }

            byClipName = byClip != null;
            return byClip;
        }
    }
}
