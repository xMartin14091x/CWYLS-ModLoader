using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-AUDIO paid-content refusal (project rule: no paid content is ever modded). The game's only DLC is
    /// the original soundtrack (Steam app 3718890): story music that plays a soundtrack track is never
    /// replaced, and a pack track never re-lists a game track.
    /// </summary>
    public class AudioPaidContentTests
    {
        private static readonly SoundtrackCatalog Soundtrack = AudioReplacementPlanTests.Soundtrack;

        [Fact]
        public void The_soundtrack_dlc_is_the_games_dlc_1()
        {
            // NestopiSystem.Steam/BulbulSteamDLC - DLC_1 = 3718890u.
            Assert.Equal(3718890u, AudioPaidContent.SoundtrackDlcAppId);
        }

        [Fact]
        public void Story_music_that_plays_a_soundtrack_track_is_refused()
        {
            var reason = AudioPaidContent.CheckRowReplacement(AudioReplacementPlanTests.Main32, Soundtrack);

            Assert.NotNull(reason);
            Assert.Contains("main32_Music", reason);
            Assert.Contains("'Without You'", reason);
            Assert.Contains("3718890", reason);
        }

        [Fact]
        public void A_pack_replacing_the_soundtrack_story_cue_is_refused_and_named()
        {
            var plan = AudioReplacementPlan.Build(
                AudioCandidate.FromPacks(new[]
                {
                    new KeyValuePair<string, PackContentIndex>(
                        "greedy.pack",
                        PackContentIndex.Build(new[] { "audio/direction_music/main32_Music.ogg", "audio/direction_music/Without You_main32_full.wav" })),
                }),
                new[] { AudioReplacementPlanTests.Main32 },
                Soundtrack);

            Assert.Empty(plan.Assignments);
            Assert.Equal(2, plan.RefusedCount);
            foreach (var skip in plan.Skipped)
            {
                Assert.Equal(AudioSkipKind.Paid, skip.Kind);
                Assert.Same(AudioReplacementPlanTests.Main32, skip.Row);
            }

            Assert.Contains("1 refused", AudioReplacementPlan.Build(
                AudioCandidate.FromPacks(new[]
                {
                    new KeyValuePair<string, PackContentIndex>("p", PackContentIndex.Build(new[] { "audio/direction_music/main32_Music.ogg" })),
                }),
                new[] { AudioReplacementPlanTests.Main32 },
                Soundtrack).Summarize());
        }

        [Fact]
        public void Story_music_is_refused_while_the_soundtrack_list_is_unknown()
        {
            var reason = AudioPaidContent.CheckRowReplacement(AudioReplacementPlanTests.Main32, SoundtrackCatalog.Empty);

            Assert.NotNull(reason);
            Assert.Contains("not loaded", reason);
        }

        [Fact]
        public void Story_music_that_plays_no_game_track_may_be_replaced()
        {
            var cue = new AudioRowDescriptor(AudioTable.DirectionMusic, "pack_cue", "my_own_cue", 10);

            Assert.Null(AudioPaidContent.CheckRowReplacement(cue, Soundtrack));
        }

        [Theory]
        [InlineData(AudioTable.Ambient, "LightRain", "LightRain")]
        [InlineData(AudioTable.AmbientSe, "MusicboxSound", "MemorieMusicbox_Beyond the farthest broder")]
        [InlineData(AudioTable.SystemSe, "BuyItem", "bulbul_systemse_buy_item")]
        [InlineData(AudioTable.TimerSe, "Finish_A", "bulbul_timerse_timerend_A")]
        public void Ambient_and_sound_effect_rows_are_never_paid(AudioTable table, string name, string clip)
        {
            // No row type of these tables carries a purchase or DLC flag (AmbientSoundMasterData,
            // AmbientSeSoundMasterData.cs, SystemSeMasterData.cs, TimerSeMasterData.cs) - even with a
            // soundtrack that happens to share a word.
            var row = new AudioRowDescriptor(table, name, clip, 1);

            Assert.Null(AudioPaidContent.CheckRowReplacement(row, Soundtrack));
            Assert.Null(AudioPaidContent.CheckRowReplacement(row, SoundtrackCatalog.Empty));
        }

        [Theory]
        [InlineData("Without You", "my-track")]
        [InlineData("without-you", "x")]
        [InlineData("New Song", "WithoutYou")]
        [InlineData("AQUA ROOM", "a")]
        public void A_pack_track_that_relists_a_soundtrack_track_is_refused(string title, string stem)
        {
            var reason = AudioPaidContent.CheckMusicTrack(title, stem, Soundtrack);

            Assert.NotNull(reason);
            Assert.Contains("soundtrack", reason);
            Assert.Contains("3718890", reason);
        }

        [Fact]
        public void A_pack_track_that_relists_a_free_game_track_is_refused_too()
        {
            var reason = AudioPaidContent.CheckMusicTrack("Daywind", "daywind", Soundtrack);

            Assert.NotNull(reason);
            Assert.Contains("game's own track 'Daywind'", reason);
        }

        [Fact]
        public void A_new_title_is_allowed()
        {
            Assert.Null(AudioPaidContent.CheckMusicTrack("Rainy Desk", "rainy-desk", Soundtrack));
        }

        [Fact]
        public void The_music_plan_refuses_a_relisted_track_and_keeps_the_rest()
        {
            var plan = PackMusicPlan.Build(
                new[]
                {
                    new PackMusicInput(
                        "p",
                        PackContentIndex.Build(new[] { "audio/music/Without You.ogg", "audio/music/rainy-desk.wav" }),
                        null),
                },
                Soundtrack);

            Assert.Equal("rainy-desk", Assert.Single(plan.Tracks).Title);
            Assert.Equal(1, plan.RefusedCount);
            var skip = Assert.Single(plan.Skipped);
            Assert.True(skip.IsPaid);
            Assert.Equal("audio/music/Without You.ogg", skip.Path);
        }

        [Fact]
        public void Normalize_keeps_letters_and_digits_only()
        {
            Assert.Equal("withoutyoumain32full", AudioPaidContent.Normalize("Without You_main32_full"));
            Assert.Equal(string.Empty, AudioPaidContent.Normalize(null));
            Assert.Equal("morningötea2", AudioPaidContent.Normalize("Morning Ö-Tea 2"));

            // The game writes some titles with TMP rich text.
            Assert.Equal("guesttuneplaceholderremix2026", AudioPaidContent.Normalize("Guest Tune <size=14.5>-Placeholder Remix 2026-</size>"));
        }

        [Fact]
        public void A_prefix_shorter_than_four_characters_never_matches()
        {
            var catalog = new SoundtrackCatalog(new[] { new SoundtrackTrack("Sky", "x", "Sky", "u", true) });

            Assert.Null(catalog.FindPrefixOf("skyline_story_cue"));
            Assert.NotNull(catalog.FindExact("sky"));
        }
    }
}
