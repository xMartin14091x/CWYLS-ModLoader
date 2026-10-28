using System;
using BepInEx.Logging;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// Mirrors the framework's own log lines into the console's scrollback, so the overlay shows
    /// what the rest of the loader is doing without the user leaving the game for <c>LogOutput.log</c>.
    /// </summary>
    /// <remarks>
    /// Filtered deliberately, by the same rule as <c>cwy-console.log</c>
    /// (<see cref="ConsoleLogFilter"/>, through <see cref="Logging.CwyConsoleListener.Admits"/>): CWY
    /// sources above Debug, plus Error and Fatal from anyone - a Unity exception from the game
    /// matters even though its source is not ours. Debug stays out; the trace file is where that
    /// level of detail belongs.
    /// </remarks>
    internal sealed class ConsoleLogListener : ILogListener
    {
        private readonly ConsoleBuffer _buffer;
        private int _failures;

        /// <summary>Creates the listener.</summary>
        /// <param name="buffer">Where the lines go. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is null.</exception>
        internal ConsoleLogListener(ConsoleBuffer buffer) =>
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

        /// <inheritdoc />
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs is null || !Logging.CwyConsoleListener.Admits(eventArgs))
                {
                    return;
                }

                var text = eventArgs.Data?.ToString() ?? string.Empty;

                _buffer.Add(WithoutStamp(text), KindOf(eventArgs.Level));
            }
            catch (Exception ex)
            {
                // A logging sink must never throw into the logger, and must not be silent either.
                // The first failure goes to Unity's log (LogOutput.log): BepInEx relays it as a
                // "Unity Log" warning, which the shared filter keeps out of this mirror, so it cannot
                // loop back here. Later failures are only counted.
                if (System.Threading.Interlocked.Increment(ref _failures) == 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "CWY console log mirror failed; further failures are counted, not logged: "
                        + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Nothing owned: the buffer outlives the listener and belongs to ConsoleFeature.
        }

        /// <summary>
        /// The overlay has no room for a timestamp column, so a canonical record is shown as
        /// <c>TAG  primary  k=v</c>. Every line is a record; before that change, only the trace
        /// plugin's lines carried a stamp and the rest were shown as they arrived.
        /// </summary>
        /// <param name="text">The logged message.</param>
        internal static string WithoutStamp(string text)
        {
            const int stampAndSeparator = 14;   // HH:mm:ss.fff + two spaces

            return ConsoleRecordParser.TryParse(text, out _) && text.Length > stampAndSeparator
                ? text.Substring(stampAndSeparator)
                : text;
        }

        private static ConsoleLineKind KindOf(LogLevel level)
        {
            if (level == LogLevel.Error || level == LogLevel.Fatal)
            {
                return ConsoleLineKind.Error;
            }

            return level == LogLevel.Warning ? ConsoleLineKind.Warning : ConsoleLineKind.Info;
        }
    }
}
