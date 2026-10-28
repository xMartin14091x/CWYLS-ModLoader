using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// One track the player imported from their own disk. The full path is deliberately reduced
    /// to a file name - see <see cref="GameDataPrivacy.RedactLocalMusicPath"/>.
    /// </summary>
    public sealed class LocalTrackView
    {
        /// <summary>The game's id for the imported track.</summary>
        public string Uuid { get; init; } = string.Empty;

        /// <summary>File name only, never the directory it came from.</summary>
        public string FileName { get; init; } = string.Empty;
    }

    /// <summary>
    /// Playback preferences from <c>MusicSettingV2</c> plus the imported local library from
    /// <c>LocalMusicSetting</c>, and - when the room is running - what is playing right now.
    /// </summary>
    public sealed class MusicView
    {
        /// <summary>True when music starts automatically at launch.</summary>
        public bool IsGameStartPlayMusic { get; init; }

        /// <summary>True when shuffle is on.</summary>
        public bool IsShufflePlayMusic { get; init; }

        /// <summary>The playlist tag filter the player selected, by name.</summary>
        public string CurrentAudioTag { get; init; } = string.Empty;

        /// <summary>How many tracks the player favourited.</summary>
        public int FavoriteCount { get; init; }

        /// <summary>How many tracks the player excluded from the playlist.</summary>
        public int ExcludedCount { get; init; }

        /// <summary>The player's imported tracks.</summary>
        public IReadOnlyList<LocalTrackView> LocalTracks { get; init; } = Array.Empty<LocalTrackView>();

        /// <summary>
        /// Title of the track playing right now, or <c>null</c> when nothing is playing or the
        /// music service could not be reached (it only exists once the room scene is up).
        /// </summary>
        public string? NowPlayingTitle { get; init; }
    }
}
