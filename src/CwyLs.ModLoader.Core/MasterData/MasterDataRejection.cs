using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One thing a pack asked for that was refused, and why. A rejection is never fatal: the rest
    /// of the file still loads, and the reason is logged and put in the smoke report so the modder
    /// can see exactly which row is wrong.
    /// </summary>
    public sealed class MasterDataRejection
    {
        /// <summary><see cref="Index"/> value meaning "the file as a whole", not one row.</summary>
        public const int WholeFile = -1;

        /// <summary>Creates a rejection.</summary>
        /// <param name="file">Where it came from, e.g. <c>cwy.example/data/novel.json</c>. Required.</param>
        /// <param name="index">Zero-based row index, or <see cref="WholeFile"/>.</param>
        /// <param name="member">The offending member name, or null when the row as a whole is at fault.</param>
        /// <param name="reason">What is wrong, in the modder's terms. Required.</param>
        /// <exception cref="ArgumentException"><paramref name="file"/> or <paramref name="reason"/> is blank.</exception>
        public MasterDataRejection(string file, int index, string? member, string reason)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new ArgumentException("a file is required", nameof(file));
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("a reason is required", nameof(reason));
            }

            File = file;
            Index = index < 0 ? WholeFile : index;
            Member = string.IsNullOrWhiteSpace(member) ? null : member;
            Reason = reason;
        }

        /// <summary>The file the rejected content came from.</summary>
        public string File { get; }

        /// <summary>Zero-based row index within that file, or <see cref="WholeFile"/>.</summary>
        public int Index { get; }

        /// <summary>The offending member, or null.</summary>
        public string? Member { get; }

        /// <summary>Why it was refused.</summary>
        public string Reason { get; }

        /// <summary><c>file[index].member: reason</c>, the form the log and the report use.</summary>
        public override string ToString()
        {
            var where = File;
            if (Index != WholeFile)
            {
                where += "[" + Index.ToString(CultureInfo.InvariantCulture) + "]";
            }

            if (Member != null)
            {
                where += "." + Member;
            }

            return where + ": " + Reason;
        }
    }
}
