using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// A Semantic Versioning 2.0.0 version: <c>major.minor.patch[-prerelease][+build]</c>.
    /// <para>
    /// Written out rather than taken from a NuGet package because every plugin assembly must load
    /// on Unity's Mono profile with no dependencies beyond the game's own, and because the
    /// comparison rules - not the parsing - are what pack dependency resolution hangs on.
    /// </para>
    /// <para>
    /// Ordering follows the spec: numeric comparison of major, minor and patch; a pre-release
    /// version is <em>lower</em> than the release it precedes (<c>1.0.0-rc.1 &lt; 1.0.0</c>);
    /// pre-release identifiers compare numerically when all-digits and by ordinal ASCII otherwise,
    /// with a numeric identifier always lower than an alphanumeric one; build metadata is ignored
    /// in comparison but preserved in <see cref="ToString"/>.
    /// </para>
    /// </summary>
    public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
    {
        private static readonly string[] NoIdentifiers = new string[0];

        private readonly string[] _prereleaseIdentifiers;

        private SemVer(int major, int minor, int patch, string? prerelease, string? build, string[] identifiers)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Prerelease = prerelease;
            Build = build;
            _prereleaseIdentifiers = identifiers;
        }

        /// <summary>Major version.</summary>
        public int Major { get; }

        /// <summary>Minor version.</summary>
        public int Minor { get; }

        /// <summary>Patch version.</summary>
        public int Patch { get; }

        /// <summary>Pre-release tag without the leading <c>-</c>, or null for a release build.</summary>
        public string? Prerelease { get; }

        /// <summary>Build metadata without the leading <c>+</c>, or null. Ignored when comparing.</summary>
        public string? Build { get; }

        /// <summary>True when this is a pre-release, which sorts below the matching release.</summary>
        public bool IsPrerelease => Prerelease != null;

        /// <summary>Parses a version.</summary>
        /// <param name="text">e.g. <c>1.2.3</c>, <c>0.1.0-rc.1</c>, <c>2.0.0+build.7</c>.</param>
        /// <exception cref="FormatException"><paramref name="text"/> is not a SemVer 2.0.0 version.</exception>
        public static SemVer Parse(string text)
        {
            if (!TryParse(text, out var version, out var error))
            {
                throw new FormatException(error);
            }

            return version!;
        }

        /// <summary>Parses a version, reporting failure instead of throwing.</summary>
        /// <param name="text">The candidate version; null or blank fails.</param>
        /// <param name="version">The parsed version, or null on failure.</param>
        /// <returns>True when <paramref name="text"/> is a SemVer 2.0.0 version.</returns>
        public static bool TryParse(string? text, out SemVer? version) => TryParse(text, out version, out _);

        /// <summary>Parses a version, reporting why it failed.</summary>
        /// <param name="text">The candidate version; null or blank fails.</param>
        /// <param name="version">The parsed version, or null on failure.</param>
        /// <param name="error">Why it failed, or null on success.</param>
        /// <returns>True when <paramref name="text"/> is a SemVer 2.0.0 version.</returns>
        public static bool TryParse(string? text, out SemVer? version, out string? error)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "the version is empty";
                return false;
            }

            var body = text!.Trim();

            string? build = null;
            var plus = body.IndexOf('+');
            if (plus >= 0)
            {
                build = body.Substring(plus + 1);
                body = body.Substring(0, plus);
                if (!AreDotSeparatedIdentifiers(build, allowLeadingZeroes: true))
                {
                    error = "\"" + text + "\" has malformed build metadata";
                    return false;
                }
            }

            string? prerelease = null;
            var dash = body.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = body.Substring(dash + 1);
                body = body.Substring(0, dash);
                if (!AreDotSeparatedIdentifiers(prerelease, allowLeadingZeroes: false))
                {
                    error = "\"" + text + "\" has a malformed pre-release tag";
                    return false;
                }
            }

            var parts = body.Split('.');
            if (parts.Length != 3)
            {
                error = "\"" + text + "\" is not major.minor.patch";
                return false;
            }

            if (!TryParseNumber(parts[0], out var major) ||
                !TryParseNumber(parts[1], out var minor) ||
                !TryParseNumber(parts[2], out var patch))
            {
                error = "\"" + text + "\" has a non-numeric or negative component";
                return false;
            }

            version = new SemVer(
                major,
                minor,
                patch,
                prerelease,
                build,
                prerelease is null ? NoIdentifiers : prerelease.Split('.'));
            error = null;
            return true;
        }

        /// <summary>
        /// Orders two versions, treating null as lower than any version. Build metadata is ignored.
        /// </summary>
        /// <param name="left">First version, or null.</param>
        /// <param name="right">Second version, or null.</param>
        public static int Compare(SemVer? left, SemVer? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var byNumber = left.Major.CompareTo(right.Major);
            if (byNumber != 0) { return byNumber; }

            byNumber = left.Minor.CompareTo(right.Minor);
            if (byNumber != 0) { return byNumber; }

            byNumber = left.Patch.CompareTo(right.Patch);
            if (byNumber != 0) { return byNumber; }

            return ComparePrerelease(left._prereleaseIdentifiers, right._prereleaseIdentifiers);
        }

        /// <inheritdoc />
        public int CompareTo(SemVer? other) => Compare(this, other);

        /// <inheritdoc />
        public bool Equals(SemVer? other) => Compare(this, other) == 0;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is SemVer other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            // Build metadata is excluded so that equal versions hash equally.
            unchecked
            {
                var hash = Major;
                hash = (hash * 397) ^ Minor;
                hash = (hash * 397) ^ Patch;
                hash = (hash * 397) ^ (Prerelease?.GetHashCode() ?? 0);
                return hash;
            }
        }

        /// <summary>Equality by precedence; build metadata is ignored.</summary>
        public static bool operator ==(SemVer? left, SemVer? right) => Compare(left, right) == 0;

        /// <summary>Inequality by precedence; build metadata is ignored.</summary>
        public static bool operator !=(SemVer? left, SemVer? right) => Compare(left, right) != 0;

        /// <summary>True when <paramref name="left"/> precedes <paramref name="right"/>.</summary>
        public static bool operator <(SemVer? left, SemVer? right) => Compare(left, right) < 0;

        /// <summary>True when <paramref name="left"/> follows <paramref name="right"/>.</summary>
        public static bool operator >(SemVer? left, SemVer? right) => Compare(left, right) > 0;

        /// <summary>True when <paramref name="left"/> precedes or equals <paramref name="right"/>.</summary>
        public static bool operator <=(SemVer? left, SemVer? right) => Compare(left, right) <= 0;

        /// <summary>True when <paramref name="left"/> follows or equals <paramref name="right"/>.</summary>
        public static bool operator >=(SemVer? left, SemVer? right) => Compare(left, right) >= 0;

        /// <inheritdoc />
        public override string ToString()
        {
            var sb = new StringBuilder(16);
            sb.Append(Major.ToString(CultureInfo.InvariantCulture)).Append('.')
              .Append(Minor.ToString(CultureInfo.InvariantCulture)).Append('.')
              .Append(Patch.ToString(CultureInfo.InvariantCulture));

            if (Prerelease != null)
            {
                sb.Append('-').Append(Prerelease);
            }

            if (Build != null)
            {
                sb.Append('+').Append(Build);
            }

            return sb.ToString();
        }

        private static int ComparePrerelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            // A release outranks any pre-release of the same major.minor.patch.
            if (left.Count == 0)
            {
                return right.Count == 0 ? 0 : 1;
            }

            if (right.Count == 0)
            {
                return -1;
            }

            var shared = left.Count < right.Count ? left.Count : right.Count;
            for (var i = 0; i < shared; i++)
            {
                var leftNumeric = TryParseNumber(left[i], out var leftValue);
                var rightNumeric = TryParseNumber(right[i], out var rightValue);

                if (leftNumeric && rightNumeric)
                {
                    var byValue = leftValue.CompareTo(rightValue);
                    if (byValue != 0) { return byValue; }

                    continue;
                }

                // "Numeric identifiers always have lower precedence than non-numeric identifiers."
                if (leftNumeric) { return -1; }

                if (rightNumeric) { return 1; }

                var byText = string.CompareOrdinal(left[i], right[i]);
                if (byText != 0) { return byText < 0 ? -1 : 1; }
            }

            // "A larger set of pre-release fields has a higher precedence."
            return left.Count.CompareTo(right.Count);
        }

        private static bool TryParseNumber(string text, out int value)
        {
            value = 0;
            if (text.Length == 0 || text.Length > 9)
            {
                // Nine digits keeps the int parse below overflow-free without a try/catch.
                return false;
            }

            // A leading zero is not a valid numeric component ("01" is not 1).
            if (text.Length > 1 && text[0] == '0')
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }

            value = int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            return true;
        }

        private static bool AreDotSeparatedIdentifiers(string text, bool allowLeadingZeroes)
        {
            if (text.Length == 0)
            {
                return false;
            }

            foreach (var identifier in text.Split('.'))
            {
                if (identifier.Length == 0)
                {
                    return false;
                }

                var allDigits = true;
                foreach (var c in identifier)
                {
                    var alphanumeric = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '-';
                    if (!alphanumeric)
                    {
                        return false;
                    }

                    if (c < '0' || c > '9')
                    {
                        allDigits = false;
                    }
                }

                if (allDigits && !allowLeadingZeroes && identifier.Length > 1 && identifier[0] == '0')
                {
                    return false;
                }
            }

            return true;
        }
    }
}
