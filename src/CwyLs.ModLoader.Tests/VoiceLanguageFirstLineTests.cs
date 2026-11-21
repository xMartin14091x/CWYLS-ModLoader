using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-12: the first voiced line of a scene - which line it is, and what the scene start does about
    /// its clip (decode now, start now, finish first, or nothing) - plus the table's out-of-turn take.
    /// </summary>
    public class VoiceLanguageFirstLineTests
    {
        private const long SmallWav = 265_000; // about 6 s of 16-bit mono at 22.05 kHz

        private static readonly VoiceEntry PackEntry = new VoiceEntry("Voice_A", "cwy.example", VoicePriority.Pack, clip: "pack-clip");
        private static readonly VoiceEntry RuntimeEntry = new VoiceEntry("Voice_A", VoiceRegistry.RuntimeSource, VoicePriority.Runtime, clip: "mod-clip");

        private static LanguageVoiceTable Table(params string[] paths) =>
            new LanguageVoiceTable(LanguageVoicePlan.Build(new[]
            {
                new KeyValuePair<string, PackContentIndex>("my.dub", PackContentIndex.Build(paths)),
            }));

        private static LanguageVoiceLookup NotLoaded(VoiceLoaderKind loader) =>
            new LanguageVoiceLookup(
                LanguageVoiceOutcome.NotLoaded,
                "th",
                "Voice_A",
                new VoiceLoadStep("my.dub", "audio/voice/th/Voice_A" + (loader == VoiceLoaderKind.WavSync ? ".wav" : ".ogg"), "Voice_A", loader),
                null,
                null);

        /// <summary>What the scene start would do for one line of a table, as the prefetch asks it.</summary>
        private static LanguageVoiceFirstLineAction DecideFor(LanguageVoiceTable table, string name, long bytes = SmallWav) =>
            LanguageVoiceFirstLine.Decide(table.Lookup("th", name), table.StateOf("th", name), outranked: false, bytes);

        [Fact]
        public void The_first_voiced_line_is_the_first_non_blank_name_in_reading_order()
        {
            Assert.Equal("Voice_B", LanguageVoiceFirstLine.FirstVoiced(new[] { null, "", "  ", "Voice_B", "Voice_A" }));
            Assert.Equal("Voice_A", LanguageVoiceFirstLine.FirstVoiced(new[] { "Voice_A" }));
            Assert.Null(LanguageVoiceFirstLine.FirstVoiced(new string?[] { null, " " }));
            Assert.Null(LanguageVoiceFirstLine.FirstVoiced(new string[0]));
            Assert.Null(LanguageVoiceFirstLine.FirstVoiced(null));
        }

        [Fact]
        public void A_waiting_wav_is_decoded_now_and_a_waiting_ogg_is_started_now()
        {
            Assert.Equal(
                LanguageVoiceFirstLineAction.LoadNow,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), LanguageVoiceState.Pending, false, SmallWav));
            Assert.Equal(
                LanguageVoiceFirstLineAction.StartNow,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.EngineAsync), LanguageVoiceState.Pending, false, -1));

            // The size only matters for a WAV: an Ogg goes to the engine whatever its size.
            Assert.Equal(
                LanguageVoiceFirstLineAction.StartNow,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.EngineAsync), LanguageVoiceState.Pending, false, SmallWav));
        }

        [Fact]
        public void A_clip_already_in_flight_is_finished_first_unless_it_is_a_wav_that_can_be_decoded_now()
        {
            Assert.Equal(
                LanguageVoiceFirstLineAction.FinishFirst,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.EngineAsync), LanguageVoiceState.Loading, false, -1));
            Assert.Equal(
                LanguageVoiceFirstLineAction.LoadNow,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), LanguageVoiceState.Loading, false, SmallWav));
        }

        [Theory]
        [InlineData(LanguageVoiceFirstLine.MaxLoadNowBytes, LanguageVoiceFirstLineAction.LoadNow)]
        [InlineData(LanguageVoiceFirstLine.MaxLoadNowBytes + 1, LanguageVoiceFirstLineAction.StartNow)]
        [InlineData(0L, LanguageVoiceFirstLineAction.LoadNow)]
        [InlineData(-1L, LanguageVoiceFirstLineAction.StartNow)]
        public void A_wav_is_decoded_now_only_when_its_size_is_known_and_within_the_cap(long bytes, LanguageVoiceFirstLineAction expected)
        {
            Assert.Equal(expected, LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), LanguageVoiceState.Pending, false, bytes));
        }

        [Fact]
        public void A_large_wav_already_in_flight_is_finished_first()
        {
            Assert.Equal(
                LanguageVoiceFirstLineAction.FinishFirst,
                LanguageVoiceFirstLine.Decide(
                    NotLoaded(VoiceLoaderKind.WavSync), LanguageVoiceState.Loading, false, LanguageVoiceFirstLine.MaxLoadNowBytes + 1));
        }

        [Theory]
        [InlineData(LanguageVoiceOutcome.NoSet)]
        [InlineData(LanguageVoiceOutcome.NotInSet)]
        [InlineData(LanguageVoiceOutcome.Loaded)]
        [InlineData(LanguageVoiceOutcome.Failed)]
        public void Nothing_is_hurried_unless_the_set_covers_the_line_and_its_clip_is_not_loaded(LanguageVoiceOutcome outcome)
        {
            var lookup = new LanguageVoiceLookup(outcome, "th", "Voice_A", NotLoaded(VoiceLoaderKind.WavSync).Step, null, null);

            Assert.Equal(LanguageVoiceFirstLineAction.None, LanguageVoiceFirstLine.Decide(lookup, LanguageVoiceState.Pending, false, SmallWav));
            Assert.Equal(LanguageVoiceFirstLineAction.None, LanguageVoiceFirstLine.Decide(default, LanguageVoiceState.Pending, false, SmallWav));
        }

        [Theory]
        [InlineData(LanguageVoiceState.Loaded)]
        [InlineData(LanguageVoiceState.Failed)]
        public void A_loaded_or_failed_clip_is_left_alone_so_a_broken_file_is_never_retried_here(LanguageVoiceState state)
        {
            Assert.Equal(LanguageVoiceFirstLineAction.None, LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), state, false, SmallWav));
            Assert.Equal(LanguageVoiceFirstLineAction.None, LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.EngineAsync), state, false, -1));
            Assert.Equal(LanguageVoiceFirstLineAction.None, LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), null, false, SmallWav));
        }

        [Fact]
        public void A_line_something_else_answers_first_is_not_hurried_and_the_selection_is_unchanged()
        {
            // A mod's runtime clip and Pack Maker's swaps answer before the set: loading the set's clip
            // early would not change what plays, so it is not done. A global pack clip does not
            // outrank the set (the set answers once loaded), so the set's clip is still hurried.
            Assert.True(LanguageVoiceFirstLine.Outranked(RuntimeEntry, makerSwap: false));
            Assert.True(LanguageVoiceFirstLine.Outranked(null, makerSwap: true));
            Assert.True(LanguageVoiceFirstLine.Outranked(PackEntry, makerSwap: true));
            Assert.False(LanguageVoiceFirstLine.Outranked(PackEntry, makerSwap: false));
            Assert.False(LanguageVoiceFirstLine.Outranked(null, makerSwap: false));

            Assert.Equal(
                LanguageVoiceFirstLineAction.None,
                LanguageVoiceFirstLine.Decide(NotLoaded(VoiceLoaderKind.WavSync), LanguageVoiceState.Pending, outranked: true, SmallWav));

            // The precedence itself is LanguageVoiceRules', untouched: the set's clip, once loaded,
            // still beats a pack clip and still loses to a mod's.
            var loaded = new LanguageVoiceLookup(LanguageVoiceOutcome.Loaded, "th", "Voice_A", null, "thai-clip", null);
            Assert.Equal(VoiceChoice.Language, LanguageVoiceRules.Choose(PackEntry, loaded));
            Assert.Equal(VoiceChoice.Runtime, LanguageVoiceRules.Choose(RuntimeEntry, loaded));
        }

        [Fact]
        public void TakeNow_takes_one_waiting_clip_out_of_turn_and_the_queue_never_hands_it_out_again()
        {
            var table = Table("audio/voice/th/Voice_A.ogg", "audio/voice/th/Voice_B.ogg", "audio/voice/th/Voice_C.ogg");

            // A scene's lines were queued first; then its first line is taken at once.
            Assert.Equal(2, table.Prioritize("th", new[] { "Voice_C", "Voice_B" }));
            var step = table.TakeNow("th", "Voice_C");

            Assert.NotNull(step);
            Assert.Equal("Voice_C", step!.Name);
            Assert.Equal(LanguageVoiceState.Loading, table.StateOf("th", "Voice_C"));
            Assert.Equal(1, table.CountsFor("th").Loading);

            // Neither the scene's queue nor the background hands it out a second time.
            Assert.Equal(new[] { "Voice_B", "Voice_A" }, table.TakeNext("th", 5, includeBackground: true).Select(s => s.Name));
            Assert.Empty(table.TakeNext("th", 5, includeBackground: true));

            // It completes like any other load, and the lookup then answers with it.
            var clip = new object();
            Assert.True(table.Complete("th", step, clip, out _));
            Assert.Same(clip, table.Lookup("th", "Voice_C").Clip);
        }

        [Fact]
        public void TakeNow_takes_nothing_that_is_not_waiting()
        {
            var table = Table("audio/voice/th/Voice_A.ogg", "audio/voice/th/Voice_B.ogg", "audio/voice/th/Voice_C.ogg");

            var loading = Assert.Single(table.TakeNext("th", 1, includeBackground: true));
            Assert.Null(table.TakeNow("th", loading.Name));

            var failed = table.TakeNow("th", "Voice_B");
            Assert.True(table.Fail("th", failed, "file not found"));
            Assert.Null(table.TakeNow("th", "Voice_B"));

            var loaded = table.TakeNow("th", "Voice_C");
            Assert.True(table.Complete("th", loaded, new object(), out _));
            Assert.Null(table.TakeNow("th", "Voice_C"));

            Assert.Null(table.TakeNow("th", "Voice_Z"));
            Assert.Null(table.TakeNow("th", ""));
            Assert.Null(table.TakeNow("th", null));
            Assert.Null(table.TakeNow("en", "Voice_A"));
            Assert.Null(table.TakeNow(null, "Voice_A"));
        }

        [Fact]
        public void TakeNow_accepts_any_spelling_of_the_language_like_the_rest_of_the_table()
        {
            var table = Table("audio/voice/th/Voice_A.ogg");

            Assert.NotNull(table.TakeNow("TH", "Voice_A"));
            Assert.Equal(LanguageVoiceState.Loading, table.StateOf("th", "Voice_A"));
        }

        [Fact]
        public void A_scene_start_walks_the_first_line_from_waiting_to_loaded_once()
        {
            // The flow the StartReady prefetch runs, on the real table: decide, take, load.
            var table = Table("audio/voice/th/Voice_A.wav", "audio/voice/th/Voice_B.ogg");

            Assert.Equal(LanguageVoiceFirstLineAction.LoadNow, DecideFor(table, "Voice_A"));
            var wav = table.TakeNow("th", "Voice_A");
            Assert.True(table.Complete("th", wav, "wav-clip", out _));
            Assert.Equal(LanguageVoiceFirstLineAction.None, DecideFor(table, "Voice_A"));

            Assert.Equal(LanguageVoiceFirstLineAction.StartNow, DecideFor(table, "Voice_B"));
            var ogg = table.TakeNow("th", "Voice_B");
            Assert.NotNull(ogg);

            // The same scene readied again while the request is in flight: finish it first, never start a second one.
            Assert.Equal(LanguageVoiceFirstLineAction.FinishFirst, DecideFor(table, "Voice_B"));
            Assert.Null(table.TakeNow("th", "Voice_B"));

            Assert.True(table.Complete("th", ogg, "ogg-clip", out _));
            Assert.Equal(LanguageVoiceFirstLineAction.None, DecideFor(table, "Voice_B"));
            Assert.True(table.CountsFor("th").Settled);
        }

        [Fact]
        public void A_scene_start_in_a_language_without_a_set_or_on_a_line_the_set_lacks_does_nothing()
        {
            var table = Table("audio/voice/th/Voice_A.wav");

            Assert.Equal(
                LanguageVoiceFirstLineAction.None,
                LanguageVoiceFirstLine.Decide(table.Lookup("en", "Voice_A"), table.StateOf("en", "Voice_A"), false, SmallWav));
            Assert.Equal(LanguageVoiceFirstLineAction.None, DecideFor(table, "Voice_Z"));
            Assert.Equal(LanguageVoiceState.Pending, table.StateOf("th", "Voice_A"));
        }
    }
}
