using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Buffers trace lines in memory, writes them out in blocks, and rolls over to a new part once
    /// the current file gets too large. A trace at full level produces thousands of lines a second;
    /// one file write per line would cost more frame time than the Harmony prefixes themselves.
    /// Every member is guarded by one lock, because calls arrive from whichever thread the game
    /// happened to be on.
    /// </summary>
    public sealed class BufferedFileTraceWriter : ITraceWriter
    {
        /// <summary>Bytes buffered before an automatic flush.</summary>
        public const int DefaultFlushThresholdBytes = 64 * 1024;

        /// <summary>Name of the tiny file naming the part currently being written.</summary>
        public const string PointerFileName = "latest.txt";

        private readonly object _gate = new object();
        private readonly StringBuilder _buffer = new StringBuilder(DefaultFlushThresholdBytes);
        private readonly int _flushThresholdBytes;
        private readonly long _maxBytesPerFile;
        private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private readonly string _directory;
        private readonly string _stem;
        private readonly string _extension;

        private StreamWriter? _writer;
        private int _pendingBytes;
        private long _bytesInPart;
        private long _linesWritten;
        private int _part = 1;
        private bool _disposed;

        /// <summary>Opens (and truncates) the first part of the trace file, creating its directory if needed.</summary>
        /// <param name="path">Full path of the .jsonl file; later parts get a <c>-partN</c> suffix.</param>
        /// <param name="flushThresholdBytes">Bytes to buffer before flushing. Values below 1 flush every line.</param>
        /// <param name="maxBytesPerFile">Bytes per part before rolling over. 0 or less never rotates.</param>
        public BufferedFileTraceWriter(
            string path,
            int flushThresholdBytes = DefaultFlushThresholdBytes,
            long maxBytesPerFile = 0L)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            _flushThresholdBytes = flushThresholdBytes;
            _maxBytesPerFile = maxBytesPerFile;
            // Fully qualified: the Path property of this class shadows System.IO.Path here.
            _directory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
            _stem = System.IO.Path.GetFileNameWithoutExtension(path);
            _extension = System.IO.Path.GetExtension(path);

            if (!string.IsNullOrEmpty(_directory))
            {
                Directory.CreateDirectory(_directory);
            }

            Path = path;
            Open(path);
        }

        /// <summary>Full path of the part being written right now.</summary>
        public string Path { get; private set; }

        /// <summary>Which part is being written; 1 until the first rollover.</summary>
        public int Part
        {
            get
            {
                lock (_gate)
                {
                    return _part;
                }
            }
        }

        /// <inheritdoc />
        public long LinesWritten
        {
            get
            {
                lock (_gate)
                {
                    return _linesWritten;
                }
            }
        }

        /// <summary>Bytes currently buffered and not yet on disk.</summary>
        public int PendingBytes
        {
            get
            {
                lock (_gate)
                {
                    return _pendingBytes;
                }
            }
        }

        /// <inheritdoc />
        public void Write(string line)
        {
            if (line is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed || _writer is null)
                {
                    return;
                }

                _buffer.Append(line).Append('\n');
                _pendingBytes += _encoding.GetByteCount(line) + 1;
                _linesWritten++;

                if (_pendingBytes >= _flushThresholdBytes)
                {
                    FlushLocked();
                }
            }
        }

        /// <inheritdoc />
        public void Flush()
        {
            lock (_gate)
            {
                FlushLocked();
            }
        }

        private void FlushLocked()
        {
            if (_writer is null || _buffer.Length == 0)
            {
                return;
            }

            var bytes = _pendingBytes;

            try
            {
                _writer.Write(_buffer.ToString());
                _writer.Flush();
                _bytesInPart += bytes;
            }
            catch (Exception ex)
            {
                // A full disk or a file the user deleted mid-session must not crash the game.
                TraceDiagnostics.Report($"TRACE: could not write {Path}; dropping {bytes} buffered bytes", ex);
            }
            finally
            {
                _buffer.Length = 0;
                _pendingBytes = 0;
            }

            // Rotate on a flush boundary only, so a line is never split across two parts.
            if (_maxBytesPerFile > 0 && _bytesInPart >= _maxBytesPerFile)
            {
                RotateLocked();
            }
        }

        private void RotateLocked()
        {
            var previous = Path;

            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report($"TRACE: closing {previous} before rotating threw", ex);
            }
            finally
            {
                _writer = null;
            }

            _part++;
            var next = System.IO.Path.Combine(
                _directory,
                _stem + "-part" + _part.ToString(CultureInfo.InvariantCulture) + _extension);

            _bytesInPart = 0;
            Path = next;
            Open(next);

            TraceDiagnostics.Report($"TRACE: {previous} reached {_maxBytesPerFile} bytes; continuing in {next}");
        }

        private void Open(string path)
        {
            try
            {
                // FileShare.ReadWrite so the user can tail the file while the game holds it open.
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, _encoding) { AutoFlush = false };
            }
            catch (Exception ex)
            {
                // Without a file the trace is inert but the game is untouched, which is the point.
                _writer = null;
                TraceDiagnostics.Report($"TRACE: could not open {path}", ex);
                return;
            }

            WritePointer(path);
        }

        /// <summary>
        /// Records which part is current in a one-line text file. A symlink would be nicer on macOS
        /// and unusable on Windows, so both platforms get the same plain file.
        /// </summary>
        private void WritePointer(string path)
        {
            if (string.IsNullOrEmpty(_directory))
            {
                return;
            }

            try
            {
                File.WriteAllText(
                    System.IO.Path.Combine(_directory, PointerFileName),
                    System.IO.Path.GetFileName(path) + "\n",
                    _encoding);
            }
            catch (Exception ex)
            {
                // Losing the pointer costs the reader some convenience and nothing else.
                TraceDiagnostics.Report($"TRACE: could not update {PointerFileName}", ex);
            }
        }

        /// <summary>Flushes and closes the file. Safe to call twice.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                // Flush what is left, but never rotate on the way out.
                var writer = _writer;
                if (writer != null && _buffer.Length > 0)
                {
                    try
                    {
                        writer.Write(_buffer.ToString());
                        writer.Flush();
                    }
                    catch (Exception ex)
                    {
                        TraceDiagnostics.Report($"TRACE: final flush of {Path} failed", ex);
                    }
                    finally
                    {
                        _buffer.Length = 0;
                        _pendingBytes = 0;
                    }
                }

                _disposed = true;

                try
                {
                    _writer?.Dispose();
                }
                catch (Exception ex)
                {
                    TraceDiagnostics.Report($"TRACE: closing {Path} threw", ex);
                }
                finally
                {
                    _writer = null;
                }
            }
        }
    }
}
