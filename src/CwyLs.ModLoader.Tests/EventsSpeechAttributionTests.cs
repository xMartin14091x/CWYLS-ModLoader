using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// F2 — the pure half of "which row is Satone actually speaking?": the attribution decision,
    /// the voice-index change detector, the duplicate-clip-name survey and the one-line-per-line
    /// guard. None of them touch Unity or the game, which is the point.
    /// </summary>
    public class EventsSpeechAttributionTests
    {
        private const string SayRow = "cwy_say_1_001";
        private const string PackRow = "cwy_example_01_001";
        private const string SharedVoice = "Voice_CWY_Test";

        // ---- attribution ------------------------------------------------------------------

        [Fact]
        public void With_no_row_captured_the_voice_index_answers()
        {
            Assert.Equal(
                LineAttribution.VoiceIndex,
                SpeechAttribution.Decide(ScenarioRowHint.None, SharedVoice));
        }

        [Fact]
        public void The_row_the_reader_is_reading_wins_when_it_names_this_clip()
        {
            var hint = new ScenarioRowHint(SayRow, "cwy_say_1", SharedVoice, capturedFrame: 900);

            Assert.Equal(LineAttribution.Row, SpeechAttribution.Decide(hint, SharedVoice));
        }

        [Fact]
        public void A_clip_the_captured_row_does_not_name_falls_back_to_the_index()
        {
            // The one-frame gap between CommandVoiceText and VoiceManager.Play
            // (HeroineVoiceController) is long enough for a mod or an idle line to play
            // something else. That clip is not the scenario's line.
            var hint = new ScenarioRowHint(SayRow, "cwy_say_1", SharedVoice, capturedFrame: 900);

            Assert.Equal(
                LineAttribution.VoiceIndex, SpeechAttribution.Decide(hint, "Voice_Tutorial_1_001"));
        }

        [Fact]
        public void A_text_only_row_matches_a_line_with_no_clip_and_nothing_else()
        {
            var textOnly = new ScenarioRowHint("main_06_010", "main_06", voice: null, capturedFrame: 12);

            Assert.True(SpeechAttribution.VoiceMatches(null, null));
            Assert.True(SpeechAttribution.VoiceMatches(string.Empty, null));
            Assert.Equal(LineAttribution.Row, SpeechAttribution.Decide(textOnly, null));
            Assert.Equal(LineAttribution.VoiceIndex, SpeechAttribution.Decide(textOnly, SharedVoice));
        }

        [Fact]
        public void A_row_id_the_game_uses_for_no_text_is_not_a_hint()
        {
            // ScenarioReader logs its own error for these; they are not row ids.
            Assert.False(new ScenarioRowHint(null, "g", SharedVoice, 1).IsSet);
            Assert.False(new ScenarioRowHint(string.Empty, "g", SharedVoice, 1).IsSet);
            Assert.True(new ScenarioRowHint(SayRow, null, null, 1).IsSet);
        }

        [Fact]
        public void The_hint_keeps_the_frame_it_was_captured_on_for_diagnostics()
        {
            var hint = new ScenarioRowHint(SayRow, "cwy_say_1", SharedVoice, capturedFrame: 4231);

            Assert.Equal(4231, hint.CapturedFrame);
            Assert.Equal("cwy_say_1", hint.ScenarioGroupId);
            Assert.Contains(SayRow, hint.ToString(), StringComparison.Ordinal);
            Assert.Equal("(no row)", ScenarioRowHint.None.ToString());
        }

        [Fact]
        public void Attribution_values_are_a_closed_set()
        {
            Assert.True(LineAttribution.IsDefined(LineAttribution.Row));
            Assert.True(LineAttribution.IsDefined(LineAttribution.VoiceIndex));
            Assert.True(LineAttribution.IsDefined(LineAttribution.None));
            Assert.True(LineAttribution.IsDefined(LineAttribution.Unknown));
            Assert.False(LineAttribution.IsDefined("scenario-row"));
            Assert.False(LineAttribution.IsDefined(null));
        }

        // ---- the voice-index change detector ----------------------------------------------

        [Fact]
        public void The_fingerprint_ignores_table_order_because_the_table_is_a_dictionary()
        {
            var a = VoiceIndexFingerprint.Compute(
                new[] { Row("a", "Voice_A"), Row("b", "Voice_B"), Row("c", null) });
            var b = VoiceIndexFingerprint.Compute(
                new[] { Row("c", null), Row("b", "Voice_B"), Row("a", "Voice_A") });

            Assert.Equal(a, b);
            Assert.StartsWith("r3:v2:", a, StringComparison.Ordinal);
        }

        [Fact]
        public void The_fingerprint_changes_when_a_row_is_overridden_without_changing_the_count()
        {
            // Exactly what SatoneSayRows.Install does (remove, then add) and what a content pack
            // overriding a row does. The old count check missed both.
            var before = VoiceIndexFingerprint.Compute(
                new[] { Row(PackRow, SharedVoice), Row("x", "Voice_X") });
            var after = VoiceIndexFingerprint.Compute(
                new[] { Row(SayRow, SharedVoice), Row("x", "Voice_X") });

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void The_fingerprint_changes_when_only_a_voice_column_moves()
        {
            var before = VoiceIndexFingerprint.Compute(new[] { Row("a", "Voice_A") });
            var after = VoiceIndexFingerprint.Compute(new[] { Row("a", "Voice_B") });

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void The_fingerprint_separates_an_absent_voice_from_an_empty_one()
        {
            var absent = VoiceIndexFingerprint.Compute(new[] { Row("a", null) });
            var empty = VoiceIndexFingerprint.Compute(new[] { Row("a", string.Empty) });

            Assert.NotEqual(absent, empty);

            // Neither counts as voiced, so only the hash distinguishes them.
            Assert.StartsWith("r1:v0:", absent, StringComparison.Ordinal);
            Assert.StartsWith("r1:v0:", empty, StringComparison.Ordinal);
        }

        [Fact]
        public void An_empty_table_has_the_declared_empty_fingerprint()
        {
            Assert.Equal(VoiceIndexFingerprint.Empty, VoiceIndexFingerprint.Compute(null));
            Assert.Equal(
                VoiceIndexFingerprint.Empty, VoiceIndexFingerprint.Compute(new VoiceIndexEntry[0]));
        }

        // ---- duplicate clip names ---------------------------------------------------------

        [Fact]
        public void A_table_where_every_clip_name_is_unique_reports_nothing()
        {
            var collisions = VoiceNameCollisions.Find(
                new[] { Row("a", "Voice_A"), Row("b", "Voice_B"), Row("c", null), Row("d", null) });

            Assert.Empty(collisions);
            Assert.Equal(string.Empty, VoiceNameCollisions.Summarize(collisions));
            Assert.Equal(string.Empty, VoiceNameCollisions.Summarize(null));
        }

        [Fact]
        public void Two_rows_naming_one_clip_are_reported_with_both_ids_in_table_order()
        {
            var collisions = VoiceNameCollisions.Find(
                new[] { Row(PackRow, SharedVoice), Row("x", "Voice_X"), Row(SayRow, SharedVoice) });

            var only = Assert.Single(collisions);
            Assert.Equal(SharedVoice, only.Voice);
            Assert.Equal(new[] { PackRow, SayRow }, only.Ids);
            Assert.Equal(SharedVoice + " (" + PackRow + ", " + SayRow + ")", only.Describe());
        }

        [Fact]
        public void Collisions_are_ordered_by_how_many_rows_claim_them_then_by_name()
        {
            var collisions = VoiceNameCollisions.Find(
                new[]
                {
                    Row("1", "Voice_B"), Row("2", "Voice_B"),
                    Row("3", "Voice_A"), Row("4", "Voice_A"), Row("5", "Voice_A"),
                    Row("6", "Voice_C"), Row("7", "Voice_C")
                });

            Assert.Equal(3, collisions.Count);
            Assert.Equal("Voice_A", collisions[0].Voice);
            Assert.Equal(3, collisions[0].Ids.Count);
            Assert.Equal("Voice_B", collisions[1].Voice);
            Assert.Equal("Voice_C", collisions[2].Voice);
        }

        [Fact]
        public void The_summary_names_at_most_the_requested_number_and_counts_the_rest()
        {
            var rows = new List<VoiceIndexEntry>();
            for (var i = 0; i < 12; i++)
            {
                rows.Add(Row("a" + i, "Voice_" + i));
                rows.Add(Row("b" + i, "Voice_" + i));
            }

            var collisions = VoiceNameCollisions.Find(rows);
            var summary = VoiceNameCollisions.Summarize(collisions, VoiceNameCollisions.DefaultMax);

            Assert.Equal(12, collisions.Count);
            Assert.StartsWith("12 voice names are claimed by more than one", summary, StringComparison.Ordinal);
            Assert.EndsWith("; and 2 more", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void One_collision_reads_in_the_singular()
        {
            var summary = VoiceNameCollisions.Summarize(
                VoiceNameCollisions.Find(new[] { Row(PackRow, SharedVoice), Row(SayRow, SharedVoice) }));

            Assert.StartsWith("1 voice name is claimed by more than one", summary, StringComparison.Ordinal);
        }

        // ---- one LineSpoken per line ------------------------------------------------------

        [Fact]
        public void The_same_row_published_twice_inside_the_window_is_one_line()
        {
            Assert.True(
                SpeechRepeat.IsRepeat(SharedVoice, SayRow, 10.000d, SharedVoice, SayRow, 10.030d));
        }

        [Fact]
        public void Two_different_rows_sharing_a_clip_name_are_two_lines()
        {
            // The loud smoke pass plays Voice_CWY_Test twice in one frame: once straight through
            // VoiceManager.Play (p1.voice.play) and once through a Say scenario (p2.say.scenario).
            // Both are real lines and a subscriber must see both.
            Assert.False(
                SpeechRepeat.IsRepeat(SharedVoice, PackRow, 10.000d, SharedVoice, SayRow, 10.030d));
        }

        [Fact]
        public void An_unresolved_line_is_never_proven_to_be_a_repeat()
        {
            Assert.False(SpeechRepeat.IsRepeat(SharedVoice, null, 10d, SharedVoice, null, 10.01d));
            Assert.False(SpeechRepeat.IsRepeat(SharedVoice, SayRow, 10d, SharedVoice, null, 10.01d));
            Assert.False(SpeechRepeat.IsRepeat(SharedVoice, null, 10d, SharedVoice, SayRow, 10.01d));
            Assert.False(SpeechRepeat.IsRepeat(null, SayRow, 10d, SharedVoice, SayRow, 10.01d));
        }

        [Fact]
        public void The_same_line_said_again_later_is_a_new_line()
        {
            Assert.False(
                SpeechRepeat.IsRepeat(
                    SharedVoice, SayRow, 10.000d, SharedVoice, SayRow,
                    10.000d + SpeechRepeat.DefaultWindowSeconds + 0.001d));
        }

        [Fact]
        public void A_clip_that_started_before_the_previous_one_is_not_a_repeat()
        {
            // The caller's clock is Time.unscaledTime, which is reset by a scene reload.
            Assert.False(SpeechRepeat.IsRepeat(SharedVoice, SayRow, 10d, SharedVoice, SayRow, 0.5d));
        }

        [Fact]
        public void A_window_of_zero_turns_the_guard_off()
        {
            Assert.False(
                SpeechRepeat.IsRepeat(
                    SharedVoice, SayRow, 10d, SharedVoice, SayRow, 10.001d, windowSeconds: 0d));
            Assert.True(
                SpeechRepeat.IsRepeat(
                    SharedVoice, SayRow, 10d, SharedVoice, SayRow, 10.001d, windowSeconds: 0.25d));
        }

        [Fact]
        public void A_different_clip_of_the_same_row_is_not_a_repeat()
        {
            // A mod re-voicing a line registers a new clip name for the same row.
            Assert.False(
                SpeechRepeat.IsRepeat(SharedVoice, SayRow, 10d, "MyMod_Line_0001", SayRow, 10.01d));
        }

        private static VoiceIndexEntry Row(string? id, string? voice) => new VoiceIndexEntry(id, voice);
    }
}
