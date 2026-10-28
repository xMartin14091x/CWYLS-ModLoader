using System;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// A language-exclusive group <see cref="PackLineScreen"/> refused: which row, and why. The group
    /// is not injected, so the game never sees it; its novel and localization rows stay in the merged
    /// data as unreferenced rows, which the game ignores.
    /// </summary>
    public sealed class PackLineRejection
    {
        /// <summary>The log prefix every rejection line carries.</summary>
        public const string LogPrefix = "PACKLINES";

        /// <summary>Creates a rejection.</summary>
        /// <param name="groupId">The refused scenario group id. Required.</param>
        /// <param name="reason">What is wrong, in the pack author's terms. Required.</param>
        /// <exception cref="ArgumentException">A required string is blank.</exception>
        public PackLineRejection(string groupId, string reason)
        {
            if (string.IsNullOrWhiteSpace(groupId))
            {
                throw new ArgumentException("a group id is required", nameof(groupId));
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("a reason is required", nameof(reason));
            }

            GroupId = groupId;
            Reason = reason;
        }

        /// <summary>The scenario group id from <c>data/scenario_groups.json</c>.</summary>
        public string GroupId { get; }

        /// <summary>Why it was refused.</summary>
        public string Reason { get; }

        /// <summary>The WARN line: <c>PACKLINES skipped scenario_groups row 'id': reason</c>.</summary>
        public override string ToString() => LogPrefix + " skipped scenario_groups row '" + GroupId + "': " + Reason;
    }
}
