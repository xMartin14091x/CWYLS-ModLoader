using System;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>
    /// One firing of a game R3 observable, named the way the game names it: the service and the
    /// observable property, e.g. <c>PomodoroService.OnStartWork</c>.
    /// </summary>
    /// <remarks>
    /// The event bridge in the game adapter is the only subscriber to the game's pomodoro and
    /// music observables. It turns each firing into a typed event for mods and hands the same
    /// firing, as a <see cref="GameSignal"/>, to framework consumers that need the game's own name -
    /// the data bus reports <c>event:OnStartWork</c> as its change reason. Earlier, the data bus
    /// subscribed to the same observables a second time.
    /// </remarks>
    public readonly struct GameSignal
    {
        /// <summary>Source name of <c>Bulbul.PomodoroService</c>'s observables.</summary>
        public const string PomodoroService = "PomodoroService";

        /// <summary>Source name of <c>MusicService</c>'s observables.</summary>
        public const string MusicService = "MusicService";

        /// <summary>Creates a signal.</summary>
        /// <param name="source">The service, e.g. <see cref="PomodoroService"/>.</param>
        /// <param name="name">The observable property, e.g. <c>OnStartWork</c>.</param>
        /// <exception cref="ArgumentException">Either name is null or blank.</exception>
        public GameSignal(string source, string name)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("must not be null or blank", nameof(source));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("must not be null or blank", nameof(name));
            }

            Source = source;
            Name = name;
        }

        /// <summary>The service that raised it, e.g. <c>PomodoroService</c>.</summary>
        public string Source { get; }

        /// <summary>The observable that fired, e.g. <c>OnStartWork</c>.</summary>
        public string Name { get; }

        /// <inheritdoc />
        public override string ToString() => (Source ?? "?") + "." + (Name ?? "?");
    }
}
