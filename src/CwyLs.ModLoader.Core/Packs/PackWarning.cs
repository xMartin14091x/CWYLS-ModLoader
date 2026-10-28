using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// One pack that loaded but deserves a warning line, e.g. it was made for another game version.
    /// A pack has at most one warning per resolution; the framework logs each as one <c>PACKS</c>
    /// line at warning level.
    /// </summary>
    public sealed class PackWarning
    {
        /// <summary>Creates a warning record.</summary>
        /// <param name="id">The pack's id.</param>
        /// <param name="folderName">The folder the pack was found in.</param>
        /// <param name="message">What to tell the pack author, in one line.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="message"/> is null or blank.</exception>
        public PackWarning(string id, string folderName, string message)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a pack id is required", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("a warning needs a message", nameof(message));
            }

            Id = id;
            FolderName = folderName ?? string.Empty;
            Message = message;
        }

        /// <summary>The pack's id.</summary>
        public string Id { get; }

        /// <summary>The folder the pack was found in.</summary>
        public string FolderName { get; }

        /// <summary>The warning, written for the pack author.</summary>
        public string Message { get; }

        /// <summary><c>id: message</c>, the primary column of the log line.</summary>
        public override string ToString() => Id + ": " + Message;
    }
}
