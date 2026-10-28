using System;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Json
{
    /// <summary>
    /// Thrown by <see cref="JsonReader"/> when input is not well-formed JSON. Carries the 1-based
    /// position of the offending character so a modder can jump straight to it in an editor; the
    /// <see cref="Exception.Message"/> already contains <c>line:column</c>, so logging the message
    /// alone is enough.
    /// </summary>
    public sealed class JsonParseException : Exception
    {
        /// <summary>Creates an exception describing a problem at a position in the document.</summary>
        /// <param name="problem">What went wrong, without position (it is prefixed in).</param>
        /// <param name="line">1-based line of the offending character.</param>
        /// <param name="column">1-based column of the offending character.</param>
        public JsonParseException(string problem, int line, int column)
            : base(string.Format(CultureInfo.InvariantCulture, "{0}:{1}: {2}", line, column, problem))
        {
            Problem = problem ?? string.Empty;
            Line = line;
            Column = column;
        }

        /// <summary>What went wrong, without the position prefix.</summary>
        public string Problem { get; }

        /// <summary>1-based line of the offending character.</summary>
        public int Line { get; }

        /// <summary>1-based column of the offending character.</summary>
        public int Column { get; }
    }
}
