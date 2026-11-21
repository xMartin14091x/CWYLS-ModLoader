using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>
    /// A minimal ZIP writer (PKWARE APPNOTE 6.3: local headers, central directory, end record; UTF-8 names; no ZIP64)
    /// for the export. It uses <see cref="DeflateStream"/>, the type the trace archive already runs on in the game,
    /// and not <c>ZipArchive</c>, whose assembly Unity's Mono may not ship. Callers keep the total under 2 GiB.
    /// </summary>
    public sealed class MakerZipWriter : IDisposable
    {
        internal const int MaxEntries = ushort.MaxValue;
        private static readonly uint[] Table = BuildTable();
        private readonly Stream _out;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly ushort _time;
        private readonly ushort _date;

        /// <summary>Starts a zip on <paramref name="output"/>; every entry is stamped with <paramref name="stamp"/> (local time).</summary>
        public MakerZipWriter(Stream output, DateTime stamp)
        {
            _out = output ?? throw new ArgumentNullException(nameof(output));
            var t = stamp.Year < 1980 ? new DateTime(1980, 1, 1) : stamp;
            _time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            _date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        }

        /// <summary>Entries written so far.</summary>
        public int Count => _entries.Count;

        /// <summary>Adds one file. <paramref name="deflate"/> false stores it (audio and images are compressed already).</summary>
        public void Add(string name, byte[] bytes, bool deflate)
        {
            using var input = new MemoryStream(bytes, writable: false);
            AddStream(name, input, deflate);
        }

        internal void AddStream(string name, Stream input, bool deflate)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name[0] == '/') throw new ArgumentException("zip entry names are relative with '/'", nameof(name));
            if (_entries.Count >= MaxEntries) throw new IOException("a non-ZIP64 export may contain at most 65535 entries");
            var encoded = Encoding.UTF8.GetBytes(name);
            if (encoded.Length > ushort.MaxValue) throw new IOException("a ZIP entry name may contain at most 65535 UTF-8 bytes");
            if (!_out.CanSeek) throw new ArgumentException("export output must be seekable");
            var buffer = _buffer;
            // An empty input is always stored: a DeflateStream that never sees data may write no bytes at all, and a
            // method-8 entry with 0 compressed bytes is not a terminated deflate stream (unzip -t fails, ditto extracts nothing).
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0) deflate = false;
            var entry = new Entry
            {
                Name = encoded, Method = deflate ? (ushort)8 : (ushort)0, Offset = checked((uint)_out.Position),
            };
            WriteHeader(0x04034b50, entry, central: false);
            _out.Write(entry.Name, 0, entry.Name.Length);
            var start = _out.Position;
            var crc = 0xFFFFFFFFu;
            long size = 0;
            using (var compressed = deflate ? new DeflateStream(_out, CompressionLevel.Optimal, leaveOpen: true) : null)
            {
                var destination = (Stream?)compressed ?? _out;
                while (read > 0)
                {
                    size = checked(size + read);
                    if (size > MakerPackChecks.MaxFileBytes) throw new IOException("a file grew beyond the export size limit");
                    for (var i = 0; i < read; i++) crc = Table[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
                    destination.Write(buffer, 0, read);
                    read = input.Read(buffer, 0, buffer.Length);
                }
            }
            var end = _out.Position;
            entry.Crc = ~crc;
            entry.Size = checked((uint)size);
            entry.Compressed = checked((uint)(end - start));
            _out.Position = entry.Offset;
            WriteHeader(0x04034b50, entry, central: false);
            _out.Position = end;
            _entries.Add(entry);
        }

        /// <summary>Writes the central directory and the end record.</summary>
        public void Finish()
        {
            var start = checked((uint)_out.Position);
            foreach (var entry in _entries)
            {
                WriteHeader(0x02014b50, entry, central: true);
                _out.Write(entry.Name, 0, entry.Name.Length);
            }

            var size = checked((uint)_out.Position - start);
            var w = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true);
            w.Write(0x06054b50u);
            w.Write((ushort)0); w.Write((ushort)0);
            w.Write((ushort)_entries.Count); w.Write((ushort)_entries.Count);
            w.Write(size); w.Write(start);
            w.Write((ushort)0);
            w.Flush();
        }

        public void Dispose() => _out.Flush();

        /// <summary>The CRC-32 (IEEE) of <paramref name="bytes"/>.</summary>
        public static uint Crc32(byte[] bytes)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }

        private void WriteHeader(uint signature, Entry e, bool central)
        {
            var w = new BinaryWriter(_out, Encoding.UTF8, leaveOpen: true);
            w.Write(signature);
            if (central) w.Write((ushort)20); // version made by
            w.Write((ushort)20); // version needed
            w.Write((ushort)0x0800); // UTF-8 names
            w.Write(e.Method);
            w.Write(_time); w.Write(_date);
            w.Write(e.Crc); w.Write(e.Compressed); w.Write(e.Size);
            w.Write((ushort)e.Name.Length);
            w.Write((ushort)0); // extra
            if (central)
            {
                w.Write((ushort)0); // comment
                w.Write((ushort)0); // disk
                w.Write((ushort)0); // internal attributes
                w.Write(0u); // external attributes
                w.Write(e.Offset);
            }

            w.Flush();
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }

            return table;
        }

        private sealed class Entry
        {
            public byte[] Name = new byte[0];
            public ushort Method;
            public uint Crc;
            public uint Compressed;
            public uint Size;
            public uint Offset;
        }
    }
}
