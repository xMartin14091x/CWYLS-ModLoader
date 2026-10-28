using CwyLs.ModLoader.Core.MasterData;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// Which scene groups the game's save must never record: the groups a pack added (an id the game's
    /// own data does not have). It is the pack-lines save rule (<c>PackLineSaveFilter</c>: pack-line ids
    /// never reach <c>ScenarioProgressData.PlayedScenarioGroupIDs</c> in the file), stated for every
    /// pack scene, so the two guards agree: every pack line is a group a pack added
    /// (<c>PackLineScreen</c>), and this rule keeps the story path out of the save the way pack lines
    /// keep small talk out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where it applies: the prefix on <c>Bulbul.FacilityStory.SaveScenarioPlayedLog</c>
    /// (game 1.17.3) skips the whole method for a pack scene, so a long pack scene changes neither the
    /// main-story progress (<c>FinishReadEpisode</c>), nor a collaboration's progress
    /// (<c>OnEndStory</c>), nor the played list.
    /// A vanilla game therefore loads the save unchanged in meaning, and Steam Cloud carries no mod id.
    /// </para>
    /// <para>
    /// Consequence the lint enforces (rule S05): a pack scene stays "unseen" to the game, so an Extra
    /// scene must carry a ContentType that keeps the game from offering it on its own.
    /// </para>
    /// </remarks>
    public static class ScenarioSaveRule
    {
        /// <summary>
        /// True when the save must not record <paramref name="groupId"/>: the game's own ids are known
        /// and this is not one of them. False when the baseline is empty (not captured yet): then the
        /// game's own code runs unchanged.
        /// </summary>
        /// <param name="groupId">The group the game is about to log.</param>
        /// <param name="baseline">The game's own ids (captured at master-data injection).</param>
        public static bool IsPackScene(string? groupId, MasterDataBaseline? baseline) =>
            !string.IsNullOrEmpty(groupId)
            && baseline != null
            && baseline.ScenarioGroupCount > 0
            && !baseline.HasScenarioGroup(groupId);
    }
}
