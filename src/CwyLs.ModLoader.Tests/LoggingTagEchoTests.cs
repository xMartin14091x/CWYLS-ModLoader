using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C5: a primary that repeats its own tag ("PACKS: 2 loaded ...") printed as
    /// <c>PACKS  PACKS: 2 loaded</c>. The console now drops the echo; the payload keeps it.
    /// </summary>
    public class LoggingTagEchoTests
    {
        private static readonly DateTimeOffset At = new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.FromHours(7));

        [Theory]
        [InlineData("PACKS", "PACKS: 2 loaded; 0 skipped", "2 loaded; 0 skipped")]
        [InlineData("PACKS", "PACKS:2 loaded", "2 loaded")]
        [InlineData("PACKS", "PACKS:    2 loaded", "2 loaded")]
        public void A_leading_tag_and_colon_is_dropped(string tag, string primary, string expected)
        {
            Assert.Equal(expected, ConsoleEventMap.WithoutTagEcho(tag, primary));
        }

        [Theory]
        [InlineData("PACKS", "1 loaded (cwy.example@0.1.0); 0 skipped")]   // no echo
        [InlineData("PACKS", "packs: 2 loaded")]                          // case differs
        [InlineData("PACKS", "PACKSX: 2 loaded")]                         // a longer word
        [InlineData("PACKS", "PACKS 2 loaded")]                           // no colon
        [InlineData("PACKS", "PACKS:")]                                   // nothing would be left
        [InlineData("PACKS", "PACKS:   ")]                                // nothing would be left
        [InlineData("DATA", "PACKS: 2 loaded")]                           // another tag's text
        [InlineData("PACKS", "")]
        [InlineData("PACKS", null)]
        [InlineData("", "PACKS: 2 loaded")]
        public void Anything_else_is_left_alone(string tag, string? primary)
        {
            Assert.Equal(primary, ConsoleEventMap.WithoutTagEcho(tag, primary));
        }

        [Fact]
        public void The_mapped_record_carries_the_trimmed_primary_and_the_bus_keeps_the_original()
        {
            var payload = new Dictionary<string, object?>
            {
                [ConsolePayload.TagKey] = ConsoleTags.Packs,
                [ConsolePayload.PrimaryKey] = "PACKS: 2 loaded (cwy.example@0.1.0, my.thai@0.1.0); 0 skipped",
            };

            Assert.True(ConsoleEventMap.TryMap(new BusEvent("packs", "scan", payload, At), out var record));
            Assert.Equal("2 loaded (cwy.example@0.1.0, my.thai@0.1.0); 0 skipped", record!.Primary);
            Assert.Equal(
                "PACKS: 2 loaded (cwy.example@0.1.0, my.thai@0.1.0); 0 skipped",
                payload[ConsolePayload.PrimaryKey]);
        }
    }
}
