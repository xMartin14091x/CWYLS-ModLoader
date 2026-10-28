using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;

namespace CwyLs.ModLoader.Core.Data
{
    /// <summary>
    /// Which game observable firings (<see cref="GameSignal"/>) the data bus reports as a change,
    /// and under which section and reason. Pure, so the set is unit-tested.
    /// </summary>
    /// <remarks>
    /// The set is exactly what the data bus used to subscribe to itself, so its
    /// <c>DATA ... changed reason=event:&lt;Name&gt;</c> lines are unchanged: the six pomodoro
    /// transitions and the two music observables. Pause, unpause, the per-frame timer update and
    /// the shuffle toggles are typed events for mods but not data changes.
    /// </remarks>
    public static class GameDataSignals
    {
        /// <summary>Prefix of the change reason, e.g. <c>event:OnStartWork</c>.</summary>
        public const string ReasonPrefix = "event:";

        private static readonly Dictionary<string, GameDataSection> Sections =
            new Dictionary<string, GameDataSection>(StringComparer.Ordinal)
            {
                [Key(GameSignal.PomodoroService, "OnStartPomodoro")] = GameDataSection.Pomodoro,
                [Key(GameSignal.PomodoroService, "OnStartWork")] = GameDataSection.Pomodoro,
                [Key(GameSignal.PomodoroService, "OnEndWork")] = GameDataSection.Pomodoro,
                [Key(GameSignal.PomodoroService, "OnStartBreak")] = GameDataSection.Pomodoro,
                [Key(GameSignal.PomodoroService, "OnEndBreak")] = GameDataSection.Pomodoro,
                [Key(GameSignal.PomodoroService, "OnCompletePomodoro")] = GameDataSection.Pomodoro,
                [Key(GameSignal.MusicService, "OnChangeMusic")] = GameDataSection.Music,
                [Key(GameSignal.MusicService, "OnPlayMusic")] = GameDataSection.Music,
            };

        /// <summary>
        /// The section and change reason for a signal, or false when the data bus ignores it.
        /// </summary>
        /// <param name="signal">The observable that fired.</param>
        /// <param name="section">The section it changes, or <see cref="GameDataSection.Unknown"/>.</param>
        /// <param name="reason">The change reason, e.g. <c>event:OnPlayMusic</c>, or empty.</param>
        public static bool TryMap(GameSignal signal, out GameDataSection section, out string reason)
        {
            if (signal.Source != null
                && signal.Name != null
                && Sections.TryGetValue(Key(signal.Source, signal.Name), out section))
            {
                reason = ReasonPrefix + signal.Name;
                return true;
            }

            section = GameDataSection.Unknown;
            reason = string.Empty;
            return false;
        }

        private static string Key(string source, string name) => source + "." + name;
    }
}
