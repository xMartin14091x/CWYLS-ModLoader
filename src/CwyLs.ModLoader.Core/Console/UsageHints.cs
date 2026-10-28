using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// Turns a command's usage string into the words Tab can offer for its arguments. A usage
    /// line is already the documentation - <c>packs list|rescan</c>, <c>trace level off|events|trace</c>
    /// - so the completion for an argument comes from the same single source as the help text.
    /// <para>
    /// A placeholder (<c>&lt;name&gt;</c>, <c>[n]</c>) is a value the user supplies, so it is
    /// never offered.
    /// </para>
    /// </summary>
    public static class UsageHints
    {
        /// <summary>
        /// The literal words a usage line allows after the command name, in the order they appear,
        /// without duplicates.
        /// </summary>
        /// <param name="usage">A usage line, e.g. <c>packs list|rescan</c>. Null yields none.</param>
        public static IReadOnlyList<string> Parse(string? usage)
        {
            if (string.IsNullOrWhiteSpace(usage))
            {
                return Array.Empty<string>();
            }

            var hints = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tokens = usage!.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            // The first token is the command name itself, which the argument completion never offers.
            for (var i = 1; i < tokens.Length; i++)
            {
                foreach (var part in tokens[i].Split('|'))
                {
                    var word = part.Trim();
                    if (word.Length == 0 || word[0] == '<' || word[0] == '[' || word[0] == '(')
                    {
                        continue;
                    }

                    if (seen.Add(word))
                    {
                        hints.Add(word);
                    }
                }
            }

            return hints;
        }
    }
}
