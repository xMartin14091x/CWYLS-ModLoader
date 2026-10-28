using System;
using System.Text;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Renders an exception with its causes for a one-line trace failure. HarmonyX wraps whatever
    /// went wrong while applying a patch as "IL Compile Error (unknown location)" and keeps the real
    /// reason in <see cref="Exception.InnerException"/>; logging only the outer message is how the
    /// Windows <c>ScreenSystem</c> failures (a type initializer throwing) reached the log unexplained.
    /// </summary>
    internal static class TraceFailureText
    {
        /// <summary>How many inner exceptions are appended before the chain is cut.</summary>
        internal const int MaxDepth = 4;

        /// <summary>
        /// <c>outer message &lt;- InnerType: inner message &lt;- ...</c>, at most <see cref="MaxDepth"/>
        /// causes deep, each message on one line.
        /// </summary>
        /// <param name="error">The exception; null renders as <c>(no exception)</c>.</param>
        internal static string Chain(Exception? error)
        {
            if (error == null)
            {
                return "(no exception)";
            }

            var sb = new StringBuilder(OneLine(error.Message));
            var inner = error.InnerException;
            for (var depth = 0; inner != null && depth < MaxDepth; depth++)
            {
                sb.Append(" <- ").Append(inner.GetType().Name).Append(": ").Append(OneLine(inner.Message));
                inner = inner.InnerException;
            }

            if (inner != null)
            {
                sb.Append(" <- ...");
            }

            return sb.ToString();
        }

        private static string OneLine(string? message) =>
            string.IsNullOrEmpty(message) ? "(no message)" : message!.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
