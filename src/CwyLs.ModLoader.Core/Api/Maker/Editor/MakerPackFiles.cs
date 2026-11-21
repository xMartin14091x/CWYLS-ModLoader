using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>
    /// Every file operation Pack Maker performs inside one pack folder: safe path resolution, atomic
    /// writes, moving creator files aside into <see cref="MakerEditRules.BackupFolder"/> and back, and
    /// the ledger of the files Pack Maker itself wrote.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safe paths.</b> A path is taken relative to the pack root, normalised with
    /// <see cref="PackContentIndex.Normalise"/> (no <c>..</c>, no drive, no reserved device name), and
    /// must still lie under the root once made absolute. No folder between the root and the file may be
    /// a symbolic link or junction, so a link planted inside a pack cannot send a write elsewhere. (The
    /// root itself may be a link: a creator's working folder linked into <c>CWYLS Mods</c> is fine.)
    /// </para>
    /// <para>
    /// <b>Atomic writes.</b> The bytes go to a hidden temporary file in the same folder
    /// (<c>.&lt;name&gt;.&lt;random&gt;.maker-tmp</c>: hidden, so a pack rescan in between never
    /// indexes it), are flushed to disk, then replace the target in one rename. A crash leaves either
    /// the old file or the new one, never half of either.
    /// </para>
    /// <para>
    /// <b>Creator files are never deleted.</b> A file Pack Maker has to displace is moved to the same
    /// path under <see cref="MakerEditRules.BackupFolder"/> (a dated name when that is taken, so no
    /// backup is ever overwritten). Pack Maker deletes only a file whose current bytes match what its
    /// ledger (<see cref="MakerEditRules.LedgerPath"/>) says it wrote there.
    /// </para>
    /// <para>Not thread-safe on its own: the editor serialises its writes.</para>
    /// </remarks>
    public sealed class MakerPackFiles
    {
        private const string TempSuffix = ".maker-tmp";

        /// <summary>Creates the file helper for one pack.</summary>
        /// <param name="root">Absolute path of the pack folder.</param>
        /// <exception cref="ArgumentException"><paramref name="root"/> is blank.</exception>
        public MakerPackFiles(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("a pack folder is required", nameof(root));
            }

            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        /// <summary>Absolute path of the pack folder, without a trailing separator.</summary>
        public string Root { get; }

        /// <summary>A short, stable content hash for the ledger: byte count and FNV-1a 64.</summary>
        /// <param name="bytes">The content.</param>
        public static string Fingerprint(byte[] bytes)
        {
            if (bytes is null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }

            var hash = 14695981039346656037UL;
            foreach (var b in bytes)
            {
                hash = (hash ^ b) * 1099511628211UL;
            }

            return bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Resolves a pack-relative path to an absolute one inside the pack, or says why it may not be
        /// used.
        /// </summary>
        /// <param name="relativePath">A forward-slash path relative to the pack root.</param>
        /// <param name="fullPath">The absolute path, or empty.</param>
        /// <param name="problem">Why the path is refused, or null.</param>
        public bool TryResolve(string? relativePath, out string fullPath, out string? problem)
        {
            fullPath = string.Empty;
            var normalised = PackContentIndex.Normalise(relativePath);
            if (normalised is null)
            {
                problem = "'" + relativePath + "' is not a path inside the pack";
                return false;
            }

            try
            {
                var candidate = Path.GetFullPath(Path.Combine(Root, normalised.Replace('/', Path.DirectorySeparatorChar)));
                if (!candidate.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    problem = "'" + relativePath + "' leaves the pack folder";
                    return false;
                }

                // Every existing step below the root, the file included, must be a plain folder or file.
                var step = Root;
                foreach (var segment in normalised.Split('/'))
                {
                    step = Path.Combine(step, segment);
                    if ((Directory.Exists(step) || File.Exists(step))
                        && (File.GetAttributes(step) & FileAttributes.ReparsePoint) != 0)
                    {
                        problem = "'" + relativePath + "' goes through a link inside the pack";
                        return false;
                    }
                }

                fullPath = candidate;
                problem = null;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException
                                       || ex is UnauthorizedAccessException)
            {
                problem = "'" + relativePath + "' cannot be used: " + ex.Message;
                return false;
            }
        }

        /// <summary>The absolute path of a pack-relative path; throws when it is not a safe one.</summary>
        /// <param name="relativePath">A forward-slash path relative to the pack root.</param>
        /// <exception cref="UnauthorizedAccessException">The path is not a safe path inside the pack.</exception>
        public string Resolve(string relativePath)
        {
            if (!TryResolve(relativePath, out var full, out var problem))
            {
                throw new UnauthorizedAccessException(problem);
            }

            return full;
        }

        /// <summary>True when a file exists at the path.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public bool Exists(string relativePath) => File.Exists(Resolve(relativePath));

        /// <summary>A file's bytes, or null when there is none.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public byte[]? ReadBytes(string relativePath)
        {
            var full = Resolve(relativePath);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }

        /// <summary>A text file as UTF-8 without its BOM, or null when there is none.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        /// <param name="bom">True when the file starts with a UTF-8 BOM.</param>
        public string? ReadText(string relativePath, out bool bom)
        {
            bom = false;
            var bytes = ReadBytes(relativePath);
            if (bytes is null)
            {
                return null;
            }

            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            return bom
                ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Lists every plain file below a pack-relative folder without following links. Returned paths are
        /// pack-relative, forward-slash and ordinally sorted.
        /// </summary>
        public IReadOnlyList<string> ListFiles(string relativeFolder)
        {
            var root = Resolve(relativeFolder);
            var found = new List<string>();
            if (!Directory.Exists(root))
            {
                return found;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("'" + Relative(folder) + "' is a link inside the pack");
                }

                foreach (var child in Directory.GetDirectories(folder))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new UnauthorizedAccessException("'" + Relative(child) + "' is a link inside the pack");
                    }

                    pending.Push(child);
                }

                foreach (var file in Directory.GetFiles(folder))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new UnauthorizedAccessException("'" + Relative(file) + "' is a link inside the pack");
                    }

                    var relative = Relative(file);
                    Resolve(relative);
                    found.Add(relative);
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>Total bytes in plain files below a safe pack-relative folder.</summary>
        public long BytesUnder(string relativeFolder)
        {
            long total = 0;
            foreach (var relative in ListFiles(relativeFolder))
            {
                total = checked(total + new FileInfo(Resolve(relative)).Length);
            }

            return total;
        }

        /// <summary>Writes UTF-8 text atomically, with a BOM when <paramref name="bom"/>.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        /// <param name="text">The whole file.</param>
        /// <param name="bom">Write a UTF-8 BOM first (keep the file as it was).</param>
        public void WriteTextAtomic(string relativePath, string text, bool bom)
        {
            var body = new UTF8Encoding(false).GetBytes(text ?? string.Empty);
            if (!bom)
            {
                WriteAtomic(relativePath, body);
                return;
            }

            var withBom = new byte[body.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Buffer.BlockCopy(body, 0, withBom, 3, body.Length);
            WriteAtomic(relativePath, withBom);
        }

        /// <summary>Writes bytes atomically: hidden temp file in the same folder, flushed, then one rename over the target.</summary>
        /// <param name="relativePath">A pack-relative path; missing folders are created.</param>
        /// <param name="bytes">The whole file.</param>
        public void WriteAtomic(string relativePath, byte[] bytes)
        {
            var full = Resolve(relativePath);
            var folder = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(folder);

            // The new folders must not have become links either (a race with the creator is harmless,
            // but the check is cheap).
            Resolve(relativePath);

            var temp = Path.Combine(
                folder, "." + Path.GetFileName(full) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + TempSuffix);
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(full))
                {
                    File.Replace(temp, full, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, full);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }

        /// <summary>
        /// Moves a pack file aside to the same path under <see cref="MakerEditRules.BackupFolder"/>,
        /// or to a dated name next to it when an earlier backup already holds that path.
        /// </summary>
        /// <param name="relativePath">The file to move.</param>
        /// <returns>The pack-relative path it now has.</returns>
        public string MoveAside(string relativePath)
        {
            var source = Resolve(relativePath);
            var backup = MakerEditRules.BackupPathOf(PackContentIndex.Normalise(relativePath)!);
            var target = Resolve(backup);

            if (File.Exists(target))
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var name = Path.GetFileNameWithoutExtension(backup);
                var extension = Path.GetExtension(backup);
                var folder = backup.Substring(0, backup.LastIndexOf('/') + 1);
                for (var n = 1; File.Exists(target); n++)
                {
                    backup = folder + name + "." + stamp + (n > 1 ? "-" + n.ToString(CultureInfo.InvariantCulture) : string.Empty) + extension;
                    target = Resolve(backup);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
            return backup;
        }

        /// <summary>
        /// Moves the backup of <paramref name="relativePath"/> (the one at the same path under
        /// <see cref="MakerEditRules.BackupFolder"/>) back into place, when the place is free.
        /// </summary>
        /// <param name="relativePath">Where the file belongs.</param>
        /// <returns>True when a backup was moved back.</returns>
        public bool TryRestore(string relativePath)
        {
            var backup = Resolve(MakerEditRules.BackupPathOf(PackContentIndex.Normalise(relativePath)!));
            var target = Resolve(relativePath);
            if (!File.Exists(backup) || File.Exists(target))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(backup, target);
            return true;
        }

        /// <summary>Deletes a file Pack Maker owns. Callers check <see cref="IsMakerWritten"/> first.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public void Delete(string relativePath)
        {
            var full = Resolve(relativePath);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }

        /// <summary>
        /// The pack's files for one voice name in one language, as the language voice plan would see
        /// them: <c>audio/voice/&lt;folder&gt;/&lt;clip&gt;.ogg|.wav</c> where the folder is any spelling of
        /// <paramref name="code"/> (<c>th</c>, <c>TH</c>, <c>thai</c>). Ordinal order, so the last one is
        /// the one the plan would load.
        /// </summary>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name (case-sensitive).</param>
        public IReadOnlyList<string> VoiceFiles(string code, string clip) => ListVoice(string.Empty, code, clip);

        /// <summary>
        /// <see cref="VoiceFiles"/> for every voice name of one language at once, from one listing of the voice folders: a
        /// catalogue page asks for hundreds of clips, and listing a folder of thousands of dubs per clip took seconds.
        /// Same rules and order as <see cref="VoiceFiles"/>; a name with no file is absent.
        /// </summary>
        /// <param name="code">A canonical language code.</param>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> VoiceIndex(string code)
        {
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var voiceRoot = Path.Combine(Root, PackContentIndex.VoiceFolder.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(voiceRoot))
            {
                foreach (var folder in Directory.GetDirectories(voiceRoot))
                {
                    var folderName = Path.GetFileName(folder);
                    if (string.IsNullOrEmpty(folderName) || folderName[0] == '.'
                        || !LanguageVoicePlan.TryNormalizeCode(folderName, out var folderCode, out _)
                        || !string.Equals(folderCode, code, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var file in Directory.GetFiles(folder))
                    {
                        var fileName = Path.GetFileName(file);
                        var extension = Path.GetExtension(fileName);
                        if (fileName.Length == 0 || fileName[0] == '.'
                            || !(string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        var clip = Path.GetFileNameWithoutExtension(fileName);
                        if (!found.TryGetValue(clip, out var list)) found[clip] = list = new List<string>(1);
                        list.Add(PackContentIndex.VoiceFolder + folderName + "/" + fileName);
                    }
                }
            }

            var index = new Dictionary<string, IReadOnlyList<string>>(found.Count, StringComparer.Ordinal);
            foreach (var pair in found)
            {
                pair.Value.Sort(StringComparer.Ordinal);
                index[pair.Key] = pair.Value;
            }

            return index;
        }

        /// <summary>
        /// The original paths of the backups held for one voice name in one language (only the primary
        /// backups, at the same path under <see cref="MakerEditRules.BackupFolder"/>; dated ones are
        /// left for the creator).
        /// </summary>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name.</param>
        public IReadOnlyList<string> VoiceBackups(string code, string clip) =>
            ListVoice(MakerEditRules.BackupFolder + "/", code, clip);

        /// <summary>Null when the pack folder accepts a write (a hidden probe file, created and deleted); otherwise why not.</summary>
        public string? WriteProblem()
        {
            var probe = Path.Combine(Root, ".maker-probe-" + Guid.NewGuid().ToString("N").Substring(0, 8) + TempSuffix);
            try
            {
                if (!Directory.Exists(Root))
                {
                    return "the folder does not exist";
                }

                using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.WriteByte(0);
                }

                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return ex.Message;
            }
            finally
            {
                TryDelete(probe);
            }
        }

        /// <summary>True when the file exists and its bytes are exactly what Pack Maker recorded writing there.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public bool IsMakerWritten(string relativePath)
        {
            var bytes = ReadBytes(relativePath);
            if (bytes is null)
            {
                return false;
            }

            var recorded = MakerSubtitleFile.Read(ReadText(MakerEditRules.LedgerPath, out _), Key(relativePath));
            return recorded != null && string.Equals(recorded, Fingerprint(bytes), StringComparison.Ordinal);
        }

        /// <summary>Records that Pack Maker wrote these bytes at this path.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        /// <param name="bytes">What was written.</param>
        public void RecordWritten(string relativePath, byte[] bytes) => EditLedger(relativePath, Fingerprint(bytes));

        /// <summary>Forgets a path in the ledger (the file was removed or moved aside).</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public void ForgetWritten(string relativePath) => EditLedger(relativePath, null);

        private static string Key(string relativePath) => PackContentIndex.Normalise(relativePath) ?? relativePath;

        private string Relative(string fullPath) =>
            fullPath.Substring(Root.Length + 1).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

        private void EditLedger(string relativePath, string? fingerprint)
        {
            var existing = ReadText(MakerEditRules.LedgerPath, out var bom);
            var edit = MakerSubtitleFile.Apply(existing, Key(relativePath), fingerprint);
            if (!edit.Ok)
            {
                // A ledger a person broke by hand is set aside next to itself, never "repaired": start
                // a new one. Until then every file reads as the creator's, which only means more backups.
                var broken = MakerEditRules.BackupFolder + "/maker-written." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                             + "-" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".broken.json";
                File.Move(Resolve(MakerEditRules.LedgerPath), Resolve(broken));
                existing = null;
                bom = false;
                edit = MakerSubtitleFile.Apply(null, Key(relativePath), fingerprint);
            }

            if (edit.Changed && edit.Text != null)
            {
                WriteTextAtomic(MakerEditRules.LedgerPath, edit.Text, bom);
            }
        }

        private IReadOnlyList<string> ListVoice(string prefix, string code, string clip)
        {
            var found = new List<string>();
            var voiceRoot = Path.Combine(Root, (prefix + PackContentIndex.VoiceFolder).TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(voiceRoot))
            {
                return found;
            }

            foreach (var folder in Directory.GetDirectories(voiceRoot))
            {
                var folderName = Path.GetFileName(folder);
                if (string.IsNullOrEmpty(folderName) || folderName[0] == '.'
                    || !LanguageVoicePlan.TryNormalizeCode(folderName, out var folderCode, out _)
                    || !string.Equals(folderCode, code, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(folder))
                {
                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(fileName);
                    if (fileName.Length == 0 || fileName[0] == '.'
                        || !(string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
                        || !string.Equals(Path.GetFileNameWithoutExtension(fileName), clip, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    found.Add(PackContentIndex.VoiceFolder + folderName + "/" + fileName);
                }
            }

            found.Sort(StringComparer.Ordinal);
            return found;
        }

        /// <summary>
        /// Atomically replaces a target while retaining the actual displaced destination, then checks it
        /// against the caller's precondition. On mismatch restores that destination instead of losing an edit.
        /// Symlink replacement by a process already able to write the pack is outside this concurrency boundary.
        /// </summary>
        public bool WriteChecked(string relativePath, byte[] bytes, string? expected, string backupPath)
        {
            var full = Resolve(relativePath);
            var backup = Resolve(backupPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            var temp = full + "." + Guid.NewGuid().ToString("N") + TempSuffix;
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (!File.Exists(full))
                {
                    if (expected != null) return false;
                    try { File.Move(temp, full); return true; }
                    catch (IOException) when (File.Exists(full)) { return false; }
                }
                if (File.Exists(backup))
                {
                    if (Fingerprint(File.ReadAllBytes(backup)) != expected || Fingerprint(File.ReadAllBytes(full)) != expected)
                        throw new IOException("A prior replacement backup still needs recovery.");
                    File.Delete(backup); // Verified duplicate of the current before-version; safe to replace it.
                }
                File.Replace(temp, full, backup, ignoreMetadataErrors: true);
                if (Fingerprint(File.ReadAllBytes(backup)) == expected) return true;
                // Keep the staged intended bytes in the journal; put the actual displaced edit back.
                File.Replace(backup, full, null, ignoreMetadataErrors: true);
                return false;
            }
            finally { TryDelete(temp); }
        }

        public void RestoreReplacement(string backupPath, string targetPath)
        {
            File.Replace(Resolve(backupPath), Resolve(targetPath), null, ignoreMetadataErrors: true);
        }

        /// <summary>Retires an owned journal file by moving its actual bytes aside before verification.</summary>
        public bool DeleteChecked(string relativePath, string? expected, string backupPath)
        {
            var full = Resolve(relativePath);
            var backup = Resolve(backupPath);
            if (!File.Exists(full)) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (File.Exists(backup)) throw new IOException("A prior deletion backup still needs recovery.");
            File.Move(full, backup);
            if (Fingerprint(File.ReadAllBytes(backup)) == expected) return true;
            File.Move(backup, full); // No overwrite: a later writer's new file must also survive.
            return false;
        }

        public void RestoreDeletion(string backupPath, string targetPath) => File.Move(Resolve(backupPath), Resolve(targetPath));

        /// <summary>
        /// Where <see cref="MoveAside"/> would put <paramref name="relativePath"/> now (the same path under
        /// <see cref="MakerEditRules.BackupFolder"/>, or a dated name next to it), without moving anything; paths in
        /// <paramref name="taken"/> count as occupied. Studio publication stages its move-aside as a commit with it.
        /// </summary>
        public string FreeBackupPath(string relativePath, ICollection<string>? taken = null)
        {
            var backup = MakerEditRules.BackupPathOf(PackContentIndex.Normalise(relativePath)!);
            bool Busy(string candidate) => File.Exists(Resolve(candidate)) || (taken != null && taken.Contains(candidate));
            if (!Busy(backup)) return backup;
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = Path.GetFileNameWithoutExtension(backup);
            var extension = Path.GetExtension(backup);
            var folder = backup.Substring(0, backup.LastIndexOf('/') + 1);
            for (var n = 1; ; n++)
            {
                var candidate = folder + name + "." + stamp + (n > 1 ? "-" + n.ToString(CultureInfo.InvariantCulture) : string.Empty) + extension;
                if (!Busy(candidate)) return candidate;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A stray hidden temp file is harmless and the loader never reads it.
            }
        }
    }
}
