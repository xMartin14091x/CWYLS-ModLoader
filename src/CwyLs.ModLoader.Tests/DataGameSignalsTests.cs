using System;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C5: the data bus hears pomodoro and music changes as signals from the event bridge instead
    /// of subscribing a second time. The mapping must reproduce exactly what it subscribed to before,
    /// with the same <c>event:&lt;Name&gt;</c> reasons.
    /// </summary>
    public class DataGameSignalsTests
    {
        [Theory]
        [InlineData(GameSignal.PomodoroService, "OnStartPomodoro", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.PomodoroService, "OnStartWork", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.PomodoroService, "OnEndWork", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.PomodoroService, "OnStartBreak", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.PomodoroService, "OnEndBreak", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.PomodoroService, "OnCompletePomodoro", GameDataSection.Pomodoro)]
        [InlineData(GameSignal.MusicService, "OnChangeMusic", GameDataSection.Music)]
        [InlineData(GameSignal.MusicService, "OnPlayMusic", GameDataSection.Music)]
        public void The_eight_observables_the_data_bus_subscribed_to_are_still_changes(
            string source, string name, GameDataSection expected)
        {
            Assert.True(GameDataSignals.TryMap(new GameSignal(source, name), out var section, out var reason));
            Assert.Equal(expected, section);
            Assert.Equal("event:" + name, reason);
        }

        [Theory]
        [InlineData(GameSignal.PomodoroService, "OnPause")]
        [InlineData(GameSignal.PomodoroService, "OnUnpause")]
        [InlineData(GameSignal.PomodoroService, "OnUpdatePomodoro")]
        [InlineData(GameSignal.MusicService, "OnEnableShuffle")]
        [InlineData(GameSignal.MusicService, "OnDisableShuffle")]
        [InlineData(GameSignal.MusicService, "OnStartWork")]
        [InlineData("NoteService", "OnAddPage")]
        public void Signals_the_data_bus_never_reported_stay_out(string source, string name)
        {
            Assert.False(GameDataSignals.TryMap(new GameSignal(source, name), out var section, out var reason));
            Assert.Equal(GameDataSection.Unknown, section);
            Assert.Equal(string.Empty, reason);
        }

        [Fact]
        public void A_default_signal_maps_to_nothing_rather_than_throwing()
        {
            Assert.False(GameDataSignals.TryMap(default, out _, out _));
        }

        [Fact]
        public void A_signal_needs_a_source_and_a_name()
        {
            Assert.Throws<ArgumentException>(() => new GameSignal(" ", "OnStartWork"));
            Assert.Throws<ArgumentException>(() => new GameSignal(GameSignal.PomodoroService, null!));
            Assert.Equal("PomodoroService.OnStartWork", new GameSignal(GameSignal.PomodoroService, "OnStartWork").ToString());
            Assert.Equal("?.?", default(GameSignal).ToString());
        }
    }
}
