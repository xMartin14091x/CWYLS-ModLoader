using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.MasterData;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>How bad a lint finding is.</summary>
    public enum ScenarioLintSeverity
    {
        /// <summary>Likely a mistake; the scene still plays.</summary>
        Warning = 1,

        /// <summary>The scene stalls, throws, never plays as meant, or leaks into the game's own flow.</summary>
        Error = 2,
    }

    /// <summary>One problem in a pack scene. Immutable.</summary>
    public sealed class ScenarioLintFinding
    {
        internal ScenarioLintFinding(ScenarioLintSeverity severity, string rule, string groupId, string rowId, string message)
        {
            Severity = severity;
            Rule = rule;
            GroupId = groupId;
            RowId = rowId;
            Message = message;
        }

        /// <summary>How bad.</summary>
        public ScenarioLintSeverity Severity { get; }

        /// <summary>The rule, e.g. <c>S05</c> (the table in <see cref="ScenarioLint"/>).</summary>
        public string Rule { get; }

        /// <summary>The scene.</summary>
        public string GroupId { get; }

        /// <summary>The row, or empty for a finding about the whole scene.</summary>
        public string RowId { get; }

        /// <summary>What is wrong and what to do.</summary>
        public string Message { get; }

        /// <summary><c>error S05 cwy_demo_01: …</c> or <c>warning S16 cwy_demo_01/row_003: …</c>.</summary>
        public override string ToString() =>
            (Severity == ScenarioLintSeverity.Error ? "error " : "warning ") + Rule + " " + GroupId +
            (RowId.Length == 0 ? string.Empty : "/" + RowId) + ": " + Message;
    }

    /// <summary>A scene group as the game keys it: id, scene type number and episode number. Immutable.</summary>
    /// <param name="Id">The group id.</param>
    /// <param name="Scenario">The <c>ScenarioType</c> number.</param>
    /// <param name="EpisodeNumber">The episode number.</param>
    public sealed record ScenarioGroupKey(string Id, int Scenario, float EpisodeNumber);

    /// <summary>What the lint knows about the game besides the packs. Immutable once built.</summary>
    public sealed class ScenarioLintContext
    {
        /// <summary>The game's own ids (localization, novel, group); <see cref="MasterDataBaseline.Empty"/> offline.</summary>
        public MasterDataBaseline Baseline { get; init; } = MasterDataBaseline.Empty;

        /// <summary>
        /// True when <see cref="Baseline"/> and <see cref="GameGroups"/> are the running game's data. False
        /// (offline): a reference the packs do not resolve is not reported, because the game may have it.
        /// </summary>
        public bool GameDataKnown { get; init; }

        /// <summary>The game's own groups with their keys (for the duplicate-episode rule).</summary>
        public IReadOnlyList<ScenarioGroupKey> GameGroups { get; init; } = Array.Empty<ScenarioGroupKey>();

        /// <summary>Where script commands are looked up (names, handlers, whether the seam is in).</summary>
        public ScenarioCommandRegistry Registry { get; init; } = ScenarioCommandRegistry.Shared;

        /// <summary>True when an action name is registered; null skips the unknown-action rule.</summary>
        public Func<string, bool>? IsKnownAction { get; init; }
    }

    /// <summary>The lint's answer. Immutable.</summary>
    public sealed class ScenarioLintReport
    {
        internal ScenarioLintReport(IReadOnlyList<ScenarioLintFinding> findings, IReadOnlyList<string> packScenes, int packLines)
        {
            Findings = findings;
            PackScenes = packScenes;
            PackLineCount = packLines;
            foreach (var finding in findings)
            {
                if (finding.Severity == ScenarioLintSeverity.Error)
                {
                    ErrorCount++;
                }
                else
                {
                    WarningCount++;
                }
            }
        }

        /// <summary>A report with nothing in it.</summary>
        public static ScenarioLintReport Empty { get; } =
            new ScenarioLintReport(Array.Empty<ScenarioLintFinding>(), Array.Empty<string>(), 0);

        /// <summary>Every finding, scene by scene in pack order, rows in reading order.</summary>
        public IReadOnlyList<ScenarioLintFinding> Findings { get; }

        /// <summary>The groups packs added (pack scenes and pack lines), in pack order.</summary>
        public IReadOnlyList<string> PackScenes { get; }

        /// <summary>How many of <see cref="PackScenes"/> are pack lines (a <c>Languages</c> member).</summary>
        public int PackLineCount { get; }

        /// <summary>Errors.</summary>
        public int ErrorCount { get; }

        /// <summary>Warnings.</summary>
        public int WarningCount { get; }

        /// <summary>The findings of one scene.</summary>
        /// <param name="groupId">The group id.</param>
        public IReadOnlyList<ScenarioLintFinding> For(string? groupId)
        {
            var list = new List<ScenarioLintFinding>();
            foreach (var finding in Findings)
            {
                if (string.Equals(finding.GroupId, groupId, StringComparison.Ordinal))
                {
                    list.Add(finding);
                }
            }

            return list;
        }

        /// <summary>The first error of one scene, or null.</summary>
        /// <param name="groupId">The group id.</param>
        public ScenarioLintFinding? FirstErrorOf(string? groupId)
        {
            foreach (var finding in Findings)
            {
                if (finding.Severity == ScenarioLintSeverity.Error && string.Equals(finding.GroupId, groupId, StringComparison.Ordinal))
                {
                    return finding;
                }
            }

            return null;
        }

        /// <summary><c>3 pack scene(s) (1 pack line), 0 error(s), 2 warning(s)</c>.</summary>
        public string Summarize() =>
            PackScenes.Count + " pack scene(s) (" + PackLineCount + " pack line(s)), " + ErrorCount + " error(s), " + WarningCount + " warning(s)";

        /// <summary>The summary and every finding, one per line.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder(Summarize());
            foreach (var finding in Findings)
            {
                sb.Append('\n').Append(finding);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Checks pack scenes for the pitfalls the game's reader has and for
    /// script-command rows that would not run. Advisory: nothing is blocked
    /// except <c>scene play</c> of a scene with errors. Pure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <b>pack scene</b> is a group a pack added (its id is not the game's). Groups with a
    /// <c>Languages</c> member are pack lines: <c>PackLineScreen</c> screens them,
    /// so only the script-command rules (S20-S24) run on them here. Rows a pack
    /// adds to or overrides in the game's own groups get the script-command rules too.
    /// </para>
    /// <list type="table">
    /// <listheader><term>Rule</term><description>Finding (game behaviour, game 1.17.3)</description></listheader>
    /// <item><term>S01 error</term><description>No rows: <c>ScenarioReader.StartReady</c> reads row 0 unconditionally.</description></item>
    /// <item><term>S02 error</term><description>Long story without a Text/ChangeMotion/AddTextLoop/DirectionGameStartCamera row: the pre-scan runs off the end.</description></item>
    /// <item><term>S03 error</term><description>(Scenario, EpisodeNumber) not unique: every trigger looks groups up by that pair (<c>FacilityStory</c>).</description></item>
    /// <item><term>S04 warning</term><description>Episode below 1000: the mod range starts at 1000 so a game update cannot collide.</description></item>
    /// <item><term>S05 error</term><description>Extra/AprilFool/Birthday scene the game would offer by itself: ContentType must be a blocking name (<c>ScenarioGroupMasterWrapper</c>); the save guard keeps pack scenes unseen, so <c>None</c> would be offered again and again (<c>HeroineAI</c>).</description></item>
    /// <item><term>S06 warning</term><description>Long pack scene of a type other than ExtraScenario (story progress and collaboration code read those types).</description></item>
    /// <item><term>S10 error / S11 warning</term><description>WaitInput, Tutorial stall (the reader has no case for them); ReadNextForScript waits for code, tutorial commands drive the tutorial UI.</description></item>
    /// <item><term>S12 error</term><description>A Selection/Jump/TutorialSelection/IfBetweenDateTime/StartTextLoop target is not a row of the group.</description></item>
    /// <item><term>S13 error</term><description>A LocalizationID that does not exist, or none on VoiceText/Selection (the reader's lookup is null and throws).</description></item>
    /// <item><term>S14 error</term><description>An argument the game parses with float/int.Parse, Enum.Parse or ParseExact would throw.</description></item>
    /// <item><term>S15 warning</term><description>The row before a branch start falls through into the branch (end it with Jump or EndScenario).</description></item>
    /// <item><term>S16 warning</term><description>A short scene plays story music or ambience and never stops it (the short EndStory path does not).</description></item>
    /// <item><term>S20 error</term><description>A script-command row while the seam is not installed: the reader would stall.</description></item>
    /// <item><term>S21 warning</term><description>A script command no one handles (yet): the row is skipped with a warning.</description></item>
    /// <item><term>S22 error</term><description>A built-in's Arg1 its handler would refuse (<see cref="ScenarioBuiltInArgs"/>).</description></item>
    /// <item><term>S23 warning</term><description><c>cwy.do</c>/<c>cwy.defer</c> names an action that is not registered.</description></item>
    /// <item><term>S24 warning</term><description>In a long story, a script-command row before the first line never runs (the pre-scan skips to it).</description></item>
    /// <item><term>S25 warning</term><description>A pack row of a group nobody has: it never plays.</description></item>
    /// </list>
    /// </remarks>
    public static class ScenarioLint
    {
        /// <summary>The first episode number of the mod range.</summary>
        public const float ModEpisodeFloor = 1000f;

        /// <summary>The ContentType names that keep the game from offering an Extra/AprilFool/Birthday scene (any value but None and PurchaseEnvironment).</summary>
        public static readonly IReadOnlyList<string> BlockingContentTypes = new[] { "SmallTalk_CatHeadphone", "SmallTalk_Cicada", "SmallTalk_PassageOfTime" };

        /// <summary><c>Bulbul.AmbientSoundType</c>, for <c>PlayAmbientBGM</c>'s <c>Enum.Parse</c>.</summary>
        public static EnumTable AmbientSound { get; } = new EnumTable(
            "Bulbul.AmbientSoundType",
            ("RadioNoise", 0), ("PinkNoise", 1), ("RecordNoise", 2), ("DeepSea", 3), ("Wind", 4), ("Fireworks_First", 5),
            ("Fireworks_Second", 6), ("WindBell", 7), ("LightRain", 8), ("HeavyRain", 9), ("ThunderRain", 10), ("CookSimmer", 11),
            ("Crickets", 12), ("Frog1", 13), ("Frog2", 14), ("Chicada", 15), ("Higurashi", 16), ("TurtleDove", 17),
            ("BirdChorus", 18), ("Whale", 19), ("HotSpring", 20), ("Locomotive", 21), ("Sea", 22), ("RoomNoise", 23),
            ("CookTypeB", 24), ("City", 25), ("Jellyfish", 26), ("Fireplace", 27), ("Brook", 28), ("Horizon", 29));

        // CommandType numbers (Bulbul.MasterData.CommandType).
        private const int Text = 0, WaitForSeconds = 1, WaitInput = 2, Selection = 3, Jump = 4, PlayBgm = 5, PlaySe = 6,
            PlayAmbient = 7, StopBgm = 8, StopAmbient = 10, EndScenario = 12, Tutorial = 14, TutorialSelection = 15,
            TutorialOpenExplanation = 19, VoiceText = 20, ChangeMotion = 21, PomodoroVoiceText = 22, TalkLogOnlyPlayBgm = 25,
            ChangeVolumeBgm = 40, ReadNextForScript = 50, IfBetweenDateTime = 70, AddTextLoop = 80, StartTextLoop = 81,
            BlackFadeOut = 90, SetDirectionDelay = 100, Glitch = 200, ScreenMove = 205, BlackOut = 210, Monochrome = 215,
            ChromaticAberration = 216, AdjustAllUnstable = 219, DirectionGameStartCamera = 250, ShineSatone = 500, ShineKouchan = 501;

        // ScenarioType numbers (Bulbul.MasterData.ScenarioType) the rules name.
        private const int ExtraScenario = 24, AprilFool = 102, Birthday = 103;

        /// <summary>True for the types <c>Bulbul.MasterData.ScenarioTypeExtensions.IsLongStory</c> accepts.</summary>
        /// <param name="scenario">A <c>ScenarioType</c> number.</param>
        public static bool IsLongStory(int scenario) =>
            scenario == 1 || scenario == 2 || scenario == 3 || scenario == ExtraScenario || (scenario >= 50 && scenario <= 56);

        /// <summary>Lints the merged pack data.</summary>
        /// <param name="merged">The packs' rows (<c>MasterDataMerge.Merge</c>).</param>
        /// <param name="context">What is known about the game; null = offline defaults.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="merged"/> is null.</exception>
        public static ScenarioLintReport Lint(MasterDataSet merged, ScenarioLintContext? context = null)
        {
            if (merged is null)
            {
                throw new ArgumentNullException(nameof(merged));
            }

            var ctx = context ?? new ScenarioLintContext();
            var run = new Run(merged, ctx);
            run.Go();
            return new ScenarioLintReport(run.Findings, run.PackScenes, run.PackLines);
        }

        private sealed class Run
        {
            private readonly MasterDataSet _merged;
            private readonly ScenarioLintContext _ctx;
            private readonly HashSet<string> _packLocalization = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, List<NovelRow>> _rowsByGroup = new Dictionary<string, List<NovelRow>>(StringComparer.Ordinal);
            private readonly HashSet<string> _packGroupIds = new HashSet<string>(StringComparer.Ordinal);

            internal Run(MasterDataSet merged, ScenarioLintContext ctx)
            {
                _merged = merged;
                _ctx = ctx;
            }

            internal List<ScenarioLintFinding> Findings { get; } = new List<ScenarioLintFinding>();

            internal List<string> PackScenes { get; } = new List<string>();

            internal int PackLines { get; private set; }

            internal void Go()
            {
                foreach (var row in _merged.Localization)
                {
                    _packLocalization.Add(row.Id);
                }

                foreach (var row in _merged.Novel)
                {
                    if (!_rowsByGroup.TryGetValue(row.ScenarioGroupId, out var list))
                    {
                        list = new List<NovelRow>();
                        _rowsByGroup.Add(row.ScenarioGroupId, list);
                    }

                    list.Add(row);
                }

                var keys = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var game in _ctx.GameGroups)
                {
                    keys[Key(game.Scenario, game.EpisodeNumber)] = game.Id;
                }

                foreach (var group in _merged.ScenarioGroups)
                {
                    _packGroupIds.Add(group.Id);
                    if (_ctx.Baseline.HasScenarioGroup(group.Id))
                    {
                        continue;
                    }

                    PackScenes.Add(group.Id);
                    var rows = _rowsByGroup.TryGetValue(group.Id, out var found) ? found : new List<NovelRow>();
                    var isLine = group.Languages != null;
                    if (isLine)
                    {
                        PackLines++;
                    }
                    else
                    {
                        GroupRules(group, rows, keys);
                    }

                    ScriptRules(group.Id, group.Scenario, rows, longStoryRules: !isLine);
                }

                // Rows packs add to (or override in) the game's own groups, and rows of groups nobody has.
                foreach (var pair in _rowsByGroup)
                {
                    if (_packGroupIds.Contains(pair.Key) && !_ctx.Baseline.HasScenarioGroup(pair.Key))
                    {
                        continue;
                    }

                    if (!_packGroupIds.Contains(pair.Key) && _ctx.GameDataKnown && !_ctx.Baseline.HasScenarioGroup(pair.Key))
                    {
                        Add(ScenarioLintSeverity.Warning, "S25", pair.Key, pair.Value[0].Id,
                            "no pack and not the game has the group " + pair.Key + ", so its " + pair.Value.Count + " row(s) never play");
                    }

                    ScriptRules(pair.Key, -1, pair.Value, longStoryRules: false);
                }
            }

            private void GroupRules(ScenarioGroupRow group, List<NovelRow> rows, Dictionary<string, string> keys)
            {
                var id = group.Id;
                var key = Key(group.Scenario, group.EpisodeNumber);
                if (keys.TryGetValue(key, out var other))
                {
                    Add(ScenarioLintSeverity.Error, "S03", id, string.Empty,
                        TypeName(group.Scenario) + " episode " + Episode(group.EpisodeNumber) + " is also " + other
                        + "; the game finds a scene by type and episode, so one of them never plays (or room setup fails for small talk)");
                }
                else
                {
                    keys.Add(key, id);
                }

                if (group.EpisodeNumber < ModEpisodeFloor)
                {
                    Add(ScenarioLintSeverity.Warning, "S04", id, string.Empty,
                        "episode " + Episode(group.EpisodeNumber) + " is below " + Episode(ModEpisodeFloor)
                        + "; mod scenes use 1000 and up so a game update cannot take the same number");
                }

                if (group.Scenario == ExtraScenario || group.Scenario == AprilFool || group.Scenario == Birthday)
                {
                    var blocking = false;
                    foreach (var name in BlockingContentTypes)
                    {
                        blocking |= string.Equals(group.ContentType, name, StringComparison.Ordinal);
                    }

                    if (!blocking)
                    {
                        Add(ScenarioLintSeverity.Error, "S05", id, string.Empty,
                            "ContentType \"" + group.ContentType + "\" lets the game offer this " + TypeName(group.Scenario)
                            + " scene on its own, and pack scenes are never logged as seen, so it would be offered again and again; "
                            + "use \"SmallTalk_Cicada\" (any of " + string.Join(", ", BlockingContentTypes) + ") and start it with scene play or a trigger");
                    }
                }

                var isLong = IsLongStory(group.Scenario);
                if (isLong && group.Scenario != ExtraScenario)
                {
                    Add(ScenarioLintSeverity.Warning, "S06", id, string.Empty,
                        "a long pack scene of type " + TypeName(group.Scenario)
                        + "; the game's story-progress and collaboration code reads that type, ExtraScenario is the neutral one");
                }

                if (rows.Count == 0)
                {
                    Add(ScenarioLintSeverity.Error, "S01", id, string.Empty,
                        "the scene has no rows in novel.json; the game reads row 0 when it starts and throws");
                    return;
                }

                if (isLong && FirstStartRow(rows, motionOnly: true) < 0)
                {
                    Add(ScenarioLintSeverity.Error, "S02", id, string.Empty,
                        "a long story needs a Text, ChangeMotion, AddTextLoop or DirectionGameStartCamera row; without one the game's start scan runs off the end");
                }

                var rowIds = new HashSet<string>(StringComparer.Ordinal);
                var jumpTargets = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    rowIds.Add(row.Id);
                    if (row.Command == Jump)
                    {
                        jumpTargets.Add(row.Arg1);
                    }
                }

                var playsBgm = false;
                var stopsBgm = false;
                var playsAmbient = false;
                var stopsAmbient = false;
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    RowRules(id, row, rowIds);
                    playsBgm |= row.Command == PlayBgm;
                    stopsBgm |= row.Command == StopBgm;
                    playsAmbient |= row.Command == PlayAmbient;
                    stopsAmbient |= row.Command == StopAmbient;
                }

                FallThroughRule(id, rows, jumpTargets);

                if (!isLong && ((playsBgm && !stopsBgm) || (playsAmbient && !stopsAmbient)))
                {
                    Add(ScenarioLintSeverity.Warning, "S16", id, string.Empty,
                        "a short scene starts " + (playsBgm && !stopsBgm ? "story music (PlayBGM)" : "story ambience (PlayAmbientBGM)")
                        + " and never stops it; the short end path does not, so stop it before EndScenario");
                }
            }

            private void RowRules(string groupId, NovelRow row, HashSet<string> rowIds)
            {
                var command = row.Command;
                switch (command)
                {
                    case WaitInput:
                    case Tutorial:
                        Add(ScenarioLintSeverity.Error, "S10", groupId, row.Id,
                            CommandName(command) + " stalls the scene: the game never continues after it");
                        break;
                    case ReadNextForScript:
                        Add(ScenarioLintSeverity.Warning, "S11", groupId, row.Id,
                            "ReadNextForScript waits until code calls ScenarioReader.ReadNextForScript and hides the text box; use cwy.wait or a mod's command");
                        break;
                    default:
                        if (command >= TutorialSelection && command <= TutorialOpenExplanation)
                        {
                            Add(ScenarioLintSeverity.Warning, "S11", groupId, row.Id,
                                CommandName(command) + " drives the tutorial's UI; outside the tutorial it may hide the text box or wait for a page");
                        }

                        break;
                }

                foreach (var target in JumpTargetsOf(row))
                {
                    if (!rowIds.Contains(target))
                    {
                        Add(ScenarioLintSeverity.Error, "S12", groupId, row.Id,
                            CommandName(command) + " goes to \"" + target + "\", which is not a row of this scene; the game logs an error and the scene stops");
                    }
                }

                LocalizationRule(groupId, row);
                ArgumentRules(groupId, row);
            }

            private void LocalizationRule(string groupId, NovelRow row)
            {
                var id = row.LocalizationId ?? string.Empty;
                var none = id.Length == 0 || id == "0";
                if (none)
                {
                    if (row.Command == VoiceText || row.Command == Selection || row.Command == TutorialSelection)
                    {
                        Add(ScenarioLintSeverity.Error, "S13", groupId, row.Id,
                            CommandName(row.Command) + " needs a LocalizationID (its text); without one the reader's lookup is null and throws");
                    }

                    return;
                }

                if (_packLocalization.Contains(id))
                {
                    return;
                }

                if (_ctx.GameDataKnown && !_ctx.Baseline.HasLocalization(id))
                {
                    Add(ScenarioLintSeverity.Error, "S13", groupId, row.Id,
                        "LocalizationID \"" + id + "\" is in no pack's localization.json and not in the game; the reader throws on it");
                }
            }

            private void ArgumentRules(string groupId, NovelRow row)
            {
                switch (row.Command)
                {
                    case Text:
                        FloatOrEmpty(groupId, row, 1, row.Arg1, "the delay before the line (ScenarioReader)");
                        if (!(row.LocalizationId.Length == 0 || row.LocalizationId == "0"))
                        {
                            Int(groupId, row, 3, row.Arg3, "lip-sync 0 or 1 (:1250)");
                        }

                        break;
                    case PlayBgm:
                    case TalkLogOnlyPlayBgm:
                        Float(groupId, row, 3, row.Arg3, "the start offset (:1546)");
                        Float(groupId, row, 4, row.Arg4, "the fade-in (:1547)");
                        break;
                    case PlaySe:
                        Float(groupId, row, 3, row.Arg3, "the delay (:1158)");
                        Float(groupId, row, 4, row.Arg4, "the stop time (:1159)");
                        break;
                    case PlayAmbient:
                        if (!IsExactAmbient(row.Arg1))
                        {
                            Add(ScenarioLintSeverity.Error, "S14", groupId, row.Id,
                                "PlayAmbientBGM Arg1 \"" + row.Arg1 + "\" is not an ambient sound name (exact case; Enum.Parse throws, :1634): "
                                + AmbientSound.Describe());
                        }

                        break;
                    case VoiceText:
                        Int(groupId, row, 1, row.Arg1, "lip-sync 1 or 0 (:1277)");
                        break;
                    case ChangeMotion:
                        Float(groupId, row, 1, row.Arg1, "the delay (:1022)");
                        break;
                    case PomodoroVoiceText:
                        Int(groupId, row, 3, row.Arg3, "lip-sync, -1 = none (:1317)");
                        if (row.Arg2 != -1f)
                        {
                            Int(groupId, row, 1, row.Arg1, "the voice timing type (:684)");
                        }

                        break;
                    case ChangeVolumeBgm:
                        Float(groupId, row, 3, row.Arg3, "the fade (:1792)");
                        break;
                    case IfBetweenDateTime:
                        DateWindow(groupId, row);
                        break;
                    case AddTextLoop:
                        FloatOrEmpty(groupId, row, 1, row.Arg1, "the delay (:1340)");
                        break;
                    case StartTextLoop:
                        Float(groupId, row, 1, row.Arg1, "the shortest gap (ScenarioCommandTextLoop)");
                        Float(groupId, row, 4, row.Arg4, "the time before the choices (:1206)");
                        break;
                    case BlackFadeOut:
                        Float(groupId, row, 1, row.Arg1, "the fade time (:919)");
                        break;
                    case SetDirectionDelay:
                        Float(groupId, row, 1, row.Arg1, "the delay (:952)");
                        break;
                    case Glitch:
                    case ScreenMove:
                    case Monochrome:
                    case ChromaticAberration:
                    case AdjustAllUnstable:
                        Float(groupId, row, 1, row.Arg1, "the ratio (:1053-1176)");
                        break;
                    case BlackOut:
                        Float(groupId, row, 1, row.Arg1, "the ratio (:1066)");
                        if (!string.IsNullOrEmpty(row.Arg3))
                        {
                            Int(groupId, row, 3, row.Arg3, "use the blind, 1 or 0 (:1071)");
                        }

                        break;
                    case ShineSatone:
                    case ShineKouchan:
                        Float(groupId, row, 1, row.Arg1, "the wait (:1000)");
                        Float(groupId, row, 3, row.Arg3, "the hold (:1002)");
                        Float(groupId, row, 4, row.Arg4, "the fade-out (:1003)");
                        break;
                }
            }

            private void ScriptRules(string groupId, int scenario, List<NovelRow> rows, bool longStoryRules)
            {
                var startIndex = longStoryRules && IsLongStory(scenario) ? FirstStartRow(rows, motionOnly: false) : -1;
                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (!ScenarioCommandIds.IsInRange(row.Command))
                    {
                        continue;
                    }

                    var name = CommandName(row.Command);
                    if (!_ctx.Registry.RuntimeAvailable)
                    {
                        Add(ScenarioLintSeverity.Error, "S20", groupId, row.Id,
                            name + " would stall the scene: " + _ctx.Registry.RuntimeDetail);
                    }

                    if (!_ctx.Registry.TryGetBinding(row.Command, out _))
                    {
                        Add(ScenarioLintSeverity.Warning, "S21", groupId, row.Id,
                            "no one handles " + name + " (yet); the row is skipped with a warning when the scene reaches it");
                    }

                    var problem = ScenarioBuiltInArgs.Check(row.Command, row.Arg1, out var action);
                    if (problem.Length > 0)
                    {
                        Add(ScenarioLintSeverity.Error, "S22", groupId, row.Id, problem);
                    }
                    else if (action.Length > 0 && _ctx.IsKnownAction != null && !_ctx.IsKnownAction(action))
                    {
                        Add(ScenarioLintSeverity.Warning, "S23", groupId, row.Id,
                            name + " runs \"" + action + "\", which no one has registered (yet); it ends as NotFound");
                    }

                    if (startIndex >= 0 && i < startIndex)
                    {
                        Add(ScenarioLintSeverity.Warning, "S24", groupId, row.Id,
                            name + " comes before the scene's first line; a long story starts at that line, so this row never runs");
                    }
                }
            }

            private void FallThroughRule(string groupId, List<NovelRow> rows, HashSet<string> jumpTargets)
            {
                var branchStarts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (row.Command == Selection || row.Command == TutorialSelection)
                    {
                        branchStarts.Add(row.Arg1);
                    }
                    else if (row.Command == IfBetweenDateTime)
                    {
                        branchStarts.Add(row.Arg3);
                        branchStarts.Add(row.Arg4);
                    }
                }

                for (var i = 1; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (!branchStarts.Contains(row.Id) || jumpTargets.Contains(row.Id))
                    {
                        continue;
                    }

                    var previous = rows[i - 1].Command;
                    var stops = previous == Selection || previous == Jump || previous == EndScenario || previous == TutorialSelection
                                || previous == IfBetweenDateTime || previous == StartTextLoop || ScenarioCommandIds.IsInRange(previous);
                    if (!stops)
                    {
                        Add(ScenarioLintSeverity.Warning, "S15", groupId, row.Id,
                            "the row before this branch (" + rows[i - 1].Id + ") falls into it; end the previous branch with Jump or EndScenario");
                    }
                }
            }

            private static IEnumerable<string> JumpTargetsOf(NovelRow row)
            {
                switch (row.Command)
                {
                    case Selection:
                    case Jump:
                    case TutorialSelection:
                        yield return row.Arg1;
                        break;
                    case IfBetweenDateTime:
                        yield return row.Arg3;
                        yield return row.Arg4;
                        break;
                    case StartTextLoop:
                        yield return row.Arg3;
                        break;
                }
            }

            private void DateWindow(string groupId, NovelRow row)
            {
                var parts = (row.Arg1 ?? string.Empty).Split('~');
                var ok = parts.Length == 2;
                for (var i = 0; ok && i < 2; i++)
                {
                    ok = DateTime.TryParseExact(parts[i], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                }

                if (!ok)
                {
                    Add(ScenarioLintSeverity.Error, "S14", groupId, row.Id,
                        "IfBetweenDateTime Arg1 \"" + row.Arg1 + "\" is not yyyyMMddHHmmss~yyyyMMddHHmmss (ParseExact throws, :852)");
                }
            }

            private void Float(string groupId, NovelRow row, int slot, string? text, string what)
            {
                if (!IsFloat(text))
                {
                    Add(ScenarioLintSeverity.Error, "S14", groupId, row.Id,
                        CommandName(row.Command) + " Arg" + slot + " \"" + text + "\" is not a number (" + what + "; float.Parse throws)");
                }
            }

            private void FloatOrEmpty(string groupId, NovelRow row, int slot, string? text, string what)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    Float(groupId, row, slot, text, what);
                }
            }

            private void Int(string groupId, NovelRow row, int slot, string? text, string what)
            {
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                {
                    Add(ScenarioLintSeverity.Error, "S14", groupId, row.Id,
                        CommandName(row.Command) + " Arg" + slot + " \"" + text + "\" is not a whole number (" + what + "; int.Parse throws)");
                }
            }

            /// <summary>Enum.Parse without ignoreCase: the exact member name, or a declared number.</summary>
            private static bool IsExactAmbient(string? text)
            {
                var token = text?.Trim() ?? string.Empty;
                if (!AmbientSound.TryParseText(token, out var value))
                {
                    return false;
                }

                return int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
                       || (AmbientSound.TryName(value, out var name) && string.Equals(name, token, StringComparison.Ordinal));
            }

            private static bool IsFloat(string? text) =>
                text != null && text.IndexOf(',') < 0
                && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

            /// <summary>
            /// The row a long story starts at (as <c>ScenarioReader</c> picks it): the first Text, ChangeMotion,
            /// AddTextLoop or DirectionGameStartCamera row, or - for the start index only - WaitForSeconds.
            /// </summary>
            private static int FirstStartRow(List<NovelRow> rows, bool motionOnly)
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    var c = rows[i].Command;
                    if (c == Text || c == ChangeMotion || c == AddTextLoop || c == DirectionGameStartCamera || (!motionOnly && c == WaitForSeconds))
                    {
                        return i;
                    }
                }

                return -1;
            }

            private void Add(ScenarioLintSeverity severity, string rule, string groupId, string rowId, string message) =>
                Findings.Add(new ScenarioLintFinding(severity, rule, groupId ?? string.Empty, rowId ?? string.Empty, message));

            private string CommandName(int command)
            {
                if (MasterDataEnums.Command.TryName(command, out var name) && name != null)
                {
                    return name;
                }

                return _ctx.Registry.TryGetName(command, out var script) && script != null
                    ? script
                    : command.ToString(CultureInfo.InvariantCulture);
            }

            private static string TypeName(int scenario) =>
                MasterDataEnums.Scenario.TryName(scenario, out var name) && name != null ? name : scenario.ToString(CultureInfo.InvariantCulture);

            private static string Episode(float episode) => episode.ToString("0.###", CultureInfo.InvariantCulture);

            private static string Key(int scenario, float episode) =>
                scenario.ToString(CultureInfo.InvariantCulture) + "|" + episode.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
