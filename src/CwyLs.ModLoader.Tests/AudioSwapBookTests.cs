using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>D-AUDIO: originals, claims and the A/B selection behind every row swap.</summary>
    public class AudioSwapBookTests
    {
        private const string Key = "ambient/LightRain";

        private sealed class Clip
        {
            public Clip(string name) => Name = name;

            public string Name { get; }

            public override string ToString() => Name;
        }

        private static KeyValuePair<string, AudioPackClaim<Clip>> PackClaim(string key, Clip clip) =>
            new KeyValuePair<string, AudioPackClaim<Clip>>(key, new AudioPackClaim<Clip>("pack/" + clip.Name, clip));

        [Fact]
        public void The_pack_variant_is_the_default_and_the_row_gets_the_replacement()
        {
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            book.SetPackClaims(new[] { PackClaim(Key, pack) });

            book.Observe(Key, game);

            Assert.Equal(AudioVariant.Pack, book.VariantOf(Key));
            Assert.Same(pack, book.Desired(Key, game, active: true));
            Assert.Same(game, book.OriginalOf(Key));
        }

        [Fact]
        public void Selecting_the_original_puts_the_game_clip_back()
        {
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            book.SetPackClaims(new[] { PackClaim(Key, pack) });
            book.Observe(Key, game);

            book.Select(Key, AudioVariant.Original);

            Assert.Same(game, book.Desired(Key, pack, active: true));
            Assert.Null(book.Desired(Key, game, active: true));

            book.Select(Key, AudioVariant.Pack);
            Assert.Same(pack, book.Desired(Key, game, active: true));
        }

        [Fact]
        public void One_of_our_clips_is_never_recorded_as_the_original()
        {
            // A rebuilt master table (cloned rows) can hand us a row that still holds our clip.
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            book.SetPackClaims(new[] { PackClaim(Key, pack) });
            book.Observe(Key, game);

            book.Observe(Key, pack);

            Assert.Same(game, book.OriginalOf(Key));
            Assert.True(book.IsOurs(pack));
            Assert.False(book.IsOurs(game));
        }

        [Fact]
        public void A_reloaded_game_clip_replaces_the_remembered_original()
        {
            var book = new AudioSwapBook<Clip>();
            var first = new Clip("game-load-1");
            var second = new Clip("game-load-2");
            book.Observe(Key, first);

            book.Observe(Key, second);

            Assert.Same(second, book.OriginalOf(Key));
        }

        [Fact]
        public void Switching_the_feature_off_restores_every_row()
        {
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            book.SetPackClaims(new[] { PackClaim(Key, pack) });
            book.Observe(Key, game);

            Assert.Same(game, book.Desired(Key, pack, active: false));
            Assert.Null(book.Desired(Key, game, active: false));
        }

        [Fact]
        public void A_mod_claim_beats_the_pack_and_falls_back_when_disposed()
        {
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            var mod = new Clip("mod");
            book.SetPackClaims(new[] { PackClaim(Key, pack) });
            book.Observe(Key, game);

            var handle = book.AddRuntime(Key, "mod:sample", mod);
            Assert.Same(mod, book.ReplacementOf(Key, out var source));
            Assert.Equal("mod:sample", source);

            handle.Dispose();
            handle.Dispose();
            Assert.Same(pack, book.ReplacementOf(Key, out source));
            Assert.Equal("pack/pack", source);

            // The retired mod clip is still recognised, so a row holding it is put right.
            Assert.True(book.IsOurs(mod));
            Assert.Same(pack, book.Desired(Key, mod, active: true));
        }

        [Fact]
        public void A_pack_reload_drops_pack_claims_that_are_gone_but_keeps_mod_claims()
        {
            var book = new AudioSwapBook<Clip>();
            var game = new Clip("game");
            var pack = new Clip("pack");
            var mod = new Clip("mod");
            book.SetPackClaims(new[] { PackClaim(Key, pack), PackClaim("ambient/HeavyRain", new Clip("heavy")) });
            book.AddRuntime("system_se/Click", "mod:x", mod);
            book.Observe(Key, game);

            book.SetPackClaims(null);

            Assert.Null(book.ReplacementOf(Key, out _));
            Assert.Same(mod, book.ReplacementOf("system_se/Click", out _));
            Assert.Equal(1, book.ClaimedCount);

            // The row still holds the old pack clip: it goes back to the game's.
            Assert.Same(game, book.Desired(Key, pack, active: true));
        }

        [Fact]
        public void Without_a_known_original_a_foreign_row_is_left_alone()
        {
            var book = new AudioSwapBook<Clip>();
            book.SetPackClaims(new[] { PackClaim(Key, new Clip("pack")) });

            Assert.Null(book.Desired(Key, null, active: true));
            Assert.Null(book.Desired("ambient/Wind", new Clip("wind"), active: true));
        }
    }
}
