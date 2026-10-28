using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.PackLines
{
    /// <summary>
    /// What <see cref="PackLineScreen.Screen"/> decided: the rows to inject, the lines that may join
    /// the game's pools, and the groups it refused.
    /// </summary>
    public sealed class PackLineScreenResult
    {
        private static readonly PackLine[] NoLines = new PackLine[0];
        private static readonly PackLineRejection[] NoRejections = new PackLineRejection[0];

        /// <summary>Creates a result.</summary>
        /// <param name="set">The rows to inject. Required.</param>
        /// <param name="lines">Accepted lines, in merged order; null means none.</param>
        /// <param name="rejected">Refused groups, in merged order; null means none.</param>
        /// <param name="heldBack">Language-exclusive groups left out because pack lines are not active.</param>
        /// <param name="admitted">Whether pack lines were active when this was decided.</param>
        /// <exception cref="ArgumentNullException"><paramref name="set"/> is null.</exception>
        public PackLineScreenResult(
            MasterDataSet set,
            IReadOnlyList<PackLine>? lines,
            IReadOnlyList<PackLineRejection>? rejected,
            int heldBack,
            bool admitted)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            Lines = lines ?? NoLines;
            Rejected = rejected ?? NoRejections;
            HeldBack = heldBack < 0 ? 0 : heldBack;
            Admitted = admitted;
        }

        /// <summary>Nothing merged, nothing screened.</summary>
        public static PackLineScreenResult Empty { get; } =
            new PackLineScreenResult(MasterDataSet.Empty, null, null, 0, false);

        /// <summary>
        /// The rows to inject: the merged set itself when no group declares languages, otherwise the
        /// merged set without the language-exclusive groups that were refused or held back.
        /// </summary>
        public MasterDataSet Set { get; }

        /// <summary>The language-exclusive lines that may join the game's pools; empty unless <see cref="Admitted"/>.</summary>
        public IReadOnlyList<PackLine> Lines { get; }

        /// <summary>Language-exclusive groups refused as malformed; each is one WARN line.</summary>
        public IReadOnlyList<PackLineRejection> Rejected { get; }

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
    /// A set with no exclusive group is returned unchanged (the same instance), so packs that do not
    /// use the members load exactly as before.
    /// </para>
    /// </remarks>
    public static class PackLineScreen
    {
        private const int CommandText = 0;
        private const int CommandWaitInput = 2;
        private const int CommandSelection = 3;
        private const int CommandJump = 4;
        private const int CommandVoiceText = 20;
        private const int CommandPomodoroVoiceText = 22;
        private const int CommandTutorialFirst = 14;
        private const int CommandTutorialLast = 19;
        private const int CommandReadNextForScript = 50;

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

                return new PackLineScreenResult(Rebuild(set, kept), null, null, exclusive, false);
            }

            var context = new Context(set, known);
            var lines = new List<PackLine>(exclusive);
            var rejected = new List<PackLineRejection>();

            foreach (var group in set.ScenarioGroups)
            {
                if (group.Languages == null)
                {
                    kept.Add(group);
                    continue;
                }

                if (TryAccept(group, context, out var line, out var problem))
                {
                    kept.Add(group);
                    lines.Add(line!);
                }
                else
                {
                    rejected.Add(new PackLineRejection(group.Id, problem));
                }
            }

            return new PackLineScreenResult(Rebuild(set, kept), lines, rejected, 0, true);
        }

        private static MasterDataSet Rebuild(MasterDataSet set, List<ScenarioGroupRow> groups) =>
            new MasterDataSet(set.Localization, set.Novel, groups, set.Subtitles, set.Report);

        private static bool TryAccept(ScenarioGroupRow group, Context context, out PackLine? line, out string problem)
        {
            line = null;
            var scenarioName = MasterDataEnums.Scenario.TryName(group.Scenario, out var name) && name != null
                ? name
                : group.Scenario.ToString(CultureInfo.InvariantCulture);

            if (context.Known.HasScenarioGroup(group.Id))
            {
                problem = "overrides the game's own group; a pack line must be a new group id";
                return false;
            }

            if (!PackLinePools.TryResolve(group.Scenario, scenarioName, group.Pool, out var pool, out problem) || pool is null)
            {
                return false;
            }

            if (!TryEpisode(group.EpisodeNumber, out var episode, out problem))
            {
                return false;
            }

            var key = Key(group.Scenario, episode);
            if (context.Claimed.TryGetValue(key, out var holder) && !string.Equals(holder, group.Id, StringComparison.Ordinal))
            {
                problem = "EpisodeNumber " + episode.ToString(CultureInfo.InvariantCulture) + " of " + scenarioName
                          + " is already used by group '" + holder + "'; every (Scenario, EpisodeNumber) must be unique";
                return false;
            }

            if (!context.RowsByGroup.TryGetValue(group.Id, out var rows) || rows.Count == 0)
            {
                problem = "has no novel rows (data/novel.json rows whose ScenarioGroupID is '" + group.Id + "')";
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

            var voices = context.VoicesOf(rows);
            foreach (var voice in voices)
            {
                if (!VoiceNameRules.IsValid(voice))
                {
                    problem = "speaks Voice \"" + voice + "\", which no clip can be named: " + VoiceNameRules.CharacterRuleReason;
                    return false;
                }
            }

            context.Claimed[key] = group.Id;
            line = new PackLine(group.Id, pool, episode, group.Languages!, voices);
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

                        if (pool.IsClickStyle && row.Arg2 != -1f && row.BodyMotion == -1)
                        {
                            problem = where + ": outside the timer, a PomodoroVoiceText row waits for its body motion's voice cue (ScenarioReader), "
                                      + "so it needs a BodyMotion (copy one of the game's own rows) or Arg2 -1 to speak at once";
                            return false;
                        }

                        timerVoice++;
                        break;

                    case CommandSelection:
                    case CommandJump:
                        if (!rowIds.Contains(row.Arg1))
                        {
                            problem = where + " jumps to '" + row.Arg1 + "', which is not a row of this group (ScenarioReader looks targets up inside the group)";
                            return false;
                        }

                        break;
                }

                if (HasLocalization(row) && (row.Command == CommandText || row.Command == CommandVoiceText || row.Command == CommandPomodoroVoiceText))
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

                    // Ordinary pack groups claim their (Scenario, EpisodeNumber) first: a pack line
                    // may not take an episode another pack group already plays under.
                    if (group.Languages == null && Math.Abs(group.EpisodeNumber % 1f) == 0f
                        && group.EpisodeNumber >= int.MinValue && group.EpisodeNumber <= int.MaxValue)
                    {
                        var key = Key(group.Scenario, (int)group.EpisodeNumber);
                        if (!Claimed.ContainsKey(key))
                        {
                            Claimed.Add(key, group.Id);
                        }
                    }
                }
            }

            internal MasterDataBaseline Known { get; }

            internal HashSet<string> LocalizationIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            internal HashSet<string> GroupIds { get; } = new HashSet<string>(StringComparer.Ordinal);

            internal Dictionary<string, string> VoiceById { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            internal Dictionary<string, List<NovelRow>> RowsByGroup { get; } =
                new Dictionary<string, List<NovelRow>>(StringComparer.Ordinal);

            internal Dictionary<long, string> Claimed { get; } = new Dictionary<long, string>();

            internal bool HasLocalization(string id) => LocalizationIds.Contains(id) || Known.HasLocalization(id);

            /// <summary>The voice names the spoken rows use, as the pack's own localization rows give them.</summary>
            internal List<string> VoicesOf(List<NovelRow> rows)
            {
                var voices = new List<string>();
                foreach (var row in rows)
                {
                    var spoken = row.Command == CommandText || row.Command == CommandVoiceText || row.Command == CommandPomodoroVoiceText;
                    if (spoken && PackLineScreen.HasLocalization(row) && VoiceById.TryGetValue(row.LocalizationId, out var voice)
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
