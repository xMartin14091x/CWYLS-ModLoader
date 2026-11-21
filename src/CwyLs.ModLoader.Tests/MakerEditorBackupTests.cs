using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Loaders;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="MakerStores"/> over a real temporary pack folder: atomic writes, creator files moved
    /// aside (never deleted), backups restored, the ledger that tells Pack Maker's own files from a
    /// creator's, and the layering rule. Audio is an invented tone; text is invented.
    /// </summary>
    public sealed class MakerEditorBackupTests : IDisposable
    {
        private readonly string _pack = Path.Combine(Path.GetTempPath(), "cwy-maker-backup-" + Guid.NewGuid().ToString("N"), "my.thai");
        private readonly MakerPackFiles _files;

        public MakerEditorBackupTests()
        {
            Directory.CreateDirectory(_pack);
            _files = new MakerPackFiles(_pack);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(_pack)!, recursive: true);
            }
            catch (IOException)
            {
                // A temp folder the OS still holds is not a test failure.
            }
        }

        [Fact]
        public void A_subtitle_save_creates_the_table_and_leaves_no_temp_file()
        {
            var result = MakerStores.SaveSubtitle(_files, "th", "cwy_demo_01_001", "สวัสดี");

            Assert.True(result.Ok);
            Assert.True(result.Written);
            Assert.Null(result.SafetyCopy);
            Assert.Equal("{\n  \"cwy_demo_01_001\": \"สวัสดี\"\n}\n", File.ReadAllText(Path.Combine(_pack, "data", "subtitles", "th.json")));
            Assert.Single(Directory.GetFiles(Path.Combine(_pack, "data", "subtitles"), "*", SearchOption.AllDirectories));
        }

        [Fact]
        public void The_first_edit_of_a_creators_table_keeps_a_copy_once()
        {
            Write("data/subtitles/th.json", "{\n  \"a\": \"mine\"\n}\n");

            var first = MakerStores.SaveSubtitle(_files, "th", "a", "edited");
            var second = MakerStores.SaveSubtitle(_files, "th", "a", "edited again");

            Assert.Equal("_maker_backup/data/subtitles/th.json", first.SafetyCopy);
            Assert.Null(second.SafetyCopy);
            Assert.Equal("{\n  \"a\": \"mine\"\n}\n", Read("_maker_backup/data/subtitles/th.json"));
            Assert.Equal("{\n  \"a\": \"edited again\"\n}\n", Read("data/subtitles/th.json"));
        }

        [Fact]
        public void A_bom_is_kept()
        {
            File.WriteAllBytes(Path.Combine(Dir("data/subtitles"), "th.json"), MakerEditorTestBytes.WithText(Encoding.UTF8.GetPreamble(), "{\"a\": \"1\"}"));

            MakerStores.SaveSubtitle(_files, "th", "b", "2");

            var bytes = File.ReadAllBytes(Path.Combine(_pack, "data", "subtitles", "th.json"));
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal("{\"a\": \"1\",\"b\": \"2\"}", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }

        [Fact]
        public void A_broken_table_is_left_untouched()
        {
            Write("data/subtitles/th.json", "{ \"a\": \"1\", }");

            var result = MakerStores.SaveSubtitle(_files, "th", "a", "2");

            Assert.False(result.Ok);
            Assert.Contains("data/subtitles/th.json", result.Problem);
            Assert.Equal("{ \"a\": \"1\", }", Read("data/subtitles/th.json"));
            Assert.False(File.Exists(Path.Combine(_pack, "_maker_backup", "data", "subtitles", "th.json")));
        }

        [Fact]
        public void Removing_a_subtitle_deletes_only_its_key()
        {
            Write("data/subtitles/th.json", "{\n  \"a\": \"1\",\n  \"b\": \"2\"\n}\n");

            var result = MakerStores.SaveSubtitle(_files, "th", "a", null);

            Assert.True(result.Written);
            Assert.Equal("1", result.Previous);
            Assert.Null(result.Current);
            Assert.Equal("{\n  \"b\": \"2\"\n}\n", Read("data/subtitles/th.json"));
        }

        [Fact]
        public void A_dub_moves_the_creators_ogg_aside_and_is_written()
        {
            Write("audio/voice/th/Voice_X.ogg", "creator ogg");
            var wav = Tone();

            var result = MakerStores.SaveVoice(_files, "th", "Voice_X", wav);

            Assert.Equal("audio/voice/th/Voice_X.wav", result.Path);
            Assert.False(result.Existed);
            Assert.Equal(new[] { Pair("audio/voice/th/Voice_X.ogg", "_maker_backup/audio/voice/th/Voice_X.ogg") }, result.MovedAside);
            Assert.False(File.Exists(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.ogg")));
            Assert.Equal("creator ogg", Read("_maker_backup/audio/voice/th/Voice_X.ogg"));
            Assert.Equal(wav, File.ReadAllBytes(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.wav")));
            Assert.True(_files.IsMakerWritten("audio/voice/th/Voice_X.wav"));
        }

        [Fact]
        public void A_second_save_overwrites_pack_makers_own_file_without_a_backup()
        {
            MakerStores.SaveVoice(_files, "th", "Voice_X", Tone(440));
            var second = MakerStores.SaveVoice(_files, "th", "Voice_X", Tone(880));

            Assert.True(second.Existed);
            Assert.Empty(second.MovedAside);
            Assert.False(Directory.Exists(Path.Combine(_pack, "_maker_backup", "audio")));
            Assert.Equal(Tone(880), File.ReadAllBytes(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.wav")));
        }

        [Fact]
        public void A_creators_own_wav_at_the_target_is_moved_aside_before_the_save()
        {
            Write("audio/voice/th/Voice_X.wav", "creator wav");

            var result = MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            Assert.True(result.Existed);
            Assert.Equal(new[] { Pair("audio/voice/th/Voice_X.wav", "_maker_backup/audio/voice/th/Voice_X.wav") }, result.MovedAside);
            Assert.Equal("creator wav", Read("_maker_backup/audio/voice/th/Voice_X.wav"));
        }

        [Fact]
        public void A_file_in_another_spelling_of_the_language_folder_is_moved_aside_too()
        {
            Write("audio/voice/thai/Voice_X.ogg", "creator ogg in thai/");
            Write("audio/voice/thai/Voice_Y.ogg", "another line, untouched");

            var result = MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            Assert.Single(result.MovedAside);
            Assert.Equal("audio/voice/thai/Voice_X.ogg", result.MovedAside[0].Key);
            Assert.True(File.Exists(Path.Combine(_pack, "audio", "voice", "thai", "Voice_Y.ogg")));
            Assert.Equal(new[] { "audio/voice/th/Voice_X.wav" }, _files.VoiceFiles("th", "Voice_X"));
        }

        [Fact]
        public void An_existing_folder_spelled_in_capitals_is_reused()
        {
            Directory.CreateDirectory(Path.Combine(_pack, "audio", "voice", "TH"));

            Assert.Equal("audio/voice/TH/Voice_X.wav", MakerStores.VoicePath(_files, "th", "Voice_X"));
        }

        [Fact]
        public void Removing_a_dub_deletes_pack_makers_wav_and_restores_the_ogg()
        {
            Write("audio/voice/th/Voice_X.ogg", "creator ogg");
            MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            var result = MakerStores.RemoveVoice(_files, "th", "Voice_X");

            Assert.True(result.Existed);
            Assert.True(result.Deleted);
            Assert.Equal(new[] { "audio/voice/th/Voice_X.ogg" }, result.Restored);
            Assert.Equal("creator ogg", Read("audio/voice/th/Voice_X.ogg"));
            Assert.False(File.Exists(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.wav")));
            Assert.False(File.Exists(Path.Combine(_pack, "_maker_backup", "audio", "voice", "th", "Voice_X.ogg")));
            Assert.Null(MakerSubtitleFile.Read(Read("_maker_backup/maker-written.json"), "audio/voice/th/Voice_X.wav"));
        }

        [Fact]
        public void Removing_restores_a_creators_wav_the_save_displaced()
        {
            Write("audio/voice/th/Voice_X.wav", "creator wav");
            MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            var result = MakerStores.RemoveVoice(_files, "th", "Voice_X");

            Assert.True(result.Deleted);
            Assert.Equal(new[] { "audio/voice/th/Voice_X.wav" }, result.Restored);
            Assert.Equal("creator wav", Read("audio/voice/th/Voice_X.wav"));
        }

        [Fact]
        public void Removing_a_creators_own_wav_moves_it_aside_and_does_not_bring_it_straight_back()
        {
            Write("audio/voice/th/Voice_X.wav", "creator wav");

            var result = MakerStores.RemoveVoice(_files, "th", "Voice_X");

            Assert.False(result.Deleted);
            Assert.Empty(result.Restored);
            Assert.Equal("creator wav", Read("_maker_backup/audio/voice/th/Voice_X.wav"));
            Assert.False(File.Exists(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.wav")));
        }

        [Fact]
        public void A_wav_edited_by_hand_after_a_save_is_no_longer_pack_makers_and_is_kept()
        {
            MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());
            File.WriteAllText(Path.Combine(_pack, "audio", "voice", "th", "Voice_X.wav"), "retouched by the creator");

            var result = MakerStores.RemoveVoice(_files, "th", "Voice_X");

            Assert.False(result.Deleted);
            Assert.Equal("retouched by the creator", Read("_maker_backup/audio/voice/th/Voice_X.wav"));
        }

        [Fact]
        public void A_backup_is_never_overwritten()
        {
            Write("_maker_backup/audio/voice/th/Voice_X.ogg", "older backup");
            Write("audio/voice/th/Voice_X.ogg", "newer creator ogg");

            var result = MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            var moved = result.MovedAside[0].Value;
            Assert.StartsWith("_maker_backup/audio/voice/th/Voice_X.", moved);
            Assert.EndsWith(".ogg", moved);
            Assert.NotEqual("_maker_backup/audio/voice/th/Voice_X.ogg", moved);
            Assert.Equal("older backup", Read("_maker_backup/audio/voice/th/Voice_X.ogg"));
            Assert.Equal("newer creator ogg", Read(moved));
        }

        [Fact]
        public void Removing_with_nothing_there_is_a_quiet_no_op()
        {
            var result = MakerStores.RemoveVoice(_files, "th", "Voice_X");

            Assert.False(result.Existed);
            Assert.Empty(result.Restored);
            Assert.False(Directory.Exists(Path.Combine(_pack, "_maker_backup")));
        }

        [Fact]
        public void A_restore_waits_when_its_place_is_taken()
        {
            Write("_maker_backup/audio/voice/th/Voice_X.ogg", "backup");
            Write("audio/voice/th/Voice_X.ogg", "someone put a new one here");

            Assert.False(_files.TryRestore("audio/voice/th/Voice_X.ogg"));
            Assert.Equal("backup", Read("_maker_backup/audio/voice/th/Voice_X.ogg"));
        }

        [Fact]
        public void Voice_files_ignore_hidden_files_other_names_and_formats()
        {
            Write("audio/voice/th/Voice_X.ogg", "1");
            Write("audio/voice/th/.Voice_X.ogg", "mac resource fork");
            Write("audio/voice/th/Voice_X.mp3", "not loaded");
            Write("audio/voice/en/Voice_X.ogg", "another language");

            var found = _files.VoiceFiles("th", "Voice_X");

            Assert.Contains("audio/voice/th/Voice_X.ogg", found);
            Assert.DoesNotContain("audio/voice/th/.Voice_X.ogg", found);
            Assert.DoesNotContain("audio/voice/th/Voice_X.mp3", found);
            Assert.DoesNotContain("audio/voice/en/Voice_X.ogg", found);
        }

        [Fact]
        public void Atomic_writes_replace_the_whole_file()
        {
            _files.WriteAtomic("data/x.bin", new byte[] { 1, 2, 3, 4, 5 });
            _files.WriteAtomic("data/x.bin", new byte[] { 9 });

            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(_pack, "data", "x.bin")));
            Assert.Single(Directory.GetFiles(Path.Combine(_pack, "data")));
        }

        [Fact]
        public void A_broken_ledger_is_set_aside_and_a_new_one_started()
        {
            Write("_maker_backup/maker-written.json", "{ not json");

            MakerStores.SaveVoice(_files, "th", "Voice_X", Tone());

            Assert.True(_files.IsMakerWritten("audio/voice/th/Voice_X.wav"));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(_pack, "_maker_backup"), "maker-written*").Length);
        }

        [Fact]
        public void The_later_pack_wins_like_the_loader()
        {
            var layers = new List<MakerLayer<string>>
            {
                new MakerLayer<string>("a.base", "base text"),
                new MakerLayer<string>("b.mine", null),
                new MakerLayer<string>("c.top", "top text"),
            };

            Assert.Equal("c.top", MakerLayers.Effective(layers).PackId);
            layers[2] = new MakerLayer<string>("c.top", null);
            Assert.Equal("a.base", MakerLayers.Effective(layers).PackId);
            Assert.False(MakerLayers.Effective(new MakerLayer<string>[0]).HasValue);
            Assert.Equal("b", MakerLayers.LastPath(new[] { "a", "b" }));
            Assert.Null(MakerLayers.LastPath(new string[0]));
        }

        [Fact]
        public void Fingerprints_change_with_any_byte()
        {
            Assert.Equal(MakerPackFiles.Fingerprint(new byte[] { 1, 2 }), MakerPackFiles.Fingerprint(new byte[] { 1, 2 }));
            Assert.NotEqual(MakerPackFiles.Fingerprint(new byte[] { 1, 2 }), MakerPackFiles.Fingerprint(new byte[] { 2, 1 }));
            Assert.StartsWith("2:", MakerPackFiles.Fingerprint(new byte[] { 1, 2 }));
        }

        private static KeyValuePair<string, string> Pair(string a, string b) => new KeyValuePair<string, string>(a, b);

        private static byte[] Tone(double hz = 440) => WavWriter.Tone(hz, 0.05, 22050, 1);

        private string Dir(string relative)
        {
            var dir = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private void Write(string relative, string text)
        {
            var full = Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        private string Read(string relative) =>
            File.ReadAllText(Path.Combine(_pack, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal static class MakerEditorTestBytes
    {
        public static byte[] WithText(byte[] head, string tail)
        {
            var body = Encoding.UTF8.GetBytes(tail);
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }
    }
}
