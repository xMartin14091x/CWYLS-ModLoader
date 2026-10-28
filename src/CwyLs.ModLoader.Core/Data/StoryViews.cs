using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Story playback preference plus scenario progress (<c>StoryData</c> and
    /// <c>ScenarioProgressData</c>), so a mod can tell how far the player has got before writing
    /// dialogue that would spoil something.
    /// </summary>
    public sealed class StoryView
    {
        /// <summary>True when the player reads story scenes on auto-advance.</summary>
        public bool IsUseAuto { get; init; }

        /// <summary>Episode number of the last main episode the player finished.</summary>
        public float FinishedMainEpisodeNumber { get; init; }

        /// <summary>Episode number the game will offer next.</summary>
        public float NextEpisodeNumber { get; init; }

        /// <summary>Player level required to unlock <see cref="NextEpisodeNumber"/>.</summary>
        public int NextEpisodeUnlockLevel { get; init; }

        /// <summary>How many scenario groups the player has played at least once.</summary>
        public int PlayedScenarioGroupCount { get; init; }

        /// <summary>Ids of the scenario groups the player has played.</summary>
        public IReadOnlyList<string> PlayedScenarioGroupIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Unlocked achievements from <c>AchievementSaveData</c>.
    /// </summary>
    /// <remarks>
    /// The game exposes only <c>IsUnlocked(string)</c> - the backing list is private - so the
    /// adapter reads the field reflectively. When that read fails (a game update renamed it),
    /// <see cref="Available"/> is false and <see cref="UnlockedIds"/> is empty rather than the
    /// snapshot claiming the player has unlocked nothing.
    /// </remarks>
    public sealed class AchievementsView
    {
        /// <summary>False when the adapter could not read the game's private list at all.</summary>
        public bool Available { get; init; }

        /// <summary>Ids of unlocked achievements. Empty when <see cref="Available"/> is false.</summary>
        public IReadOnlyList<string> UnlockedIds { get; init; } = Array.Empty<string>();

        /// <summary>Number of unlocked achievements.</summary>
        public int UnlockedCount => UnlockedIds.Count;
    }
}
