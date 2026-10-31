using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// What <see cref="PackLineScreen.Screen"/> decided: the rows to inject, the lines that may join
    /// the game's pools, the groups it refused and the warnings that are not refusals.
    /// </summary>
    public sealed class PackLineScreenResult
    {
        private static readonly PackLine[] NoLines = new PackLine[0];
        private static readonly PackLineRejection[] NoRejections = new PackLineRejection[0];
        private static readonly string[] NoNotes = new string[0];

        /// <summary>Creates a result.</summary>
        /// <param name="set">The rows to inject. Required.</param>
        /// <param name="lines">Accepted lines, in merged order; null means none.</param>
        /// <param name="rejected">Refused groups, in merged order; null means none.</param>
        /// <param name="heldBack">Language-exclusive groups left out because pack lines are not active.</param>
        /// <param name="admitted">Whether pack lines were active when this was decided.</param>
        /// <param name="notes">Warnings that are not refusals, in merged order; null means none.</param>
        /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
        public PackLineScreenResult(
            MasterDataSet set,
            IReadOnlyList<PackLine>? lines,
            IReadOnlyList<PackLineRejection>? rejected,
            int heldBack,
            bool admitted,
            IReadOnlyList<string>? notes = null)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            Lines = lines ?? NoLines;
            Rejected = rejected ?? NoRejections;
            HeldBack = heldBack < 0 ? 0 : heldBack;
            Admitted = admitted;
            Notes = notes ?? NoNotes;
        }

        /// <summary>Nothing merged, nothing screened.</summary>
        public static PackLineScreenResult Empty { get; } =
            new PackLineScreenResult(MasterDataSet.Empty, null, null, 0, false);

        /// <summary>
        /// The rows to inject: the merged set itself when no group declares languages, otherwise the
        /// merged set without the language-exclusive groups that were refused or held back, and with
        /// each accepted spread group (<c>"Pool": "gamestart.any"</c> or <c>"*"</c>) replaced by its
        /// copies: their groups take the source group's place, their novel rows go at the end, and the
        /// source's own novel rows are left out. The localization rows are always the merged ones.
        /// </summary>
        public MasterDataSet Set { get; }

        /// <summary>
        /// The language-exclusive lines that may join the game's pools, one per copy for a spread group;
        /// empty unless <see cref="Admitted"/>.
        /// </summary>
        public IReadOnlyList<PackLine> Lines { get; }

        /// <summary>Language-exclusive groups refused as malformed; each is one WARN line.</summary>
        public IReadOnlyList<PackLineRejection> Rejected { get; }

        /// <summary>
        /// Warnings about accepted lines that are not refusals (a global line at <c>Weight</c> 1, a
        /// global line that skips the timer pools, ...); each is one WARN line. Never null.
        /// </summary>
        public IReadOnlyList<string> Notes { get; }

        /// <summary>Language-exclusive groups left out of the injection because pack lines are not active.</summary>
        public int HeldBack { get; }

        /// <summary>True when pack lines were active, so the exclusive groups were screened rather than held back.</summary>
        public bool Admitted { get; }

        /// <summary><c>lines 3, rejected 0, held back 0</c>.</summary>
        public override string ToString() =>
            "lines " + Lines.Count + ", rejected " + Rejected.Count + ", held back " + HeldBack;
    }

    /// <summary>
    /// Decides which language-exclusive scenario groups (a <c>Languages</c> member in
    /// <c>data/scenario_groups.json</c>) reach the game. Runs on the merged pack rows just
    /// before injection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safe by default.</b> While pack lines are not active (the feature is off, not initialised, or
    /// a seam is missing) every exclusive group is held back: the game's hard-coded pools would never
    /// draw it anyway, but the small-talk pool is built from the master data
    /// (<c>SmallTalkSelector.Setup</c>) and would offer it in every language.
    /// </para>
    /// <para>
    /// <b>Fail soft.</b> While active, a malformed exclusive group is refused with one
    /// <see cref="PackLineRejection"/> and left out of the injection - a pack mistake must not reach
    /// code that would throw: <c>ToDictionary(EpisodeNumber)</c> on a duplicate small talk
    /// (<c>SmallTalkSelector.Setup</c>), <c>FirstOrDefault(...).LocalizationID</c> on a timer group
    /// without rows (<c>NormalWorkStartVoiceSelector</c> and the other timer selectors),
    /// <c>int.Parse</c>/<c>float.Parse</c> on a script argument (<c>ScenarioReader</c>) or a
    /// small-talk column (<c>SmallTalkSelector</c>).
    /// </para>
    /// <para>
    /// <b>Spreads.</b> A group whose <c>Pool</c> is a spread (<see cref="PackLinePools.TryGetSpread"/>:
    /// <c>gamestart.any</c>, or <c>*</c> for a global line) is written once and copied into every
    /// pool the spread stands for; its own <c>Scenario</c> is ignored (write <c>"None"</c>). Each copy
    /// is a group <c>&lt;id&gt;@&lt;pool&gt;</c> of that pool's scenario, with the same episode
    /// number, languages and weight, and its own novel rows <c>&lt;row&gt;@&lt;pool&gt;</c> (jump
    /// targets follow); the localization rows are shared. Where several target pools share a
    /// scenario (the four clock slots, the three worked-time slots), the first slot's copy keeps the
    /// written episode and each later slot's copy gets its own: the lowest episode above it (wrapping
    /// to 1000 at the top) that no group of the input writes for that scenario and no line or copy
    /// screened before it claims, so the game's <c>(Scenario, EpisodeNumber)</c> lookup and
    /// <see cref="PackLineRegistry.TryGet"/> find each slot's own copy. Each copy is screened like a group
    /// written for its pool, and if one fails the whole group is refused with one rejection naming
    /// that pool. A global line fits each pool's shape: in a timer pool its one spoken row becomes a
    /// <c>PomodoroVoiceText</c> row that speaks at once (several spoken rows: the timer pools are
    /// skipped, with one note); in small talk it gets the lottery columns <c>SmallTalkSelector</c>
    /// needs, unless it already carries valid ones.
    /// </para>
    /// <para>
    /// A set with no exclusive group is returned unchanged (the same instance), and so is its novel
    /// list while no spread group is accepted, so packs that do not use the members load exactly as
    /// before.
    /// </para>
    /// </remarks>
    public static class PackLineScreen
    {
        /// <summary>What a spread copy's group id and its rows' ids put between the written id and the pool id.</summary>
        public const string CopySeparator = "@";

        private const int CommandText = 0;
        private const int CommandWaitInput = 2;
        private const int CommandSelection = 3;
        private const int CommandJump = 4;
        private const int CommandVoiceText = 20;
        private const int CommandPomodoroVoiceText = 22;
        private const int CommandTutorialFirst = 14;
        private const int CommandTutorialLast = 19;
        private const int CommandReadNextForScript = 50;
        private const int CommandIfBetweenDateTime = 70;
        private const int CommandStartTextLoop = 81;

        private static readonly string[] SmallTalkTimings = { "Break", "GameStart", "GameStartAndBreak" };

        private static readonly string[] SmallTalkContentTypes =
        {
            "None", "SmallTalk_CatHeadphone", "SmallTalk_Cicada", "SmallTalk_PassageOfTime", "PurchaseEnvironment"
        };

        /// <summary>Screens the merged rows.</summary>
        /// <param name="merged">The merged pack rows. Null is treated as empty.</param>
        /// <param name="baseline">The game's own ids; null means none (before the game has loaded).</param>
        /// <param name="admit">True while pack lines are active; false holds every exclusive group back.</param>
        /// <returns>What to inject and which lines join the pools.</returns>
        public static PackLineScreenResult Screen(MasterDataSet? merged, MasterDataBaseline? baseline, bool admit)
        {
            var set = merged ?? MasterDataSet.Empty;
            var known = baseline ?? MasterDataBaseline.Empty;

            var exclusive = 0;
            foreach (var group in set.ScenarioGroups)
            {
                if (group.Languages != null)
                {
                    exclusive++;
                }
            }

            if (exclusive == 0)
            {
                return new PackLineScreenResult(set, null, null, 0, admit);
            }

            var kept = new List<ScenarioGroupRow>(set.ScenarioGroups.Count);
            if (!admit)
            {
                foreach (var group in set.ScenarioGroups)
                {
                    if (group.Languages == null)
                    {
                        kept.Add(group);
                    }
                }

                return new PackLineScreenResult(Rebuild(set, kept, set.Novel), null, null, exclusive, false);
            }

            var context = new Context(set, known);
            var lines = new List<PackLine>(exclusive);
            var rejected = new List<PackLineRejection>();
            var notes = new List<string>();
            HashSet<string>? spreadSources = null;
            var copyRows = new List<NovelRow>();

            foreach (var group in set.ScenarioGroups)
            {
                if (group.Languages == null)
                {
                    if (PackLinePools.TryGetSpread(group.Pool, out _))
                    {
                        notes.Add("'" + group.Id + "' has \"Pool\": \"" + group.Pool.Trim() + "\" but no \"Languages\", so it is an ordinary "
                                  + "group and is never offered as a pack line; add \"Languages\" (e.g. [\"th\"])");
                    }

                    kept.Add(group);
                    continue;
                }

                string problem;
                if (PackLinePools.TryGetSpread(group.Pool, out var targets))
                {
                    if (TrySpread(group, SpreadName(group.Pool), targets, context, out var copies, out var spreadNotes, out problem))
                    {
                        spreadSources ??= new HashSet<string>(StringComparer.Ordinal);
                        spreadSources.Add(group.Id);
                        foreach (var copy in copies)
                        {
                            kept.Add(copy.Group);
                            copyRows.AddRange(copy.Rows);
                            lines.Add(copy.Line);
                        }

                        notes.AddRange(spreadNotes);
                    }
                    else
                    {
                        rejected.Add(new PackLineRejection(group.Id, problem));
                    }

                    continue;
                }

                if (TryAccept(group, context, out var line, out problem))
                {
                    kept.Add(group);
                    lines.Add(line!);
                }
                else
                {
                    rejected.Add(new PackLineRejection(group.Id, problem));
                }
            }

            var novel = spreadSources is null ? set.Novel : WithCopies(set.Novel, spreadSources, copyRows);
            return new PackLineScreenResult(Rebuild(set, kept, novel), lines, rejected, 0, true, notes);
        }

        /// <summary>The id of a spread copy: <c>&lt;id&gt;@&lt;poolId&gt;</c>, for its group and for each of its novel rows.</summary>
        /// <param name="id">The id the pack wrote (group or novel row).</param>
        /// <param name="poolId">The pool the copy joins.</param>
        public static string CopyId(string id, string poolId) => id + CopySeparator + poolId;

        private static MasterDataSet Rebuild(MasterDataSet set, List<ScenarioGroupRow> groups, IReadOnlyList<NovelRow> novel) =>
            new MasterDataSet(set.Localization, novel, groups, set.Subtitles, set.Report);

        /// <summary>The merged novel rows without the accepted spread groups' own rows, then every copy's rows.</summary>
        private static List<NovelRow> WithCopies(IReadOnlyList<NovelRow> novel, HashSet<string> sources, List<NovelRow> copies)
        {
            var rows = new List<NovelRow>(novel.Count + copies.Count);
            foreach (var row in novel)
            {
                if (!sources.Contains(row.ScenarioGroupId))
                {
                    rows.Add(row);
                }
            }

            rows.AddRange(copies);
            return rows;
        }

        private static bool TryAccept(ScenarioGroupRow group, Context context, out PackLine? line, out string problem)
        {
            line = null;
            if (context.Known.HasScenarioGroup(group.Id))
            {
                problem = "overrides the game's own group; a pack line must be a new group id";
                return false;
            }

            if (!PackLinePools.TryResolve(group.Scenario, ScenarioName(group.Scenario), group.Pool, out var pool, out problem) || pool is null)
            {
                return false;
            }

            if (!TryEpisode(group.EpisodeNumber, out var episode, out problem) || !TryWeight(group.Weight, out problem))
            {
                return false;
            }

            context.RowsByGroup.TryGetValue(group.Id, out var rows);
            if (!TryScreen(group, pool, episode, rows, context, out var voices, out problem))
            {
                return false;
            }

            context.Claimed[Key(group.Scenario, episode)] = group.Id;
            line = new PackLine(group.Id, pool, episode, group.Languages!, voices, group.Weight);
            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// Copies a spread group into every target pool and screens each copy. All or nothing: nothing
        /// is claimed until every copy passes.
        /// </summary>
        private static bool TrySpread(
            ScenarioGroupRow group,
            string spread,
            IReadOnlyList<PackLinePool> targets,
            Context context,
            out List<SpreadCopy> copies,
            out List<string> notes,
            out string problem)
        {
            copies = new List<SpreadCopy>(targets.Count);
            notes = new List<string>();

            if (context.Known.HasScenarioGroup(group.Id))
            {
                problem = "overrides the game's own group; a pack line must be a new group id";
                return false;
            }

            if (!TryEpisode(group.EpisodeNumber, out var episode, out problem) || !TryWeight(group.Weight, out problem))
            {
                return false;
            }

            if (!context.RowsByGroup.TryGetValue(group.Id, out var rows) || rows.Count == 0)
            {
                problem = NoRows(group.Id);
                return false;
            }

            var global = string.Equals(spread, PackLinePools.Global, StringComparison.Ordinal);
            var spoken = CountSpoken(rows);
            var skipsTimers = false;

            // (Scenario, EpisodeNumber) -> copy id, for this group's copies: the first copy in a
            // scenario keeps the written episode, a later one in the same scenario gets its own.
            var pending = new Dictionary<long, string>();
            var scenarios = new HashSet<int>();

            foreach (var pool in targets)
            {
                if (pool.Shape == PackLineShape.TimerVoice && spoken > 1)
                {
                    skipsTimers = true;
                    continue;
                }

                var copyId = CopyId(group.Id, pool.Id);
                var slotEpisode = episode;
                if (!scenarios.Add(pool.Scenario) && !TryFreeEpisode(pool.Scenario, episode, pending, context, out slotEpisode))
                {
                    problem = "as its copy for " + pool.Id + " (\"Pool\": \"" + spread + "\"): " + pool.ScenarioName
                              + " has no free EpisodeNumber left (" + PackLinePools.MinimumEpisode.ToString(CultureInfo.InvariantCulture)
                              + "-" + PackLinePools.MaximumEpisode.ToString(CultureInfo.InvariantCulture)
                              + ") to give this slot its own; every (Scenario, EpisodeNumber) must be unique";
                    return false;
                }

                var copy = CopyGroup(group, pool, copyId, slotEpisode, context, notes);
                var copyRows = CopyRows(rows, pool, copyId);

                List<string>? voices = null;
                if (!TryCopyIds(copy, copyRows, context, out problem)
                    || !TryScreen(copy, pool, slotEpisode, copyRows, context, out voices, out problem))
                {
                    problem = "as its copy for " + pool.Id + " (\"Pool\": \"" + spread + "\"): " + problem;
                    return false;
                }

                pending[Key(pool.Scenario, slotEpisode)] = copyId;
                copies.Add(new SpreadCopy(
                    copy, copyRows, new PackLine(copyId, pool, slotEpisode, group.Languages!, voices, group.Weight, group.Id, spread)));
            }

            if (copies.Count == 0)
            {
                problem = "\"Pool\": \"" + spread + "\" left it no pool to join";
                return false;
            }

            if (skipsTimers)
            {
                notes.Add("'" + group.Id + "' is global but speaks " + spoken.ToString(CultureInfo.InvariantCulture)
                          + " rows: a timer line speaks one PomodoroVoiceText row, so it skips the timer pools (pomodoro.*, countup.*)");
            }

            if (global && group.Weight == 1d)
            {
                notes.Add("'" + group.Id + "' is global with Weight 1: Satone says it at every pick of every pool it joins "
                          + "(clicks, self-talk, timer lines, greetings, small talk)");
            }

            foreach (var copy in copies)
            {
                context.Claimed[Key(copy.Group.Scenario, copy.Line.Episode)] = copy.Group.Id;
                context.GroupIds.Add(copy.Group.Id);
                foreach (var row in copy.Rows)
                {
                    context.NovelIds.Add(row.Id);
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// The checks a group passes in its pool, whether the pack wrote it there or a spread copied it:
        /// a free episode, rows, the pool's shape and speakable voices.
        /// </summary>
        private static bool TryScreen(
            ScenarioGroupRow group, PackLinePool pool, int episode, List<NovelRow>? rows, Context context,
            out List<string> voices, out string problem)
        {
            voices = new List<string>();
            if (context.Claimed.TryGetValue(Key(group.Scenario, episode), out var holder) && !string.Equals(holder, group.Id, StringComparison.Ordinal))
            {
                problem = "EpisodeNumber " + episode.ToString(CultureInfo.InvariantCulture) + " of " + ScenarioName(group.Scenario)
                          + " is already used by group '" + holder + "'; every (Scenario, EpisodeNumber) must be unique";
                return false;
            }

            if (rows is null || rows.Count == 0)
            {
                problem = NoRows(group.Id);
                return false;
            }

            if (!CheckRows(group, pool, rows, context, out problem))
            {
                return false;
            }

            if (pool.Shape == PackLineShape.SmallTalk && !CheckSmallTalk(group, context, out problem))
            {
                return false;
            }

            // A weighted small talk is rolled before the lottery (PackLinesRuntime.RollSmallTalk), which
            // applies the play timing, unlock and last-played rules but not the special content types'
            // own conditions (headphones on, cicada season, hours since the window changed).
            if (pool.Shape == PackLineShape.SmallTalk && group.Weight.HasValue
                && group.ContentType != "None" && group.ContentType != "PurchaseEnvironment")
            {
                problem = "a weighted small talk needs ContentType \"None\" or \"PurchaseEnvironment\": the conditions of "
                          + group.ContentType + " (SmallTalkSelector) are not applied to weighted lines; drop \"Weight\" to join "
                          + "the lottery under those conditions";
                return false;
            }

            voices = context.VoicesOf(rows);
            foreach (var voice in voices)
            {
                if (!VoiceNameRules.IsValid(voice))
                {
                    problem = "speaks Voice \"" + voice + "\", which no clip can be named: " + VoiceNameRules.CharacterRuleReason;
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>A copy's ids must be new: a copy that took a group's or a row's id would override it at injection.</summary>
        private static bool TryCopyIds(ScenarioGroupRow copy, List<NovelRow> rows, Context context, out string problem)
        {
            if (context.HasScenarioGroup(copy.Id))
            {
                problem = "the copy's id '" + copy.Id + "' is already a scenario group's id";
                return false;
            }

            foreach (var row in rows)
            {
                if (context.HasNovelRow(row.Id))
                {
                    problem = "the copy's novel row id '" + row.Id + "' is already a novel row's id";
                    return false;
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// The episode for a spread copy in a scenario an earlier copy of the same group already holds
        /// (a second clock or worked-time slot): the lowest episode above <paramref name="written"/>,
        /// wrapping to <see cref="PackLinePools.MinimumEpisode"/> after <see cref="PackLinePools.MaximumEpisode"/>,
        /// that no group of the input writes for <paramref name="scenario"/> (so a line later in the input
        /// keeps the episode it wrote), no line or copy screened so far claims, and no other copy of this
        /// group holds. Depends on the input alone, so the same packs give the same episodes every load.
        /// </summary>
        /// <returns>False only when every episode of the range is taken.</returns>
        private static bool TryFreeEpisode(int scenario, int written, Dictionary<long, string> pending, Context context, out int episode)
        {
            bool Free(int candidate)
            {
                var key = Key(scenario, candidate);
                return !context.Claimed.ContainsKey(key) && !context.Written.Contains(key) && !pending.ContainsKey(key);
            }

            for (var candidate = written + 1; candidate <= PackLinePools.MaximumEpisode; candidate++)
            {
                if (Free(candidate))
                {
                    episode = candidate;
                    return true;
                }
            }

            for (var candidate = PackLinePools.MinimumEpisode; candidate < written; candidate++)
            {
                if (Free(candidate))
                {
                    episode = candidate;
                    return true;
                }
            }

            episode = 0;
            return false;
        }

        /// <summary>
        /// The copy's group: the pool's scenario and id and the slot's episode, everything else as
        /// written. A global line's small-talk copy gets the lottery's columns (<c>Break</c>, <c>None</c>,
        /// 25 %, 5 %) unless the pack wrote valid ones; columns the pack wrote that do not play add a note.
        /// </summary>
        private static ScenarioGroupRow CopyGroup(
            ScenarioGroupRow group, PackLinePool pool, string copyId, int episode, Context context, List<string> notes)
        {
            var defaults = false;
            if (pool.Shape == PackLineShape.SmallTalk && !CheckSmallTalk(group, context, out var why))
            {
                defaults = true;
                if (WritesSmallTalkColumns(group))
                {
                    notes.Add("'" + group.Id + "' is global, but its small-talk columns do not play (" + why
                              + "), so its small-talk copy uses PlayTiming Break, ContentType None, UnlockStoryEpisode None, Arg1 25, Arg2 5");
                }
            }

            return new ScenarioGroupRow
            {
                Id = copyId,
                TitleLocalizationId = group.TitleLocalizationId,
                SubtitleLocalizationId = group.SubtitleLocalizationId,
                Scenario = pool.Scenario,
                EpisodeNumber = episode,
                UnlockLevel = group.UnlockLevel,
                UnlockStoryEpisode = defaults ? "None" : group.UnlockStoryEpisode,
                PlayTiming = defaults ? "Break" : group.PlayTiming,
                ContentType = defaults ? "None" : group.ContentType,
                Arg1 = defaults ? "25" : group.Arg1,
                Arg2 = defaults ? "5" : group.Arg2,
                Arg3 = defaults ? "0" : group.Arg3,
                Languages = group.Languages,
                Pool = pool.Id,
                Weight = group.Weight,
            };
        }

        /// <summary>True when the pack wrote any small-talk column (anything but the parser's defaults).</summary>
        private static bool WritesSmallTalkColumns(ScenarioGroupRow group) =>
            group.PlayTiming != "None" || group.ContentType != "None" || group.UnlockStoryEpisode != "None"
            || group.Arg1 != "0" || group.Arg2 != "0" || group.Arg3 != "0";

        /// <summary>
        /// The copy's rows, in order, with their ids and jump targets moved to the copy. In a timer pool
        /// the spoken row becomes a <c>PomodoroVoiceText</c> row that speaks at once: <c>BodyMotion</c> -1
        /// (<c>HeroineAI</c> then lets the voice play without waiting for a motion's cue), <c>Arg1</c>
        /// "0" (no cue), <c>Arg2</c> 0, <c>Arg3</c> "0" (her mouth moves), same face, look and text.
        /// </summary>
        private static List<NovelRow> CopyRows(List<NovelRow> rows, PackLinePool pool, string copyId)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                ids.Add(row.Id);
            }

            string Target(string value) => ids.Contains(value) ? CopyId(value, pool.Id) : value;

            var timer = pool.Shape == PackLineShape.TimerVoice;
            var copies = new List<NovelRow>(rows.Count);
            foreach (var row in rows)
            {
                var id = CopyId(row.Id, pool.Id);
                if (timer && IsSpoken(row))
                {
                    copies.Add(new NovelRow
                    {
                        Id = id,
                        ScenarioGroupId = copyId,
                        Command = CommandPomodoroVoiceText,
                        Arg1 = "0",
                        Arg2 = 0f,
                        Arg3 = "0",
                        Arg4 = "0",
                        Talker = row.Talker,
                        LocalizationId = row.LocalizationId,
                        BodyMotion = -1,
                        FacialMotion = row.FacialMotion,
                        LookScale = row.LookScale,
                        LookSpeedSeconds = row.LookSpeedSeconds,
                        LookEaseType = row.LookEaseType,
                    });
                    continue;
                }

                var jumps = row.Command == CommandSelection || row.Command == CommandJump;
                var dated = row.Command == CommandIfBetweenDateTime;
                var loop = row.Command == CommandStartTextLoop;
                copies.Add(new NovelRow
                {
                    Id = id,
                    ScenarioGroupId = copyId,
                    Command = row.Command,
                    Arg1 = jumps ? Target(row.Arg1) : row.Arg1,
                    Arg2 = row.Arg2,
                    Arg3 = dated || loop ? Target(row.Arg3) : row.Arg3,
                    Arg4 = dated ? Target(row.Arg4) : row.Arg4,
                    Talker = row.Talker,
                    LocalizationId = row.LocalizationId,
                    BodyMotion = row.BodyMotion,
                    FacialMotion = row.FacialMotion,
                    LookScale = row.LookScale,
                    LookSpeedSeconds = row.LookSpeedSeconds,
                    LookEaseType = row.LookEaseType,
                });
            }

            return copies;
        }

        private static string SpreadName(string pool) =>
            string.Equals(pool.Trim(), PackLinePools.Global, StringComparison.Ordinal) ? PackLinePools.Global : PackLinePools.AnyGameStart;

        private static string ScenarioName(int scenario) =>
            MasterDataEnums.Scenario.TryName(scenario, out var name) && name != null
                ? name
                : scenario.ToString(CultureInfo.InvariantCulture);

        private static string NoRows(string groupId) =>
            "has no novel rows (data/novel.json rows whose ScenarioGroupID is '" + groupId + "')";

        private static bool TryWeight(double? weight, out string problem)
        {
            if (weight.HasValue && (double.IsNaN(weight.Value) || weight.Value < 0d || weight.Value > 1d))
            {
                problem = "Weight must be a number from 0 to 1 (0.25 = a quarter of this pool's picks, 1 = every pick), not "
                          + weight.Value.ToString("R", CultureInfo.InvariantCulture);
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool TryEpisode(float value, out int episode, out string problem)
        {
            episode = 0;
            if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value % 1f) > 0f)
            {
                problem = "EpisodeNumber must be a whole number (the game's pools hold whole episode numbers), not "
                          + value.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            if (value < PackLinePools.MinimumEpisode || value > PackLinePools.MaximumEpisode)
            {
                problem = "EpisodeNumber must be between " + PackLinePools.MinimumEpisode.ToString(CultureInfo.InvariantCulture)
                          + " and " + PackLinePools.MaximumEpisode.ToString(CultureInfo.InvariantCulture)
                          + " so it can never collide with one of the game's own episodes, not "
                          + value.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            episode = (int)value;
            problem = string.Empty;
            return true;
        }

        private static bool CheckRows(
            ScenarioGroupRow group, PackLinePool pool, List<NovelRow> rows, Context context, out string problem)
        {
            var spoken = 0;
            var timerVoice = 0;
            var rowIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                rowIds.Add(row.Id);
            }

            foreach (var row in rows)
            {
                var where = "novel row '" + row.Id + "'";

                if (row.Command == CommandWaitInput || (row.Command >= CommandTutorialFirst && row.Command <= CommandTutorialLast) || row.Command == CommandReadNextForScript)
                {
                    problem = where + " uses a command that waits for input or a tutorial step (" + row.Command.ToString(CultureInfo.InvariantCulture)
                              + "), which stalls a short talk";
                    return false;
                }

                if (HasLocalization(row) && !context.HasLocalization(row.LocalizationId))
                {
                    problem = where + " names LocalizationID '" + row.LocalizationId + "', which no localization row has";
                    return false;
                }

                switch (row.Command)
                {
                    case CommandText:
                        if (row.Arg1.Length > 0 && !IsDecimal(row.Arg1))
                        {
                            problem = where + ": a Text row's Arg1 is a delay in seconds read with float.Parse (ScenarioReader); \""
                                      + row.Arg1 + "\" is not a plain number such as \"0\" or \"1.5\"";
                            return false;
                        }

                        if (!IsWhole(row.Arg3))
                        {
                            problem = where + ": a Text row's Arg3 is read with int.Parse (ScenarioReader); \"" + row.Arg3 + "\" is not a whole number";
                            return false;
                        }

                        break;

                    case CommandVoiceText:
                        if (!IsWhole(row.Arg1))
                        {
                            problem = where + ": a VoiceText row's Arg1 is read with int.Parse (ScenarioReader); \"" + row.Arg1 + "\" is not a whole number";
                            return false;
                        }

                        break;

                    case CommandPomodoroVoiceText:
                        if (!IsWhole(row.Arg1) || !IsWhole(row.Arg3))
                        {
                            problem = where + ": a PomodoroVoiceText row's Arg1 and Arg3 are read with int.Parse (ScenarioReader; HeroineAI); \""
                                      + row.Arg1 + "\"/\"" + row.Arg3 + "\" are not both whole numbers";
                            return false;
                        }

                        // Outside the timer (clicks, self-talk, greetings) nothing but the body motion's
                        // animation event lets the voice play (HeroineAI.PlayPomodoroVoice); HeroineAI
                        // only clears the wait itself for a timer line.
                        if (pool.Family != PackLineFamily.TimerLine && row.Arg2 != -1f && row.BodyMotion == -1)
                        {
                            problem = where + ": outside the timer, a PomodoroVoiceText row waits for its body motion's voice cue (ScenarioReader), "
                                      + "so it needs a BodyMotion (copy one of the game's own rows) or Arg2 -1 to speak at once";
                            return false;
                        }

                        timerVoice++;
                        break;

                    case CommandStartTextLoop:
                        if (pool.Family == PackLineFamily.GameStart)
                        {
                            problem = where + " starts a text loop: it shows answer buttons and loops until the player picks one "
                                      + "(ScenarioReader), which a startup greeting must not wait for; write the greeting as spoken rows only";
                            return false;
                        }

                        break;

                    case CommandSelection:
                    case CommandJump:
                        if (row.Command == CommandSelection && pool.Family == PackLineFamily.GameStart)
                        {
                            problem = where + " is a Selection: a startup greeting reads in auto mode, so a choice would wait for the player; "
                                      + "write the greeting as spoken rows only";
                            return false;
                        }

                        if (!rowIds.Contains(row.Arg1))
                        {
                            problem = where + " jumps to '" + row.Arg1 + "', which is not a row of this group (ScenarioReader looks targets up inside the group)";
                            return false;
                        }

                        break;
                }

                if (IsSpoken(row))
                {
                    spoken++;
                }
            }

            if (spoken == 0)
            {
                problem = "has no spoken row: add a Text, VoiceText or PomodoroVoiceText row with a LocalizationID";
                return false;
            }

            if (pool.Shape == PackLineShape.TimerVoice && timerVoice == 0)
            {
                problem = "a timer line needs a PomodoroVoiceText row; HeroineAI looks for one (HeroineAI) and skips the line without it";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool CheckSmallTalk(ScenarioGroupRow group, Context context, out string problem)
        {
            if (!Contains(SmallTalkTimings, group.PlayTiming))
            {
                problem = "PlayTiming \"" + group.PlayTiming + "\" never plays a small talk; use one of "
                          + string.Join(", ", SmallTalkTimings) + " (exact case: SmallTalkSelector Enum.Parse)";
                return false;
            }

            if (!Contains(SmallTalkContentTypes, group.ContentType))
            {
                problem = "ContentType \"" + group.ContentType + "\" is not one of " + string.Join(", ", SmallTalkContentTypes)
                          + " (exact case: SmallTalkSelector Enum.Parse)";
                return false;
            }

            if (!IsPercent(group.Arg1) || !IsPercent(group.Arg2))
            {
                problem = "Arg1 and Arg2 are the % chance when unseen and when seen (SmallTalkSelector); write whole numbers 0-100, not \""
                          + group.Arg1 + "\"/\"" + group.Arg2 + "\"";
                return false;
            }

            if ((group.ContentType == "SmallTalk_Cicada" || group.ContentType == "SmallTalk_PassageOfTime") && !IsWhole(group.Arg3))
            {
                problem = "Arg3 is the hours threshold for " + group.ContentType + " (SmallTalkSelector); \"" + group.Arg3 + "\" is not a whole number";
                return false;
            }

            if (!string.Equals(group.UnlockStoryEpisode, "None", StringComparison.Ordinal)
                && !context.HasScenarioGroup(group.UnlockStoryEpisode))
            {
                problem = "UnlockStoryEpisode \"" + group.UnlockStoryEpisode + "\" names no scenario group, so the line could never play; use \"None\" or a group id such as main_01";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool HasLocalization(NovelRow row) =>
            !string.IsNullOrEmpty(row.LocalizationId) && !string.Equals(row.LocalizationId, "0", StringComparison.Ordinal);

        /// <summary>A row the player hears or reads: <c>Text</c>, <c>VoiceText</c> or <c>PomodoroVoiceText</c> with a <c>LocalizationID</c>.</summary>
        private static bool IsSpoken(NovelRow row) =>
            HasLocalization(row)
            && (row.Command == CommandText || row.Command == CommandVoiceText || row.Command == CommandPomodoroVoiceText);

        private static int CountSpoken(List<NovelRow> rows)
        {
            var count = 0;
            foreach (var row in rows)
            {
                if (IsSpoken(row))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool Contains(string[] values, string value)
        {
            foreach (var candidate in values)
            {
                if (string.Equals(candidate, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A whole number <c>int.Parse</c> reads the same in every culture.</summary>
        internal static bool IsWhole(string? text) =>
            text != null && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

        /// <summary>A decimal <c>float.Parse</c> reads the same in every culture: digits, at most one '.', optional leading '-'.</summary>
        internal static bool IsDecimal(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var digits = 0;
            var dots = 0;
            for (var i = 0; i < text!.Length; i++)
            {
                var c = text[i];
                if (c >= '0' && c <= '9')
                {
                    digits++;
                }
                else if (c == '.')
                {
                    dots++;
                }
                else if (!(c == '-' && i == 0))
                {
                    return false;
                }
            }

            return digits > 0 && dots <= 1 && text[text.Length - 1] != '.';
        }

        private static bool IsPercent(string? text) =>
            text != null && int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= 0 && value <= 100;

        private static long Key(int scenario, int episode) => ((long)scenario << 32) | (uint)episode;

        /// <summary>One copy of a spread group: its group, its novel rows and the line it becomes.</summary>
        private sealed class SpreadCopy
        {
            internal SpreadCopy(ScenarioGroupRow group, List<NovelRow> rows, PackLine line)
            {
                Group = group;
                Rows = rows;
                Line = line;
            }

            internal ScenarioGroupRow Group { get; }

            internal List<NovelRow> Rows { get; }

            internal PackLine Line { get; }
        }

        /// <summary>Lookups over the merged rows, built once per screen.</summary>
        private sealed class Context
        {
            internal Context(MasterDataSet set, MasterDataBaseline known)
            {
                Known = known;

                foreach (var row in set.Localization)
                {
                    LocalizationIds.Add(row.Id);
                    VoiceById[row.Id] = row.Voice;
                }

                foreach (var row in set.Novel)
                {
                    NovelIds.Add(row.Id);
                    if (!RowsByGroup.TryGetValue(row.ScenarioGroupId, out var rows))
                    {
                        rows = new List<NovelRow>();
                        RowsByGroup.Add(row.ScenarioGroupId, rows);
                    }

                    rows.Add(row);
                }

                foreach (var group in set.ScenarioGroups)
                {
                    GroupIds.Add(group.Id);
                    if (Math.Abs(group.EpisodeNumber % 1f) != 0f
                        || group.EpisodeNumber < int.MinValue || group.EpisodeNumber > int.MaxValue)
                    {
                        continue;
                    }

                    var episode = (int)group.EpisodeNumber;

                    // Every episode a group writes, in each scenario it would play under (a spread's
                    // first copy keeps it in every target scenario): an automatic episode never takes
                    // one, so a line later in the input keeps the episode it wrote.
                    if (group.Languages != null && PackLinePools.TryGetSpread(group.Pool, out var targets))
                    {
                        foreach (var pool in targets)
                        {
                            Written.Add(Key(pool.Scenario, episode));
                        }
                    }
                    else
                    {
                        Written.Add(Key(group.Scenario, episode));
                    }

                    // Ordinary pack groups claim their (Scenario, EpisodeNumber) first: a pack line
                    // may not take an episode another pack group already plays under.
                    if (group.Languages == null)
                    {
                        var key = Key(group.Scenario, episode);
                        if (!Claimed.ContainsKey(key))
                        {
                            Claimed.Add(key, group.Id);
                        }
                    }
                }
            }

            /// <summary>Every (Scenario, EpisodeNumber) a group of the input writes, screened or not; see <see cref="TryFreeEpisode"/>.</summary>
            internal HashSet<long> Written { get; } = new HashSet<long>();

            internal MasterDataBaseline Known { get; }

            internal HashSet<string> LocalizationIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Every merged scenario group id, plus each accepted spread copy's.</summary>
            internal HashSet<string> GroupIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Every merged novel row id, plus each accepted spread copy's rows.</summary>
            internal HashSet<string> NovelIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            internal Dictionary<string, string> VoiceById { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            internal Dictionary<string, List<NovelRow>> RowsByGroup { get; } =
                new Dictionary<string, List<NovelRow>>(StringComparer.Ordinal);

            internal Dictionary<long, string> Claimed { get; } = new Dictionary<long, string>();

            internal bool HasLocalization(string id) => LocalizationIds.Contains(id) || Known.HasLocalization(id);

            internal bool HasNovelRow(string id) => NovelIds.Contains(id) || Known.NovelRowsWithId(id) > 0;

            /// <summary>The voice names the spoken rows use, as the pack's own localization rows give them.</summary>
            internal List<string> VoicesOf(List<NovelRow> rows)
            {
                var voices = new List<string>();
                foreach (var row in rows)
                {
                    if (IsSpoken(row) && VoiceById.TryGetValue(row.LocalizationId, out var voice)
                        && !string.IsNullOrWhiteSpace(voice) && !voices.Contains(voice))
                    {
                        voices.Add(voice);
                    }
                }

                return voices;
            }

            internal bool HasScenarioGroup(string id) => GroupIds.Contains(id) || Known.HasScenarioGroup(id);
        }
    }
}
