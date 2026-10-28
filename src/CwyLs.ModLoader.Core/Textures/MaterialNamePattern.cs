using System;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Material names with wildcards: <c>*</c> matches any run of characters (including none),
    /// <c>?</c> exactly one. Case-insensitive, like every other material-name match in the
    /// framework. <c>mt_env_*</c> is every window-view scenery material; <c>mt_desk*</c> is the
    /// desk family (which also matches <c>mt_desktop</c> and <c>mt_desk_lamp</c> - name them
    /// exactly when that matters).
    /// </summary>
    public static class MaterialNamePattern
    {
        /// <summary>True when <paramref name="pattern"/> contains a wildcard.</summary>
        /// <param name="pattern">A material name or pattern.</param>
        public static bool IsPattern(string? pattern) =>
            !string.IsNullOrEmpty(pattern) && (pattern!.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0);

        /// <summary>True when <paramref name="name"/> matches <paramref name="pattern"/>.</summary>
        /// <param name="pattern">A name or a wildcard pattern; null or blank matches nothing.</param>
        /// <param name="name">A material's base name (no <c>" (Instance)"</c> suffix); null matches nothing.</param>
        public static bool Matches(string? pattern, string? name)
        {
            if (string.IsNullOrWhiteSpace(pattern) || name is null)
            {
                return false;
            }

            var p = pattern!.Trim();
            if (!IsPattern(p))
            {
                return string.Equals(p, name, StringComparison.OrdinalIgnoreCase);
            }

            return Match(p, 0, name, 0);
        }

        private static bool Match(string p, int pi, string s, int si)
        {
            // Iterative glob with single-star backtracking: linear in practice, no recursion depth.
            int star = -1, mark = 0;
            while (si < s.Length)
            {
                if (pi < p.Length && (p[pi] == '?' || char.ToLowerInvariant(p[pi]) == char.ToLowerInvariant(s[si])))
                {
                    pi++;
                    si++;
                }
                else if (pi < p.Length && p[pi] == '*')
                {
                    star = pi++;
                    mark = si;
                }
                else if (star >= 0)
                {
                    pi = star + 1;
                    si = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (pi < p.Length && p[pi] == '*')
            {
                pi++;
            }

            return pi == p.Length;
        }
    }
}
