using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CwyLs.ModLoader.Core.Backups;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Launch save backups against temp folders: copy, marker, skip-unchanged, prune, and what is never deleted.</summary>
    public class SaveBackupRunnerTests : IDisposable
    {
        private static readonly TimeSpan Offset = TimeSpan.FromHours(7);

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cwy-savebackup-tests-" + Guid.NewGuid().ToString("N"));

        public SaveBackupRunnerTests()
        {
            Directory.CreateDirectory(_dir);
            Directory.CreateDirectory(Save);
            Put("Release/v2/76561190000000001/PlayerDataV3.es3", "player one");
            Put("Release/v2/76561190000000001/SettingData.es3", "settings");
            Put("Release/v2/76561190000000001/SettingData.es3.bak", "settings (game backup)");
            Put("Release/v2/76561190000000002/PlayerDataV3.es3", "player two");
            Put("Demo/v2/x/notes.es3", new string('n', 5000));
            Directory.CreateDirectory(Path.Combine(Save, "Release", "empty"));
        }

        private string Save => Path.Combine(_dir, "persistent", "SaveData");

        private string Root => Path.Combine(_dir, "game", "CWYLS Backups");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                {
                    foreach (var file in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                    {
                        TryChmod(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    Directory.Delete(_dir, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void Put(string relative, string content) => PutIn(Save, relative, content);

        private static void PutIn(string root, string relative, string content)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        private static DateTimeOffset At(string stamp) =>
            new DateTimeOffset(DateTime.ParseExact(stamp, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), Offset);

        private SaveBackupResult Run(string stamp, SaveBackupRules? rules = null, string? save = null, string? root = null) =>
            SaveBackupRunner.Run(new SaveBackupRequest(save ?? Save, root ?? Root, At(stamp), "0.9.0", "1.18.0", rules));

        private static Dictionary<string, byte[]> Snapshot(string folder) =>
            Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllBytes);

        private string[] RootNames() =>
            Directory.Exists(Root)
                ? Directory.GetFileSystemEntries(Root).Select(p => Path.GetFileName(p) ?? string.Empty).OrderBy(n => n, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();

        /// <summary>A valid backup folder made by hand: one file and a marker with the given hash.</summary>
        private void FakeBackup(string name, string hash = "", string content = "old")
        {
            var folder = Path.Combine(Root, name);
            PutIn(folder, "SaveData/Release/v2/1/PlayerDataV3.es3", content);
            var manifest = new SaveBackupManifest(1, null, "0.9.0", "1.18.0", 1, content.Length, hash.Length == 0 ? new string('0', 64) : hash);
            File.WriteAllText(Path.Combine(folder, "backup.json"), manifest.ToJson());
        }

        private static bool TryChmod(string path, UnixFileMode mode)
        {
            if (OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                File.SetUnixFileMode(path, mode);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [Fact]
        public void The_first_run_copies_the_save_exactly_and_writes_the_marker()
        {
            var result = Run("20261101-140312");

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
            Assert.Equal("save-20261101-140312", result.Name);
            Assert.Equal(new[] { "save-20261101-140312" }, RootNames());

            var copy = Path.Combine(Root, "save-20261101-140312", "SaveData");
            Assert.Equal(Snapshot(Save), Snapshot(copy));
            Assert.True(Directory.Exists(Path.Combine(copy, "Release", "empty")));
            Assert.Equal(5, result.Files);
            Assert.Equal(Snapshot(Save).Values.Sum(b => (long)b.Length), result.Bytes);

            var markerText = File.ReadAllText(Path.Combine(Root, "save-20261101-140312", "backup.json"));
            Assert.True(SaveBackupManifest.TryParse(markerText, out var marker, out var problem), problem);
            Assert.Equal(1, marker!.Schema);
            Assert.Equal("0.9.0", marker.LoaderVersion);
            Assert.Equal("1.18.0", marker.GameVersion);
            Assert.Equal(5, marker.Files);
            Assert.Equal(result.Bytes, marker.Bytes);
            Assert.Equal(SaveBackupContent.Measure(Save).Sha256, marker.ContentSha256);
            Assert.Equal(At("20261101-140312"), marker.Created);
            Assert.Equal(1, result.Kept);
            Assert.Equal(0, result.Removed);
        }

        [Fact]
        public void The_copy_keeps_each_file_last_write_time()
        {
            var file = Path.Combine(Save, "Release", "v2", "76561190000000001", "SettingData.es3");
            var when = new DateTime(2026, 10, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file, when);

            Run("20261101-140312");

            var copied = Path.Combine(Root, "save-20261101-140312", "SaveData", "Release", "v2", "76561190000000001", "SettingData.es3");
            Assert.Equal(when, File.GetLastWriteTimeUtc(copied));
        }

        [Fact]
        public void An_unchanged_save_makes_no_new_backup()
        {
            Run("20261101-140312");
            var second = Run("20261101-180000");

            Assert.Equal(SaveBackupOutcome.Unchanged, second.Outcome);
            Assert.Equal("save-20261101-140312", second.UnchangedSince);
            Assert.Null(second.Name);
            Assert.Equal(5, second.Files);
            Assert.Equal(1, second.Kept);
            Assert.Equal(new[] { "save-20261101-140312" }, RootNames());
        }

        [Fact]
        public void A_changed_save_makes_a_new_backup()
        {
            Run("20261101-140312");
            Put("Release/v2/76561190000000001/PlayerDataV3.es3", "player one, later");
            var second = Run("20261101-180000");

            Assert.Equal(SaveBackupOutcome.BackedUp, second.Outcome);
            Assert.Equal(new[] { "save-20261101-140312", "save-20261101-180000" }, RootNames());
            Assert.Equal(2, second.Kept);
            Assert.Equal("player one, later", File.ReadAllText(Path.Combine(Root, "save-20261101-180000", "SaveData", "Release", "v2", "76561190000000001", "PlayerDataV3.es3")));
            Assert.Equal("player one", File.ReadAllText(Path.Combine(Root, "save-20261101-140312", "SaveData", "Release", "v2", "76561190000000001", "PlayerDataV3.es3")));
        }

        [Fact]
        public void Unchanged_compares_with_the_newest_backup_only()
        {
            var live = SaveBackupContent.Measure(Save).Sha256;
            FakeBackup("save-20261030-100000", live);          // older, same content
            FakeBackup("save-20261031-100000", new string('a', 64)); // newest, different content
            Directory.CreateDirectory(Path.Combine(Root, "save-20261031-235959")); // newer, but not a backup (no marker)

            var result = Run("20261101-140312");

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
        }

        [Fact]
        public void A_newest_backup_without_a_valid_hash_counts_as_changed()
        {
            FakeBackup("save-20261031-100000", "not a hash");
            Assert.Equal(SaveBackupOutcome.BackedUp, Run("20261101-140312").Outcome);
        }

        [Fact]
        public void Retention_prunes_the_README_worked_example()
        {
            foreach (var stamp in SaveBackupPolicyTests.WorkedExample.Skip(1))
            {
                FakeBackup("save-" + stamp);
            }

            var result = Run(SaveBackupPolicyTests.WorkedExample[0]);

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
            Assert.Equal(10, result.Kept);
            Assert.Equal(4, result.Removed);
            Assert.Empty(result.PruneProblems);
            var expectedKept = SaveBackupPolicyTests.WorkedExampleExpected.Where(p => p.Value != "REMOVE").Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(expectedKept, RootNames());
        }

        [Fact]
        public void Pruning_also_runs_when_the_save_is_unchanged()
        {
            Run("20261101-140312");
            for (var day = 1; day <= 9; day++)
            {
                FakeBackup("save-202610" + day.ToString("00", CultureInfo.InvariantCulture) + "-120000");
            }

            var result = Run("20261101-180000", SaveBackupRules.Clamp(2, 0, 0, 0));

            Assert.Equal(SaveBackupOutcome.Unchanged, result.Outcome);
            Assert.Equal(2, result.Kept);
            Assert.Equal(8, result.Removed);
            Assert.Equal(new[] { "save-20261009-120000", "save-20261101-140312" }, RootNames());
        }

        [Fact]
        public void Folders_that_are_not_ours_are_never_deleted()
        {
            FakeBackup("save-20200110-000000"); // ours: may go
            Directory.CreateDirectory(Path.Combine(Root, "save-20200101-000000"));                        // no marker
            PutIn(Path.Combine(Root, "save-20200102-000000"), "backup.json", "{ not json");                 // broken marker
            PutIn(Path.Combine(Root, "save-20200103-000000"), "backup.json", "{\"kind\":\"other\",\"schema\":1}"); // someone else's
            PutIn(Path.Combine(Root, "save-20200104-000000"), "backup.json", "[1,2]");                      // not an object
            FakeBackup("Save-20200105-000000");                  // wrong case
            FakeBackup("save-scene-20200106-000000-main_05");    // the scene-preview pattern
            FakeBackup("save-20200107-000000.partial-0badf00d"); // a crash leftover
            FakeBackup("save-20200108-000000-copy");             // a user's copy
            FakeBackup("save-20201301-000000");                  // not a date
            PutIn(Path.Combine(Root, "notes"), "keep.txt", "mine");
            PutIn(Root, "readme.txt", "a file, not a folder");
            var before = Snapshot(Root);

            var result = Run("20261101-140312", SaveBackupRules.Clamp(1, 0, 0, 0));

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
            Assert.Equal(1, result.Removed);
            Assert.Equal(10, result.LeftAlone);
            Assert.False(Directory.Exists(Path.Combine(Root, "save-20200110-000000")));
            foreach (var (path, bytes) in before.Where(p => !p.Key.StartsWith("save-20200110-000000/", StringComparison.Ordinal)))
            {
                Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Root, path)));
            }
        }

        [Fact]
        public void The_inventory_explains_what_it_leaves_alone()
        {
            Directory.CreateDirectory(Path.Combine(Root, "save-20200101-000000"));
            FakeBackup("save-20200107-000000.partial-0badf00d");
            Directory.CreateDirectory(Path.Combine(Root, "notes"));
            PutIn(Path.Combine(Root, "save-20200103-000000"), "backup.json", "{\"kind\":\"other\",\"schema\":1}");

            var inventory = SaveBackupInventory.Scan(Root);

            Assert.Empty(inventory.Backups);
            var reasons = inventory.LeftAlone.ToDictionary(s => s.Name, s => s.Reason);
            Assert.Equal("no backup.json", reasons["save-20200101-000000"]);
            Assert.StartsWith("an unfinished backup", reasons["save-20200107-000000.partial-0badf00d"]);
            Assert.Equal("not named save-YYYYMMDD-HHMMSS", reasons["notes"]);
            Assert.Contains("kind", reasons["save-20200103-000000"]);
        }

        [Fact]
        public void A_missing_save_folder_does_nothing()
        {
            Directory.Delete(Save, recursive: true);
            var result = Run("20261101-140312");

            Assert.Equal(SaveBackupOutcome.NoSaveFolder, result.Outcome);
            Assert.False(Directory.Exists(Root));
        }

        [Fact]
        public void An_empty_save_folder_does_nothing()
        {
            Directory.Delete(Save, recursive: true);
            Directory.CreateDirectory(Path.Combine(Save, "Release"));
            var result = Run("20261101-140312");

            Assert.Equal(SaveBackupOutcome.EmptySaveFolder, result.Outcome);
            Assert.False(Directory.Exists(Root));
        }

        [Fact]
        public void A_backups_folder_inside_the_save_folder_is_refused()
        {
            var before = Snapshot(Save);
            var inside = Path.Combine(Save, "CWYLS Backups");
            var result = Run("20261101-140312", root: inside);

            Assert.Equal(SaveBackupOutcome.Failed, result.Outcome);
            Assert.Contains("Steam Cloud", result.Failure);
            Assert.False(Directory.Exists(inside));
            Assert.Equal(before, Snapshot(Save));

            Assert.Equal(SaveBackupOutcome.Failed, Run("20261101-140312", root: Save).Outcome);
            Assert.Equal(SaveBackupOutcome.Failed, Run("20261101-140312", root: Save + Path.DirectorySeparatorChar).Outcome);
            Assert.Equal(SaveBackupOutcome.Failed, Run("20261101-140312", save: Path.Combine(Root, "x", "SaveData")).Outcome);
        }

        [Fact]
        public void Inside_means_inside_not_a_sibling_with_the_same_prefix()
        {
            Assert.True(SaveBackupRunner.IsSameOrInside(Path.Combine(_dir, "a", "b"), Path.Combine(_dir, "a")));
            Assert.True(SaveBackupRunner.IsSameOrInside(Path.Combine(_dir, "a"), Path.Combine(_dir, "a") + Path.DirectorySeparatorChar));
            Assert.True(SaveBackupRunner.IsSameOrInside(Path.Combine(_dir, "A", "b"), Path.Combine(_dir, "a")));
            Assert.False(SaveBackupRunner.IsSameOrInside(Path.Combine(_dir, "ab"), Path.Combine(_dir, "a")));
            Assert.False(SaveBackupRunner.IsSameOrInside(Path.Combine(_dir, "a"), Path.Combine(_dir, "a", "b")));
        }

        [Fact]
        public void A_taken_name_is_left_alone_and_nothing_half_made_stays()
        {
            PutIn(Path.Combine(Root, "save-20261101-140312"), "mine.txt", "a user's folder");
            FakeBackup("save-20200101-000000");

            var result = Run("20261101-140312", SaveBackupRules.Clamp(1, 0, 0, 0));

            Assert.Equal(SaveBackupOutcome.Failed, result.Outcome);
            Assert.Contains("already", result.Failure);
            Assert.Equal("a user's folder", File.ReadAllText(Path.Combine(Root, "save-20261101-140312", "mine.txt")));
            Assert.Equal(new[] { "save-20200101-000000", "save-20261101-140312" }, RootNames()); // nothing pruned after a failure
        }

        [Fact]
        public void A_copy_that_fails_leaves_no_backup_and_prunes_nothing()
        {
            var unreadable = Path.Combine(Save, "Release", "v2", "76561190000000002", "PlayerDataV3.es3");
            if (!TryChmod(unreadable, UnixFileMode.None) || CanRead(unreadable))
            {
                return; // Windows, or running as root: no way to make a file unreadable here.
            }

            FakeBackup("save-20200101-000000");
            FakeBackup("save-20200102-000000");

            var result = SaveBackupRunner.Run(new SaveBackupRequest(Save, Root, At("20261101-140312"), "0.9.0", "1.18.0", SaveBackupRules.Clamp(1, 0, 0, 0)));

            Assert.Equal(SaveBackupOutcome.Failed, result.Outcome);
            Assert.NotNull(result.Failure);
            Assert.Equal(new[] { "save-20200101-000000", "save-20200102-000000" }, RootNames());
        }

        [Fact]
        public void Until_the_rename_a_backup_does_not_look_like_one_and_a_late_failure_removes_it()
        {
            FakeBackup("save-20200101-000000");
            string? partial = null;
            var request = new SaveBackupRequest(Save, Root, At("20261101-140312"), "0.9.0", "1.18.0", SaveBackupRules.Clamp(1, 0, 0, 0))
            {
                BeforeRename = folder =>
                {
                    partial = folder;

                    // Complete copy and marker, yet a crash here would leave something no scan calls a backup.
                    Assert.Equal(Snapshot(Save), Snapshot(Path.Combine(folder, "SaveData")));
                    Assert.True(File.Exists(Path.Combine(folder, "backup.json")));
                    var inventory = SaveBackupInventory.Scan(Root);
                    Assert.Equal(new[] { "save-20200101-000000" }, inventory.Backups.Select(b => b.Name));
                    Assert.StartsWith("an unfinished backup", inventory.LeftAlone.Single(s => s.Name == Path.GetFileName(folder)).Reason);

                    throw new IOException("simulated failure before the rename");
                },
            };

            var result = SaveBackupRunner.Run(request);

            Assert.Equal(SaveBackupOutcome.Failed, result.Outcome);
            Assert.Contains("simulated failure", result.Failure);
            Assert.NotNull(partial);
            Assert.StartsWith("save-20261101-140312.partial-", Path.GetFileName(partial));
            Assert.False(Directory.Exists(partial));
            Assert.Equal(new[] { "save-20200101-000000" }, RootNames()); // no backup, and nothing pruned after a failure
        }

        [Fact]
        public void A_backup_that_holds_a_link_is_never_deleted_and_the_link_target_is_untouched()
        {
            if (OperatingSystem.IsWindows())
            {
                return; // creating symbolic links needs extra rights there
            }

            var outside = Path.Combine(_dir, "outside");
            PutIn(outside, "precious.txt", "do not delete");
            FakeBackup("save-20200101-000000");
            Directory.CreateSymbolicLink(Path.Combine(Root, "save-20200101-000000", "SaveData", "link"), outside);

            var result = Run("20261101-140312", SaveBackupRules.Clamp(1, 0, 0, 0));

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
            Assert.Equal(0, result.Removed);
            var problem = Assert.Single(result.PruneProblems);
            Assert.Contains("save-20200101-000000", problem);
            Assert.Contains("link", problem);
            Assert.True(Directory.Exists(Path.Combine(Root, "save-20200101-000000")));
            Assert.Equal("do not delete", File.ReadAllText(Path.Combine(outside, "precious.txt")));
        }

        [Fact]
        public void Links_in_the_save_folder_are_not_followed_or_copied()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            var outside = Path.Combine(_dir, "elsewhere");
            PutIn(outside, "big.bin", "not a save");
            Directory.CreateSymbolicLink(Path.Combine(Save, "Release", "linked"), outside);

            var result = Run("20261101-140312");

            Assert.Equal(SaveBackupOutcome.BackedUp, result.Outcome);
            Assert.Equal(new[] { "Release/linked" }, result.SkippedLinks);
            Assert.Equal(5, result.Files);
            Assert.False(Directory.Exists(Path.Combine(Root, "save-20261101-140312", "SaveData", "Release", "linked")));
        }

        [Fact]
        public void The_content_hash_ignores_creation_order_and_tracks_names_bytes_and_moves()
        {
            var a = Path.Combine(_dir, "ha");
            var b = Path.Combine(_dir, "hb");
            PutIn(a, "x/1.es3", "one");
            PutIn(a, "y/2.es3", "two");
            PutIn(b, "y/2.es3", "two");
            PutIn(b, "x/1.es3", "one");
            Directory.CreateDirectory(Path.Combine(b, "empty"));
            var hash = SaveBackupContent.Measure(a).Sha256;
            Assert.Equal(hash, SaveBackupContent.Measure(b).Sha256);

            PutIn(b, "y/2.es3", "twO");
            Assert.NotEqual(hash, SaveBackupContent.Measure(b).Sha256);

            var c = Path.Combine(_dir, "hc");
            PutIn(c, "x/1.es3", "one");
            PutIn(c, "x/2.es3", "two"); // same bytes, other folder
            Assert.NotEqual(hash, SaveBackupContent.Measure(c).Sha256);

            var d = Path.Combine(_dir, "hd");
            PutIn(d, "x/1.es3", "onet"); // a byte moved from one file to the next
            PutIn(d, "y/2.es3", "wo");
            Assert.NotEqual(hash, SaveBackupContent.Measure(d).Sha256);
        }

        [Fact]
        public void The_content_hash_is_the_documented_framing()
        {
            var folder = Path.Combine(_dir, "framing");
            PutIn(folder, "b.es3", "BB");
            PutIn(folder, "a/z.es3", "A");

            var expected = new List<byte>();
            foreach (var (path, content) in new[] { ("a/z.es3", "A"), ("b.es3", "BB") }) // ordinal path order
            {
                expected.AddRange(Encoding.UTF8.GetBytes(path));
                expected.Add(0);
                expected.AddRange(BitConverter.GetBytes((ulong)content.Length)); // little-endian on every test host
                expected.AddRange(Encoding.UTF8.GetBytes(content));
            }

            var summary = SaveBackupContent.Measure(folder);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected.ToArray())).ToLowerInvariant(), summary.Sha256);
            Assert.Equal(2, summary.Files);
            Assert.Equal(3, summary.Bytes);
        }

        [Fact]
        public void The_empty_folder_hash_is_the_hash_of_nothing()
        {
            var folder = Path.Combine(_dir, "nothing");
            Directory.CreateDirectory(folder);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", SaveBackupContent.Measure(folder).Sha256);
        }

        [Fact]
        public void CopyTree_refuses_an_existing_target()
        {
            var target = Path.Combine(_dir, "exists");
            Directory.CreateDirectory(target);
            Assert.Throws<IOException>(() => SaveBackupContent.CopyTree(Save, target));
        }

        [Fact]
        public void Read_only_files_do_not_stop_a_prune()
        {
            FakeBackup("save-20200101-000000");
            var file = Path.Combine(Root, "save-20200101-000000", "SaveData", "Release", "v2", "1", "PlayerDataV3.es3");
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

            var result = Run("20261101-140312", SaveBackupRules.Clamp(1, 0, 0, 0));

            Assert.Equal(1, result.Removed);
            Assert.Empty(result.PruneProblems);
            Assert.Equal(new[] { "save-20261101-140312" }, RootNames());
        }

        private static bool CanRead(string path)
        {
            try
            {
                using var _ = File.OpenRead(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
