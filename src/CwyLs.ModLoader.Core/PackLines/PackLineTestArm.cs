using System;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// The console's <c>lines test &lt;id&gt;</c> for a timer line: the next time the game
    /// picks a line for that timer moment and mode, it gets this one. The game's own timer moment
    /// still decides when it plays, so the line goes through the whole normal pipeline. One line at a
    /// time; thread-safe.
    /// </summary>
    public sealed class PackLineTestArm
    {
        private readonly object _gate = new object();
        private PackLine? _armed;
        private DateTime _armedAt;

        /// <summary>The armed line, or null.</summary>
        public PackLine? Armed
        {
            get { lock (_gate) { return _armed; } }
        }

        /// <summary>When the armed line was armed (the clock the caller passed); default when nothing is armed.</summary>
        public DateTime ArmedAt
        {
            get { lock (_gate) { return _armed is null ? default : _armedAt; } }
        }

        /// <summary>Arms a timer line, replacing any armed one.</summary>
        /// <param name="line">A line of a <see cref="PackLineFamily.TimerLine"/> pool. Required.</param>
        /// <param name="now">The time, for <see cref="ArmedAt"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        /// <exception cref="ArgumentException">The line is not a timer line.</exception>
        public void Arm(PackLine line, DateTime now)
        {
            if (line is null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            if (line.Pool.Family != PackLineFamily.TimerLine)
            {
                throw new ArgumentException(
                    line.Pool.Id + " is not a timer pool; click-style lines are played at once, not armed", nameof(line));
            }

            lock (_gate)
            {
                _armed = line;
                _armedAt = now;
            }
        }

        /// <summary>Disarms. Returns the line that was armed, or null.</summary>
        public PackLine? Clear()
        {
            lock (_gate)
            {
                var was = _armed;
                _armed = null;
                return was;
            }
        }

        /// <summary>
        /// Takes the armed line when the game is picking for its timer moment and mode and the game
        /// shows one of its languages; otherwise leaves it armed.
        /// </summary>
        /// <param name="phase">The moment the game is picking for.</param>
        /// <param name="mode">The timer mode the game is in.</param>
        /// <param name="languageCode">The language the game shows.</param>
        /// <param name="line">The taken line, or null.</param>
        /// <returns>True when a line was taken (and disarmed).</returns>
        public bool TryTake(PackLineTimerPhase phase, PackLineTimerMode mode, string? languageCode, out PackLine? line)
        {
            lock (_gate)
            {
                line = null;
                var armed = _armed;
                if (armed is null
                    || armed.Pool.Phase != phase
                    || armed.Pool.TimerMode != mode
                    || !armed.IsOfferedIn(languageCode))
                {
                    return false;
                }

                _armed = null;
                line = armed;
                return true;
            }
        }
    }
}
