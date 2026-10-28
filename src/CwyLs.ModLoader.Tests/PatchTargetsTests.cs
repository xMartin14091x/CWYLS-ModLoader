using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Covers the Unity-free half of P0-3: the target model, the report wording the startup log
    /// depends on, the canonical signature formatter and the game-version gate. The reflection
    /// resolver itself lives in the plugin assembly and is exercised in game; everything it
    /// decides with is here, behind <see cref="IMemberResolver"/>.
    /// </summary>
    public class PatchTargetsTests
    {
        private static PatchTarget Method(string id, string member, params string[] parameters) =>
            new PatchTarget(id, "Assembly-CSharp", "Bulbul.HeroineService", MemberKind.Method, member, parameters);

        /// <summary>A resolver whose answer per target id is scripted by the test.</summary>
        private sealed class FakeResolver : IMemberResolver
        {
            private readonly Dictionary<string, Func<PatchTarget, PatchTargetResult>> _answers =
                new Dictionary<string, Func<PatchTarget, PatchTargetResult>>(StringComparer.Ordinal);

            public FakeResolver Missing(string id, string detail)
            {
                _answers[id] = target => PatchTargetResult.Missing(target, detail);
                return this;
            }

            public FakeResolver Throws(string id, Exception error)
            {
                _answers[id] = _ => throw error;
                return this;
            }

            public FakeResolver Returns(string id, PatchTargetResult? result)
            {
                _answers[id] = _ => result!;
                return this;
            }

            /// <summary>Anything the test did not script resolves, which keeps each test focused.</summary>
            public PatchTargetResult Resolve(PatchTarget target) =>
                _answers.TryGetValue(target.Id, out var answer)
                    ? answer(target)
                    : PatchTargetResult.Ok(target, target.ExpectedSignature ?? "resolved");
        }

        // ---------- report ----------------------------------------------------------------

        [Fact]
        public void A_table_that_fully_resolves_reports_zero_missing()
        {
            var targets = new[] { Method("a", "PlayVoice"), Method("b", "ChangeAnimation"), Method("c", "IsSleeping") };

            var report = PatchTargetChecker.Check(targets, new FakeResolver());

            Assert.Equal("targets: 3 ok, 0 missing", report.Summarize());
            Assert.True(report.AllFound);
            Assert.Empty(report.MissingLines());
            Assert.Equal(3, report.OkCount);
            Assert.Equal(0, report.MissingCount);
        }

        [Fact]
        public void A_missing_target_is_counted_and_formatted_for_the_log()
        {
            var targets = new[] { Method("a", "PlayVoice"), Method("heroine.motion", "ChangeAnimation"), Method("c", "IsSleeping") };
            var resolver = new FakeResolver().Missing("heroine.motion", "no method 'ChangeAnimation' on Bulbul.HeroineService");

            var report = PatchTargetChecker.Check(targets, resolver);

            Assert.False(report.AllFound);
            Assert.Equal("targets: 2 ok, 1 missing", report.Summarize());
            Assert.Equal(
                "PATCH TARGET MISSING heroine.motion: no method 'ChangeAnimation' on Bulbul.HeroineService",
                Assert.Single(report.MissingLines()));
        }

        [Fact]
        public void Every_missing_target_is_listed_in_table_order()
        {
            var targets = new[] { Method("one", "A"), Method("two", "B"), Method("three", "C") };
            var resolver = new FakeResolver().Missing("three", "gone").Missing("one", "gone");

            var report = PatchTargetChecker.Check(targets, resolver);

            Assert.Equal(new[] { "one", "three" }, report.MissingResults.Select(r => r.Target.Id));
            Assert.Equal("targets: 1 ok, 2 missing", report.Summarize());
        }

        [Fact]
        public void A_resolver_that_throws_is_a_missing_target_not_a_crash()
        {
            var targets = new[] { Method("a", "PlayVoice"), Method("boom", "ChangeAnimation") };
            var resolver = new FakeResolver().Throws("boom", new InvalidOperationException("assembly is half loaded"));

            var report = PatchTargetChecker.Check(targets, resolver);

            Assert.Equal("targets: 1 ok, 1 missing", report.Summarize());
            var line = Assert.Single(report.MissingLines());
            Assert.Contains("InvalidOperationException", line);
            Assert.Contains("assembly is half loaded", line);
        }

        [Fact]
        public void A_resolver_that_answers_with_null_is_a_missing_target()
        {
            var resolver = new FakeResolver().Returns("a", null);

            var report = PatchTargetChecker.Check(new[] { Method("a", "PlayVoice") }, resolver);

            Assert.Equal("targets: 0 ok, 1 missing", report.Summarize());
            Assert.Contains("resolver returned no result", Assert.Single(report.MissingLines()));
        }

        [Fact]
        public void Check_rejects_a_null_table_or_resolver()
        {
            Assert.Throws<ArgumentNullException>(() => PatchTargetChecker.Check(null!, new FakeResolver()));
            Assert.Throws<ArgumentNullException>(() => PatchTargetChecker.Check(Array.Empty<PatchTarget>(), null!));
            Assert.Equal("targets: 0 ok, 0 missing", PatchTargetChecker.Check(Array.Empty<PatchTarget>(), new FakeResolver()).Summarize());
        }

        // ---------- target model ----------------------------------------------------------

        [Fact]
        public void A_target_keeps_its_parameter_list_and_describes_where_it_lives()
        {
            var target = new PatchTarget(
                "heroine.playvoice", "Assembly-CSharp", "Bulbul.HeroineService", MemberKind.Method, "PlayVoice",
                new[] { "System.String", "System.Boolean", "System.Boolean" },
                expectedSignature: "UniTaskVoid PlayVoice(string, bool, bool)");

            Assert.Equal(3, target.ParameterTypeNames.Count);
            Assert.Equal("System.Boolean", target.ParameterTypeNames[2]);
            Assert.False(target.IsStatic);
            Assert.Equal("Assembly-CSharp!Bulbul.HeroineService.PlayVoice", target.Describe());
        }

        [Fact]
        public void A_target_without_parameters_has_an_empty_list_not_null()
        {
            var target = new PatchTarget("masterdata.load", "Assembly-CSharp", "Bulbul.MasterDataLoader", MemberKind.Method, "Load");

            Assert.NotNull(target.ParameterTypeNames);
            Assert.Empty(target.ParameterTypeNames);
            Assert.Null(target.ExpectedSignature);
        }

        [Fact]
        public void A_blank_expected_signature_means_unchecked()
        {
            var target = new PatchTarget("x", "A", "T", MemberKind.Field, "F", expectedSignature: "   ");

            Assert.Null(target.ExpectedSignature);
        }

        [Fact]
        public void A_target_rejects_blank_names_and_parameters_on_a_non_method()
        {
            Assert.Throws<ArgumentException>(() => new PatchTarget(" ", "A", "T", MemberKind.Method, "M"));
            Assert.Throws<ArgumentException>(() => new PatchTarget("x", "", "T", MemberKind.Method, "M"));
            Assert.Throws<ArgumentException>(() => new PatchTarget("x", "A", "T", MemberKind.Method, null!));
            Assert.Throws<ArgumentException>(
                () => new PatchTarget("x", "A", "T", MemberKind.Field, "F", new[] { "System.Int32" }));
        }

        // ---------- canonical signatures --------------------------------------------------

        [Fact]
        public void A_method_signature_reads_like_the_game_source()
        {
            Assert.Equal(
                "UniTask Load()",
                PatchSignature.ForMethod("Cysharp.Threading.Tasks.UniTask", "Load", Array.Empty<string>(), false));

            Assert.Equal(
                "void ChangeAnimation(HeroineService.AnimationType)",
                PatchSignature.ForMethod(
                    "System.Void", "ChangeAnimation", new[] { "Bulbul.HeroineService+AnimationType" }, false));

            Assert.Equal(
                "static AudioPlayer Play(string, float, bool)",
                PatchSignature.ForMethod(
                    "KanKikuchi.AudioManager.AudioPlayer", "Play",
                    new[] { "System.String", "System.Single", "System.Boolean" }, true));
        }

        [Fact]
        public void A_field_or_property_signature_carries_its_accessors()
        {
            Assert.Equal(
                "Dictionary<string, AudioClip> _audioClipDict",
                PatchSignature.ForField(
                    "System.Collections.Generic.Dictionary<System.String, UnityEngine.AudioClip>",
                    "_audioClipDict",
                    false));

            Assert.Equal(
                "static HeroineAI.ActionStateType WantTalk",
                PatchSignature.ForField("HeroineAI+ActionStateType", "WantTalk", true));

            Assert.Equal(
                "Observable<Unit> OnStartWork { get; }",
                PatchSignature.ForProperty("R3.Observable<R3.Unit>", "OnStartWork", true, false, false));

            Assert.Equal(
                "int Count { get; set; }",
                PatchSignature.ForProperty("System.Int32", "Count", true, true, false));

            Assert.Equal(
                "static string Name { set; }",
                PatchSignature.ForProperty("System.String", "Name", false, true, true));
        }

        [Theory]
        [InlineData("System.Void", "void")]
        [InlineData("System.Int32", "int")]
        [InlineData("System.String", "string")]
        [InlineData("Bulbul.MasterData.ScenarioType", "ScenarioType")]
        [InlineData("Bulbul.ScenarioReader+ScenarioReadMode", "ScenarioReader.ScenarioReadMode")]
        [InlineData("KanKikuchi.AudioManager.AudioManager`1", "AudioManager")]
        [InlineData("System.Int32[]", "int[]")]
        [InlineData("Bulbul.ScenarioInfo&", "ScenarioInfo&")]
        [InlineData("R3.Observable<System.ValueTuple<Bulbul.PomodoroService+PomodoroType, System.TimeSpan, System.TimeSpan>>",
            "Observable<ValueTuple<PomodoroService.PomodoroType, TimeSpan, TimeSpan>>")]
        public void Short_names_drop_namespaces_and_keep_the_declaring_type(string full, string expected)
        {
            Assert.Equal(expected, PatchSignature.ShortName(full));
        }

        [Theory]
        [InlineData("void")]
        [InlineData("Observable<Unit>")]
        [InlineData("Dictionary<string, AudioClip>")]
        public void Shortening_an_already_short_name_changes_nothing(string alreadyShort)
        {
            Assert.Equal(alreadyShort, PatchSignature.ShortName(alreadyShort));
        }

        /// <summary>
        /// A dotted prefix is always a namespace, so shortening is not idempotent over a nested
        /// name it produced itself. That is inherent - "A.B" alone cannot say which A is - and it
        /// is safe only because signatures are shortened once, from reflection, then compared as
        /// strings. This test exists to keep that property visible if anyone changes the rule.
        /// </summary>
        [Fact]
        public void Shortening_reads_a_dotted_prefix_as_a_namespace()
        {
            Assert.Equal("AnimationType", PatchSignature.ShortName("HeroineService.AnimationType"));
            Assert.Equal("HeroineService.AnimationType", PatchSignature.ShortName("Bulbul.HeroineService+AnimationType"));
        }

        [Fact]
        public void An_unknown_type_name_renders_as_a_question_mark()
        {
            Assert.Equal("?", PatchSignature.ShortName(null));
            Assert.Equal("?", PatchSignature.ShortName("  "));
            Assert.Equal("? Load()", PatchSignature.ForMethod(null, "Load", null, false));
        }

        // ---------- version gate ----------------------------------------------------------

        [Fact]
        public void The_gate_defaults_to_the_version_the_seams_were_read_from()
        {
            Assert.Equal("1.17.3", GameVersionGate.ExpectedGameVersion);

            var check = new GameVersionGate().Check("1.17.3");

            Assert.True(check.IsOk);
            Assert.Equal(GameVersionStatus.Ok, check.Status);
            Assert.Equal("1.17.3", check.Actual);
            Assert.Contains("1.17.3", check.Message);
        }

        [Fact]
        public void A_different_build_is_a_mismatch_that_names_both_versions()
        {
            var check = new GameVersionGate("1.17.2").Check("1.18.0");

            Assert.False(check.IsOk);
            Assert.Equal(GameVersionStatus.Mismatch, check.Status);
            Assert.Equal("1.18.0", check.Actual);
            Assert.Equal("1.17.2", check.Expected);
            Assert.Equal(
                "game version mismatch: running 1.18.0, patch targets verified against 1.17.2 (newer minor version)",
                check.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_version_the_game_did_not_report_is_a_mismatch_not_a_pass(string? actual)
        {
            var check = new GameVersionGate("1.17.2").Check(actual);

            Assert.False(check.IsOk);
            Assert.Equal(GameVersionGate.UnknownVersion, check.Actual);
            Assert.Contains("unknown", check.Message);
        }

        [Fact]
        public void The_gate_ignores_surrounding_whitespace_and_rejects_a_blank_expectation()
        {
            Assert.True(new GameVersionGate(" 1.17.2 ").Check("\t1.17.2\n").IsOk);
            Assert.Throws<ArgumentException>(() => new GameVersionGate("  "));
        }
    }
}
