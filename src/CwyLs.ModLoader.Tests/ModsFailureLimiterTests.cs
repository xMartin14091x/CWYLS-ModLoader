using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FIX (review #10): failure and drop lines are limited per bounded site - owner, stage and
    /// the first word of the label - and the site table cannot grow without end.
    /// </summary>
    public class ModsFailureLimiterTests
    {
        [Theory]
        [InlineData("chunk 17", "chunk")]
        [InlineData("LoadAudioAsync assets/a.ogg", "LoadAudioAsync")]
        [InlineData("face smile", "face")]
        [InlineData("OnModsReady", "OnModsReady")]
        [InlineData("look 0.5", "look")]
        [InlineData("tick2", "tick")]
        [InlineData("say 'hi'", "say")]
        [InlineData("a:b", "a")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void The_label_key_is_the_first_word(string? label, string expected) =>
            Assert.Equal(expected, ModFailureLimiter.LabelKey(label));

        [Fact]
        public void The_label_key_is_bounded()
        {
            Assert.Equal(ModFailureLimiter.LabelKeyLength, ModFailureLimiter.LabelKey(new string('x', 500)).Length);
        }

        [Fact]
        public void Labels_that_carry_data_share_one_site()
        {
            var limiter = new ModFailureLimiter(10);

            Assert.True(limiter.ShouldLog(ModFailureLimiter.SiteKey("mod:x", "work", "chunk 1"), 0, out _));
            Assert.False(limiter.ShouldLog(ModFailureLimiter.SiteKey("mod:x", "work", "chunk 2"), 1, out _));
            Assert.False(limiter.ShouldLog(ModFailureLimiter.SiteKey("mod:x", "work", "chunk 3"), 2, out var quiet));
            Assert.Equal(2, quiet);
            Assert.Equal(1, limiter.SiteCount);
            Assert.Equal("mod:x|work|chunk", ModFailureLimiter.SiteKey("mod:x", "work", "chunk 99"));
            Assert.Equal("||", ModFailureLimiter.SiteKey(null, null, null));
        }

        [Fact]
        public void A_full_table_prunes_idle_sites_first()
        {
            var limiter = new ModFailureLimiter(10, maxSites: 2);
            limiter.ShouldLog("a", 0, out _);
            limiter.ShouldLog("b", 0, out _);

            Assert.True(limiter.ShouldLog("c", 20, out _)); // a and b are idle: forgotten
            Assert.Equal(1, limiter.SiteCount);
        }

        [Fact]
        public void A_full_table_of_busy_sites_sends_new_ones_to_one_overflow_site()
        {
            var limiter = new ModFailureLimiter(10, maxSites: 2);
            limiter.ShouldLog("a", 0, out _);
            limiter.ShouldLog("b", 0, out _);

            Assert.True(limiter.ShouldLog("c", 1, out _));  // overflow site, first line
            Assert.False(limiter.ShouldLog("d", 2, out _)); // same overflow site, quiet
            Assert.False(limiter.ShouldLog("a", 3, out _)); // a keeps its own site
            Assert.Equal(3, limiter.SiteCount);
            Assert.Equal(2, limiter.MaxSites);
        }

        [Fact]
        public void A_site_with_swallowed_repeats_is_never_pruned()
        {
            var limiter = new ModFailureLimiter(10, maxSites: 1);
            limiter.ShouldLog("a", 0, out _);
            limiter.ShouldLog("a", 1, out _); // one swallowed

            limiter.ShouldLog("b", 50, out _); // a is past its window but still owes a count
            Assert.True(limiter.ShouldLog("a", 51, out var carried));
            Assert.Equal(1, carried);
        }
    }
}
