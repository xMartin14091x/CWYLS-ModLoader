using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Actions;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// An action call written in a script row (<c>cwy.do</c>, <c>cwy.defer</c>): the action name, then
    /// <c>name=value</c> arguments, in <c>Arg1</c>:
    /// <c>music.play title="Rainy Desk" volume=0.5</c>. Every value is text; the action's typed getters
    /// parse it in invariant culture (<see cref="ActionArgs"/>). Immutable.
    /// </summary>
    /// <remarks>
    /// Syntax: words split on spaces and tabs; a value in double quotes may hold spaces, with
    /// <c>\"</c> and <c>\\</c> as the only escapes. The name is matched lower-case
    /// (<see cref="ActionDescriptor.NamePattern"/>); argument names follow
    /// <see cref="ActionParameter.NamePattern"/> and may not repeat.
    /// </remarks>
    public sealed class ScriptActionLine
    {
        /// <summary>Longest accepted line.</summary>
        public const int MaxLength = 1024;

        private ScriptActionLine(string name, ActionArgs args, string text)
        {
            Name = name;
            Args = args;
            Text = text;
        }

        /// <summary>The action name, lower-case.</summary>
        public string Name { get; }

        /// <summary>The arguments, all text values, in the order written.</summary>
        public ActionArgs Args { get; }

        /// <summary>The line as written (trimmed).</summary>
        public string Text { get; }

        /// <summary>Parses a line.</summary>
        /// <param name="text">The row's <c>Arg1</c>.</param>
        /// <returns>The line, or why it is unusable.</returns>
        public static ScriptActionLineParse Parse(string? text)
        {
            var trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || trimmed == "0")
            {
                return ScriptActionLineParse.Fail("no action: write the action and its arguments in Arg1, e.g. music.play title=Rain");
            }

            if (trimmed.Length > MaxLength)
            {
                return ScriptActionLineParse.Fail("the line is longer than " + MaxLength + " characters");
            }

            if (!TrySplit(trimmed, out var words, out var problem))
            {
                return ScriptActionLineParse.Fail(problem);
            }

            var name = words[0].Value.ToLowerInvariant();
            if (words[0].Quoted || !ActionDescriptor.IsValidName(name))
            {
                return ScriptActionLineParse.Fail(
                    "\"" + words[0].Value + "\" is not an action name (two or more dot-separated words, like music.play)");
            }

            var pairs = new List<KeyValuePair<string, object?>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < words.Count; i++)
            {
                var word = words[i];
                var eq = word.Value.IndexOf('=');
                if (word.Quoted || eq <= 0)
                {
                    return ScriptActionLineParse.Fail("\"" + word.Value + "\" is not an argument: write name=value");
                }

                var argName = word.Value.Substring(0, eq);
                if (!IsParameterName(argName))
                {
                    return ScriptActionLineParse.Fail("\"" + argName + "\" is not an argument name (a letter, then letters, digits or _)");
                }

                if (!seen.Add(argName))
                {
                    return ScriptActionLineParse.Fail("the argument \"" + argName + "\" appears twice");
                }

                pairs.Add(new KeyValuePair<string, object?>(argName, word.Value.Substring(eq + 1)));
            }

            return ScriptActionLineParse.Ok(new ScriptActionLine(name, ActionArgs.From(pairs), trimmed));
        }

        /// <summary>The request the runner gets for this line.</summary>
        /// <param name="callerId">For the audit line: the scene's group id.</param>
        /// <param name="timeout">How long the caller waits for the main thread; null for the runner's default.</param>
        /// <returns>A <see cref="ActionCaller.Script"/> request.</returns>
        public ActionRequest ToRequest(string callerId, TimeSpan? timeout = null) =>
            new ActionRequest(Name, Args, ActionCaller.Script, string.IsNullOrWhiteSpace(callerId) ? "script" : callerId, null, timeout);

        /// <summary><c>music.play title=Rain</c>.</summary>
        public override string ToString() => Args.Count == 0 ? Name : Name + " " + Args;

        private static bool IsParameterName(string name)
        {
            if (name.Length == 0 || name.Length > 32 || !IsAsciiLetter(name[0]))
            {
                return false;
            }

            foreach (var c in name)
            {
                if (!IsAsciiLetter(c) && !(c >= '0' && c <= '9') && c != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        private static bool TrySplit(string text, out List<Word> words, out string problem)
        {
            words = new List<Word>();
            var current = new StringBuilder();
            var inWord = false;
            var quotedWord = false;
            var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '\\' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '\\'))
                    {
                        current.Append(text[++i]);
                    }
                    else if (c == '"')
                    {
                        inQuotes = false;
                    }
                    else
                    {
                        current.Append(c);
                    }

                    continue;
                }

                if (c == ' ' || c == '\t')
                {
                    if (inWord)
                    {
                        words.Add(new Word(current.ToString(), quotedWord));
                        current.Clear();
                        inWord = false;
                        quotedWord = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    // A quote opens a value: name="a b" (or a whole quoted word, which only an argument
                    // value may be - the caller refuses a quoted name).
                    inQuotes = true;
                    quotedWord = current.Length == 0;
                    inWord = true;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    problem = "an action line is one line";
                    return false;
                }

                current.Append(c);
                inWord = true;
            }

            if (inQuotes)
            {
                problem = "a quote is not closed";
                return false;
            }

            if (inWord)
            {
                words.Add(new Word(current.ToString(), quotedWord));
            }

            problem = string.Empty;
            return words.Count > 0 || Fail(out problem);
        }

        private static bool Fail(out string problem)
        {
            problem = "no action";
            return false;
        }

        private readonly struct Word
        {
            internal Word(string value, bool quoted)
            {
                Value = value;
                Quoted = quoted;
            }

            internal string Value { get; }

            internal bool Quoted { get; }
        }
    }

    /// <summary>The result of <see cref="ScriptActionLine.Parse"/>. Immutable.</summary>
    public sealed class ScriptActionLineParse
    {
        private ScriptActionLineParse(ScriptActionLine? line, string problem)
        {
            Line = line;
            Problem = problem;
        }

        /// <summary>True when the line parsed.</summary>
        public bool IsValid => Line != null;

        /// <summary>The line; null when it did not parse.</summary>
        public ScriptActionLine? Line { get; }

        /// <summary>Why the line is unusable; empty when it parsed.</summary>
        public string Problem { get; }

        internal static ScriptActionLineParse Ok(ScriptActionLine line) => new ScriptActionLineParse(line, string.Empty);

        internal static ScriptActionLineParse Fail(string problem) => new ScriptActionLineParse(null, problem);
    }
}
