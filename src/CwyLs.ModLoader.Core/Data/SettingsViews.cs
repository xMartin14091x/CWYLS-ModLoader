using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>One audio bus's volume, from the game's <c>VolumeInfo</c>.</summary>
    public sealed class VolumeView
    {
        /// <summary>Which bus, e.g. <c>Master</c>, <c>Music</c>, <c>Voice</c>.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The slider position, 0..1, regardless of mute.</summary>
        public float RawValue { get; init; }

        /// <summary>True when the bus is muted.</summary>
        public bool IsMute { get; init; }

        /// <summary>The effective volume: <see cref="RawValue"/>, or 0 when muted.</summary>
        public float EffectiveValue => IsMute ? 0f : RawValue;
    }

    /// <summary>
    /// The player's settings from <c>SettingData</c>: the volume buses plus the flags a mod or an
    /// assistant plausibly needs (language decides which text to speak; the notification and
    /// self-talk flags decide whether it is welcome to interrupt).
    /// </summary>
    public sealed class SettingsView
    {
        /// <summary>UI language the player chose, by name (e.g. <c>Japanese</c>).</summary>
        public string Language { get; init; } = string.Empty;

        /// <summary>Clock format the player chose (<c>AMPM</c> or <c>Hour24</c>).</summary>
        public string TimeFormat { get; init; } = string.Empty;

        /// <summary>True when the game window is pinned above other windows.</summary>
        public bool IsAlwaysOnTop { get; init; }

        /// <summary>True when picture-in-picture is enabled.</summary>
        public bool EnablePictureInPicture { get; init; }

        /// <summary>Window mode by name (Window / BorderlessFullScreen / ...).</summary>
        public string WindowMode { get; init; } = string.Empty;

        /// <summary>Target framerate while the window has focus.</summary>
        public int ActiveFramerate { get; init; }

        /// <summary>True when the timer plays its sound effects.</summary>
        public bool IsPlayPomodoroSe { get; init; }

        /// <summary>True when the heroine is allowed to talk to herself.</summary>
        public bool IsPlaySelfTalk { get; init; }

        /// <summary>True when pomodoro notifications are on.</summary>
        public bool IsNotificationPomodoro { get; init; }

        /// <summary>True when reminder notifications are on.</summary>
        public bool IsNotificationReminder { get; init; }

        /// <summary>Volume buses, one entry per mixer group.</summary>
        public IReadOnlyList<VolumeView> Volumes { get; init; } = Array.Empty<VolumeView>();
    }
}
