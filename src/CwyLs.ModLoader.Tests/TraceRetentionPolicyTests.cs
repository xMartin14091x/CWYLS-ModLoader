using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// A2 retention policy against the real trace folder as it stood on 2026-09-28 (27 past sessions,
    /// 1 511.9 MB; the 2026-09-24 session is four parts, 715.6 MB), plus a live session.
    /// </summary>
    public class TraceRetentionPolicyTests
    {
        private const long MB = TraceRetentionPolicy.BytesPerMB;
        private const string Current = "20260928-223512";
        private const string CurrentFile = "trace-20260928-223512.jsonl";

        /// <summary><c>stat -f '%N %z' trace-*.jsonl</c> of BepInEx/trace, 2026-09-28.</summary>
        private static readonly (string Name, long Bytes)[] RealPast =
        {
            ("trace-20260922-140225.jsonl", 358L),
            ("trace-20260922-141016.jsonl", 51695521L),
            ("trace-20260922-141605.jsonl", 46622721L),
            ("trace-20260922-142126.jsonl", 15612850L),
            ("trace-20260922-143505.jsonl", 11071490L),
            ("trace-20260922-143804.jsonl", 2868154L),
            ("trace-20260922-143915.jsonl", 23951121L),
            ("trace-20260922-144654.jsonl", 19891375L),
            ("trace-20260922-145359.jsonl", 2112418L),
            ("trace-20260922-145515.jsonl", 28342882L),
            ("trace-20260922-150532.jsonl", 25255744L),
            ("trace-20260922-151434.jsonl", 192183496L),
            ("trace-20260922-162051.jsonl", 21815626L),
            ("trace-20260922-162835.jsonl", 6788115L),
            ("trace-20260922-163146.jsonl", 5620951L),
            ("trace-20260922-163451.jsonl", 85756459L),
            ("trace-20260922-173429.jsonl", 10529909L),
            ("trace-20260922-173919.jsonl", 4422192L),
            ("trace-20260922-174303.jsonl", 35063546L),
            ("trace-20260922-180223.jsonl", 5165696L),
            ("trace-20260922-180449.jsonl", 71002569L),
            ("trace-20260922-184143.jsonl", 54958490L),
            ("trace-20260923-171615.jsonl", 12277616L),
            ("trace-20260923-172224.jsonl", 81374653L),
            ("trace-20260923-180618.jsonl", 17430449L),
            ("trace-20260924-122149-part2.jsonl", 209724671L),
            ("trace-20260924-122149-part3.jsonl", 209717068L),
            ("trace-20260924-122149-part4.jsonl", 121183167L),
            ("trace-20260924-122149.jsonl", 209724713L),
        };

        /// <summary>The real folder, the live session (3 MB so far), the pointer file and a stranger's file.</summary>
        private static List<TraceFile> RealFolder()
        {
            var files = RealPast.Select(f => new TraceFile(f.Name, f.Bytes)).ToList();
            files.Add(new TraceFile(CurrentFile, 3 * MB));
            files.Add(new TraceFile("latest.txt", 34L));
            files.Add(new TraceFile("notes-about-the-cutscene.txt", 5000L));
            return files;
        }

        private static TraceRetentionPolicy Defaults() =>
            TraceRetentionPolicy.FromConfig(new TraceConfig());

        [Fact]
        public void Defaults_are_1024_MB_two_sessions_and_compression_on()
        {
            var policy = Defaults();

            Assert.Equal(1024L * MB, policy.MaxTotalBytes);
            Assert.Equal(2, policy.MinSessions);
            Assert.True(policy.CompressClosedParts);
        }

        [Fact]
        public void Sessions_group_by_stamp_oldest_first_with_parts_in_order()
        {
            var sessions = TraceRetentionPolicy.Sessions(RealFolder());

            Assert.Equal(27, sessions.Count);
            Assert.Equal("20260922-140225", sessions[0].Stamp);
            Assert.Equal(Current, sessions[sessions.Count - 1].Stamp);

            var study = sessions.Single(s => s.Stamp == "20260924-122149");
            Assert.Equal(
                new[]
                {
                    "trace-20260924-122149.jsonl",
                    "trace-20260924-122149-part2.jsonl",
                    "trace-20260924-122149-part3.jsonl",
                    "trace-20260924-122149-part4.jsonl",
                },
                study.Files.Select(f => f.Name).ToArray());
            Assert.Equal(750349619L, study.Bytes);
        }

        [Fact]
        public void First_run_with_defaults_compresses_every_past_part_and_nothing_of_the_live_session()
        {
            var plan = Defaults().PlanCompression(RealFolder(), Current);

            Assert.Equal(29, plan.ToCompress.Count);
            Assert.Empty(plan.StaleTemps);
            Assert.DoesNotContain(plan.ToCompress, f => f.Name == CurrentFile);
            Assert.DoesNotContain(plan.ToCompress, f => f.Name == "latest.txt");
            Assert.Equal("trace-20260922-140225.jsonl", plan.ToCompress[0].Name);
            Assert.Equal(
                new[]
                {
                    "trace-20260924-122149.jsonl",
                    "trace-20260924-122149-part2.jsonl",
                    "trace-20260924-122149-part3.jsonl",
                    "trace-20260924-122149-part4.jsonl",
                },
                plan.ToCompress.Skip(25).Select(f => f.Name).ToArray());
        }

        [Fact]
        public void First_run_with_defaults_deletes_nothing_once_the_parts_are_compressed()
        {
            // Compressed JSONL is roughly a tenth of the original: 1 512 MB of past parts -> ~151 MB.
            var afterStep1 = RealPast
                .Select(f => new TraceFile(f.Name + ".gz", f.Bytes / 10))
                .Concat(new[] { new TraceFile(CurrentFile, 3 * MB), new TraceFile("latest.txt", 34L) })
                .ToList();

            var plan = Defaults().PlanDeletion(afterStep1, Current);

            Assert.Empty(plan.ToDelete);
            Assert.True(plan.CapReached);
            Assert.Equal(161362117L, plan.BytesBefore);
            Assert.Equal(plan.BytesBefore, plan.BytesAfter);

            // Every 2026-09-24 part is still there, as an archive.
            Assert.Equal(4, TraceRetentionPolicy.Sessions(afterStep1).Single(s => s.Stamp == "20260924-122149").Files.Count);
        }

        [Fact]
        public void Over_the_cap_deletes_whole_oldest_sessions_until_it_fits()
        {
            // Compression off, so the uncompressed 1 515 MB (live session included) meets the 1 024 MB cap.
            var policy = new TraceRetentionPolicy(1024L * MB, 2, compressClosedParts: false);

            var compression = policy.PlanCompression(RealFolder(), Current);
            Assert.Empty(compression.ToCompress);

            var plan = policy.PlanDeletion(RealFolder(), Current);

            Assert.Equal(1585309748L, plan.BytesBefore);
            Assert.Equal(
                new[]
                {
                    "20260922-140225", "20260922-141016", "20260922-141605", "20260922-142126",
                    "20260922-143505", "20260922-143804", "20260922-143915", "20260922-144654",
                    "20260922-145359", "20260922-145515", "20260922-150532", "20260922-151434",
                    "20260922-162051", "20260922-162835", "20260922-163146", "20260922-163451",
                },
                plan.ToDelete.Select(s => s.Stamp).ToArray());
            Assert.Equal(1045720467L, plan.BytesAfter);
            Assert.True(plan.BytesAfter <= 1024L * MB);
            Assert.True(plan.CapReached);
            Assert.DoesNotContain(plan.ToDelete, s => s.Stamp == "20260924-122149");
            Assert.DoesNotContain(plan.ToDelete, s => s.Stamp == Current);
        }

        [Fact]
        public void The_minimum_sessions_guard_keeps_the_newest_two_even_when_the_cap_is_not_met()
        {
            var policy = new TraceRetentionPolicy(1L * MB, 2, compressClosedParts: false);

            var plan = policy.PlanDeletion(RealFolder(), Current);

            Assert.Equal(24, plan.ToDelete.Count);
            Assert.Equal("20260923-172224", plan.ToDelete[plan.ToDelete.Count - 1].Stamp);
            Assert.DoesNotContain(plan.ToDelete, s => s.Stamp == "20260923-180618");
            Assert.DoesNotContain(plan.ToDelete, s => s.Stamp == "20260924-122149");
            Assert.Equal(2, plan.ProtectedPastSessions);
            Assert.Equal(770925796L, plan.BytesAfter);
            Assert.False(plan.CapReached);
        }

        [Fact]
        public void With_no_more_past_sessions_than_the_minimum_nothing_is_deleted()
        {
            var files = new[]
            {
                new TraceFile("trace-20260923-180618.jsonl", 900L * MB),
                new TraceFile("trace-20260924-122149.jsonl", 900L * MB),
                new TraceFile(CurrentFile, 10L * MB),
            };

            var plan = new TraceRetentionPolicy(1024L * MB, 2, false).PlanDeletion(files, Current);

            Assert.Empty(plan.ToDelete);
            Assert.False(plan.CapReached);
        }

        [Fact]
        public void A_zero_cap_disables_deletion()
        {
            var policy = new TraceRetentionPolicy(0L, 2, compressClosedParts: false);

            var plan = policy.PlanDeletion(RealFolder(), Current);

            Assert.Empty(plan.ToDelete);
            Assert.True(plan.CapReached);
            Assert.Equal(0L, plan.CapBytes);
            Assert.Equal(0L, TraceRetentionPolicy.FromConfig(new TraceConfig { RetainMaxTotalMB = -5 }).MaxTotalBytes);
        }

        [Fact]
        public void The_live_session_and_anything_stamped_later_are_never_planned()
        {
            var files = new[]
            {
                new TraceFile("trace-20260924-122149.jsonl", 500L * MB),
                new TraceFile("trace-20260928-223512.jsonl", 200L * MB),       // live part 1, already rolled over
                new TraceFile("trace-20260928-223512-part2.jsonl", 900L * MB), // live part 2
                new TraceFile("trace-20260929-080000.jsonl", 300L * MB),       // a later stamp: another instance
            };

            var policy = new TraceRetentionPolicy(1L * MB, 0, compressClosedParts: true);
            var compression = policy.PlanCompression(files, Current);
            var deletion = policy.PlanDeletion(files, Current);

            Assert.Equal(new[] { "trace-20260924-122149.jsonl" }, compression.ToCompress.Select(f => f.Name).ToArray());
            Assert.Equal(new[] { "20260924-122149" }, deletion.ToDelete.Select(s => s.Stamp).ToArray());
            Assert.False(deletion.CapReached);
        }

        [Fact]
        public void Archives_are_not_compressed_again()
        {
            var files = new[]
            {
                new TraceFile("trace-20260924-122149.jsonl.gz", 20L * MB),
                new TraceFile("trace-20260924-122149-part2.jsonl", 200L * MB),
            };

            var plan = Defaults().PlanCompression(files, Current);

            Assert.Equal(new[] { "trace-20260924-122149-part2.jsonl" }, plan.ToCompress.Select(f => f.Name).ToArray());
        }

        [Fact]
        public void A_temp_is_stale_only_while_its_original_still_exists()
        {
            var files = new[]
            {
                new TraceFile("trace-20260924-122149.jsonl", 200L * MB),
                new TraceFile("trace-20260924-122149.jsonl.gz.tmp", 5L * MB),        // interrupted: original present
                new TraceFile("trace-20260924-122149-part2.jsonl.gz.tmp", 20L * MB), // original gone: may be the last copy
                new TraceFile("trace-20260928-223512.jsonl.gz.tmp", 1L * MB),        // live session: never touched
            };

            var plan = Defaults().PlanCompression(files, Current);

            Assert.Equal(new[] { "trace-20260924-122149.jsonl.gz.tmp" }, plan.StaleTemps.Select(f => f.Name).ToArray());
        }

        [Fact]
        public void Temps_and_archives_count_toward_the_cap()
        {
            var files = new[]
            {
                new TraceFile("trace-20260922-140225.jsonl.gz", 600L * MB),
                new TraceFile("trace-20260923-171615.jsonl.gz.tmp", 600L * MB),
                new TraceFile("trace-20260924-122149.jsonl.gz", 10L * MB),
            };

            var plan = new TraceRetentionPolicy(1024L * MB, 1, false).PlanDeletion(files, Current);

            Assert.Equal(new[] { "20260922-140225" }, plan.ToDelete.Select(s => s.Stamp).ToArray());
        }

        [Theory]
        [InlineData("")]
        [InlineData("2026-09-28")]
        [InlineData("20260928_223512")]
        [InlineData("trace-20260928-223512")]
        public void An_invalid_current_stamp_is_refused(string stamp)
        {
            var policy = Defaults();

            Assert.Throws<ArgumentException>(() => policy.PlanCompression(RealFolder(), stamp));
            Assert.Throws<ArgumentException>(() => policy.PlanDeletion(RealFolder(), stamp));
        }

        [Theory]
        [InlineData("trace-20260924-122149.jsonl", "20260924-122149", 1, TraceFileKind.Jsonl)]
        [InlineData("trace-20260924-122149-part2.jsonl", "20260924-122149", 2, TraceFileKind.Jsonl)]
        [InlineData("trace-20260924-122149-part14.jsonl.gz", "20260924-122149", 14, TraceFileKind.Gzip)]
        [InlineData("trace-20260924-122149-part4.jsonl.gz.tmp", "20260924-122149", 4, TraceFileKind.GzipTemp)]
        public void File_names_the_writer_and_retention_produce_parse(string name, string stamp, int part, TraceFileKind kind)
        {
            Assert.True(TraceFileName.TryParse(name, out var parsed));
            Assert.NotNull(parsed);
            Assert.Equal(stamp, parsed!.Stamp);
            Assert.Equal(part, parsed.Part);
            Assert.Equal(kind, parsed.Kind);
        }

        [Theory]
        [InlineData("latest.txt")]
        [InlineData("trace-.jsonl")]
        [InlineData("trace-foo.jsonl")]
        [InlineData("trace-20260924-12214.jsonl")]
        [InlineData("trace-20260924-122149-part1.jsonl")]
        [InlineData("trace-20260924-122149-part0.jsonl")]
        [InlineData("trace-20260924-122149-part02.jsonl")]
        [InlineData("trace-20260924-122149-partX.jsonl")]
        [InlineData("trace-20260924-122149.jsonl.bak")]
        [InlineData("trace-20260924-122149.json")]
        [InlineData("Trace-20260924-122149.jsonl")]
        [InlineData("cwy-console.log")]
        [InlineData(null)]
        public void Anything_else_is_not_a_trace_file(string? name)
        {
            Assert.False(TraceFileName.TryParse(name, out var parsed));
            Assert.Null(parsed);
        }

        [Fact]
        public void Names_derive_from_the_part()
        {
            Assert.True(TraceFileName.TryParse("trace-20260924-122149-part3.jsonl.gz.tmp", out var parsed));

            Assert.Equal("trace-20260924-122149-part3.jsonl", parsed!.JsonlName);
            Assert.Equal("trace-20260924-122149-part3.jsonl.gz", parsed.ArchiveName);
            Assert.Equal("trace-20260924-122149-part3.jsonl.gz.tmp", parsed.TempName);
            Assert.Equal("trace-20260928-223512.jsonl", TraceFileName.SessionFileName(Current));
            Assert.Equal(Current, TraceFileName.StampFor(new DateTime(2026, 9, 28, 22, 35, 12)));
            Assert.Throws<ArgumentException>(() => TraceFileName.SessionFileName("nope"));
        }
    }
}
