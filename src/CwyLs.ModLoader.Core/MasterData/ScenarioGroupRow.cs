using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One row of <c>data/scenario_groups.json</c>: a named sequence of novel rows and the rules
    /// for when the game may play it.
    /// <para>
    /// Mirrors <c>Bulbul.MasterData.ScenarioGroupData</c>.
    /// Defaults are the values
    /// the game's own short groups carry (<c>"0"</c> for "no localization id", <c>"None"</c> for
    /// the three condition columns), from the game's own ScenarioGroupMaster rows.
    /// </para>
    /// </summary>
    public sealed class ScenarioGroupRow
    {
        /// <summary>Group id, the value novel rows point at with their group id. Required; unique.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Localization id of the group's title, or <c>"0"</c> when it has none.</summary>
        public string TitleLocalizationId { get; init; } = "0";

        /// <summary>Localization id of the group's subtitle, or <c>"0"</c>.</summary>
        public string SubtitleLocalizationId { get; init; } = "0";

        /// <summary>
        /// When the game plays this group; a member of <see cref="MasterDataEnums.Scenario"/>.
        /// Required - it decides which of the reader's two very different code paths runs.
        /// </summary>
        public int Scenario { get; init; }

        /// <summary>Order within its scenario type.</summary>
        public float EpisodeNumber { get; init; }

        /// <summary>Player level required before the group is offered.</summary>
        public int UnlockLevel { get; init; }

        /// <summary>Main-story episode that must be read first, or <c>"None"</c>.</summary>
        public string UnlockStoryEpisode { get; init; } = "None";

        /// <summary>When during a session it may play, or <c>"None"</c>.</summary>
        public string PlayTiming { get; init; } = "None";

        /// <summary>
        /// Extra condition name parsed by <c>ScenarioGroupMasterWrapper.IsContentTypeConditionOK</c>
        /// for Extra and Event groups, or <c>"None"</c>.
        /// </summary>
        public string ContentType { get; init; } = "None";

        /// <summary>First free-form argument.</summary>
        public string Arg1 { get; init; } = "0";

        /// <summary>Second free-form argument.</summary>
        public string Arg2 { get; init; } = "0";

        /// <summary>Third free-form argument.</summary>
        public string Arg3 { get; init; } = "0";

        /// <summary>
        /// CWY member, not a game column (pack lines): the language codes this group is exclusive
        /// to, e.g. <c>["th"]</c>, as written in the pack. Null (the member is absent) means an
        /// ordinary group the game may play in every language. A group that declares languages is a
        /// <em>pack line</em>: it is screened by <c>CwyLs.ModLoader.Core.PackLines.PackLineScreen</c>,
        /// injected only while the pack-lines feature is active, and offered by the game's selectors
        /// only while the game shows one of these languages. Never reaches the game's own
        /// <c>ScenarioGroupData</c>.
        /// </summary>
        public IReadOnlyList<string>? Languages { get; init; }

        /// <summary>
        /// CWY member, not a game column (pack lines): which pack-lines pool a language-exclusive
        /// group joins, e.g. <c>selftalk.time.7</c>. Empty means "the pool its <see cref="Scenario"/>
        /// implies"; required only where one scenario type feeds several pools
        /// (<c>HeroineSelfShortTalk_CurrentTime</c>, <c>HeroineSelfShortTalk_WorkedTime</c>).
        /// Ignored for a group without <see cref="Languages"/>.
        /// </summary>
        public string Pool { get; init; } = string.Empty;

        /// <summary><c>id</c>.</summary>
        public override string ToString() => Id;
    }
}
