using System;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// The bus's redaction rules, as pure functions so they can be unit-tested without the game.
    /// </summary>
    /// <remarks>
    /// The snapshot is a plain text file on disk that other processes - and, through a mod, a
    /// remote LLM - are meant to read. Anything that identifies the machine or the human rather
    /// than describing the play session is filtered here, once, instead of being trusted to every
    /// call site.
    /// </remarks>
    public static class GameDataPrivacy
    {
        /// <summary>
        /// Reduces the game's <c>AccountData</c> to a non-identifying flag.
        /// </summary>
        /// <param name="deviceId">
        /// The game's <c>AccountData.DeviceID</c>. It is a stable per-install identifier used to
        /// key cloud saves, so it is never published in any form - not hashed, not truncated,
        /// because a stable pseudonym is still a correlatable identifier.
        /// </param>
        /// <returns>A view carrying only whether an id exists.</returns>
        public static AccountView RedactAccount(string? deviceId) =>
            new AccountView { HasDeviceId = !string.IsNullOrEmpty(deviceId) };

        /// <summary>
        /// Reduces an imported music file's path to its file name.
        /// </summary>
        /// <param name="fullPath">
        /// The absolute path the game stored for a locally imported track. The directory part
        /// leaks the OS account name and the player's folder layout, so only the leaf survives.
        /// </param>
        /// <returns>
        /// The file name, or an empty string when the path is null, empty, or ends in a separator.
        /// Handles both separators regardless of the running OS, because a save file written on
        /// Windows can be read on macOS.
        /// </returns>
        public static string RedactLocalMusicPath(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
            {
                return string.Empty;
            }

            var cut = fullPath!.LastIndexOfAny(new[] { '/', '\\' });
            return cut < 0 ? fullPath : fullPath.Substring(cut + 1);
        }

        /// <summary>
        /// Applies the <c>Data.IncludeNoteText</c> switch to a body of free text (a note page, a
        /// diary entry). Text the player wrote is the most sensitive thing in the save file and
        /// also the thing an assistant most needs, so it is a single explicit switch rather than a
        /// per-call decision.
        /// </summary>
        /// <param name="text">The text as the game holds it.</param>
        /// <param name="include">The value of the config switch.</param>
        /// <returns>
        /// <paramref name="text"/> when <paramref name="include"/> is true, otherwise <c>null</c>.
        /// <c>null</c> means "withheld"; an empty string means "the player wrote nothing".
        /// </returns>
        public static string? ApplyTextPolicy(string? text, bool include) => include ? text : null;
    }
}
