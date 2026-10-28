using System;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>How far apart two game versions are, as <see cref="GameVersions.Difference(string, string)"/> reports it.</summary>
    public enum GameVersionDifference
    {
        /// <summary>The same version. Build metadata (<c>+…</c>) and surrounding whitespace are ignored.</summary>
        Same = 0,

        /// <summary>Only the patch number differs, e.g. <c>1.17.2</c> and <c>1.17.3</c>.</summary>
        Patch,

        /// <summary>The minor number differs, e.g. <c>1.17.3</c> and <c>1.18.0</c>.</summary>
        Minor,

        /// <summary>The major number differs, e.g. <c>1.17.3</c> and <c>2.0.0</c>.</summary>
        Major,

        /// <summary>Same major.minor.patch, different pre-release tag, e.g. <c>1.18.0-beta.1</c> and <c>1.18.0</c>.</summary>
        Prerelease,

        /// <summary>
        /// At least one side is missing or is not a version number, and the two texts are not
        /// identical, so how far apart they are cannot be said.
        /// </summary>
        Unknown,
    }

    /// <summary>
    /// Game version strings, as the running game reports them (<c>UnityEngine.Application.version</c>)
    /// and as pack manifests declare them: lenient parsing into <see cref="SemVer"/>, and comparison.
    /// <para>
    /// Shared by the pack policy (<see cref="PackGameVersionCheck"/>) and by the framework's own
    /// target-drift explanation (<c>CwyLs.ModLoader.Core.GameVersionGate</c>), so both describe a
    /// difference in the same words.
    /// </para>
    /// </summary>
    public static class GameVersions
    {
        /// <summary>Displayed in place of a version the game did not report.</summary>
        public const string Unknown = "unknown";

        /// <summary>
        /// Parses a game version. SemVer 2.0.0 is tried first; a version of only one or two numeric
        /// parts (<c>1.18</c>, <c>2</c>) is read with the missing parts as zero, because a game is
        /// not obliged to print three. Anything else (<c>1.17.3.1</c>, <c>1.17.3b</c>) fails.
        /// </summary>
        /// <param name="text">The candidate version; null or blank fails.</param>
        /// <param name="version">The parsed version, or null on failure.</param>
        /// <returns>True when <paramref name="text"/> is a version number.</returns>
        public static bool TryParse(string? text, out SemVer? version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var trimmed = text!.Trim();
            if (SemVer.TryParse(trimmed, out version))
            {
                return true;
            }

            var parts = trimmed.Split('.');
            if (parts.Length > 2)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0)
                {
                    return false;
                }

                foreach (var c in part)
                {
                    if (c < '0' || c > '9')
                    {
                        return false;
                    }
                }
            }

            // SemVer still rejects leading zeroes and overlong numbers in the padded form.
            return SemVer.TryParse(parts.Length == 1 ? trimmed + ".0.0" : trimmed + ".0", out version);
        }

        /// <summary>
        /// How far apart two game versions are. When either side is not a version number the answer
        /// is <see cref="GameVersionDifference.Same"/> for identical (trimmed, ordinal) texts and
        /// <see cref="GameVersionDifference.Unknown"/> otherwise.
        /// </summary>
        /// <param name="from">The reference version, e.g. the one a pack was made for.</param>
        /// <param name="to">The version compared against it, e.g. the running game's.</param>
        public static GameVersionDifference Difference(string? from, string? to)
        {
            if (TryParse(from, out var left) && TryParse(to, out var right))
            {
                return Difference(left!, right!);
            }

            if (!string.IsNullOrWhiteSpace(from) &&
                !string.IsNullOrWhiteSpace(to) &&
                string.Equals(from!.Trim(), to!.Trim(), StringComparison.Ordinal))
            {
                return GameVersionDifference.Same;
            }

            return GameVersionDifference.Unknown;
        }

        /// <summary>How far apart two parsed versions are. Build metadata is ignored.</summary>
        /// <param name="from">The reference version.</param>
        /// <param name="to">The version compared against it.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        public static GameVersionDifference Difference(SemVer from, SemVer to)
        {
            if (from is null)
            {
                throw new ArgumentNullException(nameof(from));
            }

            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            if (from.Major != to.Major)
            {
                return GameVersionDifference.Major;
            }

            if (from.Minor != to.Minor)
            {
                return GameVersionDifference.Minor;
            }

            if (from.Patch != to.Patch)
            {
                return GameVersionDifference.Patch;
            }

            return from == to ? GameVersionDifference.Same : GameVersionDifference.Prerelease;
        }

        /// <summary>
        /// A short phrase saying how <paramref name="actual"/> relates to <paramref name="reference"/>:
        /// <c>newer patch version</c>, <c>older minor version</c>, <c>newer major version</c> or
        /// <c>older build (pre-release tag differs)</c>. Null when the two are the same or either
        /// is not a version number.
        /// </summary>
        /// <param name="reference">The version something was made or verified for.</param>
        /// <param name="actual">The version actually running.</param>
        public static string? Describe(string? reference, string? actual)
        {
            if (!TryParse(reference, out var left) || !TryParse(actual, out var right))
            {
                return null;
            }

            var direction = right! > left! ? "newer" : "older";
            switch (Difference(left!, right!))
            {
                case GameVersionDifference.Patch:
                    return direction + " patch version";
                case GameVersionDifference.Minor:
                    return direction + " minor version";
                case GameVersionDifference.Major:
                    return direction + " major version";
                case GameVersionDifference.Prerelease:
                    return direction + " build (pre-release tag differs)";
                default:
                    return null;
            }
        }
    }
}
