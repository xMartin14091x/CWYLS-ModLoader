using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// What one kind of master-data file contributed: how many rows are new to the game, how many
    /// replaced a row the game already had, how many packs fought over the same id, and everything
    /// that was refused.
    /// </summary>
    public sealed class MasterDataFileReport
    {
        private static readonly MasterDataRejection[] NoRejections = new MasterDataRejection[0];

        /// <summary>Creates a report.</summary>
        /// <param name="kind">Which file this describes, e.g. <c>localization</c>. Required.</param>
        /// <param name="added">Rows whose id the game does not have.</param>
        /// <param name="overridden">Rows that replace a row the game already had.</param>
        /// <param name="packOverrides">Rows that replaced an earlier pack's row for the same id.</param>
        /// <param name="rejected">Everything refused, in file order; null means none.</param>
        /// <exception cref="ArgumentException"><paramref name="kind"/> is blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
        public MasterDataFileReport(
            string kind,
            int added,
            int overridden,
            int packOverrides = 0,
            IReadOnlyList<MasterDataRejection>? rejected = null)
        {
            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("a kind is required", nameof(kind));
            }

            if (added < 0) { throw new ArgumentOutOfRangeException(nameof(added), added, "cannot be negative"); }
            if (overridden < 0) { throw new ArgumentOutOfRangeException(nameof(overridden), overridden, "cannot be negative"); }
            if (packOverrides < 0) { throw new ArgumentOutOfRangeException(nameof(packOverrides), packOverrides, "cannot be negative"); }

            Kind = kind;
            Added = added;
            Overridden = overridden;
            PackOverrides = packOverrides;
            Rejected = rejected ?? NoRejections;
        }

        /// <summary>Which file this describes: <c>localization</c>, <c>novel</c>, <c>scenario_groups</c>, <c>subtitles</c>.</summary>
        public string Kind { get; }

        /// <summary>Rows whose id the game does not have.</summary>
        public int Added { get; }

        /// <summary>Rows that replace a row the game already had.</summary>
        public int Overridden { get; }

        /// <summary>
        /// Rows that replaced an earlier pack's row for the same id. Not an error - that is what
        /// pack order is for - but it is the first thing to look at when two packs disagree.
        /// </summary>
        public int PackOverrides { get; }

        /// <summary>Everything refused, in file order.</summary>
        public IReadOnlyList<MasterDataRejection> Rejected { get; }

        /// <summary>Rows that made it into the merged set.</summary>
        public int Accepted => Added + Overridden;

        /// <summary>True when nothing was added, overridden or refused.</summary>
        public bool IsEmpty => Added == 0 && Overridden == 0 && Rejected.Count == 0;

        /// <summary>The <c>kind +a/~o</c> form the startup log line is built from.</summary>
        public override string ToString() => Kind + " +" + Added + "/~" + Overridden;
    }
}
