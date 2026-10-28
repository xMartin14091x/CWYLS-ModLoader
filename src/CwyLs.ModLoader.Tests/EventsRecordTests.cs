using System;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class EventsRecordTests
    {
        private static LineSpoken Line(string? text = "Hello? Is it sunny over there?", string? ja = "もしもし、そっちは晴れてる？") =>
            new LineSpoken(
                "Voice_Tutorial_1_001",
                text,
                ja,
                "tutorial_01_001",
                "English",
                IsStory: true,
                "Tutorial",
                "tutorial_01",
                LineSource.Tutorial,
                MoveMouth: true,
                DurationSec: 2.05d);

        [Fact]
        public void Records_with_the_same_members_are_equal_and_hash_alike()
        {
            var a = new SatoneStateChanged("WantTalk", "BreakMovie", 1, 20);
            var b = new SatoneStateChanged("WantTalk", "BreakMovie", 1, 20);
            var c = new SatoneStateChanged("WantTalk", "WorkPC", 1, 17);

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.NotEqual(a, c);
            Assert.True(a == b);
            Assert.False(a.Equals(c));
        }

        [Fact]
        public void Value_equality_holds_for_the_bigger_records_too()
        {
            Assert.Equal(Line(), Line());
            Assert.NotEqual(Line(), Line(text: "Something else"));

            Assert.Equal(new MusicChanged("Rainy Window", 3, true), new MusicChanged("Rainy Window", 3, true));
            Assert.NotEqual(new MusicChanged("Rainy Window", 3, true), new MusicChanged("Rainy Window", 4, true));

            Assert.Equal(new RoomReady(), new RoomReady());
            Assert.Equal(new ScenarioEnded(null), new ScenarioEnded(null));
            Assert.NotEqual(new ScenarioEnded(null), new ScenarioEnded("tutorial_01"));
        }

        [Fact]
        public void ToString_names_the_record_and_its_members()
        {
            var text = new SatoneStateChanged("WantTalk", "BreakMovie", 1, 20).ToString();

            Assert.StartsWith(nameof(SatoneStateChanged), text, StringComparison.Ordinal);
            Assert.Contains("Previous = WantTalk", text, StringComparison.Ordinal);
            Assert.Contains("Next = BreakMovie", text, StringComparison.Ordinal);
            Assert.Contains("NextValue = 20", text, StringComparison.Ordinal);
        }

        [Fact]
        public void Describe_is_the_compact_console_form()
        {
            Assert.Equal("WantTalk -> BreakMovie", new SatoneStateChanged("WantTalk", "BreakMovie", 1, 20).Describe());
            Assert.Equal("RoomScene loaded", new SceneChanged("RoomScene", true).Describe());
            Assert.Equal("RoomScene unloaded", new SceneChanged("RoomScene", false).Describe());
            Assert.Equal("Sakura on", new WindowChanged("Sakura", true).Describe());
            Assert.Equal("#3 Rainy Window (shuffle)", new MusicChanged("Rainy Window", 3, true).Describe());
            Assert.Equal("#0 Rainy Window", new MusicChanged("Rainy Window", 0, false).Describe());
            Assert.Equal("TodoList", new SaveSection("TodoList").Describe());
            Assert.Equal("room ready", new RoomReady().Describe());
            Assert.Equal(
                "WorkStarted (Work)", new PomodoroEvent(PomodoroEventKind.WorkStarted, "Work").Describe());
            Assert.Equal(
                "Work 120s elapsed, 1380s left", new PomodoroTick("Work", 120d, 1380d).Describe());
            Assert.Equal(
                "Tutorial group=tutorial_01 mode=Auto",
                new ScenarioStarted("Tutorial", "tutorial_01", "Auto").Describe());
        }

        [Fact]
        public void A_spoken_line_describes_the_voice_the_source_both_texts_and_the_length()
        {
            var describe = Line().Describe();

            Assert.Equal(
                "Voice_Tutorial_1_001 [Tutorial tutorial_01] \"Hello? Is it sunny over there?\" / 「もしもし、そっちは晴れてる？」 2.1s",
                describe);
        }

        [Fact]
        public void A_spoken_line_with_no_text_still_describes_cleanly()
        {
            var describe = Line(text: null, ja: null).Describe();

            Assert.Equal("Voice_Tutorial_1_001 [Tutorial tutorial_01] 2.1s", describe);
        }

        [Fact]
        public void A_clip_the_master_data_has_never_heard_of_is_marked_as_such()
        {
            // A pack-registered clip or a mod's generated line: no row, so no id and no text.
            var unknown = new LineSpoken(
                "MyMod_Line_0001",
                null,
                null,
                null,
                "English",
                IsStory: false,
                null,
                null,
                LineSource.Other,
                MoveMouth: true,
                DurationSec: 1.4d);

            Assert.True(unknown.NoTextInMaster);
            Assert.Equal(true, unknown.Fields()["noTextInMaster"]);
            Assert.Equal("MyMod_Line_0001 [Other] " + LineSpoken.NoTextMarker + " 1.4s", unknown.Describe());
        }

        [Fact]
        public void A_line_that_has_a_master_row_but_no_resolved_text_is_not_marked()
        {
            // The row exists, the text lookup simply came back empty - a different problem, and
            // the marker must not claim the clip is unknown to the game.
            var line = Line(text: null, ja: null);

            Assert.False(line.NoTextInMaster);
            Assert.Equal(false, line.Fields()["noTextInMaster"]);
            Assert.DoesNotContain(LineSpoken.NoTextMarker, line.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public void A_long_line_is_clipped_so_the_console_stays_readable()
        {
            var long_ = new string('あ', LineSpoken.DescribeTextLimit + 20);
            var describe = Line(text: long_, ja: long_).Describe();

            Assert.Contains("…", describe, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", describe, StringComparison.Ordinal);
        }

        [Fact]
        public void Fields_is_the_flat_payload_the_diagnostic_stream_records()
        {
            var fields = Line().Fields();

            Assert.Equal("Voice_Tutorial_1_001", fields["voice"]);
            Assert.Equal("Hello? Is it sunny over there?", fields["text"]);
            Assert.Equal("もしもし、そっちは晴れてる？", fields["textJa"]);
            Assert.Equal("tutorial_01_001", fields["localizationId"]);
            Assert.Equal(false, fields["noTextInMaster"]);
            Assert.Equal("English", fields["language"]);
            Assert.Equal(true, fields["isStory"]);
            Assert.Equal("Tutorial", fields["scenarioType"]);
            Assert.Equal("tutorial_01", fields["scenarioGroupId"]);
            Assert.Equal("Tutorial", fields["source"]);
            Assert.Equal(true, fields["moveMouth"]);
            Assert.Equal(2.05d, fields["durationSec"]);
            Assert.Equal(LineAttribution.Unknown, fields["attribution"]);
        }

        [Fact]
        public void A_line_says_how_its_row_was_resolved()
        {
            var fromRow = Line() with { Attribution = LineAttribution.Row };
            var fromIndex = Line() with { Attribution = LineAttribution.VoiceIndex };

            Assert.Equal(LineAttribution.Row, fromRow.Fields()["attribution"]);
            Assert.Equal(LineAttribution.VoiceIndex, fromIndex.Fields()["attribution"]);

            // The attribution is part of the line's identity: the same clip attributed two ways is
            // two different facts, and value equality must not hide that.
            Assert.NotEqual(fromRow, fromIndex);

            // ...but it is diagnostics, not content: Describe() is unchanged.
            Assert.Equal(fromIndex.Describe(), fromRow.Describe());
        }

        [Fact]
        public void Every_event_type_yields_a_payload_and_a_single_line_description()
        {
            var events = new ICwyEvent[]
            {
                new SatoneStateChanged("WantTalk", "BreakMovie", 1, 20),
                Line(),
                new LineEnded("Voice_Tutorial_1_001", 2.05d),
                new ScenarioStarted("Tutorial", "tutorial_01", "Auto"),
                new ScenarioEnded("tutorial_01"),
                new PomodoroEvent(PomodoroEventKind.Completed, "Complete"),
                new PomodoroTick("Break", 30d, 270d),
                new WindowChanged("Sakura", false),
                new MusicChanged("Rainy Window", 3, true),
                new SaveSection("TodoList"),
                new SceneChanged("RoomScene", true),
                new GameClockTick(new DateTime(2026, 9, 22, 14, 30, 5, DateTimeKind.Local)),
                new RoomReady()
            };

            foreach (var evt in events)
            {
                var describe = evt.Describe();
                Assert.NotNull(describe);
                Assert.DoesNotContain("\n", describe, StringComparison.Ordinal);
                Assert.NotNull(evt.Fields());
            }

            Assert.Equal(13, events.Length);
        }

        [Fact]
        public void The_clock_tick_reports_local_time_in_both_forms()
        {
            var tick = new GameClockTick(new DateTime(2026, 9, 22, 14, 30, 5, DateTimeKind.Local));

            Assert.Equal("2026-09-22 14:30:05", tick.Describe());
            Assert.StartsWith("2026-09-22T14:30:05", (string)tick.Fields()["time"]!, StringComparison.Ordinal);
        }

        [Fact]
        public void The_catch_all_envelope_names_the_channel_and_delegates_the_description()
        {
            var any = new AnyEvent(nameof(SceneChanged), new SceneChanged("RoomScene", true));

            Assert.Equal("SceneChanged RoomScene loaded", any.Describe());
            Assert.Equal(
                any, new AnyEvent(nameof(SceneChanged), new SceneChanged("RoomScene", true)));
        }
    }
}
