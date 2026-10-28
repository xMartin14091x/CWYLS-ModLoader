using System;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The summary line is what a person reads in LogOutput.log after a pack loads, so its shape
    /// is pinned here rather than left to whichever caller formats it next.
    /// </summary>
    public class LoadersLoadReportTests
    {
        [Fact]
        public void An_empty_report_is_not_ok_because_it_proves_nothing()
        {
            var report = new LoadReport();

            Assert.Equal(LoadReport.NothingLoadedSummary, report.Summarize());
            Assert.Equal(0, report.Count);
            Assert.False(report.Ok);
            Assert.Empty(report.Entries);
        }

        [Fact]
        public void A_mixed_run_is_summarised_in_one_line()
        {
            var report = new LoadReport();
            report.Add(LoadEntry.Success("textures/Body/_MainTex.png", AssetKind.Texture, "512x512 RGBA32", ms: 8, bytes: 4096));
            report.Add(LoadEntry.Success("textures/Face/_MainTex.png", AssetKind.Texture, "256x256 RGBA32", ms: 3, bytes: 1024));
            report.Add(LoadEntry.Success("audio/voice/Voice_New_001.ogg", AssetKind.Audio, "1.5s", ms: 10, bytes: 4096));
            report.Add(LoadEntry.Failure("audio/voice/broken.wav", AssetKind.Audio, "24-bit WAV is not supported", ms: 1, bytes: 96));

            Assert.Equal(
                "4 file(s): 3 ok, 1 failed; 2 texture, 2 audio; 9312 bytes; 22 ms",
                report.Summarize());
            Assert.False(report.Ok);
            Assert.Equal(2, report.CountOf(AssetKind.Texture));
            Assert.Equal(2, report.CountOf(AssetKind.Audio));
        }

        [Fact]
        public void Failures_are_listed_in_order_with_their_reasons()
        {
            var report = new LoadReport();
            report.Add(LoadEntry.Success("a.png", AssetKind.Texture));
            report.Add(LoadEntry.Failure("b.png", AssetKind.Texture, "JPEG is not supported"));
            report.Add(LoadEntry.Failure("c.wav", AssetKind.Audio, "no 'data' chunk"));

            var failures = report.Failures;

            Assert.Equal(2, failures.Count);
            Assert.Equal("b.png", failures[0].Path);
            Assert.Equal("c.wav", failures[1].Path);
            Assert.Equal("no 'data' chunk", failures[1].Detail);
            Assert.Equal("FAILED audio 0ms c.wav - no 'data' chunk", failures[1].ToString());
        }

        [Fact]
        public void A_run_where_everything_loaded_is_ok()
        {
            var report = new LoadReport();
            report.Add(LoadEntry.Success("a.png", AssetKind.Texture, ms: 2, bytes: 10));

            Assert.True(report.Ok);
            Assert.Equal("1 file(s): 1 ok, 0 failed; 1 texture, 0 audio; 10 bytes; 2 ms", report.Summarize());

            report.Clear();

            Assert.False(report.Ok);
            Assert.Equal(LoadReport.NothingLoadedSummary, report.Summarize());
        }

        [Fact]
        public void Entries_are_a_detached_copy_and_nulls_are_refused()
        {
            var report = new LoadReport();
            report.Add(LoadEntry.Success("a.png", AssetKind.Texture));
            var first = report.Entries;

            report.Add(LoadEntry.Success("b.png", AssetKind.Texture));

            Assert.Single(first);
            Assert.Equal(2, report.Entries.Count);
            Assert.Throws<ArgumentNullException>(() => report.Add(null!));
            Assert.Throws<ArgumentException>(() => LoadEntry.Success(string.Empty, AssetKind.Texture));
            Assert.Throws<ArgumentOutOfRangeException>(() => LoadEntry.Success("a.png", AssetKind.Texture, ms: -1));
        }
    }
}
