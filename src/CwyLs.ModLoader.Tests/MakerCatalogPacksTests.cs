using System;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// DS-02: reading the packs' subtitle tables and voice sets off disk (which pack each entry comes
    /// from, the game's own winner rules), the change fingerprint, and the "editable pack" rules.
    /// Every pack, line and text here is invented.
    /// </summary>
    public sealed class MakerCatalogPacksTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cwy-maker-packs-tests-" + Guid.NewGuid().ToString("N"));

        public MakerCatalogPacksTests()
        {
            Directory.CreateDirectory(_root);
        }

        private string ModsRoot => Path.Combine(_root, "CWYLS Mods");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
        }

        [Fact]
        public void A_later_pack_wins_a_subtitle_and_each_entry_names_its_pack()
        {
            var first = Pack("lab.base");
            var second = Pack("lab.fix");
            Write(first, "data/subtitles/th.json", "{ \"sample_a\": \"เอ\", \"sample_b\": \"บี\" }");
            Write(second, "data/subtitles/th.json", "{ \"sample_b\": \"บี ใหม่\" }");

            var content = MakerPackScanner.Scan(new[] { first, second });

            Assert.True(content.TryGetSubtitle("th", "sample_a", out var a));
            Assert.Equal("เอ", a!.Text);
            Assert.Equal("lab.base", a.PackId);
            Assert.True(content.TryGetSubtitle("TH", "sample_b", out var b));
            Assert.Equal("บี ใหม่", b!.Text);
            Assert.Equal("lab.fix", b.PackId);
            Assert.False(content.TryGetSubtitle("th", "sample_c", out _));
            Assert.False(content.TryGetSubtitle("ko", "sample_a", out _));
            Assert.Equal(2, content.SubtitleCount);
            Assert.Equal(new[] { "th" }, content.SubtitleLanguages);
        }

        [Fact]
        public void An_empty_entry_is_no_subtitle_and_bad_entries_are_counted_not_fatal()
        {
            var pack = Pack("lab.base");
            Write(pack, "data/subtitles/th.json", "{ \"sample_a\": \"\", \"sample_b\": 42, \"sample_c\": \"ซี\" }");
            Write(pack, "data/subtitles/ko.json", "{ broken");
            Write(pack, "data/subtitles/notes.txt", "not a table");

            var content = MakerPackScanner.Scan(new[] { pack });

            Assert.False(content.TryGetSubtitle("th", "sample_a", out _));
            Assert.False(content.TryGetSubtitle("th", "sample_b", out _));
            Assert.True(content.TryGetSubtitle("th", "sample_c", out _));
            Assert.Equal(2, content.RejectedEntries);
            Assert.Equal(new[] { "ko", "th" }, content.LanguagesOf("lab.base"));
        }

        [Fact]
        public void Voice_sets_follow_the_voice_features_rules()
        {
            var first = Pack("lab.base");
            var second = Pack("lab.fix");
            WriteBytes(first, "audio/voice/th/Voice_Sample_001.wav", MakerAudioFixtures.Wav(8000, 1, 8000));
            WriteBytes(first, "audio/voice/th/Voice_Sample_002.ogg", MakerAudioFixtures.OggVorbis(48000, 48000));
            WriteBytes(first, "audio/voice/th/Voice_Sample_002.wav", MakerAudioFixtures.Wav(8000, 1, 4000));
            WriteBytes(second, "audio/voice/thai/Voice_Sample_001.wav", MakerAudioFixtures.Wav(8000, 1, 16000));
            WriteBytes(second, "audio/voice/ko/Voice_Sample_003.wav", MakerAudioFixtures.Wav(8000, 1, 800));

            var content = MakerPackScanner.Scan(new[] { first, second });

            // Later pack over earlier; "thai" is the same set as "th".
            var one = content.FindDub("th", "Voice_Sample_001");
            Assert.Equal("lab.fix", one!.PackId);
            Assert.Equal("audio/voice/thai/Voice_Sample_001.wav", one.RelativePath);

            // Within one pack, the later path wins (.wav after .ogg).
            var two = content.FindDub("th", "Voice_Sample_002");
            Assert.Equal("audio/voice/th/Voice_Sample_002.wav", two!.RelativePath);

            Assert.Null(content.FindDub("th", "Voice_Sample_003"));
            Assert.NotNull(content.FindDub("ko", "Voice_Sample_003"));
            Assert.Null(content.FindDub("th", "voice_sample_001"));
            Assert.Null(content.FindDub("th", null));

            Assert.Equal(Path.Combine(second.RootPath!, "audio", "voice", "thai", "Voice_Sample_001.wav"), content.FullPathOf(one));
            Assert.Equal(new[] { "ko", "th" }, content.LanguagesOf("lab.fix"));
            Assert.Equal(new[] { "th" }, content.LanguagesOf("lab.base"));
        }

        [Fact]
        public void Hidden_files_nested_folders_and_packs_that_did_not_load_are_ignored()
        {
            var pack = Pack("lab.base");
            WriteBytes(pack, "audio/voice/th/._Voice_Sample_001.wav", new byte[] { 0, 1, 2 });
            WriteBytes(pack, "audio/voice/th/.DS_Store", new byte[] { 0 });
            WriteBytes(pack, "audio/voice/th/deeper/Voice_Sample_002.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            WriteBytes(pack, "audio/voice/.hidden/Voice_Sample_003.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            Write(pack, "data/subtitles/.th.json", "{ \"sample_a\": \"x\" }");

            var refusedOnDisk = Pack("lab.refused");
            WriteBytes(refusedOnDisk, "audio/voice/th/Voice_Sample_009.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            var refused = new MakerPackSource("lab.refused", null, null, refusedOnDisk.RootPath, false, null, "BadManifest");

            var content = MakerPackScanner.Scan(new[] { pack, refused });

            Assert.Null(content.FindDub("th", "Voice_Sample_001"));
            Assert.Null(content.FindDub("th", "Voice_Sample_002"));
            Assert.Null(content.FindDub("th", "Voice_Sample_009"));
            Assert.False(content.ProvidesVoice("Voice_Sample_003"));
            Assert.Equal(0, content.SubtitleCount);
            Assert.Empty(content.LanguagesOf("lab.base"));
            Assert.Null(content.RootOf("lab.refused"));
        }

        [Fact]
        public void A_voice_counts_as_provided_when_a_pack_ships_it_globally_or_in_any_language()
        {
            var pack = Pack("lab.base");
            WriteBytes(pack, "audio/voice/Voice_Global_001.ogg", MakerAudioFixtures.OggVorbis(48000, 48000));
            WriteBytes(pack, "audio/voice/ko/Voice_Korean_001.wav", MakerAudioFixtures.Wav(8000, 1, 800));

            var content = MakerPackScanner.Scan(new[] { pack });

            Assert.True(content.ProvidesVoice("Voice_Global_001"));
            Assert.True(content.ProvidesVoice("Voice_Korean_001"));
            Assert.False(content.ProvidesVoice("Voice_Nowhere_001"));

            // A global clip is an override for every language, not a dub for one.
            Assert.Null(content.FindDub("ko", "Voice_Global_001"));
        }

        [Fact]
        public void Declared_languages_are_listed_even_without_files()
        {
            var pack = Pack("lab.lang", declared: new[] { "TH", "fil" });

            var content = MakerPackScanner.Scan(new[] { pack });

            Assert.Equal(new[] { "fil", "th" }, content.LanguagesOf("lab.lang"));
            Assert.Empty(content.LanguagesOf("lab.unknown"));
        }

        [Fact]
        public void The_fingerprint_changes_when_a_table_or_a_voice_folder_changes_and_only_then()
        {
            var pack = Pack("lab.base");
            Write(pack, "data/subtitles/th.json", "{ \"sample_a\": \"เอ\" }");
            WriteBytes(pack, "audio/voice/th/Voice_Sample_001.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            var packs = new[] { pack };

            var before = MakerPackScanner.Fingerprint(packs);
            Assert.Equal(before, MakerPackScanner.Fingerprint(packs));
            Assert.Equal(before, MakerPackScanner.Scan(packs).Fingerprint);

            // A table edited in place (new size).
            Write(pack, "data/subtitles/th.json", "{ \"sample_a\": \"เอ\", \"sample_b\": \"บี\" }");
            var edited = MakerPackScanner.Fingerprint(packs);
            Assert.NotEqual(before, edited);

            // A new table.
            Write(pack, "data/subtitles/ko.json", "{}");
            var added = MakerPackScanner.Fingerprint(packs);
            Assert.NotEqual(edited, added);

            // A clip added to an existing voice folder: the folder's own time moves.
            var folder = Path.Combine(pack.RootPath!, "audio", "voice", "th");
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow.AddHours(-1));
            var settled = MakerPackScanner.Fingerprint(packs);
            WriteBytes(pack, "audio/voice/th/Voice_Sample_002.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            Assert.NotEqual(settled, MakerPackScanner.Fingerprint(packs));

            // A new language folder.
            var beforeKo = MakerPackScanner.Fingerprint(packs);
            WriteBytes(pack, "audio/voice/ko/Voice_Sample_001.wav", MakerAudioFixtures.Wav(8000, 1, 800));
            Assert.NotEqual(beforeKo, MakerPackScanner.Fingerprint(packs));

            Assert.Equal(string.Empty, MakerPackScanner.Fingerprint(null));
        }

        [Fact]
        public void Packs_are_editable_only_when_every_rule_holds_and_the_first_failure_is_the_reason()
        {
            var root = ModsRoot;
            Directory.CreateDirectory(root);
            Func<string, bool> writable = _ => true;

            var good = new MakerPackSource("lab.thai", "Lab Thai", "1.0.0", Path.Combine(root, "lab.thai"), false);
            Assert.True(MakerPackRules.Decide(good, root, writable).Editable);
            Assert.Null(MakerPackRules.Decide(good, root, writable).Reason);

            var failed = new MakerPackSource("lab.broken", null, null, null, false, null, "BadManifest - id is missing");
            Assert.Equal(MakerPackRules.NotLoadedReason + "BadManifest - id is missing.", MakerPackRules.Decide(failed, root, writable).Reason);

            var code = new MakerPackSource("lab.code", null, null, Path.Combine(root, "lab.code"), true);
            Assert.Equal(MakerPackRules.CodeModReason, MakerPackRules.Decide(code, root, writable).Reason);

            foreach (var id in new[] { "cwy.example", "cwy.demo.audio", "cwy.sample.hello", "CWY.Demo.x" })
            {
                var reference = new MakerPackSource(id, null, null, Path.Combine(root, id), false);
                Assert.Equal(MakerPackRules.ReferencePackReason, MakerPackRules.Decide(reference, root, writable).Reason);
            }

            Assert.False(MakerPackRules.IsReferencePack("cwy.examples"));
            Assert.False(MakerPackRules.IsReferencePack("lab.cwy.demo.x"));

            var outside = new MakerPackSource("lab.elsewhere", null, null, Path.Combine(_root, "elsewhere", "lab.elsewhere"), false);
            Assert.Equal(MakerPackRules.OutsidePacksRootReason, MakerPackRules.Decide(outside, root, writable).Reason);

            var nested = new MakerPackSource("lab.nested", null, null, Path.Combine(root, "group", "lab.nested"), false);
            Assert.Equal(MakerPackRules.OutsidePacksRootReason, MakerPackRules.Decide(nested, root, writable).Reason);

            var noRoot = new MakerPackSource("lab.noroot", null, null, null, false);
            Assert.Equal(MakerPackRules.OutsidePacksRootReason, MakerPackRules.Decide(noRoot, root, writable).Reason);

            Assert.Equal(MakerPackRules.NotWritableReason, MakerPackRules.Decide(good, root, _ => false).Reason);
            Assert.Equal(MakerPackRules.NotWritableReason, MakerPackRules.Decide(good, root, null).Reason);
            Assert.Equal(MakerPackRules.NotWritableReason, MakerPackRules.Decide(good, root, _ => throw new UnauthorizedAccessException()).Reason);
        }

        [Fact]
        public void Pack_folders_are_compared_as_full_paths()
        {
            var root = ModsRoot;
            Assert.True(MakerPackRules.IsDirectChildOf(Path.Combine(root, "lab.thai"), root));
            Assert.True(MakerPackRules.IsDirectChildOf(Path.Combine(root, "lab.thai") + Path.DirectorySeparatorChar, root + Path.DirectorySeparatorChar));
            Assert.True(MakerPackRules.IsDirectChildOf(Path.Combine(root, "x", "..", "lab.thai"), root));
            Assert.False(MakerPackRules.IsDirectChildOf(root, root));
            Assert.False(MakerPackRules.IsDirectChildOf(Path.Combine(root, "..", "lab.thai"), root));
            Assert.False(MakerPackRules.IsDirectChildOf(null, root));
            Assert.False(MakerPackRules.IsDirectChildOf(Path.Combine(root, "lab.thai"), null));
        }

        [Fact]
        public void A_missing_folder_is_not_writable_and_an_ordinary_one_is()
        {
            var folder = Path.Combine(ModsRoot, "lab.thai");
            Assert.False(MakerPackRules.IsWritableFolder(folder));
            Directory.CreateDirectory(folder);
            Assert.True(MakerPackRules.IsWritableFolder(folder));
            Assert.False(MakerPackRules.IsWritableFolder(" "));
        }

        [Fact]
        public void Pack_sources_need_an_id_and_fill_in_the_rest()
        {
            Assert.Throws<ArgumentException>(() => new MakerPackSource(" ", null, null, null, false));
            var source = new MakerPackSource("lab.thai", "  ", null, " ", false);
            Assert.Equal("lab.thai", source.Name);
            Assert.Equal(string.Empty, source.Version);
            Assert.Null(source.RootPath);
            Assert.True(source.Loaded);
            Assert.Empty(source.DeclaredLanguages);
        }

        private MakerPackSource Pack(string id, string[]? declared = null)
        {
            var folder = Path.Combine(ModsRoot, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ \"id\": \"" + id + "\", \"name\": \"" + id + "\", \"version\": \"1.0.0\" }");
            return new MakerPackSource(id, id, "1.0.0", folder, false, declared);
        }

        private static void Write(MakerPackSource pack, string relative, string text) =>
            WriteBytes(pack, relative, new UTF8Encoding(false).GetBytes(text));

        private static void WriteBytes(MakerPackSource pack, string relative, byte[] bytes)
        {
            var path = Path.Combine(pack.RootPath!, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
}
