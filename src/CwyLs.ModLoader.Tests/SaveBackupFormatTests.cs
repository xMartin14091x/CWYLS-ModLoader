using System;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Backups;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Launch save backups: folder names, sizes, the backup.json marker and the console text.</summary>
    public class SaveBackupFormatTests
    {
        [Fact]
        public void Folder_names_are_save_plus_the_local_time_to_the_second()
        {
            Assert.Equal("save-20261101-140312", SaveBackupNames.FolderName(new DateTime(2026, 11, 1, 14, 3, 12, 999)));
            Assert.Equal("save-20260105-000000", SaveBackupNames.FolderName(new DateTime(2026, 1, 5)));
            Assert.Equal("save-20261101-140312.partial-0badf00d", SaveBackupNames.PartialName("save-20261101-140312", "0badf00d"));
        }

        [Fact]
        public void A_name_round_trips()
        {
            var time = new DateTime(2026, 2, 28, 23, 59, 59);
            Assert.True(SaveBackupNames.TryParse(SaveBackupNames.FolderName(time), out var parsed));
            Assert.Equal(time, parsed);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("save-")]
        [InlineData("save-20261101")]
        [InlineData("save-20261101-14031")]
        [InlineData("save-20261101-1403120")]
        [InlineData("save-20261101_140312")]
        [InlineData("Save-20261101-140312")]
        [InlineData("SAVE-20261101-140312")]
        [InlineData(" save-20261101-140312")]
        [InlineData("save-20261101-140312 ")]
        [InlineData("save-20261101-140312-copy")]
        [InlineData("save-20261101-140312.partial-0badf00d")]
        [InlineData("save-scene-20261101-140312-main_05")]
        [InlineData("save-20261301-140312")] // month 13
        [InlineData("save-20260230-140312")] // 30 Feb
        [InlineData("save-20261101-250000")] // hour 25
        [InlineData("save-20261101-146012")] // minute 60
        [InlineData("save-2026110١-140312")] // an Arabic-Indic digit
        [InlineData("save-+2026110-140312")]
        public void Anything_but_the_exact_pattern_is_not_a_backup_name(string? name)
        {
            Assert.False(SaveBackupNames.TryParse(name, out _));
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(1023, "1023 B")]
        [InlineData(1024, "1 KB")]
        [InlineData(188416, "184 KB")]
        [InlineData(1536, "2 KB")]
        [InlineData(1048576, "1.0 MB")]
        [InlineData(3565158, "3.4 MB")]
        [InlineData(-5, "0 B")]
        public void Sizes_print_like_the_log(long bytes, string expected)
        {
            Assert.Equal(expected, SaveBackupNames.Size(bytes));
        }

        [Fact]
        public void The_marker_round_trips()
        {
            var created = new DateTimeOffset(2026, 11, 1, 14, 3, 12, 345, TimeSpan.FromHours(7));
            var hash = new string('c', 64);
            var json = new SaveBackupManifest(1, created, "0.9.0", "1.18.0", 46, 188416, hash).ToJson();

            Assert.True(SaveBackupManifest.TryParse(json, out var back, out var problem), problem);
            Assert.Equal(1, back!.Schema);
            Assert.Equal(created, back.Created);
            Assert.Equal(TimeSpan.FromHours(7), back.Created!.Value.Offset);
            Assert.Equal("0.9.0", back.LoaderVersion);
            Assert.Equal("1.18.0", back.GameVersion);
            Assert.Equal(46, back.Files);
            Assert.Equal(188416, back.Bytes);
            Assert.Equal(hash, back.ContentSha256);
        }

        [Fact]
        public void The_marker_is_readable_json_one_member_per_line()
        {
            var json = new SaveBackupManifest(1, null, "0.9.0", null, 1, 2, null).ToJson();
            Assert.Equal(
                "{\n  \"kind\": \"cwyls.save-backup\",\n  \"schema\": 1,\n  \"created\": null,\n  \"loaderVersion\": \"0.9.0\",\n"
                + "  \"gameVersion\": null,\n  \"files\": 1,\n  \"bytes\": 2,\n  \"contentSha256\": null\n}\n",
                json);
        }

        [Theory]
        [InlineData(null, "empty")]
        [InlineData("", "not valid JSON")]
        [InlineData("{ nope", "not valid JSON")]
        [InlineData("[]", "not a JSON object")]
        [InlineData("{\"schema\":1}", "kind")]
        [InlineData("{\"kind\":\"cwyls.save-backups\",\"schema\":1}", "kind")]
        [InlineData("{\"kind\":\"CWYLS.SAVE-BACKUP\",\"schema\":1}", "kind")]
        [InlineData("{\"kind\":\"cwyls.save-backup\"}", "schema")]
        [InlineData("{\"kind\":\"cwyls.save-backup\",\"schema\":0}", "schema")]
        [InlineData("{\"kind\":\"cwyls.save-backup\",\"schema\":\"1\"}", "schema")]
        [InlineData("{\"kind\":\"cwyls.save-backup\",\"schema\":1.5}", "schema")]
        public void A_marker_that_is_not_ours_is_refused(string? json, string why)
        {
            Assert.False(SaveBackupManifest.TryParse(json, out var manifest, out var problem));
            Assert.Null(manifest);
            Assert.Contains(why, problem);
        }

        [Fact]
        public void A_marker_needs_only_kind_and_schema_and_a_bad_hash_reads_as_none()
        {
            Assert.True(SaveBackupManifest.TryParse("{\"kind\":\"cwyls.save-backup\",\"schema\":2}", out var bare, out _));
            Assert.Equal(2, bare!.Schema);
            Assert.Null(bare.ContentSha256);
            Assert.Null(bare.Created);

            Assert.True(SaveBackupManifest.TryParse(
                "{\"kind\":\"cwyls.save-backup\",\"schema\":1,\"contentSha256\":\"" + new string('C', 64) + "\",\"files\":-3}",
                out var upper, out _));
            Assert.Null(upper!.ContentSha256); // uppercase is not what we write
            Assert.Equal(0, upper.Files);
        }

        [Fact]
        public void The_list_shows_time_name_size_and_the_rules_that_keep_each_backup()
        {
            var root = Path.Combine(Path.GetTempPath(), "cwy-savebackup-text-" + Guid.NewGuid().ToString("N"));
            try
            {
                Make(root, "save-20261101-214000", 188416);
                Make(root, "save-20261101-091500", 188000);
                Make(root, "save-20261002-180000", 100);
                Directory.CreateDirectory(Path.Combine(root, "notes"));
                var inventory = SaveBackupInventory.Scan(root);

                var text = SaveBackupText.List(inventory, SaveBackupRules.Clamp(1, 1, 0, 0), true, "/live/SaveData");
                var lines = text.Split('\n');

                Assert.Equal("3 backup(s), 368 KB; keep last 1, daily 1, weekly 0, monthly 0", lines[0]);
                Assert.Equal("  2026-11-01 21:40:00  save-20261101-214000   184 KB  46 files  kept: last, daily 2026-11-01", lines[1]);
                Assert.Equal("  2026-11-01 09:15:00  save-20261101-091500   184 KB  46 files  no rule keeps it: removed at the next launch", lines[2]);
                Assert.StartsWith("  2026-10-02 18:00:00  save-20261002-180000", lines[3]);
                Assert.Equal("left alone (not backups made by the loader):", lines[4]);
                Assert.Equal("  notes - not named save-YYYYMMDD-HHMMSS", lines[5]);
                Assert.Equal("live save: /live/SaveData", lines[6]);
                Assert.Equal("backups:   " + root, lines[7]);

                var off = SaveBackupText.List(inventory, SaveBackupRules.Clamp(1, 0, 0, 0), false, "/live/SaveData");
                Assert.Contains("backups are off", off);
                Assert.Contains("no rule keeps it (not removed while backups are off)", off);
                Assert.DoesNotContain("removed at the next launch", off);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void The_list_of_a_missing_folder_says_none()
        {
            var inventory = SaveBackupInventory.Scan(Path.Combine(Path.GetTempPath(), "cwy-missing-" + Guid.NewGuid().ToString("N")));
            Assert.False(inventory.Exists);
            Assert.StartsWith("0 backup(s); keep last 5", SaveBackupText.List(inventory, SaveBackupRules.Default, true, "/s"));
        }

        [Fact]
        public void Help_says_how_to_restore_by_hand_with_the_game_closed()
        {
            var help = SaveBackupText.Help("/live/SaveData", "/game/CWYLS Backups", true);
            Assert.Contains("game CLOSED", help);
            Assert.Contains("SaveData-before-restore", help);
            Assert.Contains("live save: /live/SaveData", help);
            Assert.Contains("backups:   /game/CWYLS Backups", help);
            Assert.Contains("no command", help);
            Assert.Contains("Cloud conflict", help);
            Assert.Contains("restore by hand with the game closed", SaveBackupText.Description);
            Assert.Contains("are off", SaveBackupText.Help("/s", "/b", false));
        }

        [Fact]
        public void Folders_prints_both_paths()
        {
            Assert.Equal("live save: /s\nbackups:   /b", SaveBackupText.Folders("/s", "/b"));
        }

        private static void Make(string root, string name, long bytes)
        {
            var folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "backup.json"),
                new SaveBackupManifest(1, null, "0.9.0", "1.18.0", 46, bytes, new string('0', 64)).ToJson());
        }
    }
}
