using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P1-9a, updated by F3. The generic bus mirror showed six payload members and cut every value
    /// at 60 characters, which truncated the one line the user reads - the sentence Satone just
    /// said. These fix the layout: the key fields are always present, and only the text is capped.
    /// <para>
    /// F3 changed the shape, not the rule: the two lines are <see cref="ConsoleRecord"/>s now
    /// (<c>SPEECH</c> and <c>STATE</c>, one upper-case word each), and the fields moved into the
    /// record's ordered field list.
    /// </para>
    /// </summary>
    public class TraceSatoneConsoleTests
    {
        private static readonly DateTimeOffset At =
            new DateTimeOffset(2026, 9, 23, 14, 2, 11, 19, TimeSpan.FromHours(7));

        private static Dictionary<string, object?> Speech(
            string? text = "I made you some tea.",
            string? textJa = "お茶を淹れたよ。",
            string? voice = "Voice_Tutorial_1_001",
            string? id = "Tutorial_1_001",
            string? source = "Tutorial",
            object? duration = null,
            string? attribution = "row")
        {
            // 2.25 s is the default; it cannot be a default parameter value because it is boxed.
            duration ??= 2.25d;

            return new Dictionary<string, object?>
            {
                ["voice"] = voice,
                ["text"] = text,
                ["textJa"] = textJa,
                ["localizationId"] = id,
                ["noTextInMaster"] = id is null,
                ["language"] = "English",
                ["isStory"] = false,
                ["scenarioType"] = "Tutorial",
                ["scenarioGroupId"] = "tutorial_1",
                ["source"] = source,
                ["moveMouth"] = true,
                ["durationSec"] = duration,
                ["attribution"] = attribution,
            };
        }

        private static string Line(IReadOnlyDictionary<string, object?>? payload) =>
            ConsoleRecordFormatter.Format(SatoneConsoleLines.Speech(At, payload));

        private static string StateLine(IReadOnlyDictionary<string, object?>? payload) =>
            ConsoleRecordFormatter.Format(SatoneConsoleLines.State(At, payload));

        [Fact]
        public void The_speech_line_has_every_field_in_a_fixed_order()
        {
            Assert.Equal(
                "14:02:11.019  SPEECH  \"I made you some tea.\" 「お茶を淹れたよ。」  src=Tutorial dur=2.3s " +
                "voice=Tutorial_1_001 id=Tutorial_1_001 attr=row",
                Line(Speech()));
        }

        [Fact]
        public void The_voice_prefix_every_clip_shares_is_dropped()
        {
            Assert.Contains("voice=Tutorial_1_001", Line(Speech()));
            Assert.Contains("voice=cwy_example_hello", Line(Speech(voice: "cwy_example_hello")));
        }

        /// <summary>The whole point: the fields after the sentence must survive a long sentence.</summary>
        [Fact]
        public void A_long_sentence_is_capped_but_never_costs_a_field()
        {
            var long_ = new string('x', 400);
            var line = Line(Speech(text: long_, textJa: long_));

            Assert.Contains(new string('x', SatoneConsoleLines.TextLimit) + "…", line);
            Assert.DoesNotContain(new string('x', SatoneConsoleLines.TextLimit + 1), line);
            Assert.Contains("src=Tutorial", line);
            Assert.Contains("dur=2.3s", line);
            Assert.Contains("voice=Tutorial_1_001", line);
            Assert.Contains("id=Tutorial_1_001", line);
            Assert.Contains("attr=row", line);
        }

        [Fact]
        public void Each_language_is_capped_on_its_own()
        {
            var line = Line(Speech(text: new string('e', 300), textJa: new string('j', 300)));

            Assert.Contains(new string('e', SatoneConsoleLines.TextLimit) + "…", line);
            Assert.Contains(new string('j', SatoneConsoleLines.TextLimit) + "…", line);
        }

        [Fact]
        public void A_newline_in_a_line_does_not_break_the_console_line()
        {
            var line = Line(Speech(text: "one\ntwo\r\nthree", textJa: null));

            // The formatter collapses the run of spaces the newlines left behind: two spaces in the
            // primary would be read as the start of the field column.
            Assert.Contains("\"one two three\"", line);
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void The_japanese_is_omitted_when_it_is_the_same_text()
        {
            Assert.Equal(
                "14:02:11.019  SPEECH  \"お茶を淹れたよ。\"  src=Tutorial dur=2.3s voice=Tutorial_1_001 " +
                "id=Tutorial_1_001 attr=row",
                Line(Speech(text: "お茶を淹れたよ。", textJa: "お茶を淹れたよ。")));
        }

        /// <summary>
        /// A clip a pack registered has no row in the game's master data, so there is no text and
        /// no id. The line still has to say what played and for how long.
        /// </summary>
        [Fact]
        public void A_clip_with_no_master_row_still_reports_what_played()
        {
            Assert.Equal(
                "14:02:11.019  SPEECH  -  src=Other dur=0.4s voice=cwy_beep id=- attr=none",
                Line(Speech(text: null, textJa: null, id: null, voice: "Voice_cwy_beep",
                    source: "Other", duration: 0.4d, attribution: "none")));
        }

        [Fact]
        public void A_missing_payload_prints_the_layout_rather_than_throwing()
        {
            Assert.Equal(
                "14:02:11.019  SPEECH  -  src=- dur=- voice=- id=- attr=-",
                Line(null));

            Assert.Equal("14:02:11.019  STATE  - -> -", StateLine(null));
        }

        [Fact]
        public void The_state_line_is_the_transition_and_nothing_else()
        {
            var payload = new Dictionary<string, object?>
            {
                ["prev"] = "WantTalk",
                ["next"] = "BreakMovie",
                ["prevValue"] = 3,
                ["nextValue"] = 7,
            };

            Assert.Equal("14:02:11.019  STATE  WantTalk -> BreakMovie", StateLine(payload));
        }

        [Fact]
        public void Only_the_two_satone_events_are_formatted_here()
        {
            Assert.True(SatoneConsoleLines.Handles("satone", "speech"));
            Assert.True(SatoneConsoleLines.Handles("satone", "state"));
            Assert.False(SatoneConsoleLines.Handles("satone", "other"));
            Assert.False(SatoneConsoleLines.Handles("health", "speech"));

            Assert.Null(SatoneConsoleLines.Format(At, "health", "interval", null));
            Assert.NotNull(SatoneConsoleLines.Format(At, "satone", "speech", Speech()));
        }

        [Theory]
        [InlineData(2.25d, "2.3")]
        [InlineData(0d, "0.0")]
        [InlineData(12f, "12.0")]
        [InlineData("1.5", "1.5")]
        public void The_duration_is_one_decimal_whatever_the_producer_published(object value, string expected) =>
            Assert.Contains("dur=" + expected + "s", Line(Speech(duration: value)));

        [Fact]
        public void A_duration_that_is_not_a_number_costs_only_the_duration()
        {
            var line = Line(Speech(duration: new object()));

            Assert.Contains("dur=-", line);
            Assert.Contains("voice=Tutorial_1_001", line);
        }

        [Fact]
        public void The_speech_line_reads_back_as_the_same_record()
        {
            var line = Line(Speech());

            Assert.True(ConsoleRecordParser.TryParse(line, At, out var parsed));
            Assert.Equal(ConsoleTags.Speech, parsed!.Tag);
            Assert.Equal("Tutorial", parsed.Field("src"));
            Assert.Equal("2.3s", parsed.Field("dur"));
            Assert.Equal("row", parsed.Field("attr"));
            Assert.Equal(line, ConsoleRecordFormatter.Format(parsed));
        }
    }
}
