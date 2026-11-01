using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CwyLs.ModLoader.Core.Backups
{
    /// <summary>The files of a folder as the backup sees them: count, size, content hash, and what it skipped.</summary>
    public sealed class SaveBackupContentSummary
    {
        internal SaveBackupContentSummary(int files, long bytes, string sha256, IReadOnlyList<string> skippedLinks)
        {
            Files = files;
            Bytes = bytes;
            Sha256 = sha256;
            SkippedLinks = skippedLinks;
        }

        /// <summary>Regular files counted.</summary>
        public int Files { get; }

        /// <summary>Their combined size.</summary>
        public long Bytes { get; }

        /// <summary>The content hash (<see cref="SaveBackupContent"/>), 64 lowercase hex digits.</summary>
        public string Sha256 { get; }

        /// <summary>Relative paths of symbolic links / junctions that were not followed (normally none).</summary>
        public IReadOnlyList<string> SkippedLinks { get; }
    }

    /// <summary>
    /// File work for the launch save backups, Unity-free: hashing a folder, copying it, and removing an old
    /// backup safely. Used by <see cref="SaveBackupRunner"/>; exercised against temp folders by the tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Content hash.</b> SHA-256 over every regular file under the folder, in ordinal order of the relative
    /// path written with <c>/</c> separators. Each file contributes: the UTF-8 bytes of its relative path, one
    /// <c>0x00</c> byte, its length as 8 bytes little-endian, then its bytes. The separator and the length make
    /// the encoding unambiguous, so a rename, a move between folders or a byte moved from one file to the next
    /// all change the hash. Empty folders do not count. The hash is lowercase hex.
    /// </para>
    /// <para>
    /// <b>Links.</b> A symbolic link or junction (<see cref="FileAttributes.ReparsePoint"/>) is never followed,
    /// copied or hashed - it is listed in <see cref="SaveBackupContentSummary.SkippedLinks"/>. A backup folder that
    /// contains one is never deleted (<see cref="TryDeleteBackupFolder"/>), so a delete can never reach outside it.
    /// </para>
    /// </remarks>
    public static class SaveBackupContent
    {
        private const int BufferSize = 81920;

        /// <summary>Counts and hashes the files under <paramref name="folder"/>. Reads every file once.</summary>
        /// <param name="folder">An existing folder.</param>
        public static SaveBackupContentSummary Measure(string folder)
        {
            var skipped = new List<string>();
            var files = List(folder, skipped, null);
            using var hasher = new Hasher();
            foreach (var file in files)
            {
                hasher.AddFile(file.Relative, ReadAll(file.Full));
            }

            return new SaveBackupContentSummary(files.Count, hasher.Bytes, hasher.Finish(), skipped);
        }

        /// <summary>
        /// Copies <paramref name="source"/> into the new folder <paramref name="destination"/> (which must not exist
        /// yet): every folder and regular file, each file's bytes and last-write time. Each file is read once; the
        /// bytes written are the bytes hashed, so the returned hash describes the copy exactly.
        /// </summary>
        /// <remarks>
        /// Files are not forced to disk one by one (<c>Flush(true)</c>): measured at about 5 ms per file on macOS, 230 ms
        /// for a 46-file save, against 4 ms without. A crash of the game leaves the written data with the operating
        /// system either way; only a power cut or an OS crash in the first seconds after a launch could lose it.
        /// </remarks>
        /// <param name="source">The folder to copy.</param>
        /// <param name="destination">Where the copy goes; created here.</param>
        /// <exception cref="IOException"><paramref name="destination"/> already exists, or a file cannot be read or written.</exception>
        public static SaveBackupContentSummary CopyTree(string source, string destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new IOException("the copy target " + destination + " already exists");
            }

            var skipped = new List<string>();
            var folders = new List<string>();
            var files = List(source, skipped, folders);

            Directory.CreateDirectory(destination);
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(Path.Combine(destination, Native(folder)));
            }

            using var hasher = new Hasher();
            foreach (var file in files)
            {
                var bytes = ReadAll(file.Full);
                var target = Path.Combine(destination, Native(file.Relative));
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes, 0, bytes.Length);
                }

                TryCopyWriteTime(file.Full, target);
                hasher.AddFile(file.Relative, bytes);
            }

            return new SaveBackupContentSummary(files.Count, hasher.Bytes, hasher.Finish(), skipped);
        }

        /// <summary>
        /// Deletes one backup folder and everything in it - unless it contains a symbolic link or junction, in
        /// which case nothing is deleted. Read-only files are made writable first (Windows refuses to delete them).
        /// </summary>
        /// <param name="folder">The backup folder. The caller has already checked that it is one of ours.</param>
        /// <param name="problem">Why it was not (fully) deleted.</param>
        public static bool TryDeleteBackupFolder(string folder, out string? problem)
        {
            try
            {
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    problem = "it is a link; left alone";
                    return false;
                }

                var skipped = new List<string>();
                var files = List(folder, skipped, null);
                if (skipped.Count > 0)
                {
                    problem = "it contains a link (" + skipped[0] + "); left alone";
                    return false;
                }

                foreach (var file in files)
                {
                    var attributes = File.GetAttributes(file.Full);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file.Full, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(folder, recursive: true);
                problem = null;
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>One regular file: its path relative to the walked folder (<c>/</c> separators) and its full path.</summary>
        private readonly struct Entry
        {
            internal Entry(string relative, string full)
            {
                Relative = relative;
                Full = full;
            }

            internal string Relative { get; }

            internal string Full { get; }
        }

        /// <summary>Every regular file under <paramref name="root"/>, sorted by relative path (ordinal); links are skipped.</summary>
        private static List<Entry> List(string root, List<string> skippedLinks, List<string>? folders)
        {
            var files = new List<Entry>();
            Walk(new DirectoryInfo(root), string.Empty, files, skippedLinks, folders);
            files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
            folders?.Sort(StringComparer.Ordinal);
            return files;
        }

        private static void Walk(DirectoryInfo directory, string prefix, List<Entry> files, List<string> skippedLinks, List<string>? folders)
        {
            foreach (var item in directory.GetFileSystemInfos())
            {
                var relative = prefix + item.Name;
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedLinks.Add(relative);
                    continue;
                }

                if (item is DirectoryInfo child)
                {
                    folders?.Add(relative);
                    Walk(child, relative + "/", files, skippedLinks, folders);
                }
                else
                {
                    files.Add(new Entry(relative, item.FullName));
                }
            }
        }

        private static string Native(string relative) =>
            Path.DirectorySeparatorChar == '/' ? relative : relative.Replace('/', Path.DirectorySeparatorChar);

        /// <summary>Reads a whole file, sharing it with any writer (the game is not one yet at launch, but Steam might be).</summary>
        private static byte[] ReadAll(string path)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize);
            var length = input.Length;
            if (length > int.MaxValue)
            {
                throw new IOException(path + " is too large to back up");
            }

            var bytes = new byte[length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = input.Read(bytes, read, bytes.Length - read);
                if (n <= 0)
                {
                    throw new IOException(path + " got shorter while it was read");
                }

                read += n;
            }

            if (input.ReadByte() >= 0)
            {
                throw new IOException(path + " got longer while it was read");
            }

            return bytes;
        }

        private static void TryCopyWriteTime(string from, string to)
        {
            try
            {
                File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
            }
            catch (Exception)
            {
                // The bytes are what matter; a time stamp the file system refuses is not worth failing over.
            }
        }

        /// <summary>The framed SHA-256 described on <see cref="SaveBackupContent"/>.</summary>
        private sealed class Hasher : IDisposable
        {
            private readonly SHA256 _sha = SHA256.Create();
            private readonly byte[] _length = new byte[8];

            internal long Bytes { get; private set; }

            internal void AddFile(string relativePath, byte[] content)
            {
                var path = Encoding.UTF8.GetBytes(relativePath);
                Feed(path);
                Feed(new byte[] { 0 });

                var length = (ulong)content.LongLength;
                for (var i = 0; i < 8; i++)
                {
                    _length[i] = (byte)(length >> (8 * i));
                }

                Feed(_length);
                Feed(content);
                Bytes += content.LongLength;
            }

            internal string Finish()
            {
                _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                var hash = _sha.Hash ?? Array.Empty<byte>();
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }

                return sb.ToString();
            }

            public void Dispose() => _sha.Dispose();

            private void Feed(byte[] bytes)
            {
                if (bytes.Length > 0)
                {
                    _sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
            }
        }
    }
}
