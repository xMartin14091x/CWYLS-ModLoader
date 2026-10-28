using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>D-AUDIO: which pack file replaces which game row, and the table conventions.</summary>
    public class AudioReplacementPlanTests
    {
        internal static readonly AudioRowDescriptor LightRain = new AudioRowDescriptor(AudioTable.Ambient, "LightRain", "LightRain", 32.0);
        internal static readonly AudioRowDescriptor HeavyRain = new AudioRowDescriptor(AudioTable.Ambient, "HeavyRain", "HeavyRain", 24.576);
        internal static readonly AudioRowDescriptor ThunderRain = new AudioRowDescriptor(AudioTable.Ambient, "ThunderRain", "Rainthunder", 42.72);
        internal static readonly AudioRowDescriptor Click = new AudioRowDescriptor(AudioTable.SystemSe, "Click", "bulbul_systemse_click", 0.372);
        internal static readonly AudioRowDescriptor FinishA = new AudioRowDescriptor(AudioTable.TimerSe, "Finish_A", "bulbul_timerse_timerend_A", 2.1);
        internal static readonly AudioRowDescriptor Keyboard = new AudioRowDescriptor(AudioTable.AmbientSe, "KeyboardTyping", "KeyboardTyping", 12.0);

        /// <summary>The one story-music row of game 1.17.3 (DirectionMusicMaster.asset): plays "Without You".</summary>
        internal static readonly AudioRowDescriptor Main32 = new AudioRowDescriptor(AudioTable.DirectionMusic, "main32_Music", "Without You_main32_full", 300);

        internal static readonly SoundtrackCatalog Soundtrack = new SoundtrackCatalog(new[]
        {
            new SoundtrackTrack("Aqua Room", "akase", "Aqua Room", "1a2b3c4d-0000-4000-8000-00000000a001", true),
            new SoundtrackTrack("Without You", "N.L. One Music Studio", "Without You", "1a2b3c4d-0000-4000-8000-00000000a002", true),
            new SoundtrackTrack("Daywind", "N.L. One Music Studio", "Daywind", "1a2b3c4d-0000-4000-8000-00000000a003", false),
        });

        private static readonly AudioRowDescriptor[] Rows = { LightRain, HeavyRain, ThunderRain, Keyboard, Click, FinishA, Main32 };

        private static KeyValuePair<string, PackContentIndex> Pack(string id, params string[] paths) =>
            new KeyValuePair<string, PackContentIndex>(id, PackContentIndex.Build(paths));

        private static AudioReplacementPlan Plan(params KeyValuePair<string, PackContentIndex>[] packs) =>
            AudioReplacementPlan.Build(AudioCandidate.FromPacks(packs), Rows, Soundtrack);

        [Fact]
        public void Table_folders_are_the_frozen_pack_conventions()
        {
            Assert.Equal("audio/ambient/", AudioTables.FolderOf(AudioTable.Ambient));
            Assert.Equal("audio/ambient_se/", AudioTables.FolderOf(AudioTable.AmbientSe));
            Assert.Equal("audio/se/system/", AudioTables.FolderOf(AudioTable.SystemSe));
            Assert.Equal("audio/se/timer/", AudioTables.FolderOf(AudioTable.TimerSe));
            Assert.Equal("audio/direction_music/", AudioTables.FolderOf(AudioTable.DirectionMusic));

            foreach (var table in AudioTables.All)
            {
                Assert.True(AudioTables.TryFromKind(AudioTables.KindOf(table), out var back));
                Assert.Equal(table, back);
                Assert.True(AudioTables.TryParse(AudioTables.Id(table), out var parsed));
                Assert.Equal(table, parsed);
            }
        }

        [Theory]
        [InlineData("AMBIENT", AudioTable.Ambient)]
        [InlineData("ambient-se", AudioTable.AmbientSe)]
        [InlineData("se", AudioTable.SystemSe)]
        [InlineData("timer", AudioTable.TimerSe)]
        [InlineData("story", AudioTable.DirectionMusic)]
        public void Table_aliases_parse(string text, AudioTable expected)
        {
            Assert.True(AudioTables.TryParse(text, out var table));
            Assert.Equal(expected, table);
        }

        [Fact]
        public void Only_ambient_and_story_music_are_loops()
        {
            Assert.True(AudioTables.IsLoop(AudioTable.Ambient));
            Assert.True(AudioTables.IsLoop(AudioTable.DirectionMusic));
            Assert.False(AudioTables.IsLoop(AudioTable.SystemSe));
            Assert.False(AudioTables.IsLoop(AudioTable.TimerSe));
            Assert.False(AudioTables.IsLoop(AudioTable.AmbientSe));
        }

        [Fact]
        public void A_file_claims_the_row_named_like_it_case_insensitively()
        {
            var plan = Plan(Pack("cwy.demo.audio", "audio/ambient/lightrain.wav", "audio/se/system/Click.ogg", "audio/se/timer/Finish_A.wav"));

            Assert.Equal(3, plan.Assignments.Count);
            Assert.Equal("ambient/LightRain", plan.Assignments[0].Row.Key);
            Assert.Equal("cwy.demo.audio/audio/ambient/lightrain.wav", plan.Assignments[0].Candidate.Source);
            Assert.NotNull(plan.For("system_se/Click"));
            Assert.NotNull(plan.For("TIMER_SE/finish_a"));
            Assert.Empty(plan.Skipped);
        }

        [Fact]
        public void A_file_named_after_the_game_clip_claims_that_row()
        {
            var plan = Plan(Pack("p", "audio/ambient/Rainthunder.ogg"));

            var assignment = Assert.Single(plan.Assignments);
            Assert.Same(ThunderRain, assignment.Row);
            Assert.True(assignment.MatchedByClipName);
        }

        [Fact]
        public void An_unknown_name_is_skipped_with_a_hint()
        {
            var plan = Plan(Pack("p", "audio/ambient/Drizzle.ogg"));

            Assert.Empty(plan.Assignments);
            var skip = Assert.Single(plan.Skipped);
            Assert.Equal(AudioSkipKind.UnknownRow, skip.Kind);
            Assert.Contains("no ambient row is called 'Drizzle'", skip.Reason);
            Assert.Contains("audio list ambient", skip.Reason);
        }

        [Fact]
        public void A_file_only_matches_rows_of_its_own_table()
        {
            // "Click" is a system sound, not an ambient loop.
            var plan = Plan(Pack("p", "audio/ambient/Click.ogg"));

            Assert.Empty(plan.Assignments);
            Assert.Equal(AudioSkipKind.UnknownRow, Assert.Single(plan.Skipped).Kind);
        }

        [Fact]
        public void The_later_pack_wins_and_the_loser_is_reported()
        {
            var plan = Plan(Pack("first", "audio/ambient/LightRain.ogg"), Pack("second", "audio/ambient/LightRain.ogg"));

            Assert.Equal("second", Assert.Single(plan.Assignments).Candidate.PackId);
            var skip = Assert.Single(plan.Skipped);
            Assert.Equal(AudioSkipKind.Overridden, skip.Kind);
            Assert.Equal("first", skip.Candidate.PackId);
            Assert.Contains("overridden by second/audio/ambient/LightRain.ogg", skip.Reason);
        }

        [Fact]
        public void Inside_one_pack_the_later_path_wins()
        {
            var plan = Plan(Pack("p", "audio/ambient/LightRain.wav", "audio/ambient/LightRain.ogg"));

            // Ordinal path order: .ogg before .wav, so the WAV is the later claim.
            Assert.Equal(".wav", Assert.Single(plan.Assignments).Candidate.Extension);
        }

        [Fact]
        public void Assignments_come_out_in_the_games_row_order()
        {
            var plan = Plan(Pack("p", "audio/ambient/HeavyRain.wav", "audio/ambient/LightRain.wav"));

            Assert.Equal(new[] { "ambient/LightRain", "ambient/HeavyRain" }, plan.Assignments.Select(a => a.Row.Key));
            Assert.Equal("2 row(s) replaced from packs; 0 refused (paid); 0 unknown; 0 overridden", plan.Summarize());
        }

        [Fact]
        public void Nested_or_non_audio_files_are_not_candidates()
        {
            var candidates = AudioCandidate.FromPacks(new[]
            {
                Pack("p", "audio/ambient/old/LightRain.ogg", "audio/ambient/README.md", "audio/ambient/LightRain.mp3", "audio/voice/LightRain.ogg"),
            });

            Assert.Empty(candidates);
        }

        [Fact]
        public void An_empty_input_is_an_empty_plan()
        {
            var plan = AudioReplacementPlan.Build(null, null, null);

            Assert.Empty(plan.Assignments);
            Assert.Empty(plan.Skipped);
        }
    }
}
