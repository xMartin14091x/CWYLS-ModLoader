using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="MakerDubTiming"/>: a dub is never cut off (DS-03 Must 7). The game's reader gives up
    /// waiting for a voice after 30 s; the hold keeps the line until the dub has played + 0.3 s.
    /// </summary>
    public class MakerEditorDubTimingTests
    {
        private const double Tail = ReadingTime.VoiceTailSeconds;

        [Fact]
        public void Only_the_games_own_clip_is_not_a_replacement()
        {
            Assert.False(MakerDubTiming.IsReplacement("Game"));
            Assert.False(MakerDubTiming.IsReplacement(null));
            Assert.False(MakerDubTiming.IsReplacement(string.Empty));
            Assert.True(MakerDubTiming.IsReplacement("Language"));
            Assert.True(MakerDubTiming.IsReplacement("Pack"));
            Assert.True(MakerDubTiming.IsReplacement("Runtime"));
        }

        [Fact]
        public void A_long_dub_still_playing_after_the_readers_30_second_timeout_holds_to_its_end_plus_the_tail()
        {
            // A 6 s Japanese line dubbed at 6x: 36 s. The reader moves on at 30 s.
            var hold = MakerDubTiming.HoldSeconds(sinceClipStarted: 30d, clipSeconds: 36d, stillPlaying: true);

            Assert.Equal(6d + Tail, hold, 6);
        }

        [Fact]
        public void A_dub_that_just_ended_gets_at_most_the_tail()
        {
            Assert.Equal(Tail, MakerDubTiming.HoldSeconds(10d, 10d, stillPlaying: false), 6);
            Assert.Equal(0.1d, MakerDubTiming.HoldSeconds(10.2d, 10d, stillPlaying: false), 6);
        }

        [Fact]
        public void A_dub_stopped_early_by_a_click_never_freezes_the_box()
        {
            // 40 s take stopped at 5 s: the game reports the voice finished.
            Assert.Equal(Tail, MakerDubTiming.HoldSeconds(5d, 40d, stillPlaying: false), 6);
        }

        [Fact]
        public void A_finished_or_unusable_clip_holds_nothing()
        {
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(12d, 10d, stillPlaying: true));
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(1d, 0d, stillPlaying: true));
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(1d, double.NaN, stillPlaying: true));
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(1d, ReadingTime.MaxClipSeconds + 1d, stillPlaying: true));
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(double.PositiveInfinity, 10d, stillPlaying: true));
            Assert.Equal(0d, MakerDubTiming.HoldSeconds(-1d, 10d, stillPlaying: true));
        }

        [Fact]
        public void The_hold_is_the_clip_part_of_the_subtitle_rule()
        {
            // Same clip + tail as ReadingTime's voiced rule, measured from the clip's start.
            var since = 4d;
            var clip = 50d;
            var subtitleRule = ReadingTime.RemainingFor("x", shownAtSeconds: 0d, nowSeconds: since, clipStartedAtSeconds: 0d, clipSeconds: clip);

            Assert.Equal(subtitleRule, MakerDubTiming.HoldSeconds(since, clip, stillPlaying: true), 6);
        }

        [Fact]
        public void Raise_never_lowers_the_games_wait()
        {
            Assert.Equal(2d, MakerDubTiming.Raise(2d, 0.5d));
            Assert.Equal(7.3d, MakerDubTiming.Raise(0d, 7.3d));
            Assert.Equal(1d, MakerDubTiming.Raise(1d, double.NaN));
        }

        [Fact]
        public void The_games_talk_limits_scale_only_while_dubs_can_play()
        {
            Assert.Equal(15d, MakerDubTiming.ScaledCap(15d, dubsInForce: false));
            Assert.Equal(90d, MakerDubTiming.ScaledCap(15d, dubsInForce: true));
            Assert.Equal(20, MakerDubTiming.ScaledCap(20, dubsInForce: false));
            Assert.Equal(120, MakerDubTiming.ScaledCap(20, dubsInForce: true));
            Assert.Equal(0d, MakerDubTiming.ScaledCap(0d, dubsInForce: true));
            Assert.Equal(-1, MakerDubTiming.ScaledCap(-1, dubsInForce: true));
        }

        [Fact]
        public void A_scaled_limit_outlasts_the_longest_dub_ratio()
        {
            // A greeting whose Japanese clips fill the game's 20 s, dubbed at the longest ratio seen.
            var dubbed = 20d * MakerDubTiming.MaxDubRatio;

            Assert.True(MakerDubTiming.ScaledCap(20, dubsInForce: true) >= dubbed);
        }
    }
}
