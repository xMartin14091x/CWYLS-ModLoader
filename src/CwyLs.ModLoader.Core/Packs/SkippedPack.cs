using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>One pack that was found but not loaded, and why.</summary>
    public sealed class SkippedPack
    {
        /// <summary>Creates a skip record.</summary>
        /// <param name="id">The pack's id, or its folder name when the manifest never parsed.</param>
        /// <param name="folderName">The folder the pack was found in.</param>
        /// <param name="reason">Which rule refused it.</param>
        /// <param name="detail">The specifics - the invalid member, the missing dependency, ...</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or blank.</exception>
        public SkippedPack(string id, string folderName, PackSkipReason reason, string detail)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("an id (or folder name) is required", nameof(id));
            }

            Id = id;
            FolderName = folderName ?? string.Empty;
            Reason = reason;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The pack's id, or its folder name when the manifest never parsed.</summary>
        public string Id { get; }

        /// <summary>The folder the pack was found in.</summary>
        public string FolderName { get; }

        /// <summary>Which rule refused it.</summary>
        public PackSkipReason Reason { get; }

        /// <summary>The specifics, written for the pack author.</summary>
        public string Detail { get; }

        /// <summary><c>id: Reason - detail</c>.</summary>
        public override string ToString() =>
            Detail.Length == 0 ? Id + ": " + Reason : Id + ": " + Reason + " - " + Detail;
    }
}
