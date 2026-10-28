using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// The result of parsing one pack file: the rows that were accepted and every row that was
    /// not. Both halves matter - a pack with one bad row still installs its good ones, and the bad
    /// one is named.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    public sealed class MasterDataParse<T>
    {
        private static readonly T[] NoRows = new T[0];
        private static readonly MasterDataRejection[] NoRejections = new MasterDataRejection[0];

        /// <summary>Creates a result.</summary>
        /// <param name="file">The file that was parsed. Required.</param>
        /// <param name="rows">Accepted rows, in file order; null means none.</param>
        /// <param name="rejected">Rejections, in file order; null means none.</param>
        /// <exception cref="ArgumentException"><paramref name="file"/> is blank.</exception>
        public MasterDataParse(
            string file, IReadOnlyList<T>? rows = null, IReadOnlyList<MasterDataRejection>? rejected = null)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("a file is required", nameof(file));
            }

            File = file;
            Rows = rows ?? NoRows;
            Rejected = rejected ?? NoRejections;
        }

        /// <summary>The file this came from.</summary>
        public string File { get; }

        /// <summary>The accepted rows, in file order.</summary>
        public IReadOnlyList<T> Rows { get; }

        /// <summary>Everything that was refused, in file order.</summary>
        public IReadOnlyList<MasterDataRejection> Rejected { get; }

        /// <summary>True when nothing was refused.</summary>
        public bool Clean => Rejected.Count == 0;

        /// <summary><c>file: n row(s), m rejected</c>.</summary>
        public override string ToString() =>
            File + ": " + Rows.Count + " row(s), " + Rejected.Count + " rejected";
    }
}
