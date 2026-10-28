using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-AUDIO save safety: the music save is Steam-Cloud-synced, so a vanilla game must never see a
    /// cwy- UUID in it. The guard strips them for the write and puts them back for the live game.
    /// </summary>
    public class AudioMusicSaveGuardTests
    {
        private const string GameA = "1a2b3c4d-0000-4000-8000-00000000a001";
        private const string GameB = "1a2b3c4d-0000-4000-8000-00000000a004";
        private const string GameC = "1a2b3c4d-0000-4000-8000-00000000a005";

        private static readonly string PackX = PackMusicUuid.For("cwy.demo.audio", "audio/music/rainy-desk.ogg");
        private static readonly string PackY = PackMusicUuid.For("cwy.demo.audio", "audio/music/b-side.wav");

        [Fact]
        public void Strip_leaves_only_the_games_uuids_in_every_list()
        {
            var favorites = new List<string> { PackX, GameA };
            var order = new List<string> { GameA, PackX, GameB, PackY, GameC };
            var excluded = new List<string> { PackY };

            var stash = MusicSaveGuard.Strip(favorites, order, excluded);

            Assert.Equal(new[] { GameA }, favorites);
            Assert.Equal(new[] { GameA, GameB, GameC }, order);
            Assert.Empty(excluded);
            Assert.Equal(4, stash.RemovedCount);
            foreach (var list in new[] { favorites, order, excluded })
            {
                Assert.DoesNotContain(list, PackMusicUuid.IsPackUuid);
            }
        }

        [Fact]
        public void Restore_puts_every_list_back_exactly()
        {
            var favorites = new List<string> { PackX, GameA };
            var order = new List<string> { PackY, GameA, PackX, GameB, PackX, GameC };
            var excluded = new List<string> { GameB, PackY };
            var before = new[] { new List<string>(favorites), new List<string>(order), new List<string>(excluded) };

            var stash = MusicSaveGuard.Strip(favorites, order, excluded);
            MusicSaveGuard.Restore(stash);
            MusicSaveGuard.Restore(stash);

            Assert.Equal(before[0], favorites);
            Assert.Equal(before[1], order);
            Assert.Equal(before[2], excluded);
            Assert.True(stash.Restored);
        }

        [Fact]
        public void A_vanilla_setting_is_untouched()
        {
            var order = new List<string> { GameA, GameB };

            var stash = MusicSaveGuard.Strip(new List<string>(), order, null);

            Assert.True(stash.IsEmpty);
            Assert.Equal(new[] { GameA, GameB }, order);
            MusicSaveGuard.Restore(stash);
            Assert.Equal(new[] { GameA, GameB }, order);
        }

        [Fact]
        public void An_upper_case_prefix_is_stripped_too()
        {
            var order = new List<string> { "CWY-0123", GameA };

            MusicSaveGuard.Strip(null, order, null);

            Assert.Equal(new[] { GameA }, order);
        }

        [Fact]
        public void Capture_remembers_favourites_exclusions_and_positions()
        {
            var favorites = new List<string> { GameA, PackX };
            var order = new List<string> { GameA, PackX, GameB, PackY };
            var excluded = new List<string> { PackY };

            var state = MusicSaveGuard.Capture(MusicSaveGuard.Strip(favorites, order, excluded));

            Assert.Equal(new[] { PackX }, state.Favorites);
            Assert.Equal(new[] { PackY }, state.Excluded);
            Assert.Equal(2, state.Order.Count);
            Assert.Equal(PackX, state.Order[0].Uuid);
            Assert.Equal(1, state.Order[0].Index);
            Assert.Equal(3, state.Order[1].Index);
        }

        [Fact]
        public void Reinsert_restores_installed_tracks_and_ignores_the_rest()
        {
            var state = new PackMusicState(
                new[] { PackX, "cwy-uninstalled" },
                new[] { PackY },
                new[] { new PackMusicOrder(PackY, 3), new PackMusicOrder(PackX, 1), new PackMusicOrder("cwy-uninstalled", 0) });
            var favorites = new List<string> { GameA };
            var order = new List<string> { GameA, GameB, GameC };
            var excluded = new List<string>();

            var inserted = MusicSaveGuard.Reinsert(favorites, order, excluded, state, new HashSet<string> { PackX, PackY });

            Assert.Equal(4, inserted);
            Assert.Equal(new[] { GameA, PackX }, favorites);
            Assert.Equal(new[] { PackY }, excluded);
            Assert.Equal(new[] { GameA, PackX, GameB, PackY, GameC }, order);

            // A second launch with the same state adds nothing twice.
            Assert.Equal(0, MusicSaveGuard.Reinsert(favorites, order, excluded, state, new HashSet<string> { PackX, PackY }));
        }

        [Fact]
        public void The_state_file_round_trips_and_holds_ids_only()
        {
            var state = new PackMusicState(new[] { PackX, GameA }, new[] { PackY }, new[] { new PackMusicOrder(PackX, 7) });

            var json = state.ToJson();
            var parsed = PackMusicState.Parse(json);

            Assert.True(parsed.IsValid, parsed.Problem);
            Assert.Equal(new[] { PackX }, parsed.State.Favorites); // a game UUID is never kept here
            Assert.Equal(new[] { PackY }, parsed.State.Excluded);
            Assert.Equal(7, Assert.Single(parsed.State.Order).Index);
            Assert.DoesNotContain(GameA, json);
        }

        [Theory]
        [InlineData("{")]
        [InlineData("[]")]
        [InlineData("{\"schema\":9}")]
        public void A_bad_state_file_reads_as_empty_with_a_problem(string json)
        {
            var parsed = PackMusicState.Parse(json);

            Assert.False(parsed.IsValid);
            Assert.True(parsed.State.IsEmpty);
        }

        [Fact]
        public void Merge_keeps_what_was_remembered_for_packs_that_are_not_installed()
        {
            var now = new PackMusicState(new[] { PackX }, null, new[] { new PackMusicOrder(PackX, 2) });
            var before = new PackMusicState(new[] { PackY, "cwy-away" }, new[] { "cwy-away" }, new[] { new PackMusicOrder("cwy-away", 5), new PackMusicOrder(PackY, 1) });

            var merged = PackMusicState.Merge(now, before, new HashSet<string> { PackX, PackY });

            // PackY is installed and no longer a favourite now: forgotten. cwy-away is not installed: kept.
            Assert.Equal(new[] { PackX, "cwy-away" }, merged.Favorites);
            Assert.Equal(new[] { "cwy-away" }, merged.Excluded);
            Assert.Equal(2, merged.Order.Count);
        }
    }
}
