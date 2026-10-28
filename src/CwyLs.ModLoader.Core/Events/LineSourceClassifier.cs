using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// Turns the game's scenario type name into <em>why</em> Satone is speaking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every voice line in the game is played by <c>ScenarioReader</c>, and the scenario type it is
    /// playing is the only thing that distinguishes "reading the story" from "reacting to a click"
    /// from "announcing a pomodoro". The enum has 60-odd members (<c>Bulbul.MasterData.ScenarioType</c>)
    /// and grows with every content update, so matching is by name prefix rather than by value: a
    /// scenario type added tomorrow still classifies, instead of silently becoming
    /// <see cref="LineSource.Other"/>.
    /// </para>
    /// <para>
    /// Order matters. <c>SpeakWord_Pomodoro_*</c> and <c>Tutorial</c> are checked before the
    /// broader prefixes because a name can satisfy more than one rule.
    /// </para>
    /// </remarks>
    public static class LineSourceClassifier
    {
        /// <summary>
        /// Classifies a scenario type name.
        /// </summary>
        /// <param name="scenarioType">
        /// Name of the scenario type playing, e.g. <c>HeroineClickWork_Morning</c>; null or empty
        /// when no scenario is playing.
        /// </param>
        /// <returns>The source, or <see cref="LineSource.Other"/> when nothing matches.</returns>
        public static LineSource Classify(string? scenarioType)
        {
            if (string.IsNullOrEmpty(scenarioType))
            {
                return LineSource.Other;
            }

            var name = scenarioType!;

            if (name.IndexOf("Tutorial", StringComparison.Ordinal) >= 0)
            {
                return LineSource.Tutorial;
            }

            if (name.IndexOf("Pomodoro", StringComparison.Ordinal) >= 0)
            {
                return LineSource.Pomodoro;
            }

            if (name.StartsWith("HeroineClick", StringComparison.Ordinal))
            {
                return LineSource.ClickReaction;
            }

            if (name.StartsWith("HeroineSelfShortTalk", StringComparison.Ordinal)
                || name.StartsWith("ShortConversation", StringComparison.Ordinal)
                || name.StartsWith("SmallTalk", StringComparison.Ordinal)
                || name.StartsWith("GameStart", StringComparison.Ordinal)
                || name.StartsWith("GameEnd", StringComparison.Ordinal)
                || name.StartsWith("Photograph_GameStart", StringComparison.Ordinal))
            {
                return LineSource.SelfTalk;
            }

            if (name.EndsWith("Scenario", StringComparison.Ordinal)
                || name.StartsWith("Special_", StringComparison.Ordinal)
                || name.StartsWith("Event_", StringComparison.Ordinal)
                || name.StartsWith("GameDemo", StringComparison.Ordinal))
            {
                return LineSource.Story;
            }

            return LineSource.Other;
        }
    }
}
