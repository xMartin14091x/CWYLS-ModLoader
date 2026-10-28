using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Language voice sets: a voiced line stays in the box for <c>max(2.5 s, characters / 15, clip + 0.3 s)</c>,
    /// the clip measured from when it started, and only the line's own clip counts.
    /// </summary>
    public class SubtitlesLangClipHoldTests
    {
        private const string Short = "OK";                                  // 2.5 s to read
        private static readonly string Long = new string('x', 60);         // 4.0 s to read

        [Theory]
        [InlineData(0d, 2.5d)]          // no clip: the reading rule alone
        [InlineData(1.0d, 2.5d)]        // a short clip does not shorten reading
        [InlineData(4.0d, 4.3d)]        // a long clip + tail wins
        [InlineData(double.NaN, 2.5d)]  // a broken length is ignored
        [InlineData(-1d, 2.5d)]
        [InlineData(500d, 2.5d)]        // longer than any clip the hold follows
        public void Minimum_is_the_longest_of_reading_and_clip_plus_tail(double clip, double expected)
        {
            Assert.Equal(expected, ReadingTime.MinimumFor(Short, clip), 6);
        }

        [Fact]
        public void A_long_text_still_wins_over_a_short_clip()
        {
            Assert.Equal(4.0d, ReadingTime.MinimumFor(Long, 2.0d), 6);
        }

        [Theory]
        [InlineData(10.0, 10.016, true)]   // a frame after the text: the line's own clip
        [InlineData(10.0, 9.6, true)]      // just before the text (the reader asked first)
        [InlineData(10.0, 12.0, true)]     // the edge of the lag window
        [InlineData(10.0, 12.1, false)]    // much later: a later line's
        [InlineData(10.0, 9.4, false)]     // well before: the previous line's
        [InlineData(10.0, double.NegativeInfinity, false)]
        [InlineData(double.NaN, 10.0, false)]
        public void Only_a_clip_started_with_the_text_belongs_to_it(double shownAt, double clipAt, bool belongs)
        {
            Assert.Equal(belongs, ReadingTime.ClipBelongsToLine(shownAt, clipAt));
        }

        [Fact]
        public void When_the_game_moves_on_at_the_clips_end_the_tail_remains()
        {
            // Text at 10.00, clip (5 s) handed over at 10.02; the game waits for it and calls AutoNext
            // at 15.03. Reading (2.5 s) is long done, so what is left is the clip's 0.3 s tail.
            var remaining = ReadingTime.RemainingFor(Short, 10.0, 15.03, 10.02, 5.0);
            Assert.Equal(0.29, remaining, 6);
        }

        [Fact]
        public void A_clip_still_playing_holds_the_text_until_it_ends()
        {
            // Should AutoNext ever come while the clip plays (the game's 30 s timeout), the box stays.
            var remaining = ReadingTime.RemainingFor(Short, 10.0, 12.0, 10.02, 5.0);
            Assert.Equal(3.32, remaining, 6);
        }

        [Fact]
        public void Reading_time_wins_when_it_is_longer_than_the_clip()
        {
            var remaining = ReadingTime.RemainingFor(Long, 10.0, 11.5, 10.02, 1.0);
            Assert.Equal(2.5, remaining, 6);
        }

        [Fact]
        public void A_clip_that_is_not_the_lines_own_changes_nothing()
        {
            var withForeignClip = ReadingTime.RemainingFor(Short, 10.0, 11.0, 4.0, 8.0);
            Assert.Equal(ReadingTime.RemainingFor(Short, 10.0, 11.0), withForeignClip, 6);
            Assert.Equal(1.5, withForeignClip, 6);
        }

        [Fact]
        public void Nothing_is_left_once_both_have_passed()
        {
            Assert.Equal(0d, ReadingTime.RemainingFor(Short, 10.0, 20.0, 10.02, 5.0));
            Assert.Equal(0d, ReadingTime.RemainingFor(Short, 10.0, 20.0, 10.02, double.NaN));
        }

        [Fact]
        public void The_published_constants_match_the_rule()
        {
            Assert.Equal(0.3d, ReadingTime.VoiceTailSeconds);
            Assert.True(ReadingTime.IsUsableClip(8.21));                // the game's longest clip
            Assert.False(ReadingTime.IsUsableClip(0d));
            Assert.False(ReadingTime.IsUsableClip(ReadingTime.MaxClipSeconds + 1d));
        }
    }
}
