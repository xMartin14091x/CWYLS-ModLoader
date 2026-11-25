using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>What a <see cref="MakerDlcMarker"/> matches.</summary>
    public enum MakerDlcMarkerKind
    {
        /// <summary>A <c>ScenarioType</c> number: every scenario group of that type is DLC.</summary>
        ScenarioType,

        /// <summary>A scenario group id, exact or <c>prefix*</c>.</summary>
        Group,

        /// <summary>A localisation id, exact or <c>prefix*</c>.</summary>
        Line,

        /// <summary>
        /// The game's own paid-DLC flag on a scenario group (<see cref="MakerGameGroup.DlcFlag"/>; the pattern names the game
        /// member, e.g. <c>MyPaceOnly</c>). A flagged group is DLC. From <see cref="MakerDlcMarkers.FlagExpectedFrom"/> on, a group
        /// whose flag could not be read and that is not in <see cref="MakerDlcBaseline.KnownFreeGroups"/> is hidden too (fail closed).
        /// </summary>
        GroupFlag,
    }

    /// <summary>Whether the player owns a DLC, as far as the loader knows.</summary>
    public enum MakerDlcOwnership
    {
        /// <summary>Not known (no ownership read, or it failed). Treated as not owned.</summary>
        Unknown,

        /// <summary>Owned: the DLC's lines are listed and editable as usual.</summary>
        Owned,

        /// <summary>Not owned.</summary>
        NotOwned,
    }

    /// <summary>One rule that marks game lines as paid DLC content.</summary>
    public sealed class MakerDlcMarker
    {
        /// <summary>Creates a marker.</summary>
        /// <param name="kind">What it matches.</param>
        /// <param name="pattern">A <c>ScenarioType</c> number (invariant digits), or an id, exact or ending in <c>*</c> for a prefix.</param>
        /// <param name="appId">The Steam app id of the DLC the content belongs to.</param>
        /// <exception cref="ArgumentException">The pattern is blank, a lone <c>*</c>, or not a number for <see cref="MakerDlcMarkerKind.ScenarioType"/>.</exception>
        public MakerDlcMarker(MakerDlcMarkerKind kind, string pattern, uint appId)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*")
            {
                throw new ArgumentException("a DLC marker needs a pattern that is not empty or a lone *", nameof(pattern));
            }

            Kind = kind;
            Pattern = pattern.Trim();
            AppId = appId;
            if (kind == MakerDlcMarkerKind.ScenarioType)
            {
                if (!int.TryParse(Pattern, NumberStyles.None, CultureInfo.InvariantCulture, out var type))
                {
                    throw new ArgumentException("a ScenarioType marker needs a number, not '" + pattern + "'", nameof(pattern));
                }

                ScenarioType = type;
            }
        }

        /// <summary>What it matches.</summary>
        public MakerDlcMarkerKind Kind { get; }

        /// <summary>The pattern as given (trimmed).</summary>
        public string Pattern { get; }

        /// <summary>The Steam app id of the DLC.</summary>
        public uint AppId { get; }

        /// <summary>The <c>ScenarioType</c> number for a <see cref="MakerDlcMarkerKind.ScenarioType"/> marker, otherwise -1.</summary>
        public int ScenarioType { get; } = -1;

        /// <summary>True when an id-kind marker matches <paramref name="id"/> (ordinal; a trailing <c>*</c> is a prefix match).</summary>
        /// <param name="id">A group or line id.</param>
        public bool MatchesId(string? id)
        {
            if (string.IsNullOrEmpty(id) || Kind == MakerDlcMarkerKind.ScenarioType || Kind == MakerDlcMarkerKind.GroupFlag)
            {
                return false;
            }

            return Pattern.EndsWith("*", StringComparison.Ordinal)
                ? id!.StartsWith(Pattern.Substring(0, Pattern.Length - 1), StringComparison.Ordinal)
                : string.Equals(id, Pattern, StringComparison.Ordinal);
        }

        /// <inheritdoc />
        public override string ToString() => Kind + ":" + Pattern + " (app " + AppId.ToString(CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// The DLC markers the loader ships. Code, not configuration: a list a user could empty would be a way around the
    /// gate, so only a loader release changes it, and adding a marker only ever hides more.
    /// </summary>
    public static class MakerDlcMarkers
    {
        /// <summary>
        /// Steam app id of the game's DLC constant <c>NestopiSystem.Steam.BulbulSteamDLC.DLC_1</c> (read in 1.17.3). The Steam store
        /// lists app 3718890 as the "Room &amp; Cozy Activities Expansion" (HN-056, 2026-11-14; not verified by the loader).
        /// </summary>
        public const uint RoomExpansionAppId = 3718890;

        /// <summary>
        /// The game build these markers were made for (<see cref="GameVersionGate.ExpectedGameVersion"/>). On a newer or unreadable
        /// build the gate hides every line no classified group speaks (audit F1) until a loader release confirms the new build.
        /// </summary>
        public const string MadeForGameVersion = GameVersionGate.ExpectedGameVersion;

        /// <summary><c>Bulbul.MasterData.ScenarioType.DLCScenario</c>: the game's own type for DLC stories (no group used it in 1.17.3).</summary>
        public const int DlcScenarioType = 3;

        /// <summary>
        /// The game's own paid-DLC flag on a scenario group, <c>Bulbul.MasterData.ScenarioGroupData.MyPaceOnly</c> (game 2.0.0,
        /// 25 Nov 2026): the "Room &amp; Cozy Activities Expansion" (in-game "My Pace") groups. The game checks it against
        /// <c>IDLCState.IsOwned(DLCType.MyPace)</c> (<c>DLCType.MyPace</c> = 3718890).
        /// </summary>
        public const string DlcFlagMember = "MyPaceOnly";

        /// <summary>The first game version that has <see cref="DlcFlagMember"/>; from here on an unreadable flag hides (fail closed).</summary>
        public const string FlagExpectedFrom = "2.0.0";

        /// <summary>
        /// <c>ScenarioType</c> numbers game 2.0.0 added for the DLC's "My Pace" mode only (<c>HeroineClick_MyPace_*</c> 600-601,
        /// <c>SpeakWord_Player_MyPace*</c> 2800-2853, <c>SpeakWord_MyPace_Transition*</c> 2860-2861, <c>SpeakWord_MyPace_WorkStart</c> /
        /// <c>BreakStart</c> / <c>Finish</c> 3300-3500, <c>LocationMove_Departure</c> / <c>Arrival</c> 3600-3700 (moving between the
        /// DLC's locations), <c>MyPaceIntroduction</c> 3800). None exists in 1.18.1. Base-game groups the
        /// game also reuses in the My Pace pools (<c>ReactionTalkSelector</c>'s extra row ids, 39 groups, all in 1.18.1) are free
        /// content and stay listed.
        /// </summary>
        public static IReadOnlyList<int> MyPaceScenarioTypes { get; } = new[] { 600, 601, 2800, 2801, 2850, 2851, 2852, 2853, 2860, 2861, 3300, 3400, 3500, 3600, 3700, 3800 };

        /// <summary>
        /// Localisation id prefixes of the lines game 2.0.0 picks at playback when Satone moves between the DLC's locations
        /// (<c>LocationMoveTextSelector</c>: departures <c>goto_&lt;desk|bed|stairs&gt;_</c>, arrivals <c>location_arrival_</c>). No novel
        /// row names them, so they are marked by id. Moving only exists in the DLC's My Pace mode.
        /// </summary>
        public static IReadOnlyList<string> MovementLinePrefixes { get; } = new[] { "goto_desk_", "goto_bed_", "goto_stairs_", "location_arrival_" };

        /// <summary>
        /// Always on: every group of the game's own DLC story type, of a My Pace type, or carrying the game's own DLC flag is DLC.
        /// </summary>
        public static IReadOnlyList<MakerDlcMarker> BuiltIn { get; } = BuildBuiltIn();

        private static IReadOnlyList<MakerDlcMarker> BuildBuiltIn()
        {
            var list = new List<MakerDlcMarker>
            {
                new MakerDlcMarker(MakerDlcMarkerKind.ScenarioType, DlcScenarioType.ToString(CultureInfo.InvariantCulture), RoomExpansionAppId),
                new MakerDlcMarker(MakerDlcMarkerKind.GroupFlag, DlcFlagMember, RoomExpansionAppId),
            };
            foreach (var type in MyPaceScenarioTypes)
            {
                list.Add(new MakerDlcMarker(MakerDlcMarkerKind.ScenarioType, type.ToString(CultureInfo.InvariantCulture), RoomExpansionAppId));
            }

            foreach (var prefix in MovementLinePrefixes)
            {
                list.Add(new MakerDlcMarker(MakerDlcMarkerKind.Line, prefix + "*", RoomExpansionAppId));
            }

            return list.AsReadOnly();
        }

        /// <summary>
        /// The DLC's own groups and lines, filled when the DLC ships (24 Nov 2026) from that build's master data. Empty until then.
        /// </summary>
        public static IReadOnlyList<MakerDlcMarker> DlcDay { get; } = new MakerDlcMarker[0];

        /// <summary><see cref="BuiltIn"/> then <see cref="DlcDay"/>.</summary>
        public static IReadOnlyList<MakerDlcMarker> All { get; } = Concat(BuiltIn, DlcDay);

        private static IReadOnlyList<MakerDlcMarker> Concat(IReadOnlyList<MakerDlcMarker> a, IReadOnlyList<MakerDlcMarker> b)
        {
            var all = new List<MakerDlcMarker>(a.Count + b.Count);
            all.AddRange(a);
            all.AddRange(b);
            return all.AsReadOnly();
        }
    }

    /// <summary>
    /// Which game lines, groups and voice clips Pack Maker hides because they are paid DLC content the player is not known to own.
    /// Immutable. <see cref="IsKnown"/> is false when the gate could not decide (fail closed: callers refuse).
    /// </summary>
    public sealed class MakerDlcHidden
    {
        private static readonly HashSet<string> NoIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _lines;
        private readonly HashSet<string> _groups;
        private readonly HashSet<string> _voices;
        private readonly HashSet<string> _novelRows;

        /// <summary>Creates a verdict.</summary>
        /// <param name="lines">Hidden localisation ids.</param>
        /// <param name="groups">Hidden scenario group ids.</param>
        /// <param name="voices">Voice clip names of hidden lines.</param>
        /// <param name="unresolvedGroups">Groups named by novel rows but missing from the group list (hidden while a type marker is active).</param>
        /// <param name="summary">One line for the log: what is hidden and why.</param>
        public MakerDlcHidden(IEnumerable<string>? lines, IEnumerable<string>? groups, IEnumerable<string>? voices, int unresolvedGroups, string? summary)
            : this(true, lines, groups, voices, unresolvedGroups, summary, null)
        {
        }

        /// <summary>Creates a verdict that also names the game's novel rows of hidden content.</summary>
        /// <param name="lines">Hidden localisation ids.</param>
        /// <param name="groups">Hidden scenario group ids.</param>
        /// <param name="voices">Voice clip names of hidden lines.</param>
        /// <param name="unresolvedGroups">Groups named by novel rows but missing from the group list.</param>
        /// <param name="summary">One line for the log: what is hidden and why.</param>
        /// <param name="novelRows">Ids of the game's novel rows that belong to a hidden group or speak a hidden line.</param>
        public MakerDlcHidden(IEnumerable<string>? lines, IEnumerable<string>? groups, IEnumerable<string>? voices, int unresolvedGroups, string? summary,
            IEnumerable<string>? novelRows)
            : this(true, lines, groups, voices, unresolvedGroups, summary, novelRows)
        {
        }

        private MakerDlcHidden(bool known, IEnumerable<string>? lines, IEnumerable<string>? groups, IEnumerable<string>? voices, int unresolvedGroups, string? summary,
            IEnumerable<string>? novelRows)
        {
            _novelRows = novelRows is null ? NoIds : new HashSet<string>(novelRows, StringComparer.Ordinal);
            IsKnown = known;
            _lines = lines is null ? NoIds : new HashSet<string>(lines, StringComparer.Ordinal);
            _groups = groups is null ? NoIds : new HashSet<string>(groups, StringComparer.Ordinal);
            _voices = voices is null ? NoIds : new HashSet<string>(voices, StringComparer.Ordinal);
            UnresolvedGroups = unresolvedGroups;
            Summary = summary ?? string.Empty;
        }

        /// <summary>Nothing hidden (no active marker matched anything).</summary>
        public static MakerDlcHidden None { get; } = new MakerDlcHidden(null, null, null, 0, "no DLC lines hidden");

        /// <summary>True when <paramref name="verdict"/> is missing (not computed yet) or hides the line: fail closed.</summary>
        /// <param name="verdict">The verdict, or null.</param>
        /// <param name="id">A localisation id.</param>
        public static bool Protects(MakerDlcHidden? verdict, string? id) => verdict is null || verdict.IsLine(id);

        /// <summary>True when <paramref name="verdict"/> is missing (not computed yet) or hides the voice clip: fail closed.</summary>
        /// <param name="verdict">The verdict, or null.</param>
        /// <param name="clip">A voice clip name.</param>
        public static bool ProtectsVoice(MakerDlcHidden? verdict, string? clip) => verdict is null || verdict.IsVoice(clip);

        /// <summary>True when <paramref name="verdict"/> is missing (not computed yet) or hides the group: fail closed.</summary>
        /// <param name="verdict">The verdict, or null.</param>
        /// <param name="id">A scenario group id.</param>
        public static bool ProtectsGroup(MakerDlcHidden? verdict, string? id) => verdict is null || verdict.IsGroup(id);

        /// <summary>The gate could not decide; every caller refuses (fail closed).</summary>
        /// <param name="why">Why, for the refusal and the log.</param>
        public static MakerDlcHidden Unknown(string why) => new MakerDlcHidden(false, null, null, null, 0, why, null);

        /// <summary>False when the gate could not decide.</summary>
        public bool IsKnown { get; }

        /// <summary>How many lines are hidden.</summary>
        public int LineCount => _lines.Count;

        /// <summary>How many scenario groups are hidden.</summary>
        public int GroupCount => _groups.Count;

        /// <summary>How many voice clips are hidden.</summary>
        public int VoiceCount => _voices.Count;

        /// <summary>Groups named by novel rows but not in the group list (their type is unknown; hidden while a type marker is active).</summary>
        public int UnresolvedGroups { get; }

        /// <summary>One line for the log; never line text.</summary>
        public string Summary { get; }

        /// <summary>True when the line is hidden, or the verdict is unknown.</summary>
        /// <param name="id">A localisation id.</param>
        public bool IsLine(string? id) => !IsKnown || (id != null && _lines.Contains(id));

        /// <summary>True when the group is hidden, or the verdict is unknown.</summary>
        /// <param name="id">A scenario group id.</param>
        public bool IsGroup(string? id) => !IsKnown || (id != null && _groups.Contains(id));

        /// <summary>True when the game's novel row belongs to a hidden group or speaks a hidden line, or the verdict is unknown.</summary>
        /// <param name="rowId">A novel row id.</param>
        public bool IsNovelRow(string? rowId) => !IsKnown || (rowId != null && _novelRows.Contains(rowId));

        /// <summary>How many of the game's novel rows belong to hidden content.</summary>
        public int NovelRowCount => _novelRows.Count;

        /// <summary>True when the voice clip belongs to a hidden line, or the verdict is unknown.</summary>
        /// <param name="clip">A voice clip name (file stem).</param>
        public bool IsVoice(string? clip) => !IsKnown || (clip != null && _voices.Contains(clip));

        /// <summary>
        /// The refusal for a route keyed by a line id or a voice clip, or null when it may go ahead. 404: for a player who is not
        /// known to own the DLC, the line does not exist in Pack Maker. Unknown: <paramref name="notReadyCode"/>.
        /// </summary>
        /// <param name="lineId">The line id the route names, or null.</param>
        /// <param name="clip">The voice clip the route names, or null.</param>
        /// <param name="notReadyCode">The error code when the verdict is unknown.</param>
        public MakerResult<bool>? Refusal(string? lineId, string? clip, string notReadyCode = MakerErrorCodes.NotReady)
        {
            if (!IsKnown)
            {
                return MakerResult<bool>.Fail(notReadyCode, "Pack Maker cannot check for paid DLC lines yet (" + Summary + "); try again in a moment.");
            }

            if (lineId != null && _lines.Contains(lineId))
            {
                return MakerResult<bool>.Fail(MakerErrorCodes.NotFound,
                    "line '" + lineId + "' is paid DLC content that Pack Maker cannot confirm you own, so it is not listed or editable.");
            }

            if (clip != null && _voices.Contains(clip))
            {
                return MakerResult<bool>.Fail(MakerErrorCodes.NotFound,
                    "voice clip '" + clip + "' belongs to paid DLC content that Pack Maker cannot confirm you own, so it is not editable.");
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString() => Summary;
    }

    /// <summary>
    /// Pack Maker's DLC gate: decides from the game's scenario data which lines are paid DLC content, and hides them unless the
    /// player is known to own that DLC. Unity-free; the adapter passes it to <see cref="MakerGameData"/>, which drops the hidden
    /// rows, novel rows and groups at capture, so every catalog, editor, export and count reads the gated snapshot.
    /// </summary>
    /// <remarks>
    /// Fail closed: a marker stays active unless its app is <see cref="MakerDlcOwnership.Owned"/>; a missing ownership function,
    /// an exception or <see cref="MakerDlcOwnership.Unknown"/> all hide. A line spoken by a free group and a DLC group is hidden.
    /// While a type marker is active, a group the gate cannot classify (missing from the group list, or of unknown type) is
    /// hidden (audit IG-02). When the running game is newer than <see cref="MakerDlcMarkers.MadeForGameVersion"/>, or its version is unknown, every
    /// line that no classified group speaks is hidden too (audit F1); lines of groups classified free stay listed.
    /// No network and no Steam call is made here; the ownership function is the host's.
    /// </remarks>
    public sealed class MakerDlcGate
    {
        private readonly IReadOnlyList<MakerDlcMarker> _markers;
        private readonly Func<uint, MakerDlcOwnership>? _ownership;
        private readonly Func<string?>? _gameVersion;
        private readonly IReadOnlyCollection<string> _knownFreeGroups;

        /// <summary>Creates a gate that does not know the running game's version (treated as a build the markers were not made for).</summary>
        /// <param name="markers">The markers; null entries are dropped. Null or empty = nothing is DLC.</param>
        /// <param name="ownership">Ownership per Steam app id; null = unknown for every app (hide).</param>
        public MakerDlcGate(IEnumerable<MakerDlcMarker?>? markers, Func<uint, MakerDlcOwnership>? ownership)
            : this(markers, ownership, null, null)
        {
        }

        /// <summary>Creates a gate that compares the running game's version with the build the markers were made for.</summary>
        /// <param name="markers">The markers; null entries are dropped. Null or empty = nothing is DLC.</param>
        /// <param name="ownership">Ownership per Steam app id; null = unknown for every app (hide).</param>
        /// <param name="gameVersion">The running game's version (normally <c>Application.version</c>); null, blank, unreadable or throwing = unknown.</param>
        /// <param name="madeFor">The build the markers were made for; null = <see cref="MakerDlcMarkers.MadeForGameVersion"/>.</param>
        public MakerDlcGate(IEnumerable<MakerDlcMarker?>? markers, Func<uint, MakerDlcOwnership>? ownership, Func<string?>? gameVersion, string? madeFor)
            : this(markers, ownership, gameVersion, madeFor, null, null)
        {
        }

        /// <summary>Creates a gate with its own flag rules (tests).</summary>
        /// <param name="markers">The markers; null entries are dropped. Null or empty = nothing is DLC.</param>
        /// <param name="ownership">Ownership per Steam app id; null = unknown for every app (hide).</param>
        /// <param name="gameVersion">The running game's version; null, blank, unreadable or throwing = unknown.</param>
        /// <param name="madeFor">The build the markers were made for; null = <see cref="MakerDlcMarkers.MadeForGameVersion"/>.</param>
        /// <param name="flagExpectedFrom">The first game version with the DLC flag; null = <see cref="MakerDlcMarkers.FlagExpectedFrom"/>.</param>
        /// <param name="knownFreeGroups">Group ids known to be free; null = <see cref="MakerDlcBaseline.KnownFreeGroups"/>.</param>
        public MakerDlcGate(IEnumerable<MakerDlcMarker?>? markers, Func<uint, MakerDlcOwnership>? ownership, Func<string?>? gameVersion, string? madeFor,
            string? flagExpectedFrom, IReadOnlyCollection<string>? knownFreeGroups)
        {
            _gameVersion = gameVersion;
            MadeFor = string.IsNullOrWhiteSpace(madeFor) ? MakerDlcMarkers.MadeForGameVersion : madeFor!.Trim();
            FlagExpectedFrom = string.IsNullOrWhiteSpace(flagExpectedFrom) ? MakerDlcMarkers.FlagExpectedFrom : flagExpectedFrom!.Trim();
            _knownFreeGroups = knownFreeGroups ?? MakerDlcBaseline.KnownFreeGroups;
            var list = new List<MakerDlcMarker>();
            if (markers != null)
            {
                foreach (var marker in markers)
                {
                    if (marker != null)
                    {
                        list.Add(marker);
                    }
                }
            }

            _markers = list.AsReadOnly();
            _ownership = ownership;
        }

        /// <summary>
        /// The loader's shipped markers (<see cref="MakerDlcMarkers.All"/>) and no ownership read: every DLC line is hidden. It
        /// knows no game version, so lines no classified group speaks are hidden too; hosts use <see cref="ShippedFor"/>.
        /// </summary>
        public static MakerDlcGate Shipped { get; } = new MakerDlcGate(MakerDlcMarkers.All, null);

        /// <summary>The shipped markers and no ownership read, for the running game's version.</summary>
        /// <param name="gameVersion">The running game's version (normally <c>Application.version</c>).</param>
        public static MakerDlcGate ShippedFor(Func<string?> gameVersion) => new MakerDlcGate(MakerDlcMarkers.All, null, gameVersion, null);

        /// <summary>The markers.</summary>
        public IReadOnlyList<MakerDlcMarker> Markers => _markers;

        /// <summary>The game build the markers were made for.</summary>
        public string MadeFor { get; }

        /// <summary>The first game version that has the DLC flag (<see cref="MakerDlcMarkers.FlagExpectedFrom"/>).</summary>
        public string FlagExpectedFrom { get; }

        /// <summary>
        /// True when the running game should have the DLC flag: any version after <see cref="MakerDlcBaseline.LastVersionWithoutFlag"/>
        /// (1.18.1), or an unknown or unreadable one (fail closed). False only for 1.18.1 and older, which have no flag.
        /// </summary>
        public bool FlagExpected()
        {
            string? version = null;
            try
            {
                version = _gameVersion?.Invoke();
            }
            catch (Exception)
            {
                version = null;
            }

            if (string.IsNullOrWhiteSpace(version) || !GameVersions.TryParse(version!.Trim(), out var actual)
                || !GameVersions.TryParse(MakerDlcBaseline.LastVersionWithoutFlag, out var last))
            {
                return true;
            }

            // Every build after the last one without the flag expects it: 1.18.2-1.19.x and 2.0.0 pre-releases included (G7).
            return actual! > last!;
        }

        /// <summary>True when the group is hidden because of the DLC flag: flagged, or (flag expected) unreadable and not a pre-DLC group.</summary>
        private bool FlagHides(bool flagMarker, string id, bool? flag, bool flagExpected)
            => flagMarker && (flag == true || (flag is null && flagExpected && !_knownFreeGroups.Contains(id)));

        /// <summary>
        /// True when the running game is the build the markers were made for or an older one; false when it is newer, or its
        /// version is unknown or not a version number (then lines no classified group speaks are hidden).
        /// </summary>
        /// <param name="running">The running version as read, or <see cref="GameVersions.Unknown"/>.</param>
        public bool GameVersionConfirmed(out string running)
        {
            string? version = null;
            try
            {
                version = _gameVersion?.Invoke();
            }
            catch (Exception)
            {
                version = null;
            }

            running = string.IsNullOrWhiteSpace(version) ? GameVersions.Unknown : version!.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            if (GameVersions.Difference(MadeFor, version) == GameVersionDifference.Same)
            {
                return true;
            }

            // Older builds are confirmed only up to the last one without paid content; a build between that and the made-for build
            // (1.18.2-1.19.x, 2.0.0 pre-releases) is not (G7).
            return GameVersions.TryParse(MakerDlcBaseline.LastVersionWithoutFlag, out var last) && GameVersions.TryParse(version, out var actual)
                && !(actual! > last!);
        }

        /// <summary>The ownership of <paramref name="appId"/>; any failure is <see cref="MakerDlcOwnership.Unknown"/>.</summary>
        /// <param name="appId">A Steam app id.</param>
        public MakerDlcOwnership OwnershipOf(uint appId)
        {
            if (_ownership is null)
            {
                return MakerDlcOwnership.Unknown;
            }

            try
            {
                var answer = _ownership(appId);
                return answer == MakerDlcOwnership.Owned || answer == MakerDlcOwnership.NotOwned ? answer : MakerDlcOwnership.Unknown;
            }
            catch (Exception)
            {
                return MakerDlcOwnership.Unknown;
            }
        }

        /// <summary>
        /// True when an active marker (its app not <see cref="MakerDlcOwnership.Owned"/>) marks the scenario group as DLC: by
        /// group id or by its <c>ScenarioType</c>, or when a type marker is active and the type is unknown (fail closed). For
        /// game-side paths outside the catalog (console <c>scene play</c>).
        /// </summary>
        /// <param name="id">The scenario group id.</param>
        /// <param name="scenario">Its <c>ScenarioType</c> number, or null when unknown.</param>
        /// <remarks>Without the game's DLC flag (<c>null</c>): on a build that should have it, a group outside the known-free set is hidden (fail closed).</remarks>
        public bool HidesGroup(string? id, int? scenario) => HidesGroup(id, scenario, null);

        /// <summary>
        /// <see cref="HidesGroup(string?, int?)"/> with the game's own DLC flag: a flagged group is hidden; from
        /// <see cref="FlagExpectedFrom"/> on, a group whose flag is null (unreadable) and that is not a pre-DLC group is hidden too.
        /// </summary>
        /// <param name="id">The scenario group id.</param>
        /// <param name="scenario">Its <c>ScenarioType</c> number, or null when unknown.</param>
        /// <param name="dlcFlag">The game's DLC flag (true/false), or null when absent or unreadable.</param>
        public bool HidesGroup(string? id, int? scenario, bool? dlcFlag)
        {
            if (string.IsNullOrEmpty(id) || _markers.Count == 0)
            {
                return false;
            }

            var active = new List<MakerDlcMarker>();
            foreach (var marker in _markers)
            {
                if (OwnershipOf(marker.AppId) != MakerDlcOwnership.Owned)
                {
                    active.Add(marker);
                }
            }

            return GroupMatches(active, id!, scenario)
                || (!scenario.HasValue && active.Exists(m => m.Kind == MakerDlcMarkerKind.ScenarioType))
                || FlagHides(active.Exists(m => m.Kind == MakerDlcMarkerKind.GroupFlag), id!, dlcFlag, FlagExpected());
        }

        /// <summary>Decides what to hide in one game snapshot.</summary>
        /// <param name="rows">The game's localisation rows.</param>
        /// <param name="novelRows">The novel rows (line to group).</param>
        /// <param name="groups">The scenario groups, with their <c>ScenarioType</c> when known.</param>
        /// <returns>
        /// The verdict; <see cref="MakerDlcHidden.Unknown"/> when a type marker is active but no group list was captured. While a
        /// type marker is active, groups missing from the list or of unknown type are hidden; on a game build the markers were not
        /// made for (<see cref="GameVersionConfirmed"/>), lines no classified group speaks are hidden.
        /// </returns>
        public MakerDlcHidden Decide(IEnumerable<MakerGameRow> rows, IEnumerable<MakerGameNovelRow> novelRows, IEnumerable<MakerGameGroup> groups)
        {
            if (_markers.Count == 0)
            {
                return MakerDlcHidden.None;
            }

            var ownership = new Dictionary<uint, MakerDlcOwnership>();
            var active = new List<MakerDlcMarker>();
            foreach (var marker in _markers)
            {
                if (!ownership.TryGetValue(marker.AppId, out var owned))
                {
                    owned = OwnershipOf(marker.AppId);
                    ownership.Add(marker.AppId, owned);
                }

                if (owned != MakerDlcOwnership.Owned)
                {
                    active.Add(marker);
                }
            }

            if (active.Count == 0)
            {
                return new MakerDlcHidden(null, null, null, 0, "no DLC lines hidden (" + Describe(ownership) + ")");
            }

            var typeMarkers = active.Exists(m => m.Kind == MakerDlcMarkerKind.ScenarioType);
            var flagMarker = active.Exists(m => m.Kind == MakerDlcMarkerKind.GroupFlag);
            var flagExpected = flagMarker && FlagExpected();
            var groupTypes = new Dictionary<string, int?>(StringComparer.Ordinal);
            var groupFlags = new Dictionary<string, bool?>(StringComparer.Ordinal);
            var groupTexts = new Dictionary<string, MakerGameGroup>(StringComparer.Ordinal);
            foreach (var group in groups ?? Array.Empty<MakerGameGroup>())
            {
                if (group != null && !groupTypes.ContainsKey(group.Id))
                {
                    groupTypes.Add(group.Id, group.Scenario);
                    groupFlags.Add(group.Id, group.DlcFlag);
                    groupTexts.Add(group.Id, group);
                }
            }

            var novel = new List<MakerGameNovelRow>();
            foreach (var row in novelRows ?? Array.Empty<MakerGameNovelRow>())
            {
                if (row != null)
                {
                    novel.Add(row);
                }
            }

            if ((typeMarkers || flagMarker) && groupTypes.Count == 0 && novel.Exists(r => r.GroupId != null))
            {
                return MakerDlcHidden.Unknown("the game's scenario groups were not read, so DLC story types cannot be told apart");
            }

            // G6: lines but no way to tell which group speaks them (no novel rows, no group on any row): cannot decide.
            var rowList = new List<MakerGameRow>();
            foreach (var row in rows ?? Array.Empty<MakerGameRow>())
            {
                if (row != null) rowList.Add(row);
            }

            rows = rowList;
            if ((typeMarkers || flagMarker) && rowList.Count > 0 && !novel.Exists(r => r.GroupId != null) && !rowList.Exists(r => r.Group != null))
            {
                return MakerDlcHidden.Unknown("the game's novel rows were not read, so no line can be tied to its scenario group");
            }

            if ((typeMarkers || flagMarker) && groupTypes.Count == 0 && rowList.Exists(r => r.Group != null))
            {
                return MakerDlcHidden.Unknown("the game's scenario groups were not read, so DLC groups cannot be told apart");
            }

            // IG-02: while a type marker is active, a group whose type is unknown cannot be told free, so it is hidden.
            var hiddenGroups = new HashSet<string>(StringComparer.Ordinal);
            var flagged = 0;
            var unreadableHidden = 0;
            foreach (var pair in groupTypes)
            {
                var flag = groupFlags[pair.Key];
                if (GroupMatches(active, pair.Key, pair.Value) || (typeMarkers && !pair.Value.HasValue))
                {
                    hiddenGroups.Add(pair.Key);
                }
                else if (FlagHides(flagMarker, pair.Key, flag, flagExpected))
                {
                    hiddenGroups.Add(pair.Key);
                    if (flag == true) flagged++; else unreadableHidden++;
                }
            }

            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            var hiddenLines = new HashSet<string>(StringComparer.Ordinal);
            var grouped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in novel)
            {
                if (row.GroupId is null)
                {
                    continue;
                }

                if (row.LineId != null)
                {
                    grouped.Add(row.LineId);
                }

                if (!groupTypes.ContainsKey(row.GroupId))
                {
                    unresolved.Add(row.GroupId);
                    if (typeMarkers || flagMarker || GroupMatches(active, row.GroupId, null))
                    {
                        hiddenGroups.Add(row.GroupId);
                    }
                }

                if (row.LineId != null && hiddenGroups.Contains(row.GroupId))
                {
                    hiddenLines.Add(row.LineId);
                }
            }

            // A hidden group's title and subtitle are its own localisation rows (the story history shows them): hidden too.
            foreach (var id in hiddenGroups)
            {
                if (groupTexts.TryGetValue(id, out var texts))
                {
                    if (texts.TitleId != null) hiddenLines.Add(texts.TitleId);
                    if (texts.SubtitleId != null) hiddenLines.Add(texts.SubtitleId);
                }
            }

            // F1: on a build the markers were not made for, a voiced line no classified group speaks may be new DLC dialogue.
            // Unvoiced rows outside every group (menus, labels) stay listed, so a newer patch does not take away pack translations
            // of the game's own interface; new paid lines picked at playback are covered by the line markers.
            var confirmed = GameVersionConfirmed(out var running);
            var unclassified = 0;
            var hiddenVoices = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows ?? Array.Empty<MakerGameRow>())
            {
                if (row is null)
                {
                    continue;
                }

                if (row.Group != null && (typeMarkers || flagMarker) && !groupTypes.ContainsKey(row.Group))
                {
                    unresolved.Add(row.Group);
                    hiddenGroups.Add(row.Group);
                }

                if (!hiddenLines.Contains(row.Id))
                {
                    if (LineMatches(active, row.Id) || (row.Group != null && hiddenGroups.Contains(row.Group)))
                    {
                        hiddenLines.Add(row.Id);
                    }
                    else if (!confirmed && row.Group is null && !grouped.Contains(row.Id) && row.Voice != null)
                    {
                        hiddenLines.Add(row.Id);
                        unclassified++;
                    }
                }

                if (row.Voice != null && hiddenLines.Contains(row.Id))
                {
                    hiddenVoices.Add(row.Voice);
                }
            }

            var summary = hiddenLines.Count == 0 && hiddenGroups.Count == 0
                ? "no DLC lines hidden (" + Describe(ownership) + ")"
                : hiddenLines.Count.ToString(CultureInfo.InvariantCulture) + " line(s) in " + hiddenGroups.Count.ToString(CultureInfo.InvariantCulture)
                  + " group(s) hidden (" + Describe(ownership) + ")";
            if (unresolved.Count > 0 && typeMarkers)
            {
                // Name the ids (ids only, never line text) so a maintainer can tell DLC data from a packaging gap.
                var named = unresolved.OrderBy(g => g, StringComparer.Ordinal).Take(12).ToList();
                summary += "; " + unresolved.Count.ToString(CultureInfo.InvariantCulture) + " group(s) missing from the group list hidden ("
                    + string.Join(", ", named) + (unresolved.Count > named.Count ? ", …" : "") + ")";
            }

            if (flagged > 0)
            {
                summary += "; " + flagged.ToString(CultureInfo.InvariantCulture) + " group(s) carry the game's DLC flag ("
                    + MakerDlcMarkers.DlcFlagMember + ")";
            }

            if (unreadableHidden > 0)
            {
                summary += "; the game's DLC flag (" + MakerDlcMarkers.DlcFlagMember + ") could not be read, so "
                    + unreadableHidden.ToString(CultureInfo.InvariantCulture) + " group(s) outside the known-free set (game " + MakerDlcBaseline.CapturedOn
                    + ") are hidden";
            }

            if (!confirmed)
            {
                summary += "; game " + running + " is not the build " + MadeFor + " the DLC markers were made for, so "
                    + unclassified.ToString(CultureInfo.InvariantCulture) + " line(s) outside a classified group are hidden until a loader update";
            }
            var hiddenNovel = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in novel)
            {
                if (!string.IsNullOrEmpty(row.RowId)
                    && ((row.GroupId != null && hiddenGroups.Contains(row.GroupId)) || (row.LineId != null && hiddenLines.Contains(row.LineId))))
                {
                    hiddenNovel.Add(row.RowId);
                }
            }

            return new MakerDlcHidden(hiddenLines, hiddenGroups, hiddenVoices, unresolved.Count, summary, hiddenNovel);
        }

        private static bool GroupMatches(List<MakerDlcMarker> active, string id, int? scenario)
        {
            foreach (var marker in active)
            {
                if (marker.Kind == MakerDlcMarkerKind.Group && marker.MatchesId(id))
                {
                    return true;
                }

                if (marker.Kind == MakerDlcMarkerKind.ScenarioType && scenario.HasValue && scenario.Value == marker.ScenarioType)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LineMatches(List<MakerDlcMarker> active, string id)
        {
            foreach (var marker in active)
            {
                if (marker.Kind == MakerDlcMarkerKind.Line && marker.MatchesId(id))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Describe(Dictionary<uint, MakerDlcOwnership> ownership)
        {
            var sb = new StringBuilder();
            foreach (var pair in ownership)
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                sb.Append("app ").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(": ");
                sb.Append(pair.Value == MakerDlcOwnership.Owned ? "owned" : pair.Value == MakerDlcOwnership.NotOwned ? "not owned" : "ownership unknown");
            }

            return sb.ToString();
        }
    }
}
