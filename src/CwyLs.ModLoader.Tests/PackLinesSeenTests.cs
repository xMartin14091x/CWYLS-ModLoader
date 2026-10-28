using System.Collections.Generic;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack small talks the player has heard are remembered in mod data and put back into the game's
    /// in-memory played list before each lottery (without it, a pack small talk played 6 times against
    /// 1 game small talk, because every launch made it "unseen" again).
    /// </summary>
    public class PackLinesSeenTests
    {
        private const string Scene = "cwy_th_smalltalk_scene_1";

        [Fact]
        public void The_file_round_trips_sorted()
        {
            var text = PackLineSeen.ToJson(new[] { "b", Scene, "a" });
            var seen = PackLineSeen.Parse(text, out var problem);
            Assert.Equal(string.Empty, problem);
            Assert.Equal(new HashSet<string> { "a", "b", Scene }, seen);
            Assert.True(text.IndexOf("\"a\"") < text.IndexOf("\"b\""));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_file_is_an_empty_set_without_a_problem(string? text)
        {
            Assert.Empty(PackLineSeen.Parse(text, out var problem));
            Assert.Equal(string.Empty, problem);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"schema\":1}")]
        public void A_broken_file_is_an_empty_set_with_a_reason(string text)
        {
            Assert.Empty(PackLineSeen.Parse(text, out var problem));
            Assert.NotEqual(string.Empty, problem);
        }

        [Fact]
        public void Remembered_pack_ids_join_the_played_list_once_and_retired_ids_never_do()
        {
            var played = new List<string> { "smalltalk_01", "smalltalk_02" };
            var seen = new[] { Scene, "cwy_th_retired_scene" };
            bool IsPack(string id) => id == Scene;

            Assert.Equal(1, PackLineSeen.MarkSeen(played, seen, IsPack));
            Assert.Equal(0, PackLineSeen.MarkSeen(played, seen, IsPack));
            Assert.Equal(new[] { "smalltalk_01", "smalltalk_02", Scene }, played);
        }

        [Fact]
        public void A_missing_list_adds_nothing() =>
            Assert.Equal(0, PackLineSeen.MarkSeen(null, new[] { Scene }, _ => true));

        [Fact]
        public void Once_marked_the_save_filter_still_keeps_the_id_out_of_the_file()
        {
            var played = new List<string> { "smalltalk_01" };
            PackLineSeen.MarkSeen(played, new[] { Scene }, id => id == Scene);

            var held = PackLineSaveFilter.Strip(played, id => id == Scene);
            Assert.Equal(new[] { "smalltalk_01" }, played);
            PackLineSaveFilter.Restore(played, held);
            Assert.Equal(new[] { "smalltalk_01", Scene }, played);
        }
    }
}
