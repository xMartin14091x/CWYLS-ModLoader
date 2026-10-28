using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-SCRIPT pack-scene lint: one test per rule, the shipped packs as they are, the save rule
    /// agreeing with the pack-lines save filter, and the <c>scene play</c> rules.
    /// </summary>
    public partial class ScenarioLintTests
    {
        private const string Group = "cwy_t_01";

        // ---- the shipped packs -------------------------------------------------------------------------

        [Fact]
        public void Every_shipped_pack_lints_without_errors_and_the_demo_without_warnings() =>
            AssertShippedPacksLint("cwy.example", "cwy.demo.script");

        private static void AssertShippedPacksLint(params string[] packIds)
        {
            var merged = Merge(packIds.Select(Read).ToArray());
            Assert.True(merged.Report.RejectedCount == 0, string.Join(" | ", merged.Report.AllRejections));

            var report = ScenarioLint.Lint(merged, Context(runtime: true));

            Assert.True(report.ErrorCount == 0, report.ToString());
            Assert.Empty(report.For("cwy_demo_script_01"));
            Assert.Empty(report.For("cwy_demo_script_02"));

            // cwy.example's MotionScenario 900 predates the mod range: a warning, not an error.
            var example = Assert.Single(report.For("cwy_example_01"));
            Assert.Equal("S04", example.Rule);
            Assert.Equal(ScenarioLintSeverity.Warning, example.Severity);
        }

        [Fact]
        public void The_demo_pack_uses_each_built_in_and_its_long_scene_cannot_be_offered_by_the_game()
        {
            var merged = Merge(Read("cwy.demo.script"));
            var commands = merged.Novel.Select(r => r.Command).ToList();
            Assert.Contains(ScenarioCommandIds.Flag, commands);
            Assert.Contains(ScenarioCommandIds.Wait, commands);
            Assert.Contains(ScenarioCommandIds.Defer, commands);

            var scene = merged.ScenarioGroups.Single(g => g.Id == "cwy_demo_script_01");
            Assert.Equal(24, scene.Scenario);
            Assert.True(scene.EpisodeNumber >= ScenarioLint.ModEpisodeFloor);
            Assert.Contains(scene.ContentType, ScenarioLint.BlockingContentTypes);

            foreach (var row in merged.Novel.Where(r => r.LocalizationId.StartsWith("cwy_demo_script", StringComparison.Ordinal)))
            {
                var localization = merged.Localization.Single(l => l.Id == row.LocalizationId);
                Assert.NotEmpty(localization.En);
                Assert.NotEmpty(localization.Ja);
            }
        }

        [Fact]
        public void Script_rows_are_errors_while_the_seam_is_not_installed()
        {
            var report = ScenarioLint.Lint(Merge(Read("cwy.demo.script")), Context(runtime: false));

            var stalls = report.Findings.Where(f => f.Rule == "S20").ToList();
            Assert.Equal(3, stalls.Count);
            Assert.All(stalls, f => Assert.Equal(ScenarioLintSeverity.Error, f.Severity));
            Assert.Contains("would stall", stalls[0].Message, StringComparison.Ordinal);
        }

        // ---- group rules ----------------------------------------------------------------------------------

        [Fact]
        public void S01_a_scene_without_rows()
        {
            AssertRule("S01", Pack(Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")), Novel()), ScenarioLintSeverity.Error);
        }

        [Fact]
        public void S02_a_long_scene_without_a_starting_row()
        {
            var bad = Pack(Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")), Novel(Row("a", "VoiceText", loc: "l1", arg1: "1"), End("b")), Loc("l1"));
            AssertRule("S02", bad, ScenarioLintSeverity.Error);

            var good = Pack(Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")), Novel(Row("a", "ChangeMotion"), End("b")));
            AssertNoRule("S02", good);
        }

        [Fact]
        public void S03_two_scenes_with_one_type_and_episode_or_a_game_scene_with_it()
        {
            var twice = Pack(
                Groups(Grp("x_01", "MotionScenario", 1001), Grp("x_02", "MotionScenario", 1001)),
                Novel(Text("a", "x_01"), Text("b", "x_02")),
                Loc("la"));
            AssertRule("S03", twice, ScenarioLintSeverity.Error);

            var gameOwned = Pack(Groups(Grp("x_01", "MotionScenario", 1001)), Novel(Text("a", "x_01")), Loc("la"));
            var context = Context(runtime: true, gameGroups: new[] { new ScenarioGroupKey("motion_scenario_999", 22, 1001f) });
            Assert.Contains(ScenarioLint.Lint(Merge(gameOwned), context).Findings, f => f.Rule == "S03" && f.Message.Contains("motion_scenario_999", StringComparison.Ordinal));
        }

        [Fact]
        public void S04_an_episode_below_the_mod_range()
        {
            AssertRule("S04", Pack(Groups(Grp("x_01", "MotionScenario", 999)), Novel(Text("a", "x_01")), Loc("la")), ScenarioLintSeverity.Warning);
            AssertNoRule("S04", Pack(Groups(Grp("x_01", "MotionScenario", 1000)), Novel(Text("a", "x_01")), Loc("la")));
        }

        [Theory]
        [InlineData("ExtraScenario", "None", true)]
        [InlineData("ExtraScenario", "PurchaseEnvironment", true)]
        [InlineData("ExtraScenario", "smalltalk_cicada", true)]
        [InlineData("ExtraScenario", "2", true)]
        [InlineData("ExtraScenario", "SmallTalk_Cicada", false)]
        [InlineData("ExtraScenario", "SmallTalk_PassageOfTime", false)]
        [InlineData("Event_2026_AprilFool", "None", true)]
        [InlineData("Event_Player_HappyBirthday", "None", true)]
        [InlineData("MotionScenario", "None", false)]
        public void S05_a_scene_the_game_would_offer_on_its_own(string type, string contentType, bool expected)
        {
            var pack = Pack(Groups(Grp(Group, type, 1001, contentType)), Novel(Text("a")), Loc("la"));
            var found = ScenarioLint.Lint(Merge(pack), Context(runtime: true)).Findings.Any(f => f.Rule == "S05");
            Assert.Equal(expected, found);
        }

        [Fact]
        public void S06_a_long_scene_of_a_story_progress_type()
        {
            AssertRule("S06", Pack(Groups(Grp(Group, "MainScenario", 1001)), Novel(Text("a")), Loc("la")), ScenarioLintSeverity.Warning);
            AssertNoRule("S06", Pack(Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")), Novel(Text("a")), Loc("la")));
        }

        // ---- row rules ------------------------------------------------------------------------------------

        [Fact]
        public void S10_and_S11_stalling_and_tutorial_commands()
        {
            var pack = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(Row("a", "WaitInput"), Row("b", "Tutorial"), Row("c", "ReadNextForScript"), Row("d", "TutorialFocusTodo"), End("e")));
            var report = ScenarioLint.Lint(Merge(pack), Context(runtime: true));

            Assert.Equal(2, report.Findings.Count(f => f.Rule == "S10" && f.Severity == ScenarioLintSeverity.Error));
            Assert.Equal(2, report.Findings.Count(f => f.Rule == "S11" && f.Severity == ScenarioLintSeverity.Warning));
        }

        [Fact]
        public void S12_a_jump_to_a_row_the_scene_does_not_have()
        {
            var pack = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(
                    Row("a", "Jump", arg1: "z"),
                    Row("b", "IfBetweenDateTime", arg1: "20260101000000~20261231235959", arg3: "c", arg4: "y"),
                    Row("c", "StartTextLoop", arg1: "1", arg3: "x", arg4: "1"),
                    End("d")));
            var report = ScenarioLint.Lint(Merge(pack), Context(runtime: true));

            var targets = report.Findings.Where(f => f.Rule == "S12").Select(f => f.Message).ToList();
            Assert.Equal(3, targets.Count);
            Assert.Contains(targets, m => m.Contains("\"z\"", StringComparison.Ordinal));
            Assert.Contains(targets, m => m.Contains("\"y\"", StringComparison.Ordinal));
            Assert.Contains(targets, m => m.Contains("\"x\"", StringComparison.Ordinal));
        }

        [Fact]
        public void S13_missing_text_ids_when_they_can_be_checked()
        {
            var pack = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(Row("a", "VoiceText", arg1: "1"), Row("b", "Selection", arg1: "c"), Text("c", loc: "game_line_1"), End("d")));

            var offline = ScenarioLint.Lint(Merge(pack), Context(runtime: true));
            Assert.Equal(2, offline.Findings.Count(f => f.Rule == "S13"));

            var baseline = MasterDataBaseline.Build(new[] { "other" }, null, new[] { "some_game_group" });
            var online = ScenarioLint.Lint(Merge(pack), Context(runtime: true, baseline: baseline, known: true));
            Assert.Equal(3, online.Findings.Count(f => f.Rule == "S13"));

            var known = MasterDataBaseline.Build(new[] { "game_line_1" }, null, new[] { "some_game_group" });
            Assert.Equal(2, ScenarioLint.Lint(Merge(pack), Context(runtime: true, baseline: known, known: true)).Findings.Count(f => f.Rule == "S13"));
        }

        [Fact]
        public void S14_arguments_the_game_parses()
        {
            var pack = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(
                    Text("a", arg1: "1,5"),
                    Row("b", "ChangeMotion", arg1: "soon"),
                    Row("c", "PlayAmbientBGM", arg1: "lightrain"),
                    Row("d", "PlayAmbientBGM", arg1: "LightRain"),
                    Row("e", "IfBetweenDateTime", arg1: "2026-01-01~2026-12-31", arg3: "a", arg4: "a"),
                    Row("f", "PomodoroVoiceText", arg1: "x", arg3: "-1"),
                    Row("g", "PlayBGM", arg1: "Title", arg3: "0", arg4: "fast"),
                    Row("h", "DirectionBlackOut", arg1: "0.5", arg3: "yes"),
                    Row("i", "StopBGM"),
                    Row("j", "StopAmbientBGM"),
                    End("z")),
                Loc("la"));
            var rows = ScenarioLint.Lint(Merge(pack), Context(runtime: true)).Findings.Where(f => f.Rule == "S14").Select(f => f.RowId).ToList();

            Assert.Equal(new[] { "a", "b", "c", "e", "f", "g", "h" }, rows);
        }

        [Fact]
        public void S15_a_branch_that_another_branch_falls_into()
        {
            var falls = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(
                    Row("q", "Selection", arg1: "a1", loc: "la"), Row("q2", "Selection", arg1: "b1", loc: "la"),
                    Text("a1"), Text("a2"),
                    Text("b1"), End("b2")),
                Loc("la"));
            var finding = Assert.Single(ScenarioLint.Lint(Merge(falls), Context(runtime: true)).Findings, f => f.Rule == "S15");
            Assert.Equal("b1", finding.RowId);

            var merges = Pack(
                Groups(Grp(Group, "MotionScenario", 1001)),
                Novel(
                    Row("q", "Selection", arg1: "a1", loc: "la"), Row("q2", "Selection", arg1: "b1", loc: "la"),
                    Text("a1"), Row("a2", "Jump", arg1: "m"),
                    Text("b1"), Text("m"), End("e")),
                Loc("la"));
            AssertNoRule("S15", merges);
        }

        [Fact]
        public void S16_a_short_scene_that_never_stops_its_music()
        {
            AssertRule("S16", Pack(Groups(Grp(Group, "MotionScenario", 1001)), Novel(Row("a", "PlayBGM", arg1: "T"), Text("b"), End("c")), Loc("la")), ScenarioLintSeverity.Warning);
            AssertNoRule("S16", Pack(Groups(Grp(Group, "MotionScenario", 1001)), Novel(Row("a", "PlayBGM", arg1: "T"), Text("b"), Row("s", "StopBGM"), End("c")), Loc("la")));
            AssertNoRule("S16", Pack(Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")), Novel(Row("a", "PlayBGM", arg1: "T"), Text("b"), End("c")), Loc("la")));
        }

        // ---- script-command rules ------------------------------------------------------------------------

        [Fact]
        public void S21_to_S24_script_rows()
        {
            var registry = new ScenarioCommandRegistry();
            registry.SetRuntime(true, "test");
            Bind(registry, ScenarioBuiltInArgs.Do, ScenarioBuiltInArgs.Defer, ScenarioBuiltInArgs.Wait);
            var pack = Pack(
                Groups(Grp(Group, "ExtraScenario", 1001, "SmallTalk_Cicada")),
                Novel(
                    Row("early", "cwy.wait", arg1: "1"),
                    Text("t"),
                    Row("flag", "cwy.flag", arg1: "set x"),
                    Row("w", "cwy.wait", arg1: "0"),
                    Row("d", "cwy.do", arg1: "unknown.action"),
                    Row("e", "cwy.defer", arg1: "known.action a=1"),
                    End("z")),
                Loc("la"));

            var findings = ScenarioLint.Lint(
                Merge(pack),
                new ScenarioLintContext { Registry = registry, IsKnownAction = n => n == "known.action" }).Findings;

            Assert.Equal(new[] { "flag" }, Rows(findings, "S21"));
            Assert.Equal(new[] { "w" }, Rows(findings, "S22"));
            Assert.Equal(new[] { "d" }, Rows(findings, "S23"));
            Assert.Equal(new[] { "early" }, Rows(findings, "S24"));
            Assert.Empty(Rows(findings, "S20"));
        }

        [Fact]
        public void Script_rules_also_cover_rows_added_to_game_groups_and_pack_lines_skip_the_scene_rules()
        {
            var baseline = MasterDataBaseline.Build(new[] { "la" }, new[] { "game_row" }, new[] { "main_01" });
            var pack = Pack(
                Groups(LineGroup("cwy_th_line_001", "HeroineClickNormal", 1001)),
                Novel(
                    Row("added", "cwy.wait", arg1: "-1", group: "main_01"),
                    Row("l1", "WaitInput", group: "cwy_th_line_001"),
                    Row("orphan", "cwy.wait", arg1: "1", group: "nobody_01")),
                Loc("la"));
            var report = ScenarioLint.Lint(Merge(pack), Context(runtime: true, baseline: baseline, known: true));

            Assert.Equal(new[] { "added" }, Rows(report.Findings, "S22"));
            Assert.Empty(Rows(report.Findings, "S10"));
            Assert.Equal(new[] { "orphan" }, Rows(report.Findings, "S25"));
            Assert.Equal(1, report.PackLineCount);
            Assert.DoesNotContain("main_01", report.PackScenes);
        }

        [Fact]
        public void The_long_story_types_are_the_games()
        {
            var names = Enumerable.Range(0, 4000).Where(ScenarioLint.IsLongStory)
                .Select(v => MasterDataEnums.Scenario.TryName(v, out var n) ? n : "?").ToArray();

            Assert.Equal(
                new[]
                {
                    "MainScenario", "AfterScenario", "DLCScenario", "ExtraScenario", "Special_AlterEgo", "Special_BearsRestaurant",
                    "Special_Valentine2026", "Special_LunaNewYear2026", "Special_NearSpring2026", "Special_MagicalPrincess", "Special_StatisticsDev",
                },
                names);
            Assert.Equal(30, ScenarioLint.AmbientSound.Count);
        }

        // ---- the save rule agrees with the pack-lines save filter ------------------------------------------

        [Fact]
        public void The_save_rule_keeps_every_pack_scene_out_and_never_a_game_scene()
        {
            var baseline = MasterDataBaseline.Build(null, null, new[] { "main_01", "extra_01" });

            Assert.True(ScenarioSaveRule.IsPackScene("cwy_demo_script_01", baseline));
            Assert.False(ScenarioSaveRule.IsPackScene("extra_01", baseline));
            Assert.False(ScenarioSaveRule.IsPackScene("", baseline));
            Assert.False(ScenarioSaveRule.IsPackScene("cwy_demo_script_01", MasterDataBaseline.Empty));
            Assert.False(ScenarioSaveRule.IsPackScene("cwy_demo_script_01", null));
        }

        // ---- scene play -------------------------------------------------------------------------------------

        [Fact]
        public void Scene_play_starts_only_pack_scenes_in_an_idle_room()
        {
            var ok = Facts();
            Assert.Equal(ScenePlayPath.LongStory, ScenePlayPlanner.Plan(ok).Path);
            Assert.Equal(ScenePlayPath.ClickTalk, ScenePlayPlanner.Plan(Facts(scenario: 22)).Path);

            AssertRefused(Facts(found: false), "no scene");
            AssertRefused(Facts(pack: false), "game's own");
            AssertRefused(Facts(injected: false), "not in the game's data");
            AssertRefused(Facts(lint: "error S01"), "lint");
            AssertRefused(Facts(scenario: ScenePlayPlanner.TutorialScenario), "Tutorial");
            AssertRefused(Facts(room: false), "not ready");
            AssertRefused(Facts(playing: true), "a scene is playing");
            AssertRefused(Facts(idle: false), "busy");
            AssertRefused(Facts(satone: false), "Satone");
            AssertRefused(Facts(scenario: 22, satone: false), "Satone");
            AssertRefused(Facts(guard: false), "save-log guard");
            AssertRefused(Facts(owner: "extra_02"), "extra_02");
            Assert.Equal(ScenePlayPath.ClickTalk, ScenePlayPlanner.Plan(Facts(scenario: 22, guard: false, owner: null)).Path);
        }

        // ---- helpers -------------------------------------------------------------------------------------

        private static void AssertRefused(ScenePlayFacts facts, string expected)
        {
            var plan = ScenePlayPlanner.Plan(facts);
            Assert.False(plan.IsAccepted);
            Assert.Contains(expected, plan.Reason, StringComparison.Ordinal);
        }

        private static ScenePlayFacts Facts(
            bool found = true, bool pack = true, bool injected = true, string? lint = null, int scenario = 24, bool room = true,
            bool playing = false, bool idle = true, bool satone = true, bool guard = true, string? owner = "cwy_x_01") =>
            new ScenePlayFacts
            {
                GroupId = "cwy_x_01",
                Found = found,
                IsPackScene = pack,
                Injected = injected,
                FirstLintError = lint,
                Scenario = scenario,
                RoomReady = room,
                ScenePlaying = playing,
                RoomIdle = idle,
                SatoneFree = satone,
                SaveGuardActive = guard,
                EpisodeOwner = owner,
            };

        private static string[] Rows(IEnumerable<ScenarioLintFinding> findings, string rule) =>
            findings.Where(f => f.Rule == rule).Select(f => f.RowId).ToArray();

        private static void Bind(ScenarioCommandRegistry registry, params ScenarioCommandDescriptor[] descriptors)
        {
            foreach (var descriptor in descriptors)
            {
                registry.Register(ScenarioCommandRegistry.FrameworkOwner, descriptor, _ => { });
            }
        }

        private static void AssertRule(string rule, MasterDataPackInput pack, ScenarioLintSeverity severity)
        {
            var report = ScenarioLint.Lint(Merge(pack), Context(runtime: true));
            Assert.Contains(report.Findings, f => f.Rule == rule && f.Severity == severity);
        }

        private static void AssertNoRule(string rule, MasterDataPackInput pack)
        {
            var report = ScenarioLint.Lint(Merge(pack), Context(runtime: true));
            Assert.DoesNotContain(report.Findings, f => f.Rule == rule);
        }

        private static ScenarioLintContext Context(
            bool runtime, MasterDataBaseline? baseline = null, bool known = false, IReadOnlyList<ScenarioGroupKey>? gameGroups = null)
        {
            var registry = new ScenarioCommandRegistry();
            registry.SetRuntime(runtime, runtime ? "test seam" : ScenarioCommandRegistry.NoRuntimeDetail);
            Bind(registry, ScenarioBuiltInArgs.Do, ScenarioBuiltInArgs.Defer, ScenarioBuiltInArgs.Wait, ScenarioBuiltInArgs.Flag);
            return new ScenarioLintContext
            {
                Baseline = baseline ?? MasterDataBaseline.Empty,
                GameDataKnown = known,
                GameGroups = gameGroups ?? Array.Empty<ScenarioGroupKey>(),
                Registry = registry,
                IsKnownAction = name => name == "scene.play",
            };
        }

        private static MasterDataSet Merge(params MasterDataPackInput[] packs) => MasterDataMerge.Merge(packs);

        private static MasterDataPackInput Read(string packId)
        {
            var folder = LanguageDeclarationParserTests.PackFolder(packId);
            var input = new MasterDataPackInput(packId)
            {
                LocalizationJson = ReadIfPresent(Path.Combine(folder, "data", "localization.json")),
                NovelJson = ReadIfPresent(Path.Combine(folder, "data", "novel.json")),
                ScenarioGroupsJson = ReadIfPresent(Path.Combine(folder, "data", "scenario_groups.json")),
            };
            var subtitles = Path.Combine(folder, "data", "subtitles");
            if (Directory.Exists(subtitles))
            {
                foreach (var file in Directory.GetFiles(subtitles, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    input.AddSubtitles("data/subtitles/" + Path.GetFileName(file), File.ReadAllText(file));
                }
            }

            return input;
        }

        private static string? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        private static MasterDataPackInput Pack(string groups, string novel, string? localization = null) =>
            new MasterDataPackInput("cwy.test")
            {
                ScenarioGroupsJson = groups,
                NovelJson = novel,
                LocalizationJson = localization,
            };

        private static string Groups(params string[] groups) => "[" + string.Join(",", groups) + "]";

        private static string Grp(string id, string type, float episode, string contentType = "None") =>
            "{\"ID\":\"" + id + "\",\"Scenario\":\"" + type + "\",\"EpisodeNumber\":" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"ContentType\":\"" + contentType + "\"}";

        private static string LineGroup(string id, string type, float episode) =>
            "{\"ID\":\"" + id + "\",\"Scenario\":\"" + type + "\",\"EpisodeNumber\":" + episode.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"Languages\":[\"th\"]}";

        private static string Novel(params string[] rows) => "[" + string.Join(",", rows) + "]";

        private static string Row(string id, string command, string arg1 = "0", string arg3 = "0", string arg4 = "0", string loc = "0", string group = Group)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ID\":\"").Append(id).Append("\",\"ScenarioGroupID\":\"").Append(group)
              .Append("\",\"Command\":\"").Append(command).Append("\",\"Arg1\":\"").Append(Escape(arg1))
              .Append("\",\"Arg3\":\"").Append(arg3).Append("\",\"Arg4\":\"").Append(arg4)
              .Append("\",\"LocalizationID\":\"").Append(loc).Append("\"}");
            return sb.ToString();
        }

        private static string Text(string id, string group = Group, string loc = "la", string arg1 = "0") => Row(id, "Text", arg1: arg1, loc: loc, group: group);

        private static string End(string id) => Row(id, "EndScenario");

        private static string Loc(params string[] ids) =>
            "[" + string.Join(",", ids.Select(i => "{\"ID\":\"" + i + "\",\"En\":\"x\"}")) + "]";

        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
