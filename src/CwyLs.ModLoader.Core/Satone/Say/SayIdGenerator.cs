using System;
using System.Globalization;
using System.Threading;

namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>
    /// Hands out the ids one <c>Satone.Say</c> line needs: a number, and from it the scenario
    /// group id, the localization row id, the generated voice name and the episode number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Monotonic and unique for the lifetime of the process. That is the whole contract: the ids
    /// end up as keys in the game's master data next to its own 1381 rows, and a repeat would make
    /// one line overwrite another's text or clip.
    /// </para>
    /// <para>
    /// The episode number matters as much as the id. The game finds a scenario group by
    /// <c>(ScenarioType, EpisodeNumber)</c> in several places (for example
    /// <c>FacilityVoiceTextScenario.StartScenario</c>), so a Say line must never land on an
    /// episode number one of the game's own <c>MotionScenario</c> groups uses - they run 1..29 in
    /// v1.17.2, and <see cref="FirstEpisode"/> starts far above them.
    /// </para>
    /// </remarks>
    public sealed class SayIdGenerator
    {
        /// <summary>Prefix of every generated scenario group id.</summary>
        public const string GroupPrefix = "cwy_say_";

        /// <summary>Suffix of the single localization row a line owns.</summary>
        public const string LocalizationSuffix = "_001";

        /// <summary>Prefix of the voice name generated for a caller-supplied clip.</summary>
        public const string VoicePrefix = "CWY_Say_";

        /// <summary>Episode number of the first line; the game's own MotionScenario groups use 1..29.</summary>
        public const float FirstEpisode = 9000f;

        private int _last;

        /// <summary>Creates a generator.</summary>
        /// <param name="firstId">
        /// The id the first <see cref="Next"/> returns. Must be 1 or more; the default is 1.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstId"/> is below 1.</exception>
        public SayIdGenerator(int firstId = 1)
        {
            if (firstId < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(firstId), firstId, "the first id must be 1 or more");
            }

            _last = firstId - 1;
        }

        /// <summary>The last id handed out, or one below <c>firstId</c> before the first call.</summary>
        public int Last => Volatile.Read(ref _last);

        /// <summary>The next id. Thread-safe and strictly increasing.</summary>
        public int Next() => Interlocked.Increment(ref _last);

        /// <summary>The scenario group id for a line, e.g. <c>cwy_say_7</c>.</summary>
        /// <param name="id">The line id.</param>
        public static string GroupId(int id) => GroupPrefix + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>The localization row id for a line, e.g. <c>cwy_say_7_001</c>.</summary>
        /// <param name="id">The line id.</param>
        public static string LocalizationId(int id) => GroupId(id) + LocalizationSuffix;

        /// <summary>
        /// The voice name generated for a line whose caller supplied a clip rather than a name,
        /// e.g. <c>CWY_Say_7</c>. Always satisfies <c>VoiceNameRules.Pattern</c>.
        /// </summary>
        /// <param name="id">The line id.</param>
        public static string VoiceName(int id) => VoicePrefix + id.ToString(CultureInfo.InvariantCulture);

        /// <summary>The episode number for a line: <see cref="FirstEpisode"/> plus the id.</summary>
        /// <param name="id">The line id.</param>
        public static float EpisodeFor(int id) => FirstEpisode + id;

        /// <summary>True when <paramref name="groupId"/> is one this generator's format produces.</summary>
        /// <param name="groupId">A scenario group id, or null.</param>
        public static bool IsSayGroup(string? groupId) =>
            !string.IsNullOrEmpty(groupId) && groupId!.StartsWith(GroupPrefix, StringComparison.Ordinal);
    }
}
