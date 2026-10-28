using System;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// The two Harmony prefixes of the script runtime. Each body is a thin, never-throwing
    /// call; on any failure the game's own method runs as if we were not there.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Game member (1.17.3)</term><description>Patch and why</description></listheader>
    ///   <item><term><c>ScenarioReader.ReadSentence(int)</c> (private <c>async UniTaskVoid</c>)</term>
    ///   <description>Prefix: a row whose command is 5000-5999 falls through the method's <c>switch</c>
    ///   and the scene stalls; the prefix runs it instead and returns false. The patched
    ///   method is the state-machine stub, so skipping it starts no state machine; its result is the
    ///   default <c>UniTaskVoid</c>, whose <c>Forget()</c> does nothing.</description></item>
    ///   <item><term><c>FacilityStory.SaveScenarioPlayedLog()</c></term>
    ///   <description>Prefix: a pack scene (<c>ScenarioSaveRule</c>) is not logged as played, so the
    ///   game's save never records it.</description></item>
    /// </list>
    /// Independent of the language voice sets' <c>StartReady</c> postfix and the pack-lines <c>SaveScenarioProgressData</c>
    /// filter: different methods, own Harmony id.
    /// </remarks>
    internal static class ScenarioPatches
    {
        /// <summary>Harmony id of these patches.</summary>
        internal const string HarmonyId = BulbulPlugin.PluginGuid + ".script";

        /// <summary>True once the <c>ReadSentence</c> prefix is in place.</summary>
        internal static bool SeamApplied { get; private set; }

        /// <summary>True once the <c>SaveScenarioPlayedLog</c> prefix is in place.</summary>
        internal static bool GuardApplied { get; private set; }

        /// <summary>Patches <c>ReadSentence</c>.</summary>
        /// <param name="harmony">The feature's Harmony instance.</param>
        /// <param name="problem">Why not.</param>
        internal static bool ApplySeam(Harmony harmony, out string problem) =>
            SeamApplied = Patch(harmony, typeof(Game.ScenarioReader), "ReadSentence", new[] { typeof(int) }, nameof(ReadSentencePrefix), out problem);

        /// <summary>Patches <c>SaveScenarioPlayedLog</c>.</summary>
        /// <param name="harmony">The feature's Harmony instance.</param>
        /// <param name="problem">Why not.</param>
        internal static bool ApplyGuard(Harmony harmony, out string problem) =>
            GuardApplied = Patch(harmony, typeof(Game.FacilityStory), "SaveScenarioPlayedLog", Type.EmptyTypes, nameof(SavePlayedLogPrefix), out problem);

        /// <summary>Prefix of <c>ScenarioReader.ReadSentence(int)</c>; <c>__0</c> is the row index.</summary>
        internal static bool ReadSentencePrefix(Game.ScenarioReader __instance, int __0)
        {
            try
            {
                return ScenarioRuntime.OnReadSentence(__instance, __0);
            }
            catch (Exception ex)
            {
                // Before the row ran: the game's method runs and, for a script row, stalls - which is
                // what it would have done without us. Loud, because a scene is now stuck.
                ScenarioRuntime.WarnAlways("running a script row failed (" + ex.GetType().Name + ": " + ex.Message + "); the scene may stop here");
                return true;
            }
        }

        /// <summary>Prefix of <c>FacilityStory.SaveScenarioPlayedLog()</c>.</summary>
        internal static bool SavePlayedLogPrefix(Game.FacilityStory __instance)
        {
            try
            {
                return !ScenarioSaveGuard.ShouldSkip(__instance);
            }
            catch (Exception ex)
            {
                ScenarioRuntime.WarnAlways("the save-log guard failed (" + ex.GetType().Name + ": " + ex.Message + "); the game logs the scene as usual");
                return true;
            }
        }

        private static bool Patch(Harmony harmony, Type type, string name, Type[] parameters, string prefix, out string problem)
        {
            var target = AccessTools.Method(type, name, parameters);
            if (target is null)
            {
                problem = type.FullName + "." + name + " not found";
                return false;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ScenarioPatches), prefix));
            }
            catch (Exception ex)
            {
                problem = "patching " + type.Name + "." + name + " failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            ConsoleEvents.File(ScenarioRuntime.Owner, "patched", ("method", type.Name + "." + name));
            problem = string.Empty;
            return true;
        }
    }
}
