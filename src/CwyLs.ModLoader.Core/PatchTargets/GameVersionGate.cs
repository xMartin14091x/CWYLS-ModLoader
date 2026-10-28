using System;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core
{
    /// <summary>Outcome of a <see cref="GameVersionGate"/> check.</summary>
    public enum GameVersionStatus
    {
        /// <summary>The running game is the build the patch targets were verified against.</summary>
        Ok,

        /// <summary>
        /// The running game is a different build (<see cref="GameVersionCheck.Difference"/> says
        /// how far off), or its version could not be read.
        /// </summary>
        Mismatch
    }

    /// <summary>What a <see cref="GameVersionGate"/> found, with the line to log.</summary>
    public sealed class GameVersionCheck
    {
        internal GameVersionCheck(
            GameVersionStatus status,
            string expected,
            string actual,
            GameVersionDifference difference,
            string message)
        {
            Status = status;
            Expected = expected;
            Actual = actual;
            Difference = difference;
            Message = message;
        }

        /// <summary>Ok or Mismatch.</summary>
        public GameVersionStatus Status { get; }

        /// <summary>The version the patch-target table was verified against.</summary>
        public string Expected { get; }

        /// <summary>The version reported by the running game, or <c>"unknown"</c>.</summary>
        public string Actual { get; }

        /// <summary>
        /// How far the running game is from <see cref="Expected"/>: <see cref="GameVersionDifference.Same"/>
        /// when Ok, <see cref="GameVersionDifference.Patch"/> for a patch release (the likeliest to
        /// have kept every seam), up to <see cref="GameVersionDifference.Major"/>;
        /// <see cref="GameVersionDifference.Unknown"/> when the version is missing or not a number.
        /// </summary>
        public GameVersionDifference Difference { get; }

        /// <summary>A complete, self-explanatory log line.</summary>
        public string Message { get; }

        /// <summary>Shorthand for <see cref="Status"/> == <see cref="GameVersionStatus.Ok"/>.</summary>
        public bool IsOk => Status == GameVersionStatus.Ok;

        /// <inheritdoc />
        public override string ToString() => Message;
    }

    /// <summary>
    /// Compares the running game's version against the build the patch-target tables
    /// describe, for the framework itself. A mismatch never aborts anything: the
    /// patch-target report is the real evidence, and this gate only explains <em>why</em> a target
    /// might have moved.
    /// <para>
    /// Content packs are not gated by this class. Their <c>gameVersion</c>,
    /// <c>minGameVersion</c> and <c>maxGameVersion</c> follow <see cref="PackGameVersionCheck"/>.
    /// </para>
    /// <para>
    /// Versions compare by SemVer precedence where both sides parse (<see cref="GameVersions.TryParse"/>),
    /// so build metadata such as <c>1.17.2+steam.1</c> matches <c>1.17.2</c>; otherwise by exact text.
    /// </para>
    /// </summary>
    public sealed class GameVersionGate
    {
        /// <summary>The game build the patch-target tables were verified against.</summary>
        public const string ExpectedGameVersion = "1.17.3";

        /// <summary>Displayed in place of a version the game did not report.</summary>
        public const string UnknownVersion = GameVersions.Unknown;

        /// <summary>Creates a gate for <see cref="ExpectedGameVersion"/>.</summary>
        public GameVersionGate()
            : this(ExpectedGameVersion)
        {
        }

        /// <summary>Creates a gate for an explicit expected version (used by tests).</summary>
        /// <param name="expected">The version the targets were verified against.</param>
        /// <exception cref="ArgumentException"><paramref name="expected"/> is null or blank.</exception>
        public GameVersionGate(string expected)
        {
            if (string.IsNullOrWhiteSpace(expected))
            {
                throw new ArgumentException("must not be null or blank", nameof(expected));
            }

            Expected = expected.Trim();
        }

        /// <summary>The version the targets were verified against.</summary>
        public string Expected { get; }

        /// <summary>Compares a reported version against <see cref="Expected"/>.</summary>
        /// <param name="actual">
        /// The running game's version, normally <c>UnityEngine.Application.version</c>.
        /// Null or blank is reported as a mismatch, not silently accepted.
        /// </param>
        public GameVersionCheck Check(string? actual)
        {
            if (string.IsNullOrWhiteSpace(actual))
            {
                return new GameVersionCheck(
                    GameVersionStatus.Mismatch,
                    Expected,
                    UnknownVersion,
                    GameVersionDifference.Unknown,
                    "game version unknown; patch targets were verified against " + Expected);
            }

            var trimmed = actual!.Trim();
            var difference = GameVersions.Difference(Expected, trimmed);
            if (difference == GameVersionDifference.Same)
            {
                var what = string.Equals(trimmed, Expected, StringComparison.Ordinal)
                    ? "the build"
                    : Expected + ", the build";
                return new GameVersionCheck(
                    GameVersionStatus.Ok,
                    Expected,
                    trimmed,
                    difference,
                    "game version " + trimmed + " matches " + what + " the patch targets were verified against");
            }

            var how = GameVersions.Describe(Expected, trimmed);
            return new GameVersionCheck(
                GameVersionStatus.Mismatch,
                Expected,
                trimmed,
                difference,
                "game version mismatch: running " + trimmed + ", patch targets verified against " + Expected +
                (how is null ? string.Empty : " (" + how + ")"));
        }
    }
}
