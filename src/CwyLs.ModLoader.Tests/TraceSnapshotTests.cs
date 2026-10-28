using System;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceSnapshotTests
    {
        /// <summary>Stands in for the live game: the plugin's probe, without a game.</summary>
        private sealed class FakeStateProvider : ITraceStateProvider
        {
            private readonly TraceSnapshot _snapshot;

            public FakeStateProvider(TraceSnapshot snapshot) => _snapshot = snapshot;

            public TraceSnapshot Capture() => _snapshot;
        }

        private static TraceSnapshot Full() =>
            new TraceSnapshot
            {
                Timestamp = new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9)),
                Frame = 1200,
                Realtime = new DateTime(2026, 9, 22, 13, 45, 1, 123),
                UnityTime = 42.5,
                GameClock = "13:45:01",
                HeroineState = "WorkTyping",
                Pomodoro = new PomodoroSnapshot { Type = "Work", Running = true, RemainingSeconds = 123.45, Loop = 2 },
                Music = new MusicSnapshot
                {
                    Current = "Lo-Fi Night",
                    Index = 3,
                    Playlist = "42 tracks, shuffle",
                    IsPlaying = true,
                    Volume = 0.5,
                },
                Environment = "preset 0",
                Scene = "RoomScene",
                WindowsOpen = new[] { "Day", "Sakura" },
                FocusedControl = "Canvas/Note/Input",
            };

        private readonly TraceLineBuilder _builder = new TraceLineBuilder();

        [Fact]
        public void A_full_snapshot_has_the_documented_shape()
        {
            var line = _builder.Snapshot(new FakeStateProvider(Full()).Capture());

            Assert.Equal(
                "{\"snapshot\":true,\"t\":\"2026-09-22T13:45:01.123+09:00\",\"f\":1200," +
                "\"realtime\":\"2026-09-22T13:45:01.123\",\"unityTime\":42.5,\"gameClock\":\"13:45:01\"," +
                "\"heroineState\":\"WorkTyping\"," +
                "\"pomodoro\":{\"type\":\"Work\",\"running\":true,\"remainingSec\":123.5,\"loop\":2}," +
                "\"music\":{\"current\":\"Lo-Fi Night\",\"index\":3,\"playlist\":\"42 tracks, shuffle\"," +
                "\"isPlaying\":true,\"volume\":0.5}," +
                "\"environment\":\"preset 0\",\"scene\":\"RoomScene\",\"windowsOpen\":[\"Day\",\"Sakura\"]," +
                "\"focusedControl\":\"Canvas/Note/Input\",\"health\":null}",
                line);
        }

        [Fact]
        public void An_unresolvable_game_writes_nulls_rather_than_guesses()
        {
            var empty = new TraceSnapshot
            {
                Timestamp = new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9)),
                Realtime = new DateTime(2026, 9, 22, 13, 45, 1, 123),
            };

            var line = _builder.Snapshot(new FakeStateProvider(empty).Capture());

            Assert.Contains("\"heroineState\":null", line);
            Assert.Contains("\"pomodoro\":null", line);
            Assert.Contains("\"music\":null", line);
            Assert.Contains("\"environment\":null", line);
            Assert.Contains("\"scene\":null", line);
            Assert.Contains("\"windowsOpen\":null", line);
            Assert.Contains("\"gameClock\":null", line);
            Assert.Contains("\"focusedControl\":null", line);
        }

        [Fact]
        public void Partly_resolved_sections_keep_the_members_they_do_have()
        {
            var partial = Full();
            partial.Music = new MusicSnapshot { Current = "Lo-Fi Night" };
            partial.WindowsOpen = Array.Empty<string>();

            var line = _builder.Snapshot(partial);

            Assert.Contains("\"music\":{\"current\":\"Lo-Fi Night\",\"index\":null,\"playlist\":null,\"isPlaying\":null,\"volume\":null}", line);
            Assert.Contains("\"windowsOpen\":[]", line);
        }

        [Fact]
        public void The_console_summary_is_one_readable_line()
        {
            var summary = _builder.SnapshotSummary(Full());

            Assert.Equal(
                "TRACE snapshot f=1200 rt=42.5 clock=13:45:01 scene=RoomScene heroine=WorkTyping " +
                "pomo=Work/run/123s/loop2 music=\"Lo-Fi Night\" playing idx=3 vol=0.5 env=preset 0 " +
                "focus=Canvas/Note/Input windows=Day,Sakura",
                summary);

            Assert.DoesNotContain("\n", summary);
        }

        [Fact]
        public void The_console_summary_survives_a_snapshot_with_nothing_in_it()
        {
            var summary = _builder.SnapshotSummary(new TraceSnapshot());

            Assert.Contains("heroine=?", summary);
            Assert.Contains("pomo=?", summary);
            Assert.Contains("music=?", summary);
            Assert.Contains("windows=-", summary);
        }

        [Fact]
        public void Snapshot_rejects_a_null_reading_loudly()
        {
            Assert.Throws<ArgumentNullException>(() => _builder.Snapshot(null!));
        }
    }
}
