using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace CWY.Seamcheck
{
    /// <summary>
    /// Listens to BepInEx's log while plugin tables are read, so whatever plugin code logs offline is
    /// shown instead of vanishing into a logger with no listeners. Warnings and errors become notes
    /// in the report (they usually explain an offline difference, e.g. the voice seam falling back
    /// to its constant type names because AudioManager is not loaded in the tool); the rest goes to
    /// the debug channel.
    /// </summary>
    internal sealed class PluginLogCapture : ILogListener
    {
        private readonly ToolLog _log;
        private readonly List<string> _notes = new List<string>();

        private PluginLogCapture(ToolLog log)
        {
            _log = log;
        }

        /// <summary>Warnings and errors logged by plugin code while the capture was installed.</summary>
        internal IReadOnlyList<string> Notes => _notes;

        /// <summary>Installs a capture on BepInEx's global listener list; dispose it to remove it.</summary>
        /// <param name="log">Where debug-level plugin lines go.</param>
        internal static PluginLogCapture Install(ToolLog log)
        {
            var capture = new PluginLogCapture(log ?? throw new ArgumentNullException(nameof(log)));
            Logger.Listeners.Add(capture);
            return capture;
        }

        /// <inheritdoc />
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            var line = "[" + eventArgs.Level + "] " + eventArgs.Source?.SourceName + ": "
                + eventArgs.Data?.ToString()?.Trim();
            if ((eventArgs.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0)
            {
                _notes.Add(line);
            }
            else
            {
                _log.Debug("plugin log " + line);
            }
        }

        /// <inheritdoc />
        public void Dispose() => Logger.Listeners.Remove(this);
    }
}
