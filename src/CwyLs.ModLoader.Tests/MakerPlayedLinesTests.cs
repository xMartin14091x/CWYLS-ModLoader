using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Satone.Say;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>TD-34: <c>line.spoken</c> names the line a Pack Maker Play started, not the runtime Say row it was spoken as.</summary>
    public class MakerPlayedLinesTests
    {
        [Fact]
        public void A_Say_row_Play_queued_names_its_line_another_Say_row_names_none_and_a_game_row_names_itself()
        {
            var played = new MakerPlayedLines();
            played.Note(SayIdGenerator.LocalizationId(12), "pomodoro_start_002");

            Assert.Equal("pomodoro_start_002", played.LineId("cwy_say_12_001"));
            Assert.Null(played.LineId("cwy_say_13_001"));
            Assert.Equal("morning_wake_001", played.LineId("morning_wake_001"));
            Assert.Null(played.LineId(null));
            Assert.Null(played.LineId(string.Empty));
        }

        [Fact]
        public void Only_the_last_plays_are_kept_and_blank_notes_are_ignored()
        {
            var played = new MakerPlayedLines();
            played.Note(" ", "line");
            played.Note("cwy_say_0_001", null);
            Assert.Null(played.LineId("cwy_say_0_001"));

            for (var i = 1; i <= MakerPlayedLines.MaxEntries + 1; i++)
            {
                played.Note(SayIdGenerator.LocalizationId(i), "line_" + i);
            }

            Assert.Null(played.LineId(SayIdGenerator.LocalizationId(1)));
            Assert.Equal("line_2", played.LineId(SayIdGenerator.LocalizationId(2)));
            Assert.Equal("line_" + (MakerPlayedLines.MaxEntries + 1), played.LineId(SayIdGenerator.LocalizationId(MakerPlayedLines.MaxEntries + 1)));
        }
    }
}
