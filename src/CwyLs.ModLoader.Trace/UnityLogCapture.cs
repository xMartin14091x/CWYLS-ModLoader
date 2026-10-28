using System;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Trace;
using UnityEngine;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Forwards Unity's own log into the trace. Exceptions and errors are the single most useful
    /// thing a "what is happening right now" log can carry, and they are otherwise only in
    /// LogOutput.log with no frame number, no game clock and no surrounding calls.
    /// </summary>
    /// <remarks>
    /// The handler runs inside Unity's logger, on any thread, possibly while the game is already
    /// failing, so it allocates as little as it can, never throws, and never calls back into Unity.
    /// Repeats are merged: an exception thrown from Update arrives every frame.
    /// </remarks>
    internal static class UnityLogCapture
    {
        /// <summary>Warnings written per distinct message per second.</summary>
        private const int WarningsPerSecond = 5;

        /// <summary>Stack lines kept. Past a dozen frames it is the game's plumbing, not the cause.</summary>
        private const int StackLines = 12;

        /// <summary>Separator between message and stack when they are used as one dedupe key.</summary>
        private const string KeySeparator = " @@ ";

        private static readonly LogDedupe Dedupe = new LogDedupe();
        private static readonly RateLimiter Warnings = new RateLimiter(WarningsPerSecond);

        private static TraceConfig? _config;
        private static int _warningCount;
        private static int _errorCount;
        private static bool _installed;

        /// <summary>Subscribes to Unity's threaded log callback. Idempotent.</summary>
        internal static void Install(TraceConfig config)
        {
            _config = config;

            if (_installed)
            {
                return;
            }

            _installed = true;
            Application.logMessageReceivedThreaded += OnLogMessage;
        }

        /// <summary>Unsubscribes. Called on quit so a dead writer is never handed a message.</summary>
        internal static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;
            Application.logMessageReceivedThreaded -= OnLogMessage;
        }

        /// <summary>Reads and clears the counters the snapshot reports.</summary>
        internal static void TakeCounts(out int warnings, out int errors)
        {
            warnings = Interlocked.Exchange(ref _warningCount, 0);
            errors = Interlocked.Exchange(ref _errorCount, 0);
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            try
            {
                switch (type)
                {
                    case LogType.Exception:
                    case LogType.Error:
                    case LogType.Assert:
                        Interlocked.Increment(ref _errorCount);
                        EmitProblem(condition, stackTrace, type);
                        break;

                    case LogType.Warning:
                        Interlocked.Increment(ref _warningCount);
                        EmitWarning(condition);
                        break;

                    default:
                        // LogType.Log is the game's own running commentary; the trace has better.
                        break;
                }
            }
            catch (Exception)
            {
                // This runs inside Unity's logger: reporting a failure here could recurse straight
                // back into it, so the one safe thing to do is nothing.
            }
        }

        private static void EmitProblem(string condition, string stackTrace, LogType type)
        {
            var message = Truncate(condition, MaxMessageLength);
            var stack = FirstLines(stackTrace, StackLines);

            if (!Dedupe.ShouldEmit(message + KeySeparator + stack, out var count))
            {
                return;
            }

            // One publish, one console line: ConsoleEventMap turns Error/Exception/Assert into a
            // UNITY record at Error level and leaves Warning in the file. The direct
            // "UNITY Exception ..." twin this used to log as well is gone.
            TraceBus.EmitFields(
                "unity",
                type.ToString(),
                ("message", message),
                ("stack", stack),
                ("count", count));
        }

        private static void EmitWarning(string condition)
        {
            var message = Truncate(condition, MaxMessageLength);

            // Per distinct message, so one chatty warning cannot hide the others.
            if (!Warnings.TryAcquire(message, out var suppressed))
            {
                return;
            }

            TraceBus.EmitFields(
                "unity",
                "Warning",
                ("message", message),
                ("count", suppressed + 1));
        }

        private static int MaxMessageLength =>
            (_config?.MaxStringLength ?? TraceConfig.DefaultMaxStringLength) * 4;

        private static string Truncate(string? text, int max)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text!.Length <= max ? text : text.Substring(0, max) + ArgFormatter.Ellipsis;
        }

        /// <summary>The first <paramref name="lines"/> lines of a stack trace, newlines preserved.</summary>
        private static string FirstLines(string? text, int lines)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(256);
            var seen = 0;
            var start = 0;

            while (start < text!.Length && seen < lines)
            {
                var end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    end = text.Length;
                }

                if (seen > 0)
                {
                    sb.Append('\n');
                }

                sb.Append(text, start, end - start);
                seen++;
                start = end + 1;
            }

            return sb.ToString();
        }
    }
}
