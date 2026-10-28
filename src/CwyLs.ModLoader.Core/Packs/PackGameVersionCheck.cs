using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>What a pack's game-version fields say about the running game.</summary>
    public enum PackGameVersionStatus
    {
        /// <summary>The manifest declares none of <c>gameVersion</c>, <c>minGameVersion</c>, <c>maxGameVersion</c>. Loads silently.</summary>
        NotDeclared = 0,

        /// <summary>The running game is the declared <c>gameVersion</c> (or none is declared) and inside the declared range. Loads silently.</summary>
        Match,

        /// <summary>The running game is not the declared <c>gameVersion</c> but is inside the declared range. Loads with one warning.</summary>
        Differs,

        /// <summary>
        /// The running game's version is unknown or not a version number, so the declared fields
        /// could not be compared. Never a skip: loads with one warning.
        /// </summary>
        Unverified,

        /// <summary>
        /// The running game is older than <c>minGameVersion</c>: skipped as
        /// <see cref="PackSkipReason.GameVersionTooOld"/>, or loaded with a warning when the
        /// resolver was told to ignore game versions.
        /// </summary>
        TooOld,

        /// <summary>
        /// The running game is newer than <c>maxGameVersion</c>: skipped as
        /// <see cref="PackSkipReason.GameVersionTooNew"/>, or loaded with a warning when the
        /// resolver was told to ignore game versions.
        /// </summary>
        TooNew,
    }

    /// <summary>
    /// The pack game-version policy, SMAPI-like:
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>gameVersion</c> means "made and tested with". A different running version loads the
    ///     pack with one warning naming both versions; it is never a reason to skip.
    ///   </description></item>
    ///   <item><description>
    ///     <c>minGameVersion</c> / <c>maxGameVersion</c> are optional inclusive bounds compared by
    ///     SemVer precedence. Outside them the pack is skipped as
    ///     <see cref="PackSkipReason.GameVersionTooOld"/> / <see cref="PackSkipReason.GameVersionTooNew"/>,
    ///     unless game versions are ignored, in which case it loads with a warning.
    ///   </description></item>
    ///   <item><description>
    ///     A running version that is unknown or not a version number never skips a pack; it warns
    ///     when the pack declares anything.
    ///   </description></item>
    /// </list>
    /// Pure and immutable; <see cref="PackResolver"/> evaluates it once per pack.
    /// </summary>
    public sealed class PackGameVersionCheck
    {
        private const string LoadedAnyway = "loaded anyway";
        private const string LoadedIgnoring = "loaded anyway because IgnoreGameVersion is on";

        private PackGameVersionCheck(
            PackGameVersionStatus status,
            GameVersionDifference? difference,
            string? madeFor,
            string running,
            PackSkipReason? skipReason,
            string? warning,
            string detail)
        {
            Status = status;
            Difference = difference;
            MadeFor = madeFor;
            Running = running;
            SkipReason = skipReason;
            Warning = warning;
            Detail = detail;
        }

        /// <summary>What the fields say about the running game.</summary>
        public PackGameVersionStatus Status { get; }

        /// <summary>
        /// How the running game differs from the declared <c>gameVersion</c>, or null when the
        /// manifest declares no <c>gameVersion</c>.
        /// </summary>
        public GameVersionDifference? Difference { get; }

        /// <summary>The manifest's <c>gameVersion</c>, or null.</summary>
        public string? MadeFor { get; }

        /// <summary>The running game's version, trimmed, or <see cref="GameVersions.Unknown"/>.</summary>
        public string Running { get; }

        /// <summary>The reason the pack is skipped, or null when it loads.</summary>
        public PackSkipReason? SkipReason { get; }

        /// <summary>True when the pack is skipped.</summary>
        public bool Skips => SkipReason.HasValue;

        /// <summary>
        /// The text of the one warning line logged when the pack loads, e.g.
        /// <c>made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway</c>.
        /// Null when there is nothing to warn about, and always null when <see cref="Skips"/>.
        /// </summary>
        public string? Warning { get; }

        /// <summary>True when <see cref="Warning"/> is set.</summary>
        public bool Warns => Warning != null;

        /// <summary>
        /// One line for the pack author: the skip detail when <see cref="Skips"/>, the warning when
        /// <see cref="Warns"/>, otherwise what was compared. Never null.
        /// </summary>
        public string Detail { get; }

        /// <summary>Applies the policy to one manifest.</summary>
        /// <param name="manifest">The pack's manifest.</param>
        /// <param name="runningGameVersion">
        /// The running game's version (<c>UnityEngine.Application.version</c>), or null when it
        /// could not be read. Null, blank or unparseable never skips.
        /// </param>
        /// <param name="ignoreGameVersion">
        /// True to load a pack even when the running game is outside its
        /// <c>minGameVersion</c>/<c>maxGameVersion</c> range; it then warns instead of skipping.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
        public static PackGameVersionCheck Evaluate(
            PackManifest manifest,
            string? runningGameVersion,
            bool ignoreGameVersion = false)
        {
            if (manifest is null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var madeFor = manifest.GameVersion;
            var min = manifest.MinGameVersion;
            var max = manifest.MaxGameVersion;
            var runningText = string.IsNullOrWhiteSpace(runningGameVersion) ? null : runningGameVersion!.Trim();
            var running = runningText ?? GameVersions.Unknown;

            if (madeFor is null && min is null && max is null)
            {
                return new PackGameVersionCheck(
                    PackGameVersionStatus.NotDeclared, null, null, running, null, null,
                    "no game version declared; loads on any game build");
            }

            var parsed = GameVersions.TryParse(runningText, out var runningVersion);
            var runningPhrase = runningText is null
                ? "running game version unknown"
                : parsed
                    ? "running " + runningText
                    : "running " + runningText + " (not a version number)";

            GameVersionDifference? difference = null;
            string? madeForNote = null;
            if (madeFor != null)
            {
                difference = GameVersions.Difference(madeFor, runningText);
                if (difference != GameVersionDifference.Same)
                {
                    var how = GameVersions.Describe(madeFor, runningText);
                    madeForNote = "made for game " + madeFor + ", " + runningPhrase + (how is null ? string.Empty : " (" + how + ")");
                }
            }

            string? rangeNote = null;
            PackGameVersionStatus? outside = null;
            string? range = null;
            if (min != null || max != null)
            {
                range = DescribeRange(min, max);
                if (!parsed)
                {
                    rangeNote = (madeForNote is null ? runningPhrase + ", so " : string.Empty) + range + " not checked";
                }
                else if (min != null && runningVersion! < min)
                {
                    outside = PackGameVersionStatus.TooOld;
                }
                else if (max != null && runningVersion! > max)
                {
                    outside = PackGameVersionStatus.TooNew;
                }

                if (outside.HasValue)
                {
                    var need = "pack needs " + range + ", running " + runningText;
                    if (!ignoreGameVersion)
                    {
                        return new PackGameVersionCheck(
                            outside.Value,
                            difference,
                            madeFor,
                            running,
                            outside == PackGameVersionStatus.TooOld
                                ? PackSkipReason.GameVersionTooOld
                                : PackSkipReason.GameVersionTooNew,
                            null,
                            need);
                    }

                    rangeNote = need;
                }
            }

            var notes = new List<string>(2);
            if (madeForNote != null)
            {
                notes.Add(madeForNote);
            }

            if (rangeNote != null)
            {
                notes.Add(rangeNote);
            }

            if (notes.Count == 0)
            {
                var compared = runningPhrase +
                    (madeFor is null ? string.Empty : ", made for game " + madeFor) +
                    (range is null ? string.Empty : ", inside " + range);
                return new PackGameVersionCheck(
                    PackGameVersionStatus.Match, difference, madeFor, running, null, null, compared);
            }

            var warning = string.Join("; ", notes) + "; " + (outside.HasValue ? LoadedIgnoring : LoadedAnyway);
            var status = outside ?? (parsed ? PackGameVersionStatus.Differs : PackGameVersionStatus.Unverified);
            return new PackGameVersionCheck(status, difference, madeFor, running, null, warning, warning);
        }

        /// <summary>
        /// The declared range as a pack author reads it: <c>game &gt;= 1.17.0</c>,
        /// <c>game &lt;= 1.17.9</c>, or <c>game 1.17.0 to 1.17.9</c>.
        /// </summary>
        /// <param name="min">Inclusive lower bound, or null.</param>
        /// <param name="max">Inclusive upper bound, or null.</param>
        /// <returns>The phrase, or <c>any game</c> when both are null.</returns>
        public static string DescribeRange(SemVer? min, SemVer? max)
        {
            if (min != null && max != null)
            {
                return "game " + min + " to " + max;
            }

            if (min != null)
            {
                return "game >= " + min;
            }

            return max != null ? "game <= " + max : "any game";
        }

        /// <inheritdoc />
        public override string ToString() => Status + ": " + Detail;
    }
}
