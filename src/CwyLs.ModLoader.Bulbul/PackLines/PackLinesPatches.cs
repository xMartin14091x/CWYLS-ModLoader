using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.PackLines;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// The Harmony patches of the pack-lines feature. Every patch body is a thin,
    /// never-throwing call into <see cref="PackLinesRuntime"/>; on any failure the game's own code
    /// runs as if we were not there.
    /// </summary>
    internal static class PackLinesPatches
    {
        /// <summary>Harmony id of these patches.</summary>
        internal const string HarmonyId = BulbulPlugin.PluginGuid + ".packlines";

        /// <summary>How many game methods are patched.</summary>
        internal static int Count { get; private set; }

        /// <summary>What was patched, or why not. Never null.</summary>
        internal static string Detail { get; private set; } = "not applied";

        /// <summary>
        /// Resolves every method first and patches only when all of them exist; a failure while
        /// patching takes every patch back, so the feature is either whole or off.
        /// </summary>
        /// <param name="harmony">The feature's Harmony instance.</param>
        /// <param name="problem">Why not, or empty.</param>
        /// <returns>True when every patch is in place.</returns>
        internal static bool Apply(Harmony harmony, out string problem)
        {
            var plan = new List<Planned>();
            var missing = new List<string>();
            var game = typeof(global::RandomList).Assembly;

            Plan(plan, missing, typeof(global::RandomList), PackLinesPatchTargets.InitListMethod, Type.EmptyTypes, postfix: nameof(InitListPostfix));
            var reaction = typeof(global::ReactionTalkSelector);
            var poolParameters = new[] { typeof(Game.LocationType), typeof(bool) };
            var groupByRef = typeof(Master.ScenarioGroupData).MakeByRefType();
            Plan(plan, missing, reaction, "Setup", new[] { typeof(IReadOnlyList<Master.ScenarioGroupData>), typeof(Game.ScenarioPlayableLocations) },
                prefix: nameof(ReactionSetupPrefix), postfix: nameof(ReactionSetupPostfix));
            Plan(plan, missing, reaction, PackLinesPatchTargets.SelfTalkMethod,
                new[] { typeof(Game.LocationType), typeof(Game.TimeOfDayProvider.TimeOfDayType), typeof(bool), groupByRef },
                prefix: nameof(SelfTalkPickPrefix), postfix: nameof(SelfTalkPickPostfix), finalizer: nameof(SelectorPickFinalizer));
            Plan(plan, missing, reaction, "TryFindRow", new[] { typeof(Game.ScenarioInfo), groupByRef }, prefix: nameof(SelfTalkFindPrefix));
            Plan(plan, missing, reaction, "Commit", new[] { typeof(Master.ScenarioGroupData) }, prefix: nameof(ReactionCommitPrefix));
            Plan(plan, missing, typeof(Game.LocationTalkPool), "TryPeek",
                new[] { typeof(Game.LocationType), typeof(bool), groupByRef }, prefix: nameof(ReactionPoolPeekPrefix));
            Plan(plan, missing, typeof(Game.LocationTalkPool), "Refill", poolParameters, postfix: nameof(ReactionPoolRefillPostfix));
            Plan(plan, missing, typeof(Game.FacilityClickHeroine), "StartReaction", new[] { typeof(Master.ScenarioType) },
                prefix: nameof(StartReactionPrefix));
            Plan(plan, missing, typeof(Game.FacilityClickHeroine), "AnswerChoiceStory", Type.EmptyTypes,
                prefix: nameof(AnswerChoicePrefix), finalizer: nameof(AnswerChoiceFinalizer));

            var timerParameters = new[] { typeof(bool), typeof(Game.LocationType) };
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "Setup", Type.EmptyTypes, postfix: nameof(TimerSetupPostfix));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextWorkStartVoice", timerParameters,
                prefix: nameof(WorkStartPrefix), finalizer: nameof(SelectorPickFinalizer));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextBreakStartVoice", timerParameters,
                prefix: nameof(BreakStartPrefix), finalizer: nameof(SelectorPickFinalizer));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextFinishVoice", timerParameters,
                prefix: nameof(FinishPrefix), finalizer: nameof(SelectorPickFinalizer));

            foreach (var timer in PackLinesPatchTargets.TimerSelectors)
            {
                if (timer.CountupPoolId is null)
                {
                    continue;
                }

                var type = game.GetType(timer.TypeName, throwOnError: false);
                if (type is null)
                {
                    missing.Add(timer.TypeName);
                    continue;
                }

                Plan(plan, missing, type, "TakeNextVoice", Type.EmptyTypes, prefix: nameof(TakeNextVoicePrefix));
            }

            var smallTalk = typeof(global::SmallTalkSelector);
            var locationParameter = new[] { typeof(Game.LocationType) };
            Plan(plan, missing, smallTalk, "Setup", Type.EmptyTypes, postfix: nameof(SmallTalkSetupPostfix));
            Plan(plan, missing, smallTalk, "LotterySmallTalk",
                new[] { typeof(global::SmallTalkSelector.PlayTiming), typeof(Game.LocationType) },
                prefix: nameof(LotteryPrefix), postfix: nameof(LotteryPostfix), finalizer: nameof(SmallTalkFinalizer));
            Plan(plan, missing, smallTalk, "CanPlayReadyTalk", locationParameter,
                prefix: nameof(CanPlayReadyPrefix), postfix: nameof(CanPlayReadyPostfix), finalizer: nameof(SmallTalkFinalizer));
            Plan(plan, missing, smallTalk, "ReserveReadyTalk", locationParameter,
                prefix: nameof(ReserveReadyPrefix), postfix: nameof(LotteryPostfix), finalizer: nameof(SmallTalkFinalizer));
            Plan(plan, missing, smallTalk, PackLinesPatchTargets.PlayableEpisodeMethod,
                new[] { typeof(Master.ScenarioGroupData), typeof(global::SmallTalkSelector.PlayTiming), typeof(Game.LocationType), typeof(Game.StairsSeat?) },
                prefix: nameof(SmallTalkTimingPrefix));
            Plan(plan, missing, typeof(Game.ScenarioPlayableLocations), "IsPlayableAt",
                new[] { typeof(string), typeof(Game.LocationType) }, postfix: nameof(PlayableAtPostfix));
            Plan(plan, missing, typeof(global::GameStartTalkSelector), "GetGameStartTalk",
                new[] { typeof(Master.ScenarioType) }, postfix: nameof(GameStartPickPostfix));
            Plan(plan, missing, typeof(global::GameStartTalkSelector), "UseGameStartTalk",
                new[] { typeof(Master.ScenarioType) }, prefix: nameof(GameStartUsePrefix));

            if (missing.Count > 0)
            {
                problem = "method(s) not found: " + string.Join(", ", missing);
                Detail = problem;
                return false;
            }

            var applied = 0;
            try
            {
                foreach (var step in plan)
                {
                    harmony.Patch(step.Target, step.Prefix, step.Postfix, finalizer: step.Finalizer);
                    applied++;
                }
            }
            catch (Exception ex)
            {
                problem = "patching " + plan[applied].Label + " failed: " + ex.GetType().Name + ": " + ex.Message;
                Detail = problem;
                try
                {
                    harmony.UnpatchSelf();
                }
                catch (Exception undo)
                {
                    CwyLsModLoader.Log.LogError(
                        PackLineRejection.LogPrefix + " could not take back the " + applied + " patch(es) already applied: "
                        + undo.GetType().Name + ": " + undo.Message);
                }

                return false;
            }

            Count = applied;
            Detail = applied + " game method(s) patched";
            problem = string.Empty;
            return true;
        }

        // ---- patch bodies: each one never throws into the game -----------------------------------

        /// <summary>Postfix of <c>RandomList.InitList</c>.</summary>
        internal static void InitListPostfix(object __instance)
        {
            try
            {
                if (__instance is global::RandomList list)
                {
                    PackLinesRuntime.OnNewCycle(list);
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("new cycle", ex);
            }
        }

        internal static void ReactionSetupPrefix(ref IReadOnlyList<Master.ScenarioGroupData> __0)
        {
            try
            {
                __0 = PackLinesRuntime.WithoutPackRows(__0);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("reaction master rows", ex);
            }
        }

        internal static void ReactionSetupPostfix(object __instance)
        {
            try
            {
                PackLinesRuntime.BindReaction(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("click selector setup", ex);
            }
        }

        internal static void StartReactionPrefix(object __instance)
        {
            try
            {
                PackLinesRuntime.CheckForcedClick(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("forced click", ex);
            }
        }

        internal static void AnswerChoicePrefix(object __instance)
        {
            try
            {
                PackLinesRuntime.GateAnswerChoice(__instance, PackLinesRuntime.RoomIsBaseDesk());
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("answer-choice pick", ex);
            }
        }

        internal static void AnswerChoiceFinalizer(object __instance)
        {
            try
            {
                PackLinesRuntime.EndClickPick(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("answer-choice pick end", ex);
            }
        }

        internal static void SelfTalkPickPrefix(object __instance, Game.LocationType __0, bool __2)
        {
            try
            {
                PackLinesRuntime.GateReaction(__instance, PackLinesRuntime.IsBaseDesk(__0, __2));
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("self-talk gate", ex);
            }
        }

        internal static bool SelfTalkFindPrefix(object __instance, Game.ScenarioInfo? __0,
            ref Master.ScenarioGroupData __1, ref bool __result)
        {
            try
            {
                return UseReactionRow(PackLinesRuntime.FindSelfTalk(__instance, __0), ref __1, ref __result);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("self-talk row", ex);
                return true;
            }
        }

        internal static void SelfTalkPickPostfix(object __instance, bool __result, Master.ScenarioGroupData? __3)
        {
            try
            {
                if (__result && __3 != null)
                {
                    PackLinesRuntime.AfterSelfTalkPick(__instance, (int)__3.Scenario, (int)__3.EpisodeNumber);
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("self-talk pick", ex);
            }
        }

        internal static bool ReactionPoolPeekPrefix(Game.LocationTalkPool __instance, Game.LocationType __0, bool __1,
            ref Master.ScenarioGroupData __2, ref bool __result)
        {
            try
            {
                return !PackLinesRuntime.IsBaseDesk(__0, __1)
                       || UseReactionRow(PackLinesRuntime.PeekReaction(__instance), ref __2, ref __result);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("reaction pool pick", ex);
                return true;
            }
        }

        private static bool UseReactionRow(Master.ScenarioGroupData? row, ref Master.ScenarioGroupData resultRow, ref bool result)
        {
            if (row == null)
            {
                return true;
            }

            resultRow = row;
            result = true;
            return false;
        }

        internal static void ReactionPoolRefillPostfix(Game.LocationTalkPool __instance, Game.LocationType __0, bool __1)
        {
            try
            {
                if (PackLinesRuntime.IsBaseDesk(__0, __1))
                {
                    PackLinesRuntime.RefillReaction(__instance);
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("reaction pool refill", ex);
            }
        }

        internal static bool ReactionCommitPrefix(object __instance, Master.ScenarioGroupData __0)
        {
            try
            {
                return PackLinesRuntime.CommitReaction(__instance, __0);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("reaction commit", ex);
                return true;
            }
        }

        internal static void SelectorPickFinalizer(object __instance)
        {
            try
            {
                PackLinesRuntime.EndSelectorPick(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("selector pick end", ex);
            }
        }

        /// <summary>Postfix of <c>PomodoroVoiceSelector.Setup</c>.</summary>
        internal static void TimerSetupPostfix(object __instance)
        {
            try
            {
                PackLinesRuntime.BindTimers(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("timer selector setup", ex);
            }
        }

        /// <summary>Prefix of <c>PomodoroVoiceSelector.TakeNextWorkStartVoice</c>.</summary>
        internal static bool WorkStartPrefix(object __instance, bool __0, Game.LocationType __1, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.WorkStart, __0, __1, ref __result);

        /// <summary>Prefix of <c>PomodoroVoiceSelector.TakeNextBreakStartVoice</c>.</summary>
        internal static bool BreakStartPrefix(object __instance, bool __0, Game.LocationType __1, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.BreakStart, __0, __1, ref __result);

        /// <summary>Prefix of <c>PomodoroVoiceSelector.TakeNextFinishVoice</c>.</summary>
        internal static bool FinishPrefix(object __instance, bool __0, Game.LocationType __1, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.Finish, __0, __1, ref __result);

        /// <summary>Prefix of a two-list timer selector's <c>TakeNextVoice</c>.</summary>
        internal static bool TakeNextVoicePrefix(object __instance, ref int __result)
        {
            try
            {
                var episode = PackLinesRuntime.TakeTimerEpisode(__instance);
                if (episode.HasValue)
                {
                    __result = episode.Value;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("timer line", ex);

                // The game's own code is about to index its per-episode table with whatever is first
                // in the list; make sure that is one of its own episodes.
                try
                {
                    PackLinesRuntime.WithdrawAll(__instance);
                }
                catch (Exception inner)
                {
                    PackLinesRuntime.ReportOnce("timer line safety net", inner);
                }

                return true;
            }
        }

        /// <summary>Postfix of <c>SmallTalkSelector.Setup</c>.</summary>
        internal static void SmallTalkSetupPostfix(object __instance)
        {
            try
            {
                PackLinesRuntime.BindSmallTalk(__instance, "setup");
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk setup", ex);
            }
        }

        internal static bool LotteryPrefix(global::SmallTalkSelector __instance, global::SmallTalkSelector.PlayTiming __0,
            Game.LocationType __1, ref float __result, out bool __state) =>
            SmallTalkPick(__instance, __0, __1, ready: false, ref __result, out __state);

        internal static bool ReserveReadyPrefix(global::SmallTalkSelector __instance, Game.LocationType __0,
            ref float __result, out bool __state) =>
            SmallTalkPick(__instance, global::SmallTalkSelector.PlayTiming.Break, __0, ready: true, ref __result, out __state);

        private static bool SmallTalkPick(global::SmallTalkSelector selector, global::SmallTalkSelector.PlayTiming timing,
            Game.LocationType location, bool ready, ref float result, out bool ownsScope)
        {
            ownsScope = false;
            try
            {
                PackLinesRuntime.BeginSmallTalk(selector, location, out ownsScope);
                if (ownsScope && PackLinesRuntime.RollSmallTalk(selector, timing, ready) is float episode)
                {
                    result = episode;
                    return false;
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk pick", ex);
            }

            return true;
        }

        internal static void CanPlayReadyPrefix(object __instance, Game.LocationType __0, out bool __state)
        {
            __state = false;
            try
            {
                PackLinesRuntime.BeginSmallTalk(__instance, __0, out __state);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk availability", ex);
            }
        }

        internal static void CanPlayReadyPostfix(global::SmallTalkSelector __instance, bool __state, ref bool __result)
        {
            // During ReserveReadyTalk, a missed weight must fall back to the real cache.
            if (!__state || __result)
            {
                return;
            }

            try
            {
                __result = PackLinesRuntime.HasWeightedReadyTalk(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("weighted small-talk availability", ex);
            }
        }

        internal static void LotteryPostfix(float __result, bool __state, bool __runOriginal)
        {
            if (__state && __runOriginal)
            {
                PackLinesRuntime.AfterSmallTalkLottery(__result);
            }
        }

        internal static void SmallTalkFinalizer(bool __state)
        {
            try
            {
                PackLinesRuntime.EndSmallTalk(__state);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk pick end", ex);
            }
        }

        internal static void SmallTalkTimingPrefix(ref Master.ScenarioGroupData __0, global::SmallTalkSelector.PlayTiming __1, Game.LocationType __2)
        {
            try
            {
                if (__1 == global::SmallTalkSelector.PlayTiming.Break && __2 == Game.LocationType.Desk)
                {
                    __0 = PackLinesRuntime.BreakTimingRow(__0);
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk timing", ex);
            }
        }

        internal static void PlayableAtPostfix(string __0, Game.LocationType __1, ref bool __result)
        {
            try
            {
                if (PackLinesRuntime.PackSmallTalkPlayableAt(__0, __1) is bool playable)
                {
                    __result = playable;
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk location", ex);
            }
        }

        /// <summary>Postfix of <c>GameStartTalkSelector.GetGameStartTalk(ScenarioType)</c>: the startup greeting's pick.</summary>
        internal static void GameStartPickPostfix(Master.ScenarioType scenarioType, ref int __result)
        {
            try
            {
                __result = PackLinesGameStart.AfterPick((int)scenarioType, __result);
            }
            catch (Exception ex)
            {
                // __result is only assigned after AfterPick returned, so the game's own pick stands.
                PackLinesRuntime.ReportOnce("startup greeting", ex);
            }
        }

        /// <summary>
        /// Prefix of <c>GameStartTalkSelector.UseGameStartTalk(ScenarioType)</c>: false (skip the
        /// game's deck advance and its save) once, right after a pack line took the pick.
        /// </summary>
        internal static bool GameStartUsePrefix(Master.ScenarioType scenarioType)
        {
            try
            {
                return !PackLinesGameStart.TakeUseSkip((int)scenarioType);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("startup greeting deck", ex);
                return true;
            }
        }

        private static bool TimerPick(object instance, PackLineTimerPhase phase, bool isMyPace, Game.LocationType location,
            ref Game.ScenarioInfo result)
        {
            try
            {
                var baseDesk = PackLinesRuntime.IsBaseDesk(location, isMyPace);
                PackLinesRuntime.GateTimers(instance, baseDesk);
                if (!baseDesk)
                {
                    return true;
                }

                var arm = PackLinesFeature.TestArm;
                if (arm.Armed is PackLine armed && PackLinesRuntime.IsDeskGroup(armed)
                    && PackLinesVoiceCheck.FirstSilent(armed) is null
                    && arm.TryTake(phase, PackLinesRuntime.CurrentTimerMode(), LanguageFeature.CurrentCode, out var line)
                    && line != null)
                {
                    PackLinesRuntime.Consume(line);
                    result = new Game.ScenarioInfo((Master.ScenarioType)line.Pool.Scenario, line.Episode);
                    ConsoleEvents.File("packlines", "test", ("group", line.GroupId), ("pool", line.Pool.Id), ("episode", line.Episode));
                    return false;
                }
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("timer pick", ex);
            }

            return true;
        }

        private static void Plan(
            List<Planned> plan,
            List<string> missing,
            Type type,
            string method,
            Type[] parameters,
            string? prefix = null,
            string? postfix = null,
            string? finalizer = null)
        {
            var target = AccessTools.Method(type, method, parameters);
            var label = type.FullName + "." + method;
            if (target is null || target.DeclaringType != type)
            {
                missing.Add(label);
                return;
            }

            plan.Add(new Planned(label, target, Hook(prefix), Hook(postfix), Hook(finalizer)));
        }

        private static HarmonyMethod? Hook(string? name) =>
            name is null ? null : new HarmonyMethod(typeof(PackLinesPatches), name);

        private sealed class Planned
        {
            internal Planned(string label, MethodBase target, HarmonyMethod? prefix, HarmonyMethod? postfix, HarmonyMethod? finalizer)
            {
                Label = label;
                Target = target;
                Prefix = prefix;
                Postfix = postfix;
                Finalizer = finalizer;
            }

            internal string Label { get; }

            internal MethodBase Target { get; }

            internal HarmonyMethod? Prefix { get; }

            internal HarmonyMethod? Postfix { get; }

            internal HarmonyMethod? Finalizer { get; }
        }
    }
}
