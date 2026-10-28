namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// One page of the in-game notebook.
    /// </summary>
    /// <remarks>
    /// The game keeps only the three most recently touched pages in memory
    /// (<c>NoteDataV2.PageCache</c>); every other page's body lives on disk until it is opened.
    /// <see cref="Text"/> is therefore populated only when the body was already available or the
    /// adapter was allowed to read it - <see cref="TextAvailable"/> says which, and the
    /// <c>Data.IncludeNoteText</c> config switch can turn the body off entirely for privacy.
    /// <see cref="Title"/> always comes from the game's cheap title index and is always present.
    /// </remarks>
    public sealed class NotePageView
    {
        /// <summary>Game-assigned page id.</summary>
        public ulong Id { get; init; }

        /// <summary>Position in the player's page order, starting at 0.</summary>
        public int Order { get; init; }

        /// <summary>
        /// The page title from the game's title index. The game seeds it from the page's first
        /// body text, so it doubles as a preview. Empty when the page has never been named.
        /// </summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>
        /// Full body text, or <c>null</c> when it was not read (see <see cref="TextAvailable"/>).
        /// </summary>
        public string? Text { get; init; }

        /// <summary>
        /// True when <see cref="Text"/> holds the real body. False means "not loaded or withheld",
        /// which is not the same as "the page is empty".
        /// </summary>
        public bool TextAvailable { get; init; }

        /// <summary>Vertical scroll position the player left the page at, 0..1.</summary>
        public float ScrollValue { get; init; }
    }
}
