using CwyLs.ModLoader.Core.MasterData;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-1: the change detector that keeps a pack rescan from re-injecting identical rows.
    /// </summary>
    public class MasterDataFingerprintTests
    {
        private static MasterDataPackInput Pack(
            string id = "cwy.example",
            string? localization = """[{"ID": "a", "En": "one"}]""",
            string? novel = null,
            string? groups = null) =>
            new MasterDataPackInput(id)
            {
                LocalizationJson = localization,
                NovelJson = novel,
                ScenarioGroupsJson = groups
            };

        [Fact]
        public void No_packs_is_the_empty_fingerprint()
        {
            Assert.Equal(MasterDataFingerprint.Empty, MasterDataFingerprint.Compute(null));
            Assert.Equal(MasterDataFingerprint.Empty, MasterDataFingerprint.Compute(new MasterDataPackInput?[0]));
            Assert.Equal(
                MasterDataFingerprint.Empty, MasterDataFingerprint.Compute(new MasterDataPackInput?[] { null }));
        }

        [Fact]
        public void The_same_files_fingerprint_the_same()
        {
            var first = MasterDataFingerprint.Compute(new[] { Pack() });
            var second = MasterDataFingerprint.Compute(new[] { Pack() });

            Assert.Equal(first, second);
            Assert.NotEqual(MasterDataFingerprint.Empty, first);
            Assert.StartsWith("p1:l", first);
        }

        [Fact]
        public void A_changed_file_changes_the_fingerprint()
        {
            var before = MasterDataFingerprint.Compute(new[] { Pack() });
            var after = MasterDataFingerprint.Compute(
                new[] { Pack(localization: """[{"ID": "a", "En": "two"}]""") });

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void An_equal_length_edit_still_changes_the_fingerprint()
        {
            // The case a size-and-mtime check would miss.
            var before = MasterDataFingerprint.Compute(new[] { Pack(localization: """[{"ID": "ab"}]""") });
            var after = MasterDataFingerprint.Compute(new[] { Pack(localization: """[{"ID": "ba"}]""") });

            Assert.NotEqual(before, after);
        }

        [Fact]
        public void An_absent_file_differs_from_an_empty_one()
        {
            var absent = MasterDataFingerprint.Compute(new[] { Pack(novel: null) });
            var empty = MasterDataFingerprint.Compute(new[] { Pack(novel: "") });

            Assert.NotEqual(absent, empty);
        }

        [Fact]
        public void Pack_id_and_pack_order_are_part_of_it()
        {
            var a = MasterDataFingerprint.Compute(new[] { Pack("pack.a") });
            var b = MasterDataFingerprint.Compute(new[] { Pack("pack.b") });
            Assert.NotEqual(a, b);

            var forwards = MasterDataFingerprint.Compute(
                new[] { Pack("pack.a"), Pack("pack.b", localization: """[{"ID": "a", "En": "two"}]""") });
            var backwards = MasterDataFingerprint.Compute(
                new[] { Pack("pack.b", localization: """[{"ID": "a", "En": "two"}]"""), Pack("pack.a") });

            // Order decides which pack overrides which, so it has to be part of the fingerprint.
            Assert.NotEqual(forwards, backwards);
        }

        [Fact]
        public void Subtitle_tables_are_part_of_it()
        {
            var without = MasterDataFingerprint.Compute(new[] { Pack() });

            var withTable = Pack();
            withTable.AddSubtitles("data/subtitles/th.json", """{"a": "หนึ่ง"}""");

            var renamed = Pack();
            renamed.AddSubtitles("data/subtitles/vi.json", """{"a": "หนึ่ง"}""");

            var edited = Pack();
            edited.AddSubtitles("data/subtitles/th.json", """{"a": "สอง"}""");

            var all = new[]
            {
                without,
                MasterDataFingerprint.Compute(new[] { withTable }),
                MasterDataFingerprint.Compute(new[] { renamed }),
                MasterDataFingerprint.Compute(new[] { edited })
            };

            Assert.Equal(all.Length, new System.Collections.Generic.HashSet<string>(all).Count);
        }

        [Fact]
        public void Non_ascii_text_is_hashed_by_both_bytes_of_each_unit()
        {
            // Two characters whose low bytes match: only mixing the high byte tells them apart.
            var first = MasterDataFingerprint.Compute(new[] { Pack(localization: "Ā") });
            var second = MasterDataFingerprint.Compute(new[] { Pack(localization: "Ȁ") });

            Assert.NotEqual(first, second);
        }
    }
}
