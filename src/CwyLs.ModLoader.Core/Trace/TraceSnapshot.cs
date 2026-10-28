using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>Pomodoro half of a state snapshot. Every member is nullable: a field that could not be resolved is written as null, never guessed.</summary>
    public sealed class PomodoroSnapshot
    {
        /// <summary>Current <c>PomodoroService.PomodoroType</c> (Work, Break, Complete).</summary>
        public string? Type { get; set; }

        /// <summary>Whether the timer is running.</summary>
        public bool? Running { get; set; }

        /// <summary>Seconds left in the current work or break block.</summary>
        public double? RemainingSeconds { get; set; }

        /// <summary>Current loop number within the pomodoro set.</summary>
        public int? Loop { get; set; }
    }

    /// <summary>Music half of a state snapshot.</summary>
    public sealed class MusicSnapshot
    {
        /// <summary>Title of the track playing.</summary>
        public string? Current { get; set; }

        /// <summary>Index of that track in the current playlist, or -1 when it is not in it.</summary>
        public int? Index { get; set; }

        /// <summary>Playlist description: length plus the shuffle/repeat flags.</summary>
        public string? Playlist { get; set; }

        /// <summary>Whether music is actually sounding (a track is loaded and not paused).</summary>
        public bool? IsPlaying { get; set; }

        /// <summary>Effective music volume, 0..1, mute included.</summary>
        public double? Volume { get; set; }
    }

    /// <summary>
    /// One periodic "what is the game doing right now" reading. Built by the plugin from live game
    /// objects and rendered by <see cref="TraceLineBuilder"/>, which is why it is a plain data class.
    /// </summary>
    public sealed class TraceSnapshot
    {
        /// <summary>When the snapshot was taken.</summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>Unity frame counter.</summary>
        public int Frame { get; set; }

        /// <summary>Wall-clock time (<c>DateTime.Now</c>).</summary>
        public DateTime Realtime { get; set; }

        /// <summary>Seconds since the process started (<c>Time.realtimeSinceStartup</c>).</summary>
        public double UnityTime { get; set; }

        /// <summary>The game's own clock, when it could be read.</summary>
        public string? GameClock { get; set; }

        /// <summary>Current <c>HeroineAI.ActionStateType</c>.</summary>
        public string? HeroineState { get; set; }

        /// <summary>Pomodoro state, or null when the service was not resolvable.</summary>
        public PomodoroSnapshot? Pomodoro { get; set; }

        /// <summary>Music state, or null when the service was not resolvable.</summary>
        public MusicSnapshot? Music { get; set; }

        /// <summary>Environment description (the active preset).</summary>
        public string? Environment { get; set; }

        /// <summary>Active scene name.</summary>
        public string? Scene { get; set; }

        /// <summary>Window views currently switched on, or null when the service was not resolvable.</summary>
        public IReadOnlyList<string>? WindowsOpen { get; set; }

        /// <summary>Hierarchy path of the control that currently has keyboard focus, or null.</summary>
        public string? FocusedControl { get; set; }

        /// <summary>How the process itself is doing over the interval just ended.</summary>
        public TraceHealth? Health { get; set; }
    }

    /// <summary>Source of state snapshots. Implemented by the plugin against the live game.</summary>
    public interface ITraceStateProvider
    {
        /// <summary>Reads the current state. Implementations must never throw; unreadable fields are left null.</summary>
        TraceSnapshot Capture();
    }
}
