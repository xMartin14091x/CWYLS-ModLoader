using System;
using System.IO;

namespace CWY.Seamcheck
{
    /// <summary>
    /// The tool's diagnostic channel. Everything goes to one writer (stderr in the CLI) so standard
    /// output carries only the report. Debug lines are the equivalent of the game's LogDebug: kept
    /// out of the way unless <c>--verbose</c> is given, but never dropped silently by a catch block.
    /// </summary>
    internal sealed class ToolLog
    {
        private readonly TextWriter _sink;

        /// <summary>Creates a log over a writer.</summary>
        /// <param name="sink">Where lines go.</param>
        /// <param name="verbose">Whether debug lines are written.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sink"/> is null.</exception>
        internal ToolLog(TextWriter sink, bool verbose)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            Verbose = verbose;
        }

        /// <summary>A log that writes nowhere; for tests.</summary>
        internal static ToolLog Null => new ToolLog(TextWriter.Null, verbose: false);

        /// <summary>Whether debug lines are written.</summary>
        internal bool Verbose { get; }

        /// <summary>How many warnings were written.</summary>
        internal int Warnings { get; private set; }

        /// <summary>A detail only worth seeing when investigating (<c>--verbose</c>).</summary>
        /// <param name="message">The line.</param>
        internal void Debug(string message)
        {
            if (Verbose)
            {
                _sink.WriteLine("seamcheck: debug: " + message);
            }
        }

        /// <summary>Something the reader should look at; does not change the exit code by itself.</summary>
        /// <param name="message">The line.</param>
        internal void Warn(string message)
        {
            Warnings++;
            _sink.WriteLine("seamcheck: warning: " + message);
        }

        /// <summary>A tool failure; the caller decides the exit code.</summary>
        /// <param name="message">The line.</param>
        internal void Error(string message) => _sink.WriteLine("seamcheck: error: " + message);
    }

    /// <summary>A failure of the tool itself (bad input, unreadable table, failed control): exit code 2.</summary>
    internal sealed class SeamcheckException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">What went wrong, phrased for the console.</param>
        internal SeamcheckException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with its cause.</summary>
        /// <param name="message">What went wrong, phrased for the console.</param>
        /// <param name="inner">The underlying exception.</param>
        internal SeamcheckException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
