using System.Collections.Generic;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Bulbul.Events;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// The Phase-2 master-data checks: proof, in the running game, that a pack's rows really are in
    /// <c>MasterDataLoader</c>, that the game's own wrapper reads them, that every text table a pack
    /// ships (<c>data/subtitles/&lt;code&gt;.json</c>) was read, and that an injected scenario group is
    /// complete enough for <c>ScenarioReader.StartReady</c> to play.
    /// </summary>
    /// <remarks>
    /// The rows come from <c>packs/cwy.example</c>, deployed by <c>./deploy-packs.sh</c>. Nothing
    /// here starts a scenario: <c>StartReady</c> takes over the story UI and Satone's animation, and
    /// a smoke pass must never do that behind the player's back. The check proves the data is
    /// playable and stops there.
    /// </remarks>
    internal static class MasterDataSmokeChecks
    {
        /// <summary>Smoke-report phase these checks are grouped under.</summary>
        internal const string Phase = "P2";

        /// <summary>The scenario group shipped in <c>packs/cwy.example/data/scenario_groups.json</c>.</summary>
        internal const string ExampleGroupId = "cwy_example_01";

        /// <summary>The localization row shipped in <c>packs/cwy.example/data/localization.json</c>.</summary>
        internal const string ExampleLocalizationId = "cwy_example_01_001";

        private const string InjectedId = "p2.masterdata.injected";
        private const string WrapperId = "p2.masterdata.wrapper_reads";
        private const string TextTablesId = "p2.masterdata.text_tables";
        private const string ScenarioId = "p2.masterdata.scenario_playable";

        /// <summary>Registers every master-data check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(InjectedId, Phase, Injected));
            SmokeRegistry.Register(new SmokeCheck(WrapperId, Phase, WrapperReads));
            SmokeRegistry.Register(new SmokeCheck(TextTablesId, Phase, TextTables));
            SmokeRegistry.Register(new SmokeCheck(ScenarioId, Phase, ScenarioPlayable));
        }

        /// <summary>The packs' rows are in the game's own collections.</summary>
        private static SmokeCheckResult Injected()
        {
            if (!MasterDataFeature.Enabled)
            {
                return SmokeCheckResult.Fail(
                    InjectedId,
                    MasterDataFeature.ConfigSection + "." + MasterDataFeature.ConfigKeyEnabled
                    + "=false, so nothing was injected");
            }

            if (!MasterDataFeature.IsInjected)
            {
                return SmokeCheckResult.Fail(
                    InjectedId,
                    "not injected yet; patch: " + MasterDataFeature.PatchDetail
                    + "; loader ready: " + MasterDataFeature.IsReadyToInject());
            }

            var loader = MasterDataFeature.ResolveLoader();
            if (loader is null)
            {
                return SmokeCheckResult.Fail(InjectedId, "the MasterDataLoader is not reachable any more");
            }

            var merged = MasterDataFeature.Merged;
            if (merged.Localization.Count == 0 && merged.Novel.Count == 0 && merged.ScenarioGroups.Count == 0)
            {
                return SmokeCheckResult.Fail(
                    InjectedId, "no pack shipped a data/*.json row; " + MasterDataFeature.LastSummary);
            }

            var missing = new List<string>();

            foreach (var row in merged.Localization)
            {
                if (loader.LocalizationList is null || !loader.LocalizationList.ContainsKey(row.Id))
                {
                    missing.Add("localization " + row.Id);
                }
            }

            foreach (var row in merged.ScenarioGroups)
            {
                if (FindGroup(loader, row.Id) is null)
                {
                    missing.Add("scenario_group " + row.Id);
                }
            }

            foreach (var row in merged.Novel)
            {
                if (!HasNovelRow(loader, row.Id))
                {
                    missing.Add("novel " + row.Id);
                }
            }

            if (missing.Count > 0)
            {
                return SmokeCheckResult.Fail(
                    InjectedId, "merged but not present in the loader: " + string.Join(", ", missing.ToArray()));
            }

            return SmokeCheckResult.Pass(
                InjectedId,
                MasterDataFeature.LastSummary + " (trigger " + MasterDataFeature.LastTrigger + ")");
        }

        /// <summary>
        /// The injected row is in the game's own <c>LocalizationList</c> with the pack's text, and
        /// the game's own <c>LocalizationMasterWrapper</c> reads it.
        /// </summary>
        /// <remarks>
        /// The <em>row</em> is asserted exactly - that is what this feature owns - but the wrapper's
        /// return value is only required to be non-empty, and whatever it returned is reported.
        /// <c>LocalizationMasterWrapper.Get</c> is a documented extension point that another
        /// feature may legitimately postfix: the language feature serves a pack language's row
        /// from it. Demanding the English string back would make this check fail for a reason that
        /// has nothing to do with master-data injection.
        /// </remarks>
        private static SmokeCheckResult WrapperReads()
        {
            var row = PickLocalizationRow();
            if (row is null)
            {
                return SmokeCheckResult.Fail(WrapperId, "no pack shipped a localization row to read");
            }

            var loader = MasterDataFeature.ResolveLoader();
            if (loader?.LocalizationList is null)
            {
                return SmokeCheckResult.Fail(WrapperId, "the game's LocalizationList is not available");
            }

            if (!loader.LocalizationList.TryGetValue(row!.Id, out var data) || data is null)
            {
                return SmokeCheckResult.Fail(WrapperId, "'" + row.Id + "' is not in the game's LocalizationList");
            }

            // The real assertion: the row the game holds is the row the pack shipped.
            if (!string.Equals(data.En, row.En, System.StringComparison.Ordinal))
            {
                return SmokeCheckResult.Fail(
                    WrapperId,
                    "LocalizationList['" + row.Id + "'].En is \"" + data.En + "\" but the pack says \""
                    + row.En + "\"");
            }

            if (!string.Equals(data.Ja, row.Ja, System.StringComparison.Ordinal))
            {
                return SmokeCheckResult.Fail(
                    WrapperId,
                    "LocalizationList['" + row.Id + "'].Ja is \"" + data.Ja + "\" but the pack says \""
                    + row.Ja + "\"");
            }

            if (!GameScopes.TryProject<Game.LocalizationMasterWrapper>(out var wrapper) || wrapper is null)
            {
                return SmokeCheckResult.Fail(
                    WrapperId, "LocalizationMasterWrapper is not resolvable from the project scope yet");
            }

            var returned = wrapper!.Get(data, Game.GameLanguageType.English);
            if (string.IsNullOrEmpty(returned))
            {
                return SmokeCheckResult.Fail(
                    WrapperId,
                    "LocalizationMasterWrapper.Get('" + row.Id + "', English) returned nothing, so the row "
                    + "is in the dictionary but unreadable through the game's own accessor");
            }

            var note = string.Equals(returned, row.En, System.StringComparison.Ordinal)
                ? "the pack's En"
                : "transformed by another feature (the row itself still says \"" + row.En + "\")";

            return SmokeCheckResult.Pass(
                WrapperId,
                "row '" + row.Id + "' holds the pack's En and Ja; "
                + "LocalizationMasterWrapper.Get(English) = \"" + returned + "\" - " + note);
        }

        /// <summary>
        /// Every text table the packs ship was read, with rows, whatever its language. Which of them
        /// is a selectable language is the language feature's check (<c>pb.language.registry</c>).
        /// </summary>
        private static SmokeCheckResult TextTables()
        {
            var tables = MasterDataFeature.Subtitles;
            if (tables.Languages.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(TextTablesId, "no loaded pack ships a data/subtitles/<code>.json");
            }

            var parts = new List<string>();
            var empty = new List<string>();
            foreach (var language in tables.Languages)
            {
                var ids = tables.IdsFor(language);
                if (ids.Count == 0)
                {
                    empty.Add(language);
                    continue;
                }

                tables.TryGet(ids[0], language, out var sample);
                parts.Add(language + ": " + ids.Count + " row(s), e.g. '" + ids[0] + "' = \"" + sample + "\"");
            }

            return empty.Count == 0
                ? SmokeCheckResult.Pass(TextTablesId, string.Join("; ", parts))
                : SmokeCheckResult.Fail(TextTablesId, "empty table(s): " + string.Join(", ", empty) + "; " + string.Join("; ", parts));
        }

        /// <summary>
        /// The injected scenario group is complete: the group row is in the loader, its novel rows
        /// are there in order, every line it speaks resolves to a localization row, and its
        /// scenario type takes <c>StartReady</c>'s short path (no tutorial or long-story state).
        /// The scenario is deliberately not started.
        /// </summary>
        private static SmokeCheckResult ScenarioPlayable()
        {
            var loader = MasterDataFeature.ResolveLoader();
            if (loader is null || loader.ScenarioGroupMasterList is null || loader.NovelMasterList is null)
            {
                return SmokeCheckResult.Fail(ScenarioId, "the game's master data is not available");
            }

            var wanted = PickGroupId();
            if (wanted is null)
            {
                return SmokeCheckResult.Fail(ScenarioId, "no pack shipped a scenario group");
            }

            var group = FindGroup(loader, wanted!);
            if (group is null)
            {
                return SmokeCheckResult.Fail(ScenarioId, "'" + wanted + "' is not in ScenarioGroupMasterList");
            }

            var rows = new List<Master.NovelData>();
            foreach (var row in loader.NovelMasterList)
            {
                if (row != null && string.Equals(row.ScenarioGroupID, wanted, System.StringComparison.Ordinal))
                {
                    rows.Add(row);
                }
            }

            if (rows.Count == 0)
            {
                // StartReady reads the group's first row straight away,
                // so a group with no rows is a guaranteed IndexOutOfRangeException, not a no-op.
                return SmokeCheckResult.Fail(
                    ScenarioId, "'" + wanted + "' has no novel rows; StartReady would throw on row 0");
            }

            // A long story or the tutorial needs UI and progress state a smoke pass has no business
            // creating; the example group deliberately uses a type that takes the short path.
            if (Master.ScenarioTypeExtensions.IsLongStoryOrTutorial(group!.Scenario))
            {
                return SmokeCheckResult.Fail(
                    ScenarioId,
                    "'" + wanted + "' is a " + group.Scenario
                    + ", which StartReady only plays with story UI and progress state");
            }

            foreach (var row in rows)
            {
                if (row.Command != Master.CommandType.VoiceText && row.Command != Master.CommandType.Text)
                {
                    continue;
                }

                if (loader.LocalizationList is null
                    || !loader.LocalizationList.TryGetValue(row.LocalizationID ?? string.Empty, out var line)
                    || line is null)
                {
                    // CommandVoiceText dereferences the row it finds,
                    // so a missing localization id is a null reference at play time.
                    return SmokeCheckResult.Fail(
                        ScenarioId,
                        "row '" + row.ID + "' speaks localization id '" + row.LocalizationID
                        + "', which is not in LocalizationList");
                }
            }

            return SmokeCheckResult.Pass(
                ScenarioId,
                "'" + wanted + "' (" + group.Scenario + ") has " + rows.Count + " row(s): "
                + Describe(rows) + "; not started");
        }

        private static LocalizationRow? PickLocalizationRow()
        {
            var merged = MasterDataFeature.Merged;

            foreach (var row in merged.Localization)
            {
                if (string.Equals(row.Id, ExampleLocalizationId, System.StringComparison.Ordinal))
                {
                    return row;
                }
            }

            // The example pack is not installed: any injected row proves the same thing.
            return merged.Localization.Count > 0 ? merged.Localization[0] : null;
        }

        private static string? PickGroupId()
        {
            var merged = MasterDataFeature.Merged;

            foreach (var row in merged.ScenarioGroups)
            {
                if (string.Equals(row.Id, ExampleGroupId, System.StringComparison.Ordinal))
                {
                    return row.Id;
                }
            }

            return merged.ScenarioGroups.Count > 0 ? merged.ScenarioGroups[0].Id : null;
        }

        private static Master.ScenarioGroupData? FindGroup(Game.MasterDataLoader loader, string id)
        {
            if (loader.ScenarioGroupMasterList is null)
            {
                return null;
            }

            foreach (var group in loader.ScenarioGroupMasterList)
            {
                if (group != null && string.Equals(group.ID, id, System.StringComparison.Ordinal))
                {
                    return group;
                }
            }

            return null;
        }

        private static bool HasNovelRow(Game.MasterDataLoader loader, string id)
        {
            if (loader.NovelMasterList is null)
            {
                return false;
            }

            foreach (var row in loader.NovelMasterList)
            {
                if (row != null && string.Equals(row.ID, id, System.StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(List<Master.NovelData> rows)
        {
            var parts = new string[rows.Count];
            for (var i = 0; i < rows.Count; i++)
            {
                parts[i] = rows[i].Command.ToString();
            }

            return string.Join(" -> ", parts);
        }
    }
}
