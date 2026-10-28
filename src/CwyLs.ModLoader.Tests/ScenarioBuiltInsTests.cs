using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// E-SCRIPT built-ins: the action line of <c>cwy.do</c>/<c>cwy.defer</c>, <c>cwy.wait</c> seconds,
    /// <c>cwy.flag</c> and the flag store, and the deferred-action queue's release rule.
    /// </summary>
    public class ScenarioBuiltInsTests
    {
        [Fact]
        public void An_action_line_is_a_lower_case_name_then_name_value_arguments()
        {
            var parse = ScriptActionLine.Parse("  Music.Play title=\"Rainy Desk\" volume=0.5 note=\"say \\\"hi\\\" \\\\ bye\" empty= ");

            Assert.True(parse.IsValid, parse.Problem);
            var line = parse.Line!;
            Assert.Equal("music.play", line.Name);
            Assert.Equal("Rainy Desk", line.Args.GetString("title"));
            Assert.True(line.Args.TryGetDouble("volume", out var volume));
            Assert.Equal(0.5, volume);
            Assert.Equal("say \"hi\" \\ bye", line.Args.GetString("note"));
            Assert.Equal(string.Empty, line.Args.GetString("empty"));
            Assert.Equal(new[] { "title", "volume", "note", "empty" }, line.Args.Keys);
        }

        [Fact]
        public void An_action_line_becomes_a_script_request_named_after_the_scene()
        {
            var line = ScriptActionLine.Parse("scene.play group=cwy_demo_script_02").Line!;
            var request = line.ToRequest("cwy_demo_script_01", TimeSpan.FromSeconds(3));

            Assert.Equal("scene.play", request.Name);
            Assert.Equal(ActionCaller.Script, request.Caller);
            Assert.Equal("cwy_demo_script_01", request.CallerId);
            Assert.Equal(TimeSpan.FromSeconds(3), request.Timeout);
            Assert.Equal("cwy_demo_script_02", request.Args.GetString("group"));
            Assert.Equal("script", line.ToRequest(" ").CallerId);
        }

        [Theory]
        [InlineData("", "no action")]
        [InlineData("0", "no action")]
        [InlineData("play", "not an action name")]
        [InlineData("\"music.play\"", "not an action name")]
        [InlineData("music.play title", "name=value")]
        [InlineData("music.play =x", "name=value")]
        [InlineData("music.play 1x=2", "not an argument name")]
        [InlineData("music.play a=1 A=2", "twice")]
        [InlineData("music.play title=\"open", "not closed")]
        public void A_bad_action_line_says_why(string text, string expected)
        {
            var parse = ScriptActionLine.Parse(text);

            Assert.False(parse.IsValid);
            Assert.Null(parse.Line);
            Assert.Contains(expected, parse.Problem, StringComparison.Ordinal);
        }

        [Fact]
        public void Wait_seconds_are_invariant_and_between_zero_and_five_minutes_whatever_the_culture()
        {
            var culture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.True(ScenarioBuiltInArgs.TryParseWait(" 1.5 ", out var seconds, out _));
                Assert.Equal(1.5, seconds);
                Assert.True(ScenarioBuiltInArgs.TryParseWait("300", out _, out _));
                Assert.False(ScenarioBuiltInArgs.TryParseWait("1,5", out _, out var comma));
                Assert.Contains("1.5", comma, StringComparison.Ordinal);
                Assert.False(ScenarioBuiltInArgs.TryParseWait("0", out _, out _));
                Assert.False(ScenarioBuiltInArgs.TryParseWait("-1", out _, out _));
                Assert.False(ScenarioBuiltInArgs.TryParseWait("300.5", out _, out var tooLong));
                Assert.Contains("300", tooLong, StringComparison.Ordinal);
                Assert.False(ScenarioBuiltInArgs.TryParseWait(null, out _, out _));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = culture;
            }
        }

        [Theory]
        [InlineData("set metSatone", true, "metSatone")]
        [InlineData("CLEAR demo.script.seen", false, "demo.script.seen")]
        [InlineData("  set\ta-b_c  ", true, "a-b_c")]
        public void Flag_rows_name_set_or_clear_and_the_flag(string arg1, bool value, string name)
        {
            Assert.True(ScenarioBuiltInArgs.TryParseFlag(arg1, out var parsedValue, out var parsedName, out var problem), problem);
            Assert.Equal(value, parsedValue);
            Assert.Equal(name, parsedName);
        }

        [Theory]
        [InlineData("set")]
        [InlineData("toggle x")]
        [InlineData("set a b")]
        [InlineData("set bad:name")]
        [InlineData("")]
        public void Bad_flag_rows_are_refused(string arg1)
        {
            Assert.False(ScenarioBuiltInArgs.TryParseFlag(arg1, out _, out _, out var problem));
            Assert.NotEmpty(problem);
        }

        [Fact]
        public void The_built_in_check_matches_each_handler_and_ignores_other_commands()
        {
            Assert.Equal(string.Empty, ScenarioBuiltInArgs.Check(ScenarioCommandIds.Do, "music.play title=Rain", out var action));
            Assert.Equal("music.play", action);
            Assert.StartsWith("cwy.defer:", ScenarioBuiltInArgs.Check(ScenarioCommandIds.Defer, "nope", out _), StringComparison.Ordinal);
            Assert.NotEmpty(ScenarioBuiltInArgs.Check(ScenarioCommandIds.Wait, "soon", out _));
            Assert.NotEmpty(ScenarioBuiltInArgs.Check(ScenarioCommandIds.Flag, "set", out _));
            Assert.Equal(string.Empty, ScenarioBuiltInArgs.Check(ScenarioCommandIds.If, "anything", out _));
            Assert.Equal(new[] { "cwy.do", "cwy.defer", "cwy.wait", "cwy.flag" },
                new[] { ScenarioBuiltInArgs.Do.Name, ScenarioBuiltInArgs.Defer.Name, ScenarioBuiltInArgs.Wait.Name, ScenarioBuiltInArgs.Flag.Name });
        }

        [Fact]
        public void Flag_names_follow_the_condition_argument_rule()
        {
            foreach (var good in new[] { "metSatone", "demo.script.seen", "a-b_c", new string('x', 64) })
            {
                Assert.True(ScenarioFlags.IsValidName(good, out var problem), problem);
                Assert.True(ConditionParser.TryParseKey("flag:" + good, out _, out _, out _));
            }

            foreach (var bad in new[] { "", " ", "a b", "a:b", "ไทย", new string('x', 65) })
            {
                Assert.False(ScenarioFlags.IsValidName(bad, out _), bad);
            }
        }

        [Fact]
        public void Flags_set_and_clear_case_insensitively_and_report_only_real_changes()
        {
            var flags = new ScenarioFlags();
            var changes = new List<ScenarioFlagChange>();
            flags.Changed += changes.Add;

            Assert.True(flags.Set("MetSatone", true, "g_01").IsOk);
            Assert.True(flags.Set("metsatone", true, "g_02").IsOk);
            Assert.True(flags.IsSet("METSATONE"));
            Assert.Single(changes);
            Assert.Equal("+MetSatone by g_01", changes[0].ToString());

            Assert.True(flags.Set("metSATONE", false, "console").IsOk);
            Assert.False(flags.IsSet("MetSatone"));
            Assert.Equal(2, changes.Count);
            Assert.False(changes[1].Value);

            Assert.True(flags.Set("never.set", false, "x").IsOk);
            Assert.Equal(2, changes.Count);
            Assert.False(flags.Set("bad name", true, "x").IsOk);
            Assert.False(flags.IsSet(null));
        }

        [Fact]
        public void Flags_are_capped_and_round_trip_through_their_file()
        {
            var flags = new ScenarioFlags();
            for (var i = 0; i < ScenarioFlags.MaxFlags; i++)
            {
                Assert.True(flags.Set("f" + i.ToString(CultureInfo.InvariantCulture), true, "t").IsOk);
            }

            var refused = flags.Set("one.more", true, "t");
            Assert.False(refused.IsOk);
            Assert.Contains("512", refused.Message, StringComparison.Ordinal);

            var small = new ScenarioFlags();
            small.Set("b", true, "t");
            small.Set("A", true, "t");
            var json = small.ToJson();
            Assert.Equal("{\"version\":1,\"flags\":[\"A\",\"b\"]}", json);

            var loaded = new ScenarioFlags();
            var changes = 0;
            loaded.Changed += _ => changes++;
            var result = loaded.Load("{\"version\":1,\"flags\":[\"A\",\"b\",\"bad name\",7]}");
            Assert.True(result.IsOk);
            Assert.Contains("2 flag(s) loaded, 2 unusable", result.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "A", "b" }, loaded.Names);
            Assert.Equal(0, changes);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"version\":1}")]
        [InlineData("[1,2]")]
        public void A_broken_flags_file_is_refused_and_keeps_the_flags(string json)
        {
            var flags = new ScenarioFlags();
            flags.Set("kept", true, "t");

            Assert.False(flags.Load(json).IsOk);
            Assert.True(flags.IsSet("kept"));
        }

        [Fact]
        public void Deferred_actions_wait_for_a_settled_quiet_room_and_run_in_order()
        {
            var queue = new DeferredActionQueue(settleSeconds: 1, maxAgeSeconds: 600);
            Assert.True(queue.Enqueue("g_01", "r_1", Line("music.play title=A"), 0).IsOk);
            Assert.True(queue.Enqueue("g_01", "r_2", Line("scene.play group=g_02"), 0.1).IsOk);

            Assert.Empty(queue.Take(false, 1).Due);
            Assert.Empty(queue.Take(true, 2).Due);
            Assert.Empty(queue.Take(true, 2.5).Due);
            Assert.Empty(queue.Take(false, 2.9).Due);
            Assert.Empty(queue.Take(true, 3).Due);

            var take = queue.Take(true, 4);
            Assert.Equal(new[] { "music.play", "scene.play" }, new[] { take.Due[0].Line.Name, take.Due[1].Line.Name });
            Assert.Equal("g_01", take.Due[0].GroupId);
            Assert.Equal("r_2", take.Due[1].RowId);
            Assert.True(take.Due[0].Id < take.Due[1].Id);
            Assert.Equal(0, queue.Count);
            Assert.Empty(queue.Take(true, 5).Due);
        }

        [Fact]
        public void Deferred_actions_expire_and_are_capped_per_scene_and_in_total()
        {
            var queue = new DeferredActionQueue(settleSeconds: 0, maxAgeSeconds: 10);
            queue.Enqueue("old", "r", Line("a.b"), 0);
            queue.Enqueue("new", "r", Line("c.d"), 5);
            var take = queue.Take(false, 11);
            Assert.Single(take.Expired);
            Assert.Equal("old", take.Expired[0].GroupId);
            Assert.Empty(take.Due);
            Assert.Equal(1, queue.Count);

            var capped = new DeferredActionQueue();
            for (var i = 0; i < DeferredActionQueue.MaxPerGroup; i++)
            {
                Assert.True(capped.Enqueue("g", "r" + i, Line("a.b"), 0).IsOk);
            }

            Assert.False(capped.Enqueue("g", "r", Line("a.b"), 0).IsOk);
            for (var g = 1; capped.Count < DeferredActionQueue.MaxTotal; g++)
            {
                Assert.True(capped.Enqueue("g" + g, "r", Line("a.b"), 0).IsOk);
            }

            Assert.False(capped.Enqueue("another", "r", Line("a.b"), 0).IsOk);
            Assert.Equal(DeferredActionQueue.MaxTotal, capped.Clear());
            Assert.Equal(0, capped.Count);
        }

        private static ScriptActionLine Line(string text) => ScriptActionLine.Parse(text).Line!;
    }
}
