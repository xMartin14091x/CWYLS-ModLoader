using System;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// Names of the files a mod keeps in its own data folder (<c>BepInEx/data/mods/&lt;id&gt;/</c>).
    /// A key is the file name: letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, 1 to 64 characters,
    /// not starting with a dot. That keeps every key a plain file in that one folder on both
    /// operating systems, with no way to reach a path outside it.
    /// </summary>
    public static class ModDataKey
    {
        /// <summary>The key rule, as a human-readable pattern.</summary>
        public const string Pattern = "^[A-Za-z0-9_-][A-Za-z0-9._-]{0,63}$";

        /// <summary>
        /// Suffix of the file an atomic write goes to before it replaces the real one. Contains a
        /// character a key cannot, so it never collides with a key.
        /// </summary>
        public const string TempSuffix = ".tmp~";

        private static readonly Regex KeyRegex =
            new Regex(@"\A[A-Za-z0-9_-][A-Za-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

        /// <summary>True when <paramref name="key"/> is a usable data file name.</summary>
        /// <param name="key">The candidate.</param>
        public static bool IsValid(string? key) =>
            key != null && KeyRegex.IsMatch(key) && key.IndexOf("..", StringComparison.Ordinal) < 0;

        /// <summary>Throws unless <paramref name="key"/> is valid, naming the rule.</summary>
        /// <param name="key">The candidate.</param>
        /// <exception cref="ArgumentException">The key breaks the rule.</exception>
        public static void Require(string? key)
        {
            if (!IsValid(key))
            {
                throw new ArgumentException(
                    "\"" + key + "\" is not a data key: use letters, digits, '_', '.' and '-' (1 to 64, not starting with '.', no '..')",
                    nameof(key));
            }
        }

        /// <summary>The temporary file name an atomic write of <paramref name="key"/> uses.</summary>
        /// <param name="key">A valid key.</param>
        public static string TempName(string key) => key + TempSuffix;
    }
}
