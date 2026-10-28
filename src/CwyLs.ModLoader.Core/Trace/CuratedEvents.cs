using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// The short list of calls that are interesting enough to mirror to the BepInEx console at Info,
    /// and the only calls patched at <see cref="TraceLevel.Events"/>. Curated calls also get a
    /// postfix so their return value is recorded; nothing else does.
    /// </summary>
    /// <remarks>
    /// Names verified against game v1.17.2. Two names from the original list do not
    /// exist in this build — <c>PomodoroService.StartPomodoroTimer</c> (the real entry point is
    /// <c>StartPomodoro</c>) and <c>MusicManager.Play</c> only exists as an <c>AudioClip</c>
    /// overload. They are kept in the table anyway: an unmatched entry costs nothing and the list
    /// keeps working if a future game version renames things back.
    /// </remarks>
    public static class CuratedEvents
    {
        /// <summary>Classes whose every traced method is curated.</summary>
        private static readonly HashSet<string> CuratedClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "WindowViewService",
            "MusicService",
            "MusicManager",
            "ScenarioMusicManager",
            "AmbientBGMManager",
            "BGMManager",
            "MyMusicManager",

            // Clicking Satone: the whole point of the mod, so every method of the handler is curated.
            "FacilityClickHeroine",

            // Synthetic class used for the lines the plugin writes itself (scene loads, shutdown).
            "Scene",
        };

        /// <summary>Individually curated <c>Class.Method</c> pairs.</summary>
        private static readonly HashSet<string> CuratedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "HeroineAI.ChangeState",
            "ScenarioReader.StartReady",
            "ScenarioReader.EndStory",
            "HeroineService.PlayVoice",

            // Pomodoro. The first two are the originally listed names; the rest are the ones this build has.
            "PomodoroService.StartPomodoroTimer",
            "PomodoroService.CompletePomodoroTimer",
            "PomodoroService.StartPomodoro",
            "PomodoroService.PlayOrPausePomodoroTimer",
            "PomodoroService.OnTimerEnd",
            "PomodoroService.SkipTimer",
            "PomodoroService.ResetTimer",

            "VoiceManager.Play",

            // Satone reacting to being clicked.
            "HeroineAI.StartHeroineClickReaction",
            "HeroineAI.FinishHeroineClickReaction",

            // Synthetic: one line per real click, emitted from the GetClickDown postfix.
            "Input.click",
        };

        /// <summary>Classes where only the save/load surface is curated.</summary>
        private static readonly HashSet<string> SaveLoadClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "SaveDataManager",
        };

        /// <summary>True when this call should be mirrored to the console and given a return-value postfix.</summary>
        /// <param name="className">Simple class name, without its namespace.</param>
        /// <param name="methodName">Method name.</param>
        public static bool IsCurated(string? className, string? methodName)
        {
            if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            if (CuratedClasses.Contains(className!))
            {
                return true;
            }

            if (SaveLoadClasses.Contains(className!))
            {
                return methodName!.StartsWith("Save", StringComparison.Ordinal)
                    || methodName.StartsWith("Load", StringComparison.Ordinal);
            }

            return CuratedMethods.Contains(className + "." + methodName);
        }

        /// <summary>The curated classes, for the start-up summary.</summary>
        public static IReadOnlyCollection<string> Classes => CuratedClasses;

        /// <summary>The individually curated <c>Class.Method</c> pairs, for the start-up summary.</summary>
        public static IReadOnlyCollection<string> Methods => CuratedMethods;
    }
}
