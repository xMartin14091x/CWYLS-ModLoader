using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// One row of the game's <c>LocalizationMaster</c> as Pack Maker lists it: id, voice name,
    /// group and the game's own Japanese and English text.
    /// </summary>
    public sealed class MakerGameRow
    {
        /// <summary>Creates a row.</summary>
        /// <param name="id">The localisation id. Required.</param>
        /// <param name="voice">The voice name; null or empty when the row has no voice.</param>
        /// <param name="group">The scenario group that plays the row, or null when none does.</param>
        /// <param name="ja">The game's Japanese text; null is stored as empty.</param>
        /// <param name="en">The game's English text; null is stored as empty.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerGameRow(string id, string? voice, string? group, string? ja, string? en)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a localisation id is required", nameof(id));
            }

            Id = id;
            Voice = string.IsNullOrEmpty(voice) ? null : voice;
            Group = string.IsNullOrEmpty(group) ? null : group;
            Ja = ja ?? string.Empty;
            En = en ?? string.Empty;
        }

        /// <summary>The localisation id.</summary>
        public string Id { get; }

        /// <summary>The voice name, or null when the row has no voice.</summary>
        public string? Voice { get; }

        /// <summary>The scenario group that plays the row (<c>NovelMaster</c>), or null.</summary>
        public string? Group { get; }

        /// <summary>The game's Japanese text. Never null.</summary>
        public string Ja { get; }

        /// <summary>The game's English text. Never null.</summary>
        public string En { get; }

        /// <inheritdoc />
        public override string ToString() => Id + (Voice is null ? string.Empty : " (" + Voice + ")");
    }

    /// <summary>
    /// One <c>NovelMaster</c> row that names a localisation line, as Studio v2 context needs it: where the row
    /// sits (its scenario group), and the raw motion identifiers the row carries. Reference metadata only.
    /// </summary>
    public sealed class MakerGameNovelRow
    {
        /// <summary>Creates a row.</summary>
        /// <param name="rowId">The novel row id; blank is stored as empty.</param>
        /// <param name="groupId">The <c>ScenarioGroupID</c>; null or empty when the row names no group.</param>
        /// <param name="lineId">The localisation id the row speaks; null, empty or <c>"0"</c> (the game's "no line") means none.</param>
        /// <param name="talker">Who speaks, as the game's own name for the value; null when unknown.</param>
        /// <param name="bodyMotion">The raw body motion id, or null when the row sets none.</param>
        /// <param name="facialMotion">The raw facial motion id, or null when the row sets none.</param>
        public MakerGameNovelRow(string? rowId, string? groupId, string? lineId, string? talker, string? bodyMotion, string? facialMotion)
        {
            RowId = rowId ?? string.Empty;
            GroupId = string.IsNullOrEmpty(groupId) ? null : groupId;
            LineId = string.IsNullOrEmpty(lineId) || lineId == "0" ? null : lineId;
            Talker = string.IsNullOrEmpty(talker) ? null : talker;
            BodyMotion = string.IsNullOrEmpty(bodyMotion) ? null : bodyMotion;
            FacialMotion = string.IsNullOrEmpty(facialMotion) ? null : facialMotion;
        }

        /// <summary>The novel row id. Never null.</summary>
        public string RowId { get; }

        /// <summary>The scenario group the row belongs to, or null.</summary>
        public string? GroupId { get; }

        /// <summary>The localisation id the row speaks, or null when it speaks none.</summary>
        public string? LineId { get; }

        /// <summary>Who speaks, or null.</summary>
        public string? Talker { get; }

        /// <summary>The raw body motion id, or null.</summary>
        public string? BodyMotion { get; }

        /// <summary>The raw facial motion id, or null.</summary>
        public string? FacialMotion { get; }
    }

    /// <summary>One <c>ScenarioGroupMaster</c> row as Studio v2 lists groups: its id and a title read from the game's text.</summary>
    public sealed class MakerGameGroup
    {
        /// <summary>Creates a group.</summary>
        /// <param name="id">The scenario group id. Required.</param>
        /// <param name="title">A readable title from the game's own localisation, or null.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerGameGroup(string id, string? title)
            : this(id, title, null)
        {
        }

        /// <summary>Creates a group with its <c>ScenarioType</c> (the DLC gate reads it).</summary>
        /// <param name="id">The scenario group id. Required.</param>
        /// <param name="title">A readable title from the game's own localisation, or null.</param>
        /// <param name="scenario">The group's <c>ScenarioType</c> number, or null when unknown.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerGameGroup(string id, string? title, int? scenario)
            : this(id, title, scenario, null)
        {
        }

        /// <summary>Creates a group with its <c>ScenarioType</c> and the game's own paid-DLC flag (the DLC gate reads both).</summary>
        /// <param name="id">The scenario group id. Required.</param>
        /// <param name="title">A readable title from the game's own localisation, or null.</param>
        /// <param name="scenario">The group's <c>ScenarioType</c> number, or null when unknown.</param>
        /// <param name="dlcFlag">
        /// The game's own "this group belongs to the paid DLC" flag (<c>ScenarioGroupData.MyPaceOnly</c> from game 2.0.0): true or
        /// false when read, null when the game has no such flag or it could not be read.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerGameGroup(string id, string? title, int? scenario, bool? dlcFlag)
            : this(id, title, scenario, dlcFlag, null, null)
        {
        }

        /// <summary>Creates a group with its type, the game's DLC flag and the localisation ids of its title and subtitle.</summary>
        /// <param name="id">The scenario group id. Required.</param>
        /// <param name="title">A readable title from the game's own localisation, or null.</param>
        /// <param name="scenario">The group's <c>ScenarioType</c> number, or null when unknown.</param>
        /// <param name="dlcFlag">The game's own paid-DLC flag (true/false), or null when absent or unreadable.</param>
        /// <param name="titleId">The localisation id of the group's title (<c>TitleLocalizationID</c>), or null.</param>
        /// <param name="subtitleId">The localisation id of the group's subtitle (<c>SubtitleLocalizationID</c>), or null.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is blank.</exception>
        public MakerGameGroup(string id, string? title, int? scenario, bool? dlcFlag, string? titleId, string? subtitleId)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("a scenario group id is required", nameof(id));
            }

            Id = id;
            Title = string.IsNullOrWhiteSpace(title) ? null : title;
            Scenario = scenario;
            DlcFlag = dlcFlag;
            TitleId = IsLocalizationId(titleId) ? titleId : null;
            SubtitleId = IsLocalizationId(subtitleId) ? subtitleId : null;
        }

        // "0" is the game's own "none" value.
        private static bool IsLocalizationId(string? id) => !string.IsNullOrWhiteSpace(id) && id != "0";

        /// <summary>The scenario group id.</summary>
        public string Id { get; }

        /// <summary>A readable title, or null.</summary>
        public string? Title { get; }

        /// <summary>The group's <c>ScenarioType</c> number, or null when the adapter did not read it.</summary>
        public int? Scenario { get; }

        /// <summary>The game's own paid-DLC flag: true or false when read, null when absent or unreadable.</summary>
        public bool? DlcFlag { get; }

        /// <summary>The localisation id of the group's title, or null. Hidden with the group.</summary>
        public string? TitleId { get; }

        /// <summary>The localisation id of the group's subtitle, or null. Hidden with the group.</summary>
        public string? SubtitleId { get; }
    }

    /// <summary>
    /// What the catalog reads from the running game, taken in one go on the main thread: the
    /// localisation rows in the game's own order, the lengths of the game's voice clips, and the
    /// voice names mods and packs registered at runtime. Immutable; the catalog serves every query
    /// from one of these until something invalidates it.
    /// </summary>
    public sealed class MakerGameData
    {
        private static readonly MakerGameRow[] NoRows = new MakerGameRow[0];
        private static readonly MakerGameNovelRow[] NoNovel = new MakerGameNovelRow[0];
        private static readonly MakerGameGroup[] NoGroups = new MakerGameGroup[0];

        private readonly Dictionary<string, MakerGameRow> _byId;
        private readonly Dictionary<string, double> _clipLengths;
        private readonly HashSet<string> _registered;
        private readonly HashSet<string> _storyIds = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Creates a snapshot.</summary>
        /// <param name="rows">The rows in the game's order; null entries and repeated ids (after the first) are dropped.</param>
        /// <param name="clipLengths">
        /// The game's own voice clips: name to length in seconds (a length of 0 or less means the
        /// clip exists but its length is unknown). Null when the clip cache could not be read.
        /// </param>
        /// <param name="registeredVoices">Voice names registered with the voice feature (packs' global clips, mods' runtime clips).</param>
        /// <param name="problem">Why the rows are missing, when they are (master data not loaded yet).</param>
        public MakerGameData(
            IEnumerable<MakerGameRow?>? rows,
            IReadOnlyDictionary<string, double>? clipLengths,
            IEnumerable<string>? registeredVoices,
            string? problem = null)
            : this(rows, clipLengths, registeredVoices, null, null, problem)
        {
        }

        /// <summary>Creates a snapshot that also carries the novel rows and scenario groups Studio v2 context is built from.</summary>
        /// <param name="rows">The rows in the game's order; null entries and repeated ids (after the first) are dropped.</param>
        /// <param name="clipLengths">The game's own voice clips: name to length in seconds; null when unreadable.</param>
        /// <param name="registeredVoices">Voice names registered with the voice feature.</param>
        /// <param name="novelRows">The <c>NovelMaster</c> rows in the game's order (rows without a line may be omitted); null means none.</param>
        /// <param name="groups">The scenario groups in the game's order; null means none.</param>
        /// <param name="problem">Why the rows are missing, when they are.</param>
        public MakerGameData(
            IEnumerable<MakerGameRow?>? rows,
            IReadOnlyDictionary<string, double>? clipLengths,
            IEnumerable<string>? registeredVoices,
            IEnumerable<MakerGameNovelRow?>? novelRows,
            IEnumerable<MakerGameGroup?>? groups,
            string? problem = null)
            : this(rows, clipLengths, registeredVoices, novelRows, groups, problem, null)
        {
        }

        /// <summary>
        /// Creates a snapshot through Pack Maker's DLC gate: the lines, novel rows and groups the gate hides are dropped here, so
        /// nothing that reads the snapshot (lists, search, one line, counts, the editors' line check, export) sees them.
        /// </summary>
        /// <param name="rows">The rows in the game's order; null entries and repeated ids (after the first) are dropped.</param>
        /// <param name="clipLengths">The game's own voice clips: name to length in seconds; null when unreadable.</param>
        /// <param name="registeredVoices">Voice names registered with the voice feature.</param>
        /// <param name="novelRows">The <c>NovelMaster</c> rows in the game's order; null means none.</param>
        /// <param name="groups">The scenario groups in the game's order (with their <c>ScenarioType</c>); null means none.</param>
        /// <param name="problem">Why the rows are missing, when they are.</param>
        /// <param name="gate">The DLC gate; null = no gate (nothing hidden). An unknown verdict makes the snapshot not loaded.</param>
        public MakerGameData(
            IEnumerable<MakerGameRow?>? rows,
            IReadOnlyDictionary<string, double>? clipLengths,
            IEnumerable<string>? registeredVoices,
            IEnumerable<MakerGameNovelRow?>? novelRows,
            IEnumerable<MakerGameGroup?>? groups,
            string? problem,
            MakerDlcGate? gate)
            : this(rows, clipLengths, registeredVoices, novelRows, groups, problem, gate, null)
        {
        }

        /// <summary>
        /// Like the main constructor, with <paramref name="gateOnlyRows"/>: the game's other localisation rows (no novel row speaks
        /// them, e.g. lines the game picks at playback by key). The DLC gate decides over them too, so routes keyed by such an id
        /// (Play, export checks) are refused for paid content, but they are never listed.
        /// </summary>
        /// <param name="rows">The rows in the game's order; null entries and repeated ids (after the first) are dropped.</param>
        /// <param name="clipLengths">The game's own voice clips: name to length in seconds; null when unreadable.</param>
        /// <param name="registeredVoices">Voice names registered with the voice feature.</param>
        /// <param name="novelRows">The <c>NovelMaster</c> rows in the game's order; null means none.</param>
        /// <param name="groups">The scenario groups in the game's order (with their <c>ScenarioType</c>); null means none.</param>
        /// <param name="problem">Why the rows are missing, when they are.</param>
        /// <param name="gate">The DLC gate; null = no gate (nothing hidden). An unknown verdict makes the snapshot not loaded.</param>
        /// <param name="gateOnlyRows">Rows the gate decides over but the snapshot never lists; null means none.</param>
        public MakerGameData(
            IEnumerable<MakerGameRow?>? rows,
            IReadOnlyDictionary<string, double>? clipLengths,
            IEnumerable<string>? registeredVoices,
            IEnumerable<MakerGameNovelRow?>? novelRows,
            IEnumerable<MakerGameGroup?>? groups,
            string? problem,
            MakerDlcGate? gate,
            IEnumerable<MakerGameRow?>? gateOnlyRows)
        {
            var hidden = MakerDlcHidden.None;
            if (gate != null && string.IsNullOrWhiteSpace(problem))
            {
                var gateRows = NonNull(rows);
                var gateNovel = NonNull(novelRows);
                var gateGroups = NonNull(groups);
                var decideRows = new List<MakerGameRow>(gateRows);
                decideRows.AddRange(NonNull(gateOnlyRows));
                hidden = gate.Decide(decideRows, gateNovel, gateGroups);
                if (!hidden.IsKnown)
                {
                    // Fail closed: a snapshot whose DLC lines cannot be told apart lists nothing.
                    rows = null;
                    novelRows = null;
                    groups = null;
                    problem = "Pack Maker cannot tell paid DLC lines apart yet (" + hidden.Summary + "); try again in a moment.";
                }
                else if (hidden.LineCount > 0 || hidden.GroupCount > 0)
                {
                    rows = gateRows.FindAll(r => !hidden.IsLine(r.Id));
                    novelRows = gateNovel.FindAll(r => !(r.LineId != null && hidden.IsLine(r.LineId)) && !(r.GroupId != null && hidden.IsGroup(r.GroupId)));
                    groups = gateGroups.FindAll(g => !hidden.IsGroup(g.Id));
                }
            }

            Hidden = hidden;
            var list = new List<MakerGameRow>();
            _byId = new Dictionary<string, MakerGameRow>(StringComparer.Ordinal);
            if (rows != null)
            {
                foreach (var row in rows)
                {
                    if (row != null && !_byId.ContainsKey(row.Id))
                    {
                        _byId.Add(row.Id, row);
                        list.Add(row);
                    }
                }
            }

            Rows = list.Count == 0 ? NoRows : (IReadOnlyList<MakerGameRow>)list;

            _clipLengths = new Dictionary<string, double>(StringComparer.Ordinal);
            if (clipLengths != null)
            {
                foreach (var pair in clipLengths)
                {
                    if (!string.IsNullOrEmpty(pair.Key))
                    {
                        _clipLengths[pair.Key] = pair.Value;
                    }
                }
            }

            // An empty cache is treated like an unreadable one: the voice manager fills it wholesale
            // at startup, so "no clips at all" means "not loaded yet", not "every line is silent".
            ClipCacheKnown = _clipLengths.Count > 0;

            _registered = new HashSet<string>(StringComparer.Ordinal);
            if (registeredVoices != null)
            {
                foreach (var name in registeredVoices)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        _registered.Add(name);
                    }
                }
            }

            var novel = new List<MakerGameNovelRow>();
            if (novelRows != null)
            {
                foreach (var row in novelRows)
                {
                    if (row != null)
                    {
                        novel.Add(row);
                        if (!string.IsNullOrWhiteSpace(row.LineId) && !MakerLineRules.IsRuntimeRow(row.LineId))
                            _storyIds.Add(row.LineId!);
                    }
                }
            }

            NovelRows = novel.Count == 0 ? NoNovel : (IReadOnlyList<MakerGameNovelRow>)novel;

            var groupList = new List<MakerGameGroup>();
            var seenGroups = new HashSet<string>(StringComparer.Ordinal);
            if (groups != null)
            {
                foreach (var group in groups)
                {
                    if (group != null && seenGroups.Add(group.Id))
                    {
                        groupList.Add(group);
                    }
                }
            }

            Groups = groupList.Count == 0 ? NoGroups : (IReadOnlyList<MakerGameGroup>)groupList;
            Problem = string.IsNullOrWhiteSpace(problem) ? null : problem;
            Stamp = ComputeStamp();
        }

        /// <summary>A snapshot with nothing in it.</summary>
        public static MakerGameData Empty { get; } = new MakerGameData(null, null, null);

        /// <summary>
        /// What the DLC gate hid from this snapshot (<see cref="MakerDlcHidden.None"/> without a gate). Routes keyed by a voice clip
        /// or reading the game's tables directly check it; export refuses a pack with content for these ids or clips.
        /// </summary>
        public MakerDlcHidden Hidden { get; }

        /// <summary>The rows, in the game's order. Never null.</summary>
        public IReadOnlyList<MakerGameRow> Rows { get; }

        /// <summary>True when the game's voice clip cache was read and held clips.</summary>
        public bool ClipCacheKnown { get; }

        /// <summary>How many game clips the cache held.</summary>
        public int ClipCount => _clipLengths.Count;

        /// <summary>The <c>NovelMaster</c> rows in the game's order. Empty when the adapter did not capture them. Never null.</summary>
        public IReadOnlyList<MakerGameNovelRow> NovelRows { get; }

        /// <summary>The scenario groups in the game's order. Empty when the adapter did not capture them. Never null.</summary>
        public IReadOnlyList<MakerGameGroup> Groups { get; }

        /// <summary>
        /// A short token that changes whenever anything this snapshot holds that Studio v2 lists changes (rows, voices,
        /// groups, novel rows, the clip cache size). Two snapshots with the same content have the same stamp.
        /// </summary>
        public string Stamp { get; }

        /// <summary>Why there are no rows (the game's data is not loaded yet), or null.</summary>
        public string? Problem { get; }

        /// <summary>True when the game's rows were read.</summary>
        public bool IsLoaded => Problem is null;

        /// <summary>The row with this id, case-sensitively.</summary>
        /// <param name="id">A localisation id.</param>
        /// <param name="row">The row, or null.</param>
        public bool TryFind(string? id, out MakerGameRow? row)
        {
            row = null;
            return !string.IsNullOrEmpty(id) && _byId.TryGetValue(id!, out row);
        }

        /// <summary>True only for a localization id referenced by a scenario row, excluding runtime speech.</summary>
        /// <param name="id">The localization id, case-sensitively.</param>
        public bool IsStoryLine(string? id) => id != null && _storyIds.Contains(id);

        /// <summary>True when the game ships a clip by this name (only meaningful when <see cref="ClipCacheKnown"/>).</summary>
        /// <param name="voice">The voice name.</param>
        public bool HasGameClip(string? voice) => !string.IsNullOrEmpty(voice) && _clipLengths.ContainsKey(voice!);

        /// <summary>The length of the game's own clip, when the cache knew it.</summary>
        /// <param name="voice">The voice name.</param>
        /// <param name="seconds">The length in seconds.</param>
        public bool TryGetClipLength(string? voice, out double seconds)
        {
            seconds = 0d;
            return !string.IsNullOrEmpty(voice) && _clipLengths.TryGetValue(voice!, out seconds) && seconds > 0d;
        }

        /// <summary>True when the voice feature had a clip registered under this name.</summary>
        /// <param name="voice">The voice name.</param>
        public bool IsRegistered(string? voice) => !string.IsNullOrEmpty(voice) && _registered.Contains(voice!);

        private string ComputeStamp()
        {
            // FNV-1a over the strings Studio lists; one pass at capture time, never per request.
            var hash = 14695981039346656037UL;
            void Add(string? text)
            {
                if (text != null)
                {
                    foreach (var c in text)
                    {
                        hash = (hash ^ c) * 1099511628211UL;
                    }
                }

                hash = (hash ^ 0x1FUL) * 1099511628211UL;
            }

            foreach (var row in Rows)
            {
                Add(row.Id);
                Add(row.Voice);
                Add(row.Group);
                Add(row.Ja);
                Add(row.En);
            }

            foreach (var novel in NovelRows)
            {
                Add(novel.RowId);
                Add(novel.GroupId);
                Add(novel.LineId);
                Add(novel.Talker);
                Add(novel.BodyMotion);
                Add(novel.FacialMotion);
            }

            foreach (var group in Groups)
            {
                Add(group.Id);
                Add(group.Title);
            }

            var voices = new List<string>(_registered);
            voices.Sort(StringComparer.Ordinal);
            foreach (var voice in voices)
            {
                Add(voice);
            }

            Add(_clipLengths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(Problem);
            if (Hidden.LineCount > 0 || Hidden.GroupCount > 0)
            {
                Add("dlc:" + Hidden.LineCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + Hidden.GroupCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static List<T> NonNull<T>(IEnumerable<T?>? items) where T : class
        {
            var list = new List<T>();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item != null)
                    {
                        list.Add(item);
                    }
                }
            }

            return list;
        }

        /// <inheritdoc />
        public override string ToString() =>
            IsLoaded
                ? Rows.Count + " row(s), " + (ClipCacheKnown ? ClipCount + " game clip(s)" : "game clips not readable")
                  + ", " + _registered.Count + " registered voice(s)"
                  + (Hidden.LineCount > 0 ? ", " + Hidden.LineCount + " DLC line(s) hidden" : string.Empty)
                : "not loaded: " + Problem;
    }
}
