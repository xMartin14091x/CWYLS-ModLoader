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
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Game member (1.17.3)</term><description>Patch and why</description></listheader>
    ///   <item><term><c>RandomList.InitList</c></term><description>Postfix: a bound list started a new cycle, so its pack episodes are owed again.</description></item>
    ///   <item><term><c>ReactionTalkSelector.Setup</c></term><description>Postfix: bind the click, self-talk, answer-choice, clock and worked-time lists.</description></item>
    ///   <item><term><c>FacilityClickHeroine.OneWord(ScenarioType)</c>, <c>AnswerChoiceStory()</c></term><description>Prefix: every click/self-talk/answer-choice pick happens inside these two, so the gate runs here, at pick time.</description></item>
    ///   <item><term><c>ReactionTalkSelector.GetNextBreakHeroineSelfShortTalkSelection</c></term><description>Postfix: a pack clock line counts toward its slot's once-a-day check.</description></item>
    ///   <item><term><c>PomodoroVoiceSelector.Setup</c></term><description>Postfix: bind the timer lists.</description></item>
    ///   <item><term><c>PomodoroVoiceSelector.TakeNext{WorkStart,BreakStart,Finish}Voice</c></term><description>Prefix: the gate, and an armed <c>lines test</c> timer line.</description></item>
    ///   <item><term>the eight two-list <c>*VoiceSelector.TakeNextVoice</c></term><description>Prefix: answer for a pack episode before the game indexes its per-episode table with it (e.g. <c>NormalWorkStartVoiceSelector</c>).</description></item>
    ///   <item><term><c>SmallTalkSelector.Setup</c>, <c>LotterySmallTalk</c></term><description>Postfix: take pack lines out of the lottery cache; prefix: put them back exactly while their language is shown.</description></item>
    ///   <item><term><c>SaveDataManager.SaveScenarioProgressData</c></term><description>Prefix + finalizer: pack-line ids never reach the save file.</description></item>
    /// </list>
    /// </remarks>
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
            Plan(plan, missing, typeof(global::ReactionTalkSelector), "Setup", Type.EmptyTypes, postfix: nameof(ReactionSetupPostfix));
            Plan(plan, missing, typeof(Game.FacilityClickHeroine), "OneWord", new[] { typeof(Master.ScenarioType) }, prefix: nameof(ClickPickPrefix));
            Plan(plan, missing, typeof(Game.FacilityClickHeroine), "AnswerChoiceStory", Type.EmptyTypes, prefix: nameof(ClickPickPrefix));
            Plan(plan, missing, typeof(global::ReactionTalkSelector), "GetNextBreakHeroineSelfShortTalkSelection",
                new[] { typeof(Game.TimeOfDayProvider.TimeOfDayType) }, postfix: nameof(SelfTalkPickPostfix));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "Setup", Type.EmptyTypes, postfix: nameof(TimerSetupPostfix));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextWorkStartVoice", Type.EmptyTypes, prefix: nameof(WorkStartPrefix));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextBreakStartVoice", Type.EmptyTypes, prefix: nameof(BreakStartPrefix));
            Plan(plan, missing, typeof(global::PomodoroVoiceSelector), "TakeNextFinishVoice", Type.EmptyTypes, prefix: nameof(FinishPrefix));

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

            Plan(plan, missing, typeof(global::SmallTalkSelector), "Setup", Type.EmptyTypes, postfix: nameof(SmallTalkSetupPostfix));
            Plan(plan, missing, typeof(global::SmallTalkSelector), "LotterySmallTalk",
                new[] { typeof(global::SmallTalkSelector.PlayTiming) }, prefix: nameof(LotteryPrefix));
            Plan(plan, missing, typeof(Game.SaveDataManager), "SaveScenarioProgressData", Type.EmptyTypes,
                prefix: nameof(SaveProgressPrefix), finalizer: nameof(SaveProgressFinalizer));

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

        /// <summary>Postfix of <c>ReactionTalkSelector.Setup</c>.</summary>
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

        /// <summary>Prefix of <c>FacilityClickHeroine.OneWord(ScenarioType)</c> and <c>AnswerChoiceStory()</c>.</summary>
        internal static void ClickPickPrefix(object __instance)
        {
            try
            {
                PackLinesRuntime.GateClickFacility(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("click pick", ex);
            }
        }

        /// <summary>Postfix of <c>ReactionTalkSelector.GetNextBreakHeroineSelfShortTalkSelection</c>.</summary>
        internal static void SelfTalkPickPostfix(object __instance, (Master.ScenarioType scenarioType, int episodeNumber) __result)
        {
            try
            {
                PackLinesRuntime.AfterSelfTalkPick(__instance, (int)__result.scenarioType, __result.episodeNumber);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("self-talk pick", ex);
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
        internal static bool WorkStartPrefix(object __instance, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.WorkStart, ref __result);

        /// <summary>Prefix of <c>PomodoroVoiceSelector.TakeNextBreakStartVoice</c>.</summary>
        internal static bool BreakStartPrefix(object __instance, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.BreakStart, ref __result);

        /// <summary>Prefix of <c>PomodoroVoiceSelector.TakeNextFinishVoice</c>.</summary>
        internal static bool FinishPrefix(object __instance, ref Game.ScenarioInfo __result) =>
            TimerPick(__instance, PackLineTimerPhase.Finish, ref __result);

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

        /// <summary>Prefix of <c>SmallTalkSelector.LotterySmallTalk</c>.</summary>
        internal static void LotteryPrefix(object __instance)
        {
            try
            {
                PackLinesRuntime.GateSmallTalk(__instance);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("small-talk lottery", ex);
            }
        }

        /// <summary>Prefix of <c>SaveDataManager.SaveScenarioProgressData</c>.</summary>
        internal static void SaveProgressPrefix()
        {
            try
            {
                PackLinesRuntime.HoldBackPlayedIds();
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("save filter", ex);
            }
        }

        /// <summary>Finalizer of <c>SaveDataManager.SaveScenarioProgressData</c>: runs after the write, thrown or not.</summary>
        internal static void SaveProgressFinalizer()
        {
            try
            {
                PackLinesRuntime.RestorePlayedIds();
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("save filter restore", ex);
            }
        }

        private static bool TimerPick(object instance, PackLineTimerPhase phase, ref Game.ScenarioInfo result)
        {
            try
            {
                PackLinesRuntime.GateTimers(instance);

                var arm = PackLinesFeature.TestArm;
                if (arm.Armed != null
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
