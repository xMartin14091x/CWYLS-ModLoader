using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Logging
{
    /// <summary>
    /// Writes every CWY log line (and every Error/Fatal from any source) to
    /// <c>BepInEx/cwy-console.log</c> in the canonical grammar: one physical line per event,
    /// stamped with the event's own time.
    /// </summary>
    /// <remarks>
    /// Two F3 fixes live here.
    /// <list type="bullet">
    /// <item>The file used to be opened with <c>FileMode.Create</c>, so every launch destroyed the
    /// previous session - including the session a bug report was about. The previous file is now
    /// rolled to <c>cwy-console.&lt;yyyyMMdd-HHmmss&gt;.log</c> (the last
    /// <see cref="KeptRolls"/> are kept) and the live file is opened for append.</item>
    /// <item>A line that is already a <see cref="ConsoleRecord"/> is written as it stands. A direct
    /// <c>LogWarning</c>/<c>LogError</c>/<c>LogInfo</c> is wrapped into one, so the file has
    /// exactly one grammar and <c>tools/mac/watch-log.py</c> needs exactly one regex.</item>
    /// </list>
    /// </remarks>
    public sealed class CwyConsoleListener : ILogListener
    {
        /// <summary>How many rolled console files are kept before the oldest is deleted.</summary>
        public const int KeptRolls = 5;

        private readonly object _gate = new object();
        private readonly StreamWriter _writer;
        private int _failures;

        /// <summary>Path of the file being written.</summary>
        public string Path { get; }

        /// <summary>The rolled copy of the previous session, or null when there was none.</summary>
        public string? RolledTo { get; }

        /// <summary>Opens the console file, rolling the previous session out of the way first.</summary>
        /// <param name="path">Full path of <c>cwy-console.log</c>.</param>
        public CwyConsoleListener(string path)
        {
            Path = path;
            RolledTo = Roll(path);
            _writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        /// <inheritdoc />
        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (e is null || !Admits(e))
                {
                    return;
                }

                var source = e.Source?.SourceName ?? "?";
                var text = e.Data?.ToString() ?? string.Empty;
                var line = ConsoleRecordParser.TryParse(text, out _)
                    ? text
                    : ConsoleRecordFormatter.Format(Wrap(text, e.Level, source));

                lock (_gate) _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                // A logging sink must never throw into the logger, and must not be silent either.
                // The first failure goes to Unity's log (LogOutput.log): BepInEx relays it as a
                // "Unity Log" warning, which the shared filter keeps out of both console sinks, so it
                // cannot loop back here. Later failures are only counted.
                if (System.Threading.Interlocked.Increment(ref _failures) == 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "CWY console file sink failed; further failures are counted, not logged ("
                        + Path + "): " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>How many lines could not be written to the console file this session.</summary>
        public int FailureCount => System.Threading.Volatile.Read(ref _failures);

        /// <summary>
        /// The one filter both console sinks apply (<see cref="ConsoleLogFilter.Admits"/>): errors
        /// from anyone, and the framework's own lines above Debug. Shared with the in-game console's
        /// log mirror, so the file and the overlay keep the same lines.
        /// </summary>
        /// <param name="e">The BepInEx log event.</param>
        public static bool Admits(LogEventArgs e)
        {
            if (e is null)
            {
                return false;
            }

            var level = e.Level;
            return ConsoleLogFilter.Admits(
                e.Source?.SourceName,
                isErrorOrFatal: level == LogLevel.Error || level == LogLevel.Fatal,
                isDebug: level == LogLevel.Debug || level == LogLevel.All);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate) _writer.Dispose();
        }

        /// <summary>
        /// Turns a line that was logged directly (not through <see cref="ConsoleMirror"/>) into a
        /// record. A warning becomes <c>WARN</c>, an error <c>ERROR</c>, and anything else is a
        /// feature's startup summary - <c>SETUP</c> - with <c>src=</c> naming the plugin.
        /// </summary>
        /// <param name="text">The logged message.</param>
        /// <param name="level">BepInEx level of the message.</param>
        /// <param name="source">BepInEx source name, e.g. <c>CWYLS Game Adapter</c>.</param>
        public static ConsoleRecord Wrap(string text, LogLevel level, string source)
        {
            string tag;
            ConsoleLevel consoleLevel;

            if (level == LogLevel.Error || level == LogLevel.Fatal)
            {
                tag = ConsoleTags.Error;
                consoleLevel = ConsoleLevel.Error;
            }
            else if (level == LogLevel.Warning)
            {
                tag = ConsoleTags.Warn;
                consoleLevel = ConsoleLevel.Warn;
            }
            else
            {
                tag = ConsoleTags.Setup;
                consoleLevel = ConsoleLevel.Info;
            }

            return new ConsoleRecord(
                DateTimeOffset.Now,
                tag,
                text,
                new[] { new ConsoleField("src", SourceToken(source)) },
                consoleLevel);
        }

        /// <summary>A BepInEx source name as a bare, space-free field value.</summary>
        /// <param name="source">e.g. <c>CWYLS Game Adapter</c>, which becomes <c>game-adapter</c>.</param>
        public static string SourceToken(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return "?";
            }

            // The framework's own plugins are "CWYLS <name>" ("CWY <name>" before the rename).
            var text = source!.Trim();
            if (text.StartsWith("CWYLS ", StringComparison.Ordinal))
            {
                text = text.Substring(6);
            }
            else if (text.StartsWith("CWY ", StringComparison.Ordinal))
            {
                text = text.Substring(4);
            }

            return text.ToLowerInvariant().Replace(' ', '-');
        }

        /// <summary>
        /// Moves the previous session's file aside and prunes old rolls. Returns the new name of
        /// the previous session, or null when there was nothing to roll.
        /// </summary>
        /// <param name="path">Full path of the live console file.</param>
        private static string? Roll(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                {
                    return null;
                }

                var directory = System.IO.Path.GetDirectoryName(path) ?? ".";
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var extension = System.IO.Path.GetExtension(path);
                var stamp = File.GetLastWriteTime(path).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

                var rolled = System.IO.Path.Combine(directory, name + "." + stamp + extension);
                for (var i = 2; File.Exists(rolled) && i < 100; i++)
                {
                    rolled = System.IO.Path.Combine(
                        directory, name + "." + stamp + "-" + i.ToString(CultureInfo.InvariantCulture) + extension);
                }

                File.Move(path, rolled);
                Prune(directory, name, extension);
                return rolled;
            }
            catch (Exception ex)
            {
                // Losing the roll is not worth losing the session: the live file is opened for
                // append either way, so the worst case is one long file.
                CwyLsModLoader.Log.LogWarning(
                    "could not roll the previous console file: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static void Prune(string directory, string name, string extension)
        {
            var live = name + extension;
            var rolls = new List<string>();
            foreach (var file in Directory.GetFiles(directory, name + ".*" + extension))
            {
                // Never the live file, whatever the platform's pattern matcher thinks of "*".
                if (!string.Equals(System.IO.Path.GetFileName(file), live, StringComparison.Ordinal))
                {
                    rolls.Add(file);
                }
            }

            rolls.Sort(StringComparer.Ordinal);

            for (var i = 0; i < rolls.Count - KeptRolls; i++)
            {
                try
                {
                    File.Delete(rolls[i]);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        "could not delete the old console file " + rolls[i] + ": " + ex.Message);
                }
            }
        }
    }
}
