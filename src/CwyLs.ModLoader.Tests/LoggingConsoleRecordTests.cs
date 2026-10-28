using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The grammar itself: quoting, missing values, the two-space columns, the round trip, and the
    /// rules that make a line parseable by one regex.
    /// </summary>
    [Collection("TraceBus")]
    public class LoggingConsoleRecordTests
    {
        private static readonly DateTimeOffset At =
            new DateTimeOffset(2026, 9, 23, 14, 2, 11, 19, TimeSpan.FromHours(7));

        private static string Format(string tag, string? primary, params (string Key, string? Value)[] fields)
        {
            var list = new List<ConsoleField>();
            foreach (var (key, value) in fields)
            {
                list.Add(new ConsoleField(key, value));
            }

            return ConsoleRecordFormatter.Format(new ConsoleRecord(At, tag, primary, list));
        }

        [Fact]
        public void The_three_columns_are_separated_by_two_spaces()
        {
            Assert.Equal(
                "14:02:11.019  SPEECH  hello  a=1",
                Format(ConsoleTags.Speech, "hello", ("a", "1")));
        }

        [Fact]
        public void A_record_with_no_fields_is_stamp_tag_primary()
        {
            Assert.Equal("14:02:11.019  READY  room ready", Format(ConsoleTags.Ready, "room ready"));
        }

        [Theory]
        [InlineData("plain", "k=plain")]
        [InlineData("two words", "k=\"two words\"")]
        [InlineData("has\"quote", "k=\"has\\\"quote\"")]
        [InlineData("back\\slash", "k=\"back\\\\slash\"")]
        [InlineData("a=b", "k=a=b")]
        [InlineData("-", "k=\"-\"")]
        [InlineData("", "k=-")]
        [InlineData(null, "k=-")]
        public void A_value_is_quoted_exactly_when_it_could_not_be_read_back_bare(string? value, string expected) =>
            Assert.EndsWith(expected, Format(ConsoleTags.Data, "x", ("k", value)));

        [Fact]
        public void A_newline_in_a_value_becomes_one_glyph_so_the_line_stays_one_line()
        {
            var line = Format(ConsoleTags.Unity, "Exception", ("msg", "first\r\nsecond\nthird"));

            Assert.DoesNotContain("\n", line);
            Assert.DoesNotContain("\r", line);
            Assert.Contains("first⏎second⏎third", line);
        }

        [Fact]
        public void A_newline_in_the_primary_becomes_one_glyph_too()
        {
            var line = Format(ConsoleTags.Speech, "one\ntwo");

            Assert.Equal("14:02:11.019  SPEECH  one⏎two", line);
        }

        /// <summary>
        /// Two spaces inside the primary would be read as the start of the field column, so runs
        /// of spaces are collapsed. This is the rule the whole grammar rests on.
        /// </summary>
        [Fact]
        public void The_primary_never_contains_two_consecutive_spaces()
        {
            var line = Format(ConsoleTags.Say, "#7   \"spaced    out\"");

            Assert.Equal("14:02:11.019  SAY  #7 \"spaced out\"", line);
            Assert.True(ConsoleRecordParser.TryParse(line, At, out var parsed));
            Assert.Equal("#7 \"spaced out\"", parsed!.Primary);
            Assert.Empty(parsed.Fields);
        }

        [Fact]
        public void A_missing_primary_prints_the_layout_rather_than_an_empty_column()
        {
            Assert.Equal("14:02:11.019  SPEECH  -  src=-", Format(ConsoleTags.Speech, null, ("src", null)));
        }

        [Fact]
        public void A_very_long_primary_is_cut_rather_than_wrapped()
        {
            var line = Format(ConsoleTags.Speech, new string('x', ConsoleRecord.PrimaryLimit + 50), ("a", "1"));

            Assert.Contains(new string('x', ConsoleRecord.PrimaryLimit) + "…", line);
            Assert.EndsWith("a=1", line);
        }

        [Fact]
        public void A_tag_is_coerced_to_one_upper_case_word_rather_than_rejected()
        {
            Assert.Equal("14:02:11.019  SATONESPEECH  x", Format("satone speech", "x"));
            Assert.Equal("14:02:11.019  NOTE  x", Format("   ", "x"));
        }

        [Fact]
        public void A_quoted_value_survives_two_spaces_inside_it()
        {
            var line = Format(ConsoleTags.Smoke, "FAIL p1.x", ("detail", "a  b"));

            Assert.True(ConsoleRecordParser.TryParse(line, At, out var parsed));
            Assert.Equal("FAIL p1.x", parsed!.Primary);
            Assert.Equal("a  b", parsed.Field("detail"));
        }

        [Fact]
        public void A_line_that_is_not_a_record_is_refused_rather_than_guessed_at()
        {
            Assert.False(ConsoleRecordParser.TryParse(null, out _));
            Assert.False(ConsoleRecordParser.TryParse(string.Empty, out _));
            Assert.False(ConsoleRecordParser.TryParse("just a sentence", out _));
            Assert.False(ConsoleRecordParser.TryParse("14:02:11.019 SPEECH one space", out _));
            Assert.False(ConsoleRecordParser.TryParse("14:02:11.019  lowercase  x", out _));
            Assert.False(ConsoleRecordParser.TryParse("[Info:CWY:LS Mod Loader] VOICE + x", out _));
        }

        [Fact]
        public void The_parser_reads_the_event_time_back()
        {
            Assert.True(ConsoleRecordParser.TryParse("14:02:11.019  READY  room ready", At, out var parsed));

            Assert.Equal(14, parsed!.Time.Hour);
            Assert.Equal(2, parsed.Time.Minute);
            Assert.Equal(11, parsed.Time.Second);
            Assert.Equal(19, parsed.Time.Millisecond);
        }

        [Fact]
        public void Warn_and_error_tags_carry_their_level_back()
        {
            Assert.True(ConsoleRecordParser.TryParse("14:02:11.019  WARN  something  src=voice", out var warn));
            Assert.Equal(ConsoleLevel.Warn, warn!.Level);

            Assert.True(ConsoleRecordParser.TryParse("14:02:11.019  ERROR  broke  src=voice", out var error));
            Assert.Equal(ConsoleLevel.Error, error!.Level);

            Assert.True(ConsoleRecordParser.TryParse("14:02:11.019  SPEECH  hi", out var info));
            Assert.Equal(ConsoleLevel.Info, info!.Level);
        }

        // ---- the producer contract --------------------------------------------------------------

        [Fact]
        public void A_producer_that_names_no_tag_and_is_not_canonical_stays_in_the_file()
        {
            var evt = new BusEvent(
                "voice", "hit", new Dictionary<string, object?> { ["name"] = "Voice_X" }, At);

            Assert.False(ConsoleEventMap.TryMap(evt, out var record));
            Assert.Null(record);
        }

        [Fact]
        public void Mirror_false_wins_over_a_tag_the_producer_named()
        {
            var evt = new BusEvent(
                "satone",
                "face",
                new Dictionary<string, object?>
                {
                    [ConsolePayload.TagKey] = ConsoleTags.Face,
                    [ConsolePayload.PrimaryKey] = "smile",
                    [ConsolePayload.MirrorKey] = false,
                },
                At);

            Assert.False(ConsoleEventMap.TryMap(evt, out _));
        }

        /// <summary>P1-9a: a reading the producer blamed on the window losing focus stays in the file.</summary>
        [Fact]
        public void An_unfocused_reading_is_never_mirrored()
        {
            var evt = new BusEvent(
                "health",
                "spike",
                new Dictionary<string, object?>
                {
                    ["ms"] = 303d,
                    [ConsoleEventMap.UnfocusedField] = true,
                },
                At);

            Assert.False(ConsoleEventMap.TryMap(evt, out _));
        }

        [Theory]
        [InlineData("event", "GameClockTick")]
        [InlineData("event", "PomodoroTick")]
        [InlineData("event", "LineEnded")]
        [InlineData("event", "SaveSection")]
        [InlineData("health", "interval")]
        [InlineData("unity", "Warning")]
        public void The_noisy_kinds_are_file_only(string sys, string name) =>
            Assert.False(
                ConsoleEventMap.TryMap(new BusEvent(sys, name, new Dictionary<string, object?>(), At), out _));

        /// <summary>
        /// A Satone.Say plays through the game's own scenario reader. Without this rule every Say
        /// cost a SCENARIO start and a SCENARIO end on top of its own SAY line, which already says
        /// via=scenario.
        /// </summary>
        [Theory]
        [InlineData("ScenarioStarted")]
        [InlineData("ScenarioEnded")]
        public void Our_own_say_scenarios_do_not_get_a_scenario_line(string name)
        {
            var ours = new Dictionary<string, object?>
            {
                ["groupId"] = "cwy_say_7",
                ["scenarioType"] = "ShortTalk",
                ["readMode"] = "Auto",
            };

            var theirs = new Dictionary<string, object?>
            {
                ["groupId"] = "game_start_02_001",
                ["scenarioType"] = "GameStart_LessTowDays",
                ["readMode"] = "Auto",
            };

            Assert.False(ConsoleEventMap.TryMap(new BusEvent("event", name, ours, At), out _));
            Assert.True(ConsoleEventMap.TryMap(new BusEvent("event", name, theirs, At), out _));
        }

        [Fact]
        public void A_console_field_spec_can_rename_a_column()
        {
            ConsoleRecord? seen = null;
            TraceBus.Reset();
            using (TraceBus.Subscribe(evt => ConsoleEventMap.TryMap(evt, out seen)))
            {
                ConsoleEvents.Emit("x", "y", ConsoleTags.Data, "p", "dur:durationSec", ("durationSec", 2.5d));
            }

            TraceBus.Reset();

            Assert.NotNull(seen);
            Assert.Equal("2.5", seen!.Field("dur"));
        }

        [Fact]
        public void A_field_the_payload_does_not_carry_still_gets_its_column()
        {
            ConsoleRecord? seen = null;
            TraceBus.Reset();
            using (TraceBus.Subscribe(evt => ConsoleEventMap.TryMap(evt, out seen)))
            {
                ConsoleEvents.Emit("x", "y", ConsoleTags.Data, "p", "missing", ("other", 1));
            }

            TraceBus.Reset();

            Assert.NotNull(seen);
            Assert.Single(seen!.Fields);
            Assert.Null(seen.Field("missing"));
            Assert.EndsWith("missing=-", ConsoleRecordFormatter.Format(seen));
        }

        [Fact]
        public void A_file_only_publish_reaches_the_bus_but_not_the_console()
        {
            var published = 0;
            ConsoleRecord? seen = null;

            TraceBus.Reset();
            using (TraceBus.Subscribe(evt =>
                   {
                       published++;
                       ConsoleEventMap.TryMap(evt, out seen);
                   }))
            {
                ConsoleEvents.File("satone", "face", ("phase", "observed"), ("outcome", "Confirmed"));
            }

            TraceBus.Reset();

            Assert.Equal(1, published);
            Assert.Null(seen);
        }
    }
}
