using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.PackLines;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// The game-side half of the pack-lines gate: finds the game's pool lists when its
    /// selectors are set up, and keeps each one in line with <see cref="PackLinePoolState"/> at the
    /// moment the game picks from it. Every member runs on the game's main thread, inside the game's
    /// own call, and never throws into it.
    /// </summary>
    internal static class PackLinesRuntime
    {
        private static readonly ConditionalWeakTable<global::RandomList, ListBinding> Lists =
            new ConditionalWeakTable<global::RandomList, ListBinding>();

        private static readonly ConditionalWeakTable<object, Root> Roots = new ConditionalWeakTable<object, Root>();

        private static readonly Dictionary<string, FieldInfo?> FieldCache = new Dictionary<string, FieldInfo?>(StringComparer.Ordinal);

        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static readonly HashSet<string> SessionGroupIds = new HashSet<string>(StringComparer.Ordinal);

        // Our own generator: the game's (UnityEngine.Random) stream is never drawn from, so its
        // shuffles and 50/50 choices are exactly what they would be without us.
        private static readonly Random Rng = new Random();

        private static FieldInfo? _items;
        private static Root? _reaction;
        private static Root? _timers;
        private static SmallTalkBinding? _smallTalk;
        private static PackLineHeldIds? _heldIds;

        /// <summary>The accepted lines. Swapped whole on a new pack state; never null.</summary>
        internal static PackLineRegistry Registry { get; private set; } = PackLineRegistry.Empty;

        /// <summary>How many game lists are bound right now (the click and timer selectors' pools).</summary>
        internal static int BoundListCount =>
            (_reaction?.Lists.Count ?? 0) + (_timers?.Lists.Count ?? 0);

        /// <summary>True once the small-talk lottery has been bound.</summary>
        internal static bool SmallTalkBound => _smallTalk != null;

        /// <summary>
        /// Resolves the one field every binding needs. False leaves the feature off.
        /// </summary>
        internal static bool Prepare(out string problem)
        {
            _items = Field(typeof(global::RandomList), PackLinesPatchTargets.RandomListItemsField);
            if (_items is null || _items.FieldType != typeof(List<int>))
            {
                problem = "RandomList." + PackLinesPatchTargets.RandomListItemsField + " is missing or is not a List<int>";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// Swaps in a new set of lines and rebinds every bound list: our old episodes leave the game's
        /// lists, the new ones start owed. Called on every injection whose lines changed.
        /// </summary>
        internal static void Use(PackLineRegistry registry)
        {
            Registry = registry ?? PackLineRegistry.Empty;
            foreach (var id in Registry.GroupIds)
            {
                SessionGroupIds.Add(id);
            }

            foreach (var root in new[] { _reaction, _timers })
            {
                if (root is null)
                {
                    continue;
                }

                foreach (var binding in root.Lists)
                {
                    binding.State.Rebind(DeckEpisodes(binding.Pool.Id), WeightedEpisodes(binding.Pool.Id), binding.Items);
                }
            }

            var smallTalk = _smallTalk;
            if (smallTalk != null)
            {
                BindSmallTalk(smallTalk.Selector, "rebind");
            }
        }

        /// <summary>Re-reads the small-talk lines from the loader after a re-injection with unchanged lines.</summary>
        internal static void RefreshSmallTalk(string why)
        {
            var smallTalk = _smallTalk;
            if (smallTalk != null)
            {
                BindSmallTalk(smallTalk.Selector, why);
            }
        }

        // ---- binding the game's lists ------------------------------------------------------------

        /// <summary>After <c>ReactionTalkSelector.Setup</c>: binds its 17 lists and the clock/worked-time lists.</summary>
        internal static void BindReaction(object selector)
        {
            var root = new Root(selector);
            foreach (var pair in PackLinesPatchTargets.ReactionLists)
            {
                AddList(root, selector, pair.Value, pair.Key);
            }

            var currentTime = Read<object>(selector, PackLinesPatchTargets.ReactionSelectorType, "_currentTimeSelfTalkSelector");
            if (currentTime != null)
            {
                foreach (var clock in PackLinesPatchTargets.ClockSelectors)
                {
                    var owner = Read<object>(currentTime, PackLinesPatchTargets.CurrentTimeSelectorType, clock.Field);
                    if (owner != null && AddList(root, owner, clock.ListField, clock.PoolId) != null)
                    {
                        root.ClockOwners[clock.PoolId] = owner;
                    }
                }
            }

            var workedTime = Read<object>(selector, PackLinesPatchTargets.ReactionSelectorType, "_workedTimeSelfTalkSelector");
            if (workedTime != null)
            {
                foreach (var worked in PackLinesPatchTargets.WorkedSelectors)
                {
                    var owner = Read<object>(workedTime, PackLinesPatchTargets.WorkedTimeSelectorType, worked.Field);
                    if (owner != null)
                    {
                        AddList(root, owner, worked.ListField, worked.PoolId);
                    }
                }
            }

            Roots.Remove(selector);
            Roots.Add(selector, root);
            _reaction = root;
            ConsoleEvents.File("packlines", "bound", ("selector", "ReactionTalkSelector"), ("lists", root.Lists.Count));
        }

        /// <summary>After <c>PomodoroVoiceSelector.Setup</c>: binds the timer selectors' lists.</summary>
        internal static void BindTimers(object selector)
        {
            var root = new Root(selector);
            foreach (var timer in PackLinesPatchTargets.TimerSelectors)
            {
                var owner = Read<object>(selector, PackLinesPatchTargets.PomodoroSelectorType, timer.Field);
                if (owner is null)
                {
                    continue;
                }

                var first = AddList(root, owner, timer.ListField, timer.PoolId);
                ListBinding? countup = null;
                if (timer.CountupPoolId != null)
                {
                    countup = AddList(root, owner, PackLinesPatchTargets.CountupListField, timer.CountupPoolId);
                }

                root.TimerLists.Remove(owner);
                root.TimerLists.Add(owner, new KeyValuePair<ListBinding?, ListBinding?>(first, countup));
            }

            Roots.Remove(selector);
            Roots.Add(selector, root);
            _timers = root;
            ConsoleEvents.File("packlines", "bound", ("selector", "PomodoroVoiceSelector"), ("lists", root.Lists.Count));
        }

        /// <summary>
        /// After <c>SmallTalkSelector.Setup</c> (and on a new pack state): takes every pack line, old or
        /// current, out of the lottery cache (<c>SmallTalkSelector.Setup</c> built it from the master
        /// data), and keeps the current ones aside - as the loader holds them now, so a line injected
        /// after the room was set up joins too - for <see cref="GateSmallTalk"/>.
        /// </summary>
        internal static void BindSmallTalk(object selector, string why)
        {
            var cache = Read<Dictionary<float, Master.ScenarioGroupData>>(
                selector, PackLinesPatchTargets.SmallTalkSelectorType, PackLinesPatchTargets.SmallTalkCacheField);
            if (cache is null)
            {
                return;
            }

            var removed = PackLineSmallTalkGate.Withdraw(cache, group => group != null && SessionGroupIds.Contains(group.ID));

            // Unweighted lines go aside for the lottery gate; weighted ones never join the lottery's
            // cache and are rolled before it (RollSmallTalk).
            var aside = new List<KeyValuePair<float, Master.ScenarioGroupData>>();
            var weighted = new List<KeyValuePair<PackLine, Master.ScenarioGroupData>>();
            var groups = global::CwyLs.ModLoader.Bulbul.MasterData.MasterDataFeature.ResolveLoader()?.ScenarioGroupMasterList;
            foreach (var line in Registry.LinesOf("smalltalk"))
            {
                var group = Find(groups, line.GroupId);
                if (group is null)
                {
                    foreach (var entry in removed)
                    {
                        if (entry.Value != null && string.Equals(entry.Value.ID, line.GroupId, StringComparison.Ordinal))
                        {
                            group = entry.Value;
                        }
                    }
                }

                if (group is null)
                {
                    continue;
                }

                if (!line.IsWeighted)
                {
                    aside.Add(new KeyValuePair<float, Master.ScenarioGroupData>(line.Episode, group));
                    continue;
                }

                weighted.Add(new KeyValuePair<PackLine, Master.ScenarioGroupData>(line, group));
                if (!IsRollableContent(group.ContentType) && Warned.Add("content:" + line.GroupId))
                {
                    CwyLsModLoader.Log.LogWarning(
                        PackLineRejection.LogPrefix + " small talk '" + line.GroupId + "' has a Weight and ContentType '" + group.ContentType
                        + "', whose special rule the weighted roll does not know; it never plays. Remove its Weight to let the "
                        + "game's lottery (and that rule) decide, or use ContentType \"None\"");
                }
            }

            _smallTalk = new SmallTalkBinding(selector, cache, aside, weighted);
            ConsoleEvents.File(
                "packlines",
                "bound",
                ("selector", "SmallTalkSelector"),
                ("withdrawn", removed.Count),
                ("aside", aside.Count),
                ("weighted", weighted.Count),
                ("why", why));
        }

        // ---- weighted small talk ----------------------------------------------------------------

        private static bool _smallTalkWon;

        /// <summary>
        /// Before the small-talk lottery (after <see cref="GateSmallTalk"/>): once the game's own two
        /// gates pass (<c>GamePlayingDefect.IsUsing()</c> false, the interval since the last small talk
        /// elapsed - <c>IsElapsedInterval</c>), the weighted small talks the lottery itself would admit
        /// for <paramref name="playTiming"/> (play timing, unlock, not the episode played last, a
        /// content type without a special rule), offered in the language shown with clips loaded, roll
        /// once. A win does what the lottery does for its pick - <c>_currentEpisodeNumber</c> - and is
        /// returned for the prefix to return in place of the lottery's own pick.
        /// </summary>
        /// <param name="selector">The <c>SmallTalkSelector</c>.</param>
        /// <param name="playTiming">The lottery's <c>PlayTiming</c> argument, as its number.</param>
        /// <returns>The winner's episode, or null to let the game's lottery run (it never sees a weighted line).</returns>
        internal static float? RollSmallTalk(object selector, int playTiming)
        {
            _smallTalkWon = false;
            var binding = _smallTalk;
            if (binding is null || binding.Weighted.Count == 0 || !ReferenceEquals(binding.Selector, selector))
            {
                return null;
            }

            if (!SmallTalkGatesPass(selector))
            {
                return null;
            }

            var code = LanguageFeature.CurrentCode;
            var before = Field(selector.GetType(), PackLinesPatchTargets.SmallTalkBeforeEpisodeField)?.GetValue(selector) is float value ? value : -1f;
            var played = PlayedIds();
            List<PackLineCandidate>? candidates = null;
            foreach (var entry in binding.Weighted)
            {
                var line = entry.Key;
                var group = entry.Value;
                if (line.Weight > 0d
                    && line.IsOfferedIn(code)
                    && group.EpisodeNumber != before
                    && (group.UnlockStoryEpisode == "None" || (played != null && played.Contains(group.UnlockStoryEpisode)))
                    && TimingAdmits(playTiming, group.PlayTiming)
                    && IsRollableContent(group.ContentType)
                    && PackLinesVoiceCheck.FirstSilent(line) is null)
                {
                    (candidates ??= new List<PackLineCandidate>()).Add(new PackLineCandidate(line, line.Weight!.Value));
                }
            }

            if (candidates is null)
            {
                return null;
            }

            DeckLast.TryGetValue(PackLineLastPlayed.KeyOf(SmallTalkPool), out var last);
            var winner = PackLineWeights.Roll(candidates, last, WeightRng.NextDouble(), WeightRng.NextDouble());
            if (winner is null)
            {
                return null;
            }

            var current = Field(selector.GetType(), PackLinesPatchTargets.SmallTalkCurrentEpisodeField);
            if (current is null || current.FieldType != typeof(float))
            {
                return null;
            }

            DeckLast[PackLineLastPlayed.KeyOf(SmallTalkPool)] = winner.SourceGroupId;
            ConsoleEvents.File(
                "packlines",
                "weighted",
                ("pool", SmallTalkPool.Id),
                ("group", winner.GroupId),
                ("episode", winner.Episode),
                ("weight", winner.Weight),
                ("timing", playTiming));

            // Last, so nothing can fail after the game's own state says "this one is next".
            current.SetValue(selector, (float)winner.Episode);
            _smallTalkWon = true;
            return winner.Episode;
        }

        /// <summary>
        /// After the small-talk lottery: when the game's own lottery picked (not a weighted win, and not
        /// -1), the last weighted small talk may come up again.
        /// </summary>
        /// <param name="result">What the lottery returned.</param>
        internal static void AfterSmallTalkLottery(float result)
        {
            if (_smallTalkWon)
            {
                _smallTalkWon = false;
                return;
            }

            if (result != -1f)
            {
                DeckLast.Remove(PackLineLastPlayed.KeyOf(SmallTalkPool));
            }
        }

        /// <summary>The lottery's first two gates, read the way <c>LotterySmallTalk</c> reads them.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SmallTalkGatesPass(object selector)
        {
            var type = selector.GetType();
            if (!(Field(type, PackLinesPatchTargets.SmallTalkDirectionField)?.GetValue(selector) is global::DirectionService direction)
                || !(Field(type, PackLinesPatchTargets.SmallTalkLoaderField)?.GetValue(selector) is Game.MasterDataLoader loader)
                || !(Field(type, PackLinesPatchTargets.SmallTalkBeforePlayField)?.GetValue(selector) is DateTime beforePlay))
            {
                // Cannot tell: leave the pick to the game's lottery.
                return false;
            }

            var defect = direction.GamePlayingDefect;
            if (defect != null && defect.IsUsing())
            {
                return false;
            }

            var interval = loader.SmallTalkData;
            return interval != null && (DateTime.Now - beforePlay).TotalMinutes >= interval.IntervalMinutes;
        }

        /// <summary>
        /// The lottery's play-timing rule (<c>LotterySmallTalk.CreateCanPlayEpisodeList</c>): a break
        /// lottery admits <c>Break</c> and <c>GameStartAndBreak</c>; a game-start one <c>GameStart</c> and
        /// <c>GameStartAndBreak</c>; <c>None</c> admits any.
        /// </summary>
        private static bool TimingAdmits(int lottery, string? groupTiming)
        {
            if (!Enum.TryParse<global::SmallTalkSelector.PlayTiming>(groupTiming, false, out var target))
            {
                return false;
            }

            switch ((global::SmallTalkSelector.PlayTiming)lottery)
            {
                case global::SmallTalkSelector.PlayTiming.Break:
                    return target == global::SmallTalkSelector.PlayTiming.Break
                           || target == global::SmallTalkSelector.PlayTiming.GameStartAndBreak;
                case global::SmallTalkSelector.PlayTiming.GameStart:
                case global::SmallTalkSelector.PlayTiming.GameStartAndBreak:
                    return target == global::SmallTalkSelector.PlayTiming.GameStart
                           || target == global::SmallTalkSelector.PlayTiming.GameStartAndBreak;
                default:
                    return true;
            }
        }

        /// <summary>
        /// A content type the lottery applies no special rule to (<c>None</c>, <c>PurchaseEnvironment</c>);
        /// the cat-headphone, cicada and passage-of-time rules read game state the roll does not.
        /// </summary>
        private static bool IsRollableContent(string? contentType) =>
            Enum.TryParse<global::SmallTalkSelector.ContentType>(contentType, false, out var content)
            && (content == global::SmallTalkSelector.ContentType.None || content == global::SmallTalkSelector.ContentType.PurchaseEnvironment);

        private static Master.ScenarioGroupData? Find(IReadOnlyList<Master.ScenarioGroupData>? groups, string id)
        {
            if (groups is null)
            {
                return null;
            }

            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group != null && string.Equals(group.ID, id, StringComparison.Ordinal))
                {
                    return group;
                }
            }

            return null;
        }

        // ---- the gate, at pick time ------------------------------------------------------------

        /// <summary>
        /// Before <c>FacilityClickHeroine.OneWord</c> / <c>AnswerChoiceStory</c>: gates the lists of the
        /// facility's own <c>ReactionTalkSelector</c>.
        /// </summary>
        internal static void GateClickFacility(object facility)
        {
            GateReaction(Read<object>(facility, PackLinesPatchTargets.ClickHeroineType, PackLinesPatchTargets.ClickSelectorField));
        }

        /// <summary>Before a click, self-talk or answer-choice pick: brings every click-selector list in line.</summary>
        internal static void GateReaction(object? selector)
        {
            var root = selector != null && Roots.TryGetValue(selector, out var found) ? found : _reaction;
            Gate(root, "reaction");
        }

        /// <summary>Before a timer pick: brings every timer list in line.</summary>
        internal static void GateTimers(object? selector)
        {
            var root = selector != null && Roots.TryGetValue(selector, out var found) ? found : _timers;
            Gate(root, "timers");
        }

        /// <summary>
        /// After <c>RandomList.InitList</c> built a new cycle: for one of our lists, everything is owed
        /// again and goes in at once when its language is shown.
        /// </summary>
        internal static void OnNewCycle(global::RandomList list)
        {
            if (!Lists.TryGetValue(list, out var binding))
            {
                return;
            }

            binding.State.StartCycle();
            var code = LanguageFeature.CurrentCode;
            var result = binding.State.Reconcile(binding.Items, episode => IsOffered(binding.Pool, episode, code), Pick);
            if (result.Changed)
            {
                ConsoleEvents.File("packlines", "cycle", ("pool", binding.Pool.Id), ("lang", code), ("placed", result.Placed));
            }

            TrackCycle(binding);

            // Refilled in the middle of a pick (GetNext on an empty list): a weighted winner that
            // waited for the refill goes in front now, so this read returns it.
            binding.State.OnCycleDuringPick(binding.Items);
        }

        /// <summary>
        /// A two-list timer selector is about to pick: when the list it will read starts with one of
        /// our episodes, answer for it (the game's own code would index its per-episode table with it).
        /// </summary>
        /// <returns>The pack episode to return, or null to let the game pick.</returns>
        internal static int? TakeTimerEpisode(object selector)
        {
            var root = _timers;
            if (root is null || !root.TimerLists.TryGetValue(selector, out var pair))
            {
                return null;
            }

            var binding = TimerModeIsCountup() ? pair.Value : pair.Key;
            if (binding is null || !binding.State.HasLines)
            {
                return null;
            }

            var next = binding.List.GetNext();
            if (!binding.State.IsPackEpisode(next))
            {
                return null;
            }

            binding.List.RemoveFromList(next);
            ConsoleEvents.File("packlines", "pick", ("pool", binding.Pool.Id), ("episode", next));
            return next;
        }

        /// <summary>Takes the episode out of its pool's list when it is there (a forced test pick).</summary>
        internal static void Consume(PackLine line)
        {
            foreach (var root in new[] { _reaction, _timers })
            {
                if (root is null)
                {
                    continue;
                }

                foreach (var binding in root.Lists)
                {
                    if (string.Equals(binding.Pool.Id, line.Pool.Id, StringComparison.Ordinal) && binding.Items.Contains(line.Episode))
                    {
                        binding.List.RemoveFromList(line.Episode);
                    }
                }
            }
        }

        /// <summary>
        /// After a self-talk pick: a pack clock line counts as one of its slot's game episodes, so the
        /// game's "already said today" check (e.g. <c>CurrentTime7HourSelfTalkSelector</c>) still holds.
        /// </summary>
        internal static void AfterSelfTalkPick(object selector, int scenario, int episode)
        {
            if (episode < PackLinePools.MinimumEpisode || !Roots.TryGetValue(selector, out var root))
            {
                return;
            }

            foreach (var pool in PackLinePools.ForScenario(scenario))
            {
                if (pool.StandIns.Count == 0 || !Registry.TryGet(pool.Id, episode, out _))
                {
                    continue;
                }

                if (!root.ClockOwners.TryGetValue(pool.Id, out var owner))
                {
                    return;
                }

                var played = Read<HashSet<int>>(owner, owner.GetType().FullName ?? pool.Selector, PackLinesPatchTargets.PlayedEpisodesField);
                if (played is null)
                {
                    return;
                }

                foreach (var standIn in pool.StandIns)
                {
                    if (played.Add(standIn))
                    {
                        ConsoleEvents.File("packlines", "clock", ("pool", pool.Id), ("episode", episode), ("countsAs", standIn));
                        break;
                    }
                }

                return;
            }
        }

        /// <summary>Before the small-talk lottery: our entries are in the cache exactly while their language is shown.</summary>
        internal static void GateSmallTalk(object selector)
        {
            // Pack small talks heard in earlier sessions count as seen, as the game's own do: without
            // this every launch made them "unseen" again and they shut out every seen game scene
            // (see the PackLineSeen remarks). The save filter still keeps the ids out of the file.
            MarkRememberedAsSeen();

            var binding = _smallTalk;
            if (binding is null || !ReferenceEquals(binding.Selector, selector))
            {
                BindSmallTalk(selector, "lottery");
                binding = _smallTalk;
            }

            if (binding is null || binding.Withdrawn.Count == 0)
            {
                return;
            }

            var code = LanguageFeature.CurrentCode;
            var result = PackLineSmallTalkGate.Reconcile(
                binding.Cache, binding.Withdrawn, episode => IsOffered(SmallTalkPool, (int)episode, code));
            if (result.Changed)
            {
                ConsoleEvents.File("packlines", "gate", ("pool", "smalltalk"), ("lang", code), ("in", result.Placed), ("out", result.Withdrawn));
            }
        }

        // ---- the save filter -------------------------------------------------------------------

        /// <summary>Prefix of <c>SaveScenarioProgressData</c>: takes pack-line ids out of the list about to be written.</summary>
        internal static void HoldBackPlayedIds()
        {
            _heldIds = null;
            if (SessionGroupIds.Count == 0)
            {
                return;
            }

            var ids = PlayedIds();
            if (ids is null)
            {
                return;
            }

            var held = PackLineSaveFilter.Strip(ids, SessionGroupIds.Contains);
            if (held.Count > 0)
            {
                _heldIds = held;
                ConsoleEvents.File("packlines", "saveFilter", ("heldBack", held.Count));
                Remember(held.Ids);
            }
        }

        // ---- one deck across sessions (mod data, never the save) --------------------------------

        private static Dictionary<string, PackLineCycleRecord>? _cycles;

        /// <summary>Which pack lines each pool's current game cycle already dealt (<see cref="PackLineCycles"/>).</summary>
        internal static string CyclesFilePath => System.IO.Path.Combine(ModLoaderPaths.DataRoot, "packlines", "cycles.json");

        private static Dictionary<string, PackLineCycleRecord> Cycles()
        {
            if (_cycles != null)
            {
                return _cycles;
            }

            var text = string.Empty;
            try
            {
                if (System.IO.File.Exists(CyclesFilePath))
                {
                    text = System.IO.File.ReadAllText(CyclesFilePath);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " reading " + CyclesFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message + "; every pack line starts owed");
            }

            _cycles = PackLineCycles.Parse(text, out var problem);
            if (problem.Length > 0)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " " + CyclesFilePath + ": " + problem + "; every pack line starts owed and the next pick rewrites it");
            }

            return _cycles;
        }

        /// <summary>The list's <c>_beforeLast</c> (<c>RandomList</c>, fixed for a whole cycle), or <see cref="PackLineCycles.Unknown"/>.</summary>
        private static int BeforeLast(global::RandomList list) =>
            Field(typeof(global::RandomList), PackLinesPatchTargets.RandomListBeforeLastField)?.GetValue(list) is int value ? value : PackLineCycles.Unknown;

        private static int GameLeft(ListBinding binding)
        {
            var left = 0;
            foreach (var episode in binding.Items)
            {
                if (!binding.State.IsPackEpisode(episode))
                {
                    left++;
                }
            }

            return left;
        }

        /// <summary>A list just bound at load: pack lines the saved cycle already dealt stay dealt.</summary>
        private static void RestoreCycle(ListBinding binding)
        {
            if (binding.State.Episodes.Count == 0 || !Cycles().TryGetValue(binding.Pool.Id, out var stored))
            {
                return;
            }

            if (!PackLineCycles.IsSameCycle(stored, BeforeLast(binding.List), GameLeft(binding)))
            {
                return;
            }

            var restored = binding.State.RestorePlayed(stored.Played);
            if (restored > 0)
            {
                ConsoleEvents.File("packlines", "cycleRestored", ("pool", binding.Pool.Id), ("played", restored));
            }
        }

        /// <summary>After the gate looked at a list: remember where its cycle stands, if that changed.</summary>
        private static void TrackCycle(ListBinding binding)
        {
            if (binding.State.Episodes.Count == 0)
            {
                return;
            }

            var record = new PackLineCycleRecord(BeforeLast(binding.List), GameLeft(binding), binding.State.PlayedThisCycle);
            var cycles = Cycles();
            if (cycles.TryGetValue(binding.Pool.Id, out var old) && old.BeforeLast == record.BeforeLast
                && old.GameLeft == record.GameLeft && SameEpisodes(old.Played, record.Played))
            {
                return;
            }

            cycles[binding.Pool.Id] = record;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CyclesFilePath)!);
                System.IO.File.WriteAllText(CyclesFilePath, PackLineCycles.ToJson(cycles));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " writing " + CyclesFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool SameEpisodes(List<int> a, List<int> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        // ---- pack small talks already heard (mod data, never the save) --------------------------

        private static HashSet<string>? _seen;

        /// <summary>The mod's own record of pack lines the player has heard.</summary>
        internal static string SeenFilePath => System.IO.Path.Combine(ModLoaderPaths.DataRoot, "packlines", "seen.json");

        private static HashSet<string> Seen()
        {
            if (_seen != null)
            {
                return _seen;
            }

            var text = string.Empty;
            try
            {
                if (System.IO.File.Exists(SeenFilePath))
                {
                    text = System.IO.File.ReadAllText(SeenFilePath);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " reading " + SeenFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message + "; heard pack lines start empty");
            }

            _seen = PackLineSeen.Parse(text, out var problem);
            if (problem.Length > 0)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " " + SeenFilePath + ": " + problem + "; heard pack lines start empty and the next one heard rewrites it");
            }

            return _seen;
        }

        private static void MarkRememberedAsSeen()
        {
            var added = PackLineSeen.MarkSeen(PlayedIds(), Seen(), SessionGroupIds.Contains);
            if (added > 0)
            {
                ConsoleEvents.File("packlines", "seen", ("marked", added));
            }
        }

        private static void Remember(IEnumerable<string> ids)
        {
            var seen = Seen();
            var changed = false;
            foreach (var id in ids)
            {
                changed |= seen.Add(id);
            }

            if (!changed)
            {
                return;
            }

            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SeenFilePath)!);
                System.IO.File.WriteAllText(SeenFilePath, PackLineSeen.ToJson(seen));
                ConsoleEvents.File("packlines", "seenSaved", ("count", seen.Count));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(PackLineRejection.LogPrefix + " writing " + SeenFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Finalizer of <c>SaveScenarioProgressData</c>: puts them back, in place.</summary>
        internal static void RestorePlayedIds()
        {
            var held = _heldIds;
            _heldIds = null;
            if (held is null)
            {
                return;
            }

            PackLineSaveFilter.Restore(PlayedIds(), held);
        }

        /// <summary>
        /// For the smoke check: how many pack-line ids the in-memory list holds now, and how many a
        /// save would hold back (a dry run on a copy).
        /// </summary>
        internal static (int InMemory, int WouldHoldBack, bool Readable) DryRunSaveFilter()
        {
            var ids = PlayedIds();
            if (ids is null)
            {
                return (0, 0, false);
            }

            var copy = new List<string>(ids);
            var held = PackLineSaveFilter.Strip(copy, SessionGroupIds.Contains);
            var inMemory = 0;
            foreach (var id in ids)
            {
                if (Registry.IsPackGroup(id))
                {
                    inMemory++;
                }
            }

            return (inMemory, held.Count, true);
        }

        // ---- the console test -----------------------------------------------------------------

        /// <summary>
        /// Plays a click-style line now through the game's own click reaction: asks for a reaction the
        /// way a click on Satone does (<c>FacilityClickHeroine.ReactionReady</c>) and puts the line in
        /// the slot the game uses for a pending "comeback" line, which
        /// <c>StartReaction</c> plays through <c>OneWord(type, episode)</c> on the next room frame.
        /// </summary>
        internal static bool PlayAsClickReaction(PackLine line, out string detail)
        {
            if (!GameScopes.TryRoom<Game.FacilityClickHeroine>(out var facility) || facility is null)
            {
                detail = "the room is not loaded, so there is no click reaction to play it through";
                return false;
            }

            var pending = Field(typeof(Game.FacilityClickHeroine), PackLinesPatchTargets.PendingLineField);
            if (pending is null)
            {
                detail = "FacilityClickHeroine." + PackLinesPatchTargets.PendingLineField + " is missing";
                return false;
            }

            if (!facility.ReactionReady(Game.FacilityClickHeroine.ReactionType.Click))
            {
                detail = "Satone cannot react right now (a scene is playing, a reaction is under way, or she is not taking clicks); try again in a moment";
                return false;
            }

            pending.SetValue(facility, new Game.ScenarioInfo((Master.ScenarioType)line.Pool.Scenario, line.Episode));
            detail = "asked for a click reaction; the room plays " + line.GroupId + " on its next frame "
                     + "(asleep: she only wakes up; wanting to talk: her story comes first and this line plays on the next click)";
            return true;
        }

        // ---- diagnostics ----------------------------------------------------------------------

        /// <summary>The live state of one pool, for <c>lines list</c>; null when nothing is bound for it.</summary>
        internal static string? Describe(PackLinePool pool)
        {
            if (pool.Family == PackLineFamily.GameStart)
            {
                return PackLinesGameStart.Describe(pool);
            }

            if (pool.Family == PackLineFamily.SmallTalk)
            {
                var smallTalk = _smallTalk;
                if (smallTalk is null)
                {
                    return "lottery not set up yet (room not loaded)";
                }

                var inCache = 0;
                foreach (var entry in smallTalk.Withdrawn)
                {
                    if (smallTalk.Cache.TryGetValue(entry.Key, out var value) && ReferenceEquals(value, entry.Value))
                    {
                        inCache++;
                    }
                }

                return inCache + " of " + smallTalk.Withdrawn.Count + " unweighted in the lottery"
                       + DescribeWeighted(pool, smallTalk.Weighted.Count, "rolled before each lottery");
            }

            foreach (var root in new[] { _reaction, _timers })
            {
                if (root is null)
                {
                    continue;
                }

                foreach (var binding in root.Lists)
                {
                    if (string.Equals(binding.Pool.Id, pool.Id, StringComparison.Ordinal))
                    {
                        return binding.State.PlacedCount + " in the game's list (" + binding.Items.Count + " left this cycle), "
                               + binding.State.OwedCount + " not yet played this cycle"
                               + DescribeWeighted(pool, binding.State.WeightedEpisodes.Count, "rolled at each pick of this pool, never in the deck");
                    }
                }
            }

            return "not set up yet (room not loaded)";
        }

        /// <summary><c>; weighted 2 (rolled at each pick..., last: x)</c>, or empty when the pool has none.</summary>
        private static string DescribeWeighted(PackLinePool pool, int count, string how)
        {
            if (count == 0)
            {
                return string.Empty;
            }

            DeckLast.TryGetValue(PackLineLastPlayed.KeyOf(pool), out var last);
            return "; weighted " + count + " (" + how + "; played last: " + (string.IsNullOrEmpty(last) ? "none" : last) + ")";
        }

        /// <summary>
        /// For the smoke check: bound lists that hold a weighted episode outside a pick (there should
        /// be none - a weighted line is only ever in front for the one pick it won).
        /// </summary>
        internal static List<string> StrayWeighted()
        {
            var stray = new List<string>();
            foreach (var root in new[] { _reaction, _timers })
            {
                if (root is null)
                {
                    continue;
                }

                foreach (var binding in root.Lists)
                {
                    if (binding.State.InPick || binding.State.WeightedEpisodes.Count == 0)
                    {
                        continue;
                    }

                    foreach (var episode in binding.Items)
                    {
                        if (binding.State.IsWeighted(episode))
                        {
                            stray.Add(binding.Pool.Id + " #" + episode);
                        }
                    }
                }
            }

            var smallTalk = _smallTalk;
            if (smallTalk != null)
            {
                foreach (var entry in smallTalk.Weighted)
                {
                    if (smallTalk.Cache.TryGetValue(entry.Key.Episode, out var value) && ReferenceEquals(value, entry.Value))
                    {
                        stray.Add("smalltalk #" + entry.Key.Episode);
                    }
                }
            }

            return stray;
        }

        /// <summary>For the smoke detail: how many weighted lines the bound lists and the lottery roll.</summary>
        internal static int WeightedBoundCount
        {
            get
            {
                var count = 0;
                foreach (var root in new[] { _reaction, _timers })
                {
                    if (root is null)
                    {
                        continue;
                    }

                    foreach (var binding in root.Lists)
                    {
                        count += binding.State.WeightedEpisodes.Count;
                    }
                }

                return count + (_smallTalk?.Weighted.Count ?? 0);
            }
        }

        /// <summary>
        /// For the smoke check: what each bound pool would hold at the game's next pick if the game
        /// showed <paramref name="code"/> - the gate run on copies, so nothing the game holds and none
        /// of our bookkeeping is touched.
        /// </summary>
        /// <param name="code">A language code; one no line is offered in simulates "any other language".</param>
        internal static List<PoolDryRun> DryRun(string code)
        {
            // Clip readiness is a fact about the language shown now; another language's dry run
            // asks the language gate only.
            var voices = string.Equals(code, LanguageFeature.CurrentCode, StringComparison.Ordinal);
            var runs = new List<PoolDryRun>();
            foreach (var root in new[] { _reaction, _timers })
            {
                if (root is null)
                {
                    continue;
                }

                foreach (var binding in root.Lists)
                {
                    if (binding.State.Episodes.Count == 0)
                    {
                        continue;
                    }

                    var copy = new List<int>(binding.Items);
                    var state = binding.State.Clone();
                    var gameBefore = CountGame(copy, state);
                    state.Reconcile(copy, episode => IsOffered(binding.Pool, episode, code, voices), _ => 0);

                    var packAfter = copy.Count - CountGame(copy, state);
                    // The gate inserts only into a non-empty list (an empty one is about to start a
                    // new cycle), so an empty copy expects nothing.
                    var expected = 0;
                    if (copy.Count > 0)
                    {
                        foreach (var episode in state.Episodes)
                        {
                            if (state.IsOwed(episode) && IsOffered(binding.Pool, episode, code, voices))
                            {
                                expected++;
                            }
                        }
                    }

                    runs.Add(new PoolDryRun(binding.Pool, gameBefore, CountGame(copy, state), packAfter, expected));
                }
            }

            var smallTalk = _smallTalk;
            if (smallTalk != null && smallTalk.Withdrawn.Count > 0)
            {
                var copy = new Dictionary<float, Master.ScenarioGroupData>(smallTalk.Cache);
                var gameBefore = copy.Count - CountOurs(copy, smallTalk.Withdrawn);
                PackLineSmallTalkGate.Reconcile(copy, smallTalk.Withdrawn, episode => IsOffered(SmallTalkPool, (int)episode, code, voices));
                var packAfter = CountOurs(copy, smallTalk.Withdrawn);
                var expected = 0;
                foreach (var entry in smallTalk.Withdrawn)
                {
                    if (IsOffered(SmallTalkPool, (int)entry.Key, code, voices))
                    {
                        expected++;
                    }
                }

                runs.Add(new PoolDryRun(SmallTalkPool, gameBefore, copy.Count - packAfter, packAfter, expected));
            }

            return runs;
        }

        // ---- helpers --------------------------------------------------------------------------

        /// <summary>Logs a failure once per site and key, so a broken seam cannot flood the console.</summary>
        internal static void ReportOnce(string site, Exception ex)
        {
            var key = site + ":" + ex.GetType().Name;
            lock (Reported)
            {
                if (!Reported.Add(key))
                {
                    CwyLsModLoader.Log.LogDebug(PackLineRejection.LogPrefix + " " + site + " failed again: " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }
            }

            CwyLsModLoader.Log.LogWarning(
                PackLineRejection.LogPrefix + " " + site + " failed, the game's own pick runs instead: " + ex.GetType().Name + ": " + ex.Message);
        }

        /// <summary>Takes every pack episode out of a timer selector's lists (the safety net after a failure).</summary>
        internal static void WithdrawAll(object selector)
        {
            var root = _timers;
            if (root is null || !root.TimerLists.TryGetValue(selector, out var pair))
            {
                return;
            }

            foreach (var binding in new[] { pair.Key, pair.Value })
            {
                if (binding != null)
                {
                    binding.State.Rebind(binding.State.Episodes, binding.State.WeightedEpisodes, binding.Items);
                }
            }
        }

        private static PackLinePool SmallTalkPool
        {
            get
            {
                PackLinePools.TryGet("smalltalk", out var pool);
                return pool!;
            }
        }

        private static int CountGame(List<int> items, PackLinePoolState state)
        {
            var count = 0;
            foreach (var value in items)
            {
                if (!state.IsPackEpisode(value))
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountOurs(
            Dictionary<float, Master.ScenarioGroupData> cache, List<KeyValuePair<float, Master.ScenarioGroupData>> withdrawn)
        {
            var count = 0;
            foreach (var entry in withdrawn)
            {
                if (cache.TryGetValue(entry.Key, out var value) && ReferenceEquals(value, entry.Value))
                {
                    count++;
                }
            }

            return count;
        }

        private static void Gate(Root? root, string site)
        {
            if (root is null || Registry.Count == 0)
            {
                return;
            }

            var code = LanguageFeature.CurrentCode;
            var placed = 0;
            var withdrawn = 0;
            var fronted = 0;
            foreach (var binding in root.Lists)
            {
                if (!binding.State.HasLines)
                {
                    continue;
                }

                var result = binding.State.Reconcile(binding.Items, episode => IsOffered(binding.Pool, episode, code), Pick);
                placed += result.Placed;
                withdrawn += result.Withdrawn;
                TrackCycle(binding);

                // Weighted lines: one roll per list, per pick. A pool without one is left exactly as
                // the deck gate left it - no roll, no draw, nothing in front.
                if (binding.State.WeightedEpisodes.Count > 0)
                {
                    var winner = RollWeighted(binding.Pool, binding.State.WeightedEpisodes, code);
                    binding.State.BeginPick(binding.Items, winner?.Episode);
                    if (winner != null)
                    {
                        fronted++;
                    }
                }
            }

            if (placed > 0 || withdrawn > 0)
            {
                ConsoleEvents.File("packlines", "gate", ("site", site), ("lang", code), ("in", placed), ("out", withdrawn), ("weighted", fronted));
            }
        }

        // ---- weighted lines in the deck pools ---------------------------------------------------

        // Their own generator, so the deck gate's insert positions are the same draws they were
        // before weighted lines existed; neither is the game's UnityEngine.Random.
        private static readonly Random WeightRng = new Random();

        // "Never twice in a row", per pool: the weighted line the game took last, cleared as soon as
        // the game takes anything else from that pool. In memory (a deck pool picks often).
        private static readonly Dictionary<string, string> DeckLast = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The weighted line that takes this pick of one list, or null: the pool's weighted lines
        /// offered in <paramref name="code"/> with their clips loaded, rolled once
        /// (<see cref="PackLineWeights.Roll"/>). Draws nothing when none is offered.
        /// </summary>
        private static PackLine? RollWeighted(PackLinePool pool, IReadOnlyList<int> weighted, string code)
        {
            List<PackLineCandidate>? candidates = null;
            foreach (var episode in weighted)
            {
                if (Registry.TryGet(pool.Id, episode, out var line)
                    && line != null
                    && line.Weight > 0d
                    && line.IsOfferedIn(code)
                    && PackLinesVoiceCheck.FirstSilent(line) is null)
                {
                    (candidates ??= new List<PackLineCandidate>()).Add(new PackLineCandidate(line, line.Weight!.Value));
                }
            }

            if (candidates is null)
            {
                return null;
            }

            DeckLast.TryGetValue(PackLineLastPlayed.KeyOf(pool), out var last);
            return PackLineWeights.Roll(candidates, last, WeightRng.NextDouble(), WeightRng.NextDouble());
        }

        /// <summary>
        /// After a click, self-talk or answer-choice pick (finalizer of <c>OneWord</c> /
        /// <c>AnswerChoiceStory</c>): takes back the weighted lines the game did not read and records
        /// what each read pool played.
        /// </summary>
        internal static void EndClickPick(object facility)
        {
            var selector = Read<object>(facility, PackLinesPatchTargets.ClickHeroineType, PackLinesPatchTargets.ClickSelectorField);
            EndPick(selector != null && Roots.TryGetValue(selector, out var found) ? found : _reaction);
        }

        /// <summary>After a timer pick (finalizer of <c>TakeNext*Voice</c>): as <see cref="EndClickPick"/>.</summary>
        internal static void EndTimerPick(object? selector)
        {
            EndPick(selector != null && Roots.TryGetValue(selector, out var found) ? found : _timers);
        }

        private static void EndPick(Root? root)
        {
            if (root is null)
            {
                return;
            }

            foreach (var binding in root.Lists)
            {
                if (!binding.State.InPick)
                {
                    continue;
                }

                var result = binding.State.EndPick(binding.Items);
                if (!result.Read)
                {
                    // Not this pool's pick: its "last" stays as it was.
                    continue;
                }

                var key = PackLineLastPlayed.KeyOf(binding.Pool);
                if (result.Weighted && Registry.TryGet(binding.Pool.Id, result.Episode, out var line) && line != null)
                {
                    DeckLast[key] = line.SourceGroupId;
                    ConsoleEvents.File(
                        "packlines",
                        "weighted",
                        ("pool", binding.Pool.Id),
                        ("group", line.GroupId),
                        ("episode", line.Episode),
                        ("weight", line.Weight));
                }
                else
                {
                    // The game's own line (or a dealt pack line) played in between: the last
                    // weighted line may come up again.
                    DeckLast.Remove(key);
                }
            }
        }

        /// <summary>The pool's unweighted pack episodes: the ones dealt into the game's deck.</summary>
        private static List<int> DeckEpisodes(string poolId)
        {
            var episodes = new List<int>();
            foreach (var line in Registry.LinesOf(poolId))
            {
                if (!line.IsWeighted)
                {
                    episodes.Add(line.Episode);
                }
            }

            return episodes;
        }

        /// <summary>The pool's weighted pack episodes: never dealt, rolled at each pick.</summary>
        private static List<int> WeightedEpisodes(string poolId)
        {
            var episodes = new List<int>();
            foreach (var line in Registry.LinesOf(poolId))
            {
                if (line.IsWeighted)
                {
                    episodes.Add(line.Episode);
                }
            }

            return episodes;
        }

        /// <summary>
        /// The gate for one episode: a pack line of this pool, offered in <paramref name="code"/>, and -
        /// when <paramref name="checkVoices"/> - every one of its clips would make a sound now
        /// (<see cref="PackLinesVoiceCheck"/>; a clip that has not loaded yet skips this pick only).
        /// </summary>
        private static bool IsOffered(PackLinePool pool, int episode, string code, bool checkVoices = true) =>
            Registry.TryGet(pool.Id, episode, out var line)
            && line != null
            && line.IsOfferedIn(code)
            && (!checkVoices || PackLinesVoiceCheck.FirstSilent(line) is null);

        private static int Pick(int count) => Rng.Next(count);

        private static ListBinding? AddList(Root root, object owner, string field, string poolId)
        {
            if (!PackLinePools.TryGet(poolId, out var pool) || pool is null)
            {
                return null;
            }

            var list = Read<global::RandomList>(owner, owner.GetType().FullName ?? poolId, field);
            if (list is null || _items is null || !(_items.GetValue(list) is List<int> items))
            {
                return null;
            }

            var binding = new ListBinding(
                list, items, pool, new PackLinePoolState(DeckEpisodes(pool.Id), WeightedEpisodes(pool.Id)), owner);
            Lists.Remove(list);
            Lists.Add(list, binding);
            root.Lists.Add(binding);
            RestoreCycle(binding);
            return binding;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TimerModeIsCountup() =>
            Game.SaveDataManager.HasInstance
            && Game.SaveDataManager.Instance.PomodoroData.CurrentTimerMode == Game.TimerMode.Countup;

        /// <summary>The current timer mode, as the pack-lines enum.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static PackLineTimerMode CurrentTimerMode()
        {
            if (!Game.SaveDataManager.HasInstance)
            {
                return PackLineTimerMode.None;
            }

            switch (Game.SaveDataManager.Instance.PomodoroData.CurrentTimerMode)
            {
                case Game.TimerMode.Pomodoro:
                    return PackLineTimerMode.Pomodoro;
                case Game.TimerMode.Countup:
                    return PackLineTimerMode.Countup;
                default:
                    return PackLineTimerMode.None;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static List<string>? PlayedIds() =>
            Game.SaveDataManager.HasInstance ? Game.SaveDataManager.Instance.ScenarioProgressData?.PlayedScenarioGroupIDs : null;

        private static T? Read<T>(object owner, string typeLabel, string field)
            where T : class
        {
            var info = Field(owner.GetType(), field);
            if (info is null)
            {
                CwyLsModLoader.Log.LogDebug(PackLineRejection.LogPrefix + " " + typeLabel + "." + field + " is missing");
                return null;
            }

            return info.GetValue(owner) as T;
        }

        private static FieldInfo? Field(Type type, string name)
        {
            var key = type.FullName + "::" + name;
            lock (FieldCache)
            {
                if (!FieldCache.TryGetValue(key, out var info))
                {
                    info = AccessTools.Field(type, name);
                    FieldCache.Add(key, info);
                }

                return info;
            }
        }

        /// <summary>One game list we keep in line.</summary>
        private sealed class ListBinding
        {
            internal ListBinding(global::RandomList list, List<int> items, PackLinePool pool, PackLinePoolState state, object owner)
            {
                List = list;
                Items = items;
                Pool = pool;
                State = state;
                Owner = owner;
            }

            internal global::RandomList List { get; }

            internal List<int> Items { get; }

            internal PackLinePool Pool { get; }

            internal PackLinePoolState State { get; }

            internal object Owner { get; }
        }

        /// <summary>One selector the game picks through, and the lists it owns.</summary>
        private sealed class Root
        {
            internal Root(object selector)
            {
                Selector = selector;
            }

            internal object Selector { get; }

            internal List<ListBinding> Lists { get; } = new List<ListBinding>();

            internal Dictionary<string, object> ClockOwners { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

            internal Dictionary<object, KeyValuePair<ListBinding?, ListBinding?>> TimerLists { get; } =
                new Dictionary<object, KeyValuePair<ListBinding?, ListBinding?>>();
        }

        /// <summary>The small-talk lottery, the unweighted entries we keep aside, and the weighted ones.</summary>
        private sealed class SmallTalkBinding
        {
            internal SmallTalkBinding(
                object selector,
                Dictionary<float, Master.ScenarioGroupData> cache,
                List<KeyValuePair<float, Master.ScenarioGroupData>> withdrawn,
                List<KeyValuePair<PackLine, Master.ScenarioGroupData>> weighted)
            {
                Selector = selector;
                Cache = cache;
                Withdrawn = withdrawn;
                Weighted = weighted;
            }

            internal object Selector { get; }

            internal Dictionary<float, Master.ScenarioGroupData> Cache { get; }

            /// <summary>Unweighted pack small talks: in the cache exactly while their language is shown.</summary>
            internal List<KeyValuePair<float, Master.ScenarioGroupData>> Withdrawn { get; }

            /// <summary>Weighted pack small talks: never in the cache, rolled before the lottery.</summary>
            internal List<KeyValuePair<PackLine, Master.ScenarioGroupData>> Weighted { get; }
        }
    }

    /// <summary>One pool's dry run, for the smoke check.</summary>
    internal readonly struct PoolDryRun
    {
        internal PoolDryRun(PackLinePool pool, int gameBefore, int gameAfter, int packAfter, int packExpected)
        {
            Pool = pool;
            GameBefore = gameBefore;
            GameAfter = gameAfter;
            PackAfter = packAfter;
            PackExpected = packExpected;
        }

        /// <summary>The pool.</summary>
        internal PackLinePool Pool { get; }

        /// <summary>Game episodes (or game small talks) in the list before the gate ran.</summary>
        internal int GameBefore { get; }

        /// <summary>Game episodes after: must equal <see cref="GameBefore"/> - the gate never touches the game's own.</summary>
        internal int GameAfter { get; }

        /// <summary>Pack episodes the next pick could return.</summary>
        internal int PackAfter { get; }

        /// <summary>Pack episodes the gate should have let in: the shown language's lines not yet played this cycle.</summary>
        internal int PackExpected { get; }

        /// <summary>True when the dry run shows exactly the expected pool.</summary>
        internal bool Ok => GameBefore == GameAfter && PackAfter == PackExpected;

        /// <summary><c>pool game N + pack M</c>.</summary>
        public override string ToString() =>
            Pool.Id + " game " + GameAfter + " + pack " + PackAfter + (Ok ? string.Empty : " (expected game " + GameBefore + " + pack " + PackExpected + ")");
    }
}
