using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>A2 retention IO: archive-first compression, cap deletion, crash leftovers, cancellation.</summary>
    public class TraceRetentionRunnerTests : IDisposable
    {
        private const string Current = "20260928-223512";

        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "cwy-retention-tests-" + Guid.NewGuid().ToString("N"));

        private readonly List<TraceRetentionStep> _steps = new List<TraceRetentionStep>();

        public TraceRetentionRunnerTests() => Directory.CreateDirectory(_directory);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test run over.
            }
        }

        /// <summary>Repetitive JSONL, like a real trace: compresses well and is easy to compare.</summary>
        private static string Jsonl(int lines, string tag)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < lines; i++)
            {
                sb.Append("{\"t\":\"2026-09-24T12:22:09.").Append(i % 1000).Append("+07:00\",\"f\":").Append(i)
                  .Append(",\"sys\":\"Bulbul\",\"cls\":\"ScenarioReader\",\"m\":\"IsPlayingScenario\",\"tag\":\"")
                  .Append(tag).Append("\"}\n");
            }

            return sb.ToString();
        }

        private string Put(string name, string content, DateTime? modifiedUtc = null)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            if (modifiedUtc.HasValue)
            {
                File.SetLastWriteTimeUtc(path, modifiedUtc.Value);
            }

            return path;
        }

        private static string Gunzip(string path)
        {
            using var input = File.OpenRead(path);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private TraceRetentionRunner Runner(TraceRetentionPolicy policy) =>
            new TraceRetentionRunner(_directory, Current, policy, _steps.Add);

        private bool Exists(string name) => File.Exists(Path.Combine(_directory, name));

        [Fact]
        public void Past_parts_are_archived_verified_and_removed_and_everything_else_is_left_alone()
        {
            var study1 = Jsonl(3000, "p1");
            var study2 = Jsonl(2000, "p2");
            var old = Jsonl(500, "old");
            var live = Jsonl(100, "live");
            var sessionTime = new DateTime(2026, 9, 24, 7, 14, 0, DateTimeKind.Utc);

            Put("trace-20260922-140225.jsonl", old);
            Put("trace-20260924-122149.jsonl", study1, sessionTime);
            Put("trace-20260924-122149-part2.jsonl", study2);
            Put("trace-20260928-223512.jsonl", live);
            Put("latest.txt", "trace-20260928-223512.jsonl\n");
            Put("my-notes.txt", "keep me");

            var report = Runner(TraceRetentionPolicy.FromConfig(new TraceConfig())).Run(CancellationToken.None);

            Assert.Equal(3, report.PartsCompressed);
            Assert.Equal(0, report.Failures);
            Assert.Equal(0, report.SessionsDeleted);
            Assert.False(report.Cancelled);
            Assert.True(report.SavedBytes > 0);
            Assert.True(report.CapReached);

            // Archive-first: each original is gone only because its archive holds the same bytes.
            Assert.False(Exists("trace-20260924-122149.jsonl"));
            Assert.False(Exists("trace-20260924-122149-part2.jsonl"));
            Assert.False(Exists("trace-20260922-140225.jsonl"));
            Assert.Equal(study1, Gunzip(Path.Combine(_directory, "trace-20260924-122149.jsonl.gz")));
            Assert.Equal(study2, Gunzip(Path.Combine(_directory, "trace-20260924-122149-part2.jsonl.gz")));
            Assert.Equal(old, Gunzip(Path.Combine(_directory, "trace-20260922-140225.jsonl.gz")));

            // The archive keeps the session's own time.
            Assert.Equal(sessionTime, File.GetLastWriteTimeUtc(Path.Combine(_directory, "trace-20260924-122149.jsonl.gz")));

            // Untouched: the live session, the pointer, a stranger's file. No temp is left behind.
            Assert.Equal(live, File.ReadAllText(Path.Combine(_directory, "trace-20260928-223512.jsonl")));
            Assert.True(Exists("latest.txt"));
            Assert.True(Exists("my-notes.txt"));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));

            Assert.Equal(3, _steps.Count(s => s.Action == TraceRetentionAction.Compressed));
            Assert.All(_steps, s => Assert.True(s.BytesAfter < s.BytesBefore));
        }

        [Fact]
        public void A_second_pass_has_nothing_to_do()
        {
            Put("trace-20260924-122149.jsonl", Jsonl(1000, "p1"));
            var policy = TraceRetentionPolicy.FromConfig(new TraceConfig());

            Runner(policy).Run(CancellationToken.None);
            _steps.Clear();
            var second = Runner(policy).Run(CancellationToken.None);

            Assert.Equal(0, second.PartsCompressed);
            Assert.Equal(0, second.SessionsDeleted);
            Assert.Empty(_steps);
            Assert.Equal("retention: compressed 0 parts, saved 0 MB, deleted 0 sessions", second.Summary());
        }

        [Fact]
        public void An_interrupted_archive_is_cleared_and_the_part_archived_again()
        {
            var content = Jsonl(1500, "p1");
            Put("trace-20260924-122149.jsonl", content);
            Put("trace-20260924-122149.jsonl.gz.tmp", "half an archive");

            var report = Runner(TraceRetentionPolicy.FromConfig(new TraceConfig())).Run(CancellationToken.None);

            Assert.Equal(1, report.TempsRemoved);
            Assert.Equal(1, report.PartsCompressed);
            Assert.False(Exists("trace-20260924-122149.jsonl.gz.tmp"));
            Assert.Equal(content, Gunzip(Path.Combine(_directory, "trace-20260924-122149.jsonl.gz")));
            Assert.Equal(TraceRetentionAction.TempRemoved, _steps[0].Action);
        }

        [Fact]
        public void A_part_whose_archive_already_exists_is_archived_again_then_removed()
        {
            // A crash between renaming the verified archive and deleting the original.
            var content = Jsonl(800, "p1");
            Put("trace-20260924-122149.jsonl", content);
            Put("trace-20260924-122149.jsonl.gz", "stale archive bytes");

            var report = Runner(TraceRetentionPolicy.FromConfig(new TraceConfig())).Run(CancellationToken.None);

            Assert.Equal(1, report.PartsCompressed);
            Assert.False(Exists("trace-20260924-122149.jsonl"));
            Assert.Equal(content, Gunzip(Path.Combine(_directory, "trace-20260924-122149.jsonl.gz")));
        }

        [Fact]
        public void Over_the_cap_whole_oldest_sessions_go_and_the_newest_two_stay()
        {
            // Compression off, 1 KB cap: four past sessions of ~1 KB each plus the live one.
            Put("trace-20260922-140225.jsonl", new string('a', 1000));
            Put("trace-20260923-171615.jsonl", new string('b', 1000));
            Put("trace-20260923-171615-part2.jsonl", new string('b', 1000));
            Put("trace-20260923-180618.jsonl", new string('c', 1000));
            Put("trace-20260924-122149.jsonl", new string('d', 1000));
            Put("trace-20260928-223512.jsonl", new string('e', 100));

            var report = Runner(new TraceRetentionPolicy(1024L, 2, compressClosedParts: false)).Run(CancellationToken.None);

            Assert.Equal(2, report.SessionsDeleted);
            Assert.Equal(3, report.FilesDeleted);
            Assert.Equal(3000L, report.DeletedBytes);
            Assert.False(Exists("trace-20260922-140225.jsonl"));
            Assert.False(Exists("trace-20260923-171615.jsonl"));
            Assert.False(Exists("trace-20260923-171615-part2.jsonl"));

            // Guard: the newest two past sessions stay although the folder is still over 1 KB.
            Assert.True(Exists("trace-20260923-180618.jsonl"));
            Assert.True(Exists("trace-20260924-122149.jsonl"));
            Assert.True(Exists("trace-20260928-223512.jsonl"));
            Assert.Equal(2100L, report.FolderBytes);
            Assert.False(report.CapReached);
            Assert.True(report.NeedsAttention);
            Assert.Equal(3, _steps.Count(s => s.Action == TraceRetentionAction.Deleted));
        }

        [Fact]
        public void A_cancelled_pass_does_nothing_and_says_so()
        {
            var content = Jsonl(500, "p1");
            Put("trace-20260924-122149.jsonl", content);

            using var cancel = new CancellationTokenSource();
            cancel.Cancel();

            var report = Runner(TraceRetentionPolicy.FromConfig(new TraceConfig())).Run(cancel.Token);

            Assert.True(report.Cancelled);
            Assert.Equal(0, report.PartsCompressed);
            Assert.Equal(content, File.ReadAllText(Path.Combine(_directory, "trace-20260924-122149.jsonl")));
            Assert.False(Exists("trace-20260924-122149.jsonl.gz"));
            Assert.EndsWith("(stopped early: quitting)", report.Summary());
        }

        [Fact]
        public void A_missing_folder_is_an_empty_pass()
        {
            var runner = new TraceRetentionRunner(
                Path.Combine(_directory, "does-not-exist"), Current,
                TraceRetentionPolicy.FromConfig(new TraceConfig()), _steps.Add);

            var report = runner.Run(CancellationToken.None);

            Assert.Equal(0, report.PartsCompressed);
            Assert.Equal(0L, report.FolderBytes);
            Assert.Empty(_steps);
        }

        [Fact]
        public void A_step_sink_that_throws_does_not_stop_the_pass()
        {
            Put("trace-20260922-140225.jsonl", Jsonl(100, "a"));
            Put("trace-20260924-122149.jsonl", Jsonl(100, "b"));

            var runner = new TraceRetentionRunner(
                _directory, Current, TraceRetentionPolicy.FromConfig(new TraceConfig()),
                _ => throw new InvalidOperationException("sink broke"));

            var report = runner.Run(CancellationToken.None);

            Assert.Equal(2, report.PartsCompressed);
        }

        [Fact]
        public void Verify_rejects_a_wrong_length_a_wrong_checksum_and_a_truncated_archive()
        {
            var source = Put("trace-20260924-122149.jsonl", Jsonl(2000, "v"));
            var bytes = File.ReadAllBytes(source);
            var crc = Crc32.Update(Crc32.Initial, bytes, 0, bytes.Length);
            var archive = Path.Combine(_directory, "manual.gz");

            using (var output = File.Create(archive))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }

            TraceArchive.Verify(archive, bytes.Length, crc, CancellationToken.None);

            Assert.Throws<InvalidDataException>(() =>
                TraceArchive.Verify(archive, bytes.Length + 1, crc, CancellationToken.None));
            Assert.Throws<InvalidDataException>(() =>
                TraceArchive.Verify(archive, bytes.Length, crc ^ 1u, CancellationToken.None));

            var whole = File.ReadAllBytes(archive);
            File.WriteAllBytes(archive, whole.Take(whole.Length / 2).ToArray());
            Assert.ThrowsAny<Exception>(() => TraceArchive.Verify(archive, bytes.Length, crc, CancellationToken.None));
        }

        [Fact]
        public void Crc32_matches_the_standard_check_value()
        {
            Assert.Equal(0xCBF43926u, Crc32.Compute(Encoding.ASCII.GetBytes("123456789")));
            Assert.Equal(0u, Crc32.Compute(Array.Empty<byte>()));
        }

        [Fact]
        public void The_summary_is_one_console_primary()
        {
            var report = new TraceRetentionReport();
            Assert.Equal("retention: compressed 0 parts, saved 0 MB, deleted 0 sessions", report.Summary());
            Assert.DoesNotContain("  ", report.Summary());
            Assert.Equal("651", TraceRetentionReport.WholeMegabytes(651L * TraceRetentionPolicy.BytesPerMB + 1000L));
        }

        [Fact]
        public void The_runner_refuses_an_invalid_stamp()
        {
            Assert.Throws<ArgumentException>(() => new TraceRetentionRunner(
                _directory, "not-a-stamp", TraceRetentionPolicy.FromConfig(new TraceConfig()), _steps.Add));
        }
    }
}
