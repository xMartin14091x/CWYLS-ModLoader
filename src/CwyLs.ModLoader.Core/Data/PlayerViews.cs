using System;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>Level and experience, from the game's <c>LevelData</c>.</summary>
    public sealed class LevelView
    {
        /// <summary>Current level, 1-based.</summary>
        public int CurrentLevel { get; init; }

        /// <summary>Experience accumulated towards the next level.</summary>
        public float CurrentExp { get; init; }

        /// <summary>Experience the next level needs; 0 before the game has computed it.</summary>
        public float NextLevelNecessaryExp { get; init; }
    }

    /// <summary>
    /// Career-level player state from <c>PlayerDataV3</c>: how long they have worked, how often
    /// they log in, where they are in the level curve.
    /// </summary>
    public sealed class PlayerView
    {
        /// <summary>Level and experience.</summary>
        public LevelView Level { get; init; } = new LevelView();

        /// <summary>Number of distinct days the player has logged in.</summary>
        public int TotalLoginCount { get; init; }

        /// <summary>
        /// Last login timestamp, or <c>null</c> when the game's stored string is missing or
        /// unparseable (the game's own accessor throws in that case; the bus does not).
        /// </summary>
        public DateTime? LastLoginAt { get; init; }

        /// <summary>True while the game still wants to run the tutorial.</summary>
        public bool IsNeedTutorial { get; init; }

        /// <summary>Seconds of the work slice currently in progress.</summary>
        public double CurrentWorkSeconds { get; init; }

        /// <summary>Lifetime seconds of pomodoro work.</summary>
        public double PomodoroTotalWorkSeconds { get; init; }

        /// <summary>Work seconds at which the last story unlock happened.</summary>
        public double LastStoryUnlockWorkSeconds { get; init; }

        /// <summary>True once the player has received their first point grant.</summary>
        public bool HasReceivedFirstPoint { get; init; }
    }

    /// <summary>
    /// The player's public-facing profile: birthday, chosen icon and presence status
    /// (<c>ProfileData</c>). The custom icon image itself is never published - only whether one is set.
    /// </summary>
    public sealed class ProfileView
    {
        /// <summary>Birthday month, 1-12; 0 when unset.</summary>
        public int BirthdayMonth { get; init; }

        /// <summary>Birthday day, 1-31; 0 when unset.</summary>
        public int BirthdayDay { get; init; }

        /// <summary>The icon slot the player picked; <c>Custom</c> means an uploaded image.</summary>
        public string IconType { get; init; } = string.Empty;

        /// <summary>Presence status the player set (Active / Away / SuperFocus).</summary>
        public string Status { get; init; } = string.Empty;

        /// <summary>
        /// True when a custom icon image is stored. The image bytes are deliberately excluded:
        /// the snapshot is a text file other tools read, not an image carrier.
        /// </summary>
        public bool HasCustomIcon { get; init; }
    }

    /// <summary>
    /// The redacted view of the game's <c>AccountData</c>. The game stores a device identifier
    /// there and the bus never publishes it - see <see cref="GameDataPrivacy.RedactAccount"/> for
    /// the rule and the reasoning.
    /// </summary>
    public sealed class AccountView
    {
        /// <summary>
        /// True when the game holds a device id. The id itself is never exposed, in any form.
        /// </summary>
        public bool HasDeviceId { get; init; }
    }
}
