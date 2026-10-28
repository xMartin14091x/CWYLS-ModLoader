using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// One typed line split into a command name and its arguments.
    /// </summary>
    public sealed class ParsedCommand
    {
        /// <summary>Creates a parsed line.</summary>
        /// <param name="name">The first token. Required.</param>
        /// <param name="arguments">The tokens after it; null becomes empty.</param>
        /// <param name="raw">The line as typed; null becomes empty.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public ParsedCommand(string name, IReadOnlyList<string>? arguments, string? raw)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a command name is required", nameof(name));
            }

            Name = name;
            Arguments = arguments ?? Array.Empty<string>();
            Raw = raw ?? string.Empty;
        }

        /// <summary>The command name: the first token of the line.</summary>
        public string Name { get; }

        /// <summary>The tokens after the name, quotes and escapes already resolved. Never null.</summary>
        public IReadOnlyList<string> Arguments { get; }

        /// <summary>The line exactly as it was typed. Never null.</summary>
        public string Raw { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Arguments.Count == 0 ? Name : Name + " " + CommandLineParser.Join(Arguments);
    }

    /// <summary>
    /// Splits a console line into tokens. Whitespace separates, <c>"</c> and <c>'</c> quote a
    /// token that contains spaces, and <c>\</c> escapes the next character (so a literal quote or
    /// backslash can be typed). Single quotes are literal inside: <c>'C:\path'</c> is one token
    /// spelled exactly like that, which is what a person typing a path expects.
    /// <para>
    /// Unity-free and deterministic, so the awkward cases - an empty quoted argument, an
    /// unterminated quote, a trailing backslash - are covered by unit tests rather than found
    /// in-game.
    /// </para>
    /// </summary>
    public static class CommandLineParser
    {
        /// <summary>What <see cref="TryTokenize"/> reports for a quote that is never closed.</summary>
        public const string UnterminatedQuoteError = "unterminated quote";

        /// <summary>What <see cref="TryTokenize"/> reports for a line ending in a lone backslash.</summary>
        public const string DanglingEscapeError = "the line ends with a lone backslash";

        private static readonly string[] NoTokens = Array.Empty<string>();

        /// <summary>
        /// Splits a line, tolerating the mistakes a person makes while typing: an unterminated
        /// quote simply runs to the end of the line and a trailing backslash is dropped. Never
        /// throws. Use <see cref="TryTokenize"/> when the mistake should be reported instead.
        /// </summary>
        /// <param name="line">The line as typed; null or blank yields no tokens.</param>
        public static IReadOnlyList<string> Tokenize(string? line)
        {
            TryTokenize(line, out var tokens, out _);
            return tokens;
        }

        /// <summary>
        /// Splits a line and reports the first problem found. The tokens are produced either way
        /// (the tolerant reading), so a caller can show both the result and the warning.
        /// </summary>
        /// <param name="line">The line as typed.</param>
        /// <param name="tokens">The tokens; never null, possibly empty.</param>
        /// <param name="error">Null when the line was well-formed, otherwise what was wrong.</param>
        /// <returns>True when the line was well-formed.</returns>
        public static bool TryTokenize(string? line, out IReadOnlyList<string> tokens, out string? error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(line))
            {
                tokens = NoTokens;
                return true;
            }

            var found = new List<string>(4);
            var current = new StringBuilder();
            var started = false;
            var quote = '\0';

            for (var i = 0; i < line!.Length; i++)
            {
                var c = line[i];

                if (c == '\\' && quote != '\'')
                {
                    // A backslash escapes the next character everywhere except inside single
                    // quotes, where a Windows path must stay readable.
                    if (i + 1 >= line.Length)
                    {
                        error ??= DanglingEscapeError;
                        continue;
                    }

                    current.Append(line[++i]);
                    started = true;
                    continue;
                }

                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                        continue;
                    }

                    current.Append(c);
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;

                    // An empty quoted argument is a real argument: "" must survive as a token.
                    started = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (started)
                    {
                        found.Add(current.ToString());
                        current.Length = 0;
                        started = false;
                    }

                    continue;
                }

                current.Append(c);
                started = true;
            }

            if (quote != '\0')
            {
                error ??= UnterminatedQuoteError;
            }

            if (started)
            {
                found.Add(current.ToString());
            }

            tokens = found;
            return error is null;
        }

        /// <summary>
        /// Splits a line into a command and its arguments, or null when the line holds no command
        /// at all (blank, or nothing but whitespace).
        /// </summary>
        /// <param name="line">The line as typed.</param>
        public static ParsedCommand? Parse(string? line)
        {
            var tokens = Tokenize(line);
            if (tokens.Count == 0)
            {
                return null;
            }

            var arguments = new string[tokens.Count - 1];
            for (var i = 1; i < tokens.Count; i++)
            {
                arguments[i - 1] = tokens[i];
            }

            return new ParsedCommand(tokens[0], arguments, line);
        }

        /// <summary>
        /// Joins arguments back into one string with single spaces - what a command that takes
        /// free text (<c>log</c>, <c>subtitle</c>) wants from the tokens it was handed.
        /// </summary>
        /// <param name="arguments">The arguments; null yields an empty string.</param>
        /// <param name="startIndex">First argument to include; out-of-range yields an empty string.</param>
        public static string Join(IReadOnlyList<string>? arguments, int startIndex = 0)
        {
            if (arguments is null || startIndex < 0 || startIndex >= arguments.Count)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (var i = startIndex; i < arguments.Count; i++)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(arguments[i]);
            }

            return sb.ToString();
        }
    }
}
