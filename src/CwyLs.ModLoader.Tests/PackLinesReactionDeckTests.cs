using System;
using CwyLs.ModLoader.Bulbul.PackLines;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class PackLinesReactionDeckTests
    {
        [Fact]
        public void Dealt_line_waits_behind_vanilla_and_needs_a_commit_to_count_as_played()
        {
            var deck = Deck();

            Assert.Null(deck.Peek(3, _ => true, count => count - 1));
            Assert.Null(deck.Peek(2, _ => true, _ => 0));
            Assert.Equal(1001, deck.Peek(1, _ => true, _ => 0));
            Assert.Equal(1001, deck.Peek(1, _ => true, _ => 0));

            deck.Consume(1001);

            Assert.Null(deck.Peek(1, _ => true, _ => 0));
            Assert.Null(deck.Peek(0, _ => true, _ => 0));
        }

        [Fact]
        public void Language_changes_only_reoffer_unplayed_lines_until_the_game_refills()
        {
            var deck = Deck(1001, 1002);

            Assert.Equal(1001, deck.Peek(3, episode => episode == 1001, _ => 0));
            deck.Consume(1001);
            Assert.Equal(1002, deck.Peek(3, episode => episode == 1002, _ => 0));

            Assert.Null(deck.Peek(3, _ => false, _ => throw new InvalidOperationException()));
            Assert.Equal(1002, deck.Peek(2, _ => true, _ => 0));
            deck.Consume(1002);
            Assert.Null(deck.Peek(2, _ => true, _ => 0));

            deck.Refill();

            Assert.Null(deck.Peek(2, _ => true, count => count - 1));
            Assert.Equal(1001, deck.Peek(1, _ => true, _ => 0));
        }

        [Fact]
        public void Skipped_vanilla_draws_cannot_offer_a_dealt_line_from_an_exhausted_pool()
        {
            var deck = Deck();

            Assert.Equal(1001, deck.Peek(2, _ => true, _ => 0));
            Assert.Null(deck.Peek(0, _ => true, _ => 0));

            deck.Refill();
            Assert.Equal(1001, deck.Peek(2, _ => true, _ => 0));
        }

        [Fact]
        public void A_pack_reload_replaces_dealt_entries_without_restoring_vanilla_picks()
        {
            var deck = Deck();
            Assert.Equal(1001, deck.Peek(2, _ => true, _ => 0));

            deck.Rebind(new[] { 1002 }, new[] { 1003 });

            Assert.Equal(1002, deck.Peek(1, _ => true, _ => 0));
            deck.Consume(1002);
            Assert.Null(deck.Peek(1, _ => true, _ => throw new InvalidOperationException()));
            Assert.Null(deck.Peek(0, _ => true, _ => 0));
        }

        private static PackLinesReactionDeck Deck(params int[] episodes)
        {
            Assert.True(PackLinePools.TryGet("click.normal", out var pool));
            return new PackLinesReactionDeck(pool!, episodes.Length == 0 ? new[] { 1001 } : episodes, Array.Empty<int>());
        }
    }
}
