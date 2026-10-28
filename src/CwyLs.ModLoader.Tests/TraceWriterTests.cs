using System;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceWriterTests : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), "cwy-trace-tests-" + Guid.NewGuid().ToString("N"));

        private string NewPath() => Path.Combine(_directory, "nested", "trace-test.jsonl");

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

        private static string ReadAll(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        [Fact]
        public void Creates_its_directory_and_buffers_until_the_threshold()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path, flushThresholdBytes: 64);

            writer.Write("{\"a\":1}");

            Assert.True(File.Exists(path));
            Assert.Equal(8, writer.PendingBytes);
            Assert.Equal(1L, writer.LinesWritten);
            Assert.Equal(string.Empty, ReadAll(path));
        }

        [Fact]
        public void Flushes_by_itself_once_the_threshold_is_crossed()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path, flushThresholdBytes: 32);

            writer.Write(new string('a', 20));
            Assert.Equal(string.Empty, ReadAll(path));

            writer.Write(new string('b', 20));

            Assert.Equal(0, writer.PendingBytes);
            Assert.Equal(new string('a', 20) + "\n" + new string('b', 20) + "\n", ReadAll(path));
        }

        [Fact]
        public void Flush_writes_whatever_is_pending()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path);

            writer.Write("one");
            writer.Write("two");
            Assert.Equal(string.Empty, ReadAll(path));

            writer.Flush();

            Assert.Equal("one\ntwo\n", ReadAll(path));
            Assert.Equal(0, writer.PendingBytes);
            Assert.Equal(2L, writer.LinesWritten);
        }

        [Fact]
        public void Byte_accounting_counts_utf8_not_characters()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path, flushThresholdBytes: 1024);

            writer.Write("さとね");

            // 3 characters, 9 UTF-8 bytes, plus the newline.
            Assert.Equal(10, writer.PendingBytes);
        }

        [Fact]
        public void Dispose_flushes_and_then_ignores_further_writes()
        {
            var path = NewPath();
            var writer = new BufferedFileTraceWriter(path);

            writer.Write("line");
            writer.Dispose();
            writer.Write("after");
            writer.Dispose();

            Assert.Equal("line\n", ReadAll(path));
        }

        [Fact]
        public void The_pointer_file_names_the_part_being_written()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path);

            var pointer = Path.Combine(Path.GetDirectoryName(path)!, BufferedFileTraceWriter.PointerFileName);

            Assert.True(File.Exists(pointer));
            Assert.Equal("trace-test.jsonl", ReadAll(pointer).Trim());
            Assert.Equal(1, writer.Part);
        }

        [Fact]
        public void Rotates_to_a_new_part_once_the_size_limit_is_passed()
        {
            var path = NewPath();
            var directory = Path.GetDirectoryName(path)!;

            // Flush every line so each write is a rotation opportunity.
            using var writer = new BufferedFileTraceWriter(path, flushThresholdBytes: 1, maxBytesPerFile: 30);

            writer.Write(new string('a', 20));
            Assert.Equal(1, writer.Part);

            writer.Write(new string('b', 20));

            Assert.Equal(2, writer.Part);
            Assert.Equal(Path.Combine(directory, "trace-test-part2.jsonl"), writer.Path);
            Assert.True(File.Exists(Path.Combine(directory, "trace-test-part2.jsonl")));

            // Part 1 keeps both of its lines; nothing is split across the boundary.
            Assert.Equal(new string('a', 20) + "\n" + new string('b', 20) + "\n", ReadAll(path));

            writer.Write("third");
            writer.Flush();
            Assert.Equal("third\n", ReadAll(Path.Combine(directory, "trace-test-part2.jsonl")));

            // The pointer follows the rotation.
            Assert.Equal(
                "trace-test-part2.jsonl",
                ReadAll(Path.Combine(directory, BufferedFileTraceWriter.PointerFileName)).Trim());

            // The line counter spans the whole session, not one part.
            Assert.Equal(3L, writer.LinesWritten);
        }

        [Fact]
        public void A_zero_size_limit_never_rotates()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path, flushThresholdBytes: 1, maxBytesPerFile: 0);

            for (var i = 0; i < 50; i++)
            {
                writer.Write(new string('x', 100));
            }

            Assert.Equal(1, writer.Part);
            Assert.Equal(path, writer.Path);
        }

        [Fact]
        public void Dispose_does_not_rotate_on_the_way_out()
        {
            var path = NewPath();
            var directory = Path.GetDirectoryName(path)!;
            var writer = new BufferedFileTraceWriter(path, maxBytesPerFile: 10);

            writer.Write(new string('a', 40));
            writer.Dispose();

            Assert.Equal(1, writer.Part);
            Assert.False(File.Exists(Path.Combine(directory, "trace-test-part2.jsonl")));
        }

        [Fact]
        public void A_null_line_is_ignored_rather_than_fatal()
        {
            var path = NewPath();
            using var writer = new BufferedFileTraceWriter(path);

            writer.Write(null!);

            Assert.Equal(0L, writer.LinesWritten);
        }
    }
}
