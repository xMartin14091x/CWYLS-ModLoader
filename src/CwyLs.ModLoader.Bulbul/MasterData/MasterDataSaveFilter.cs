using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// The one save filter for pack ids (<c>PackSaveHoldBack</c>): a prefix and a finalizer on
    /// <c>SaveDataManager.SaveScenarioProgressData</c>, installed by master data whatever the pack-lines
    /// feature does. Pack lines join it; they have no save patch of their own.
    /// </summary>
    internal static class MasterDataSaveFilter
    {
        /// <summary>What was patched, or why not. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>
        /// Checks the targets and patches. Either way the outcome is recorded on
        /// <see cref="MasterDataFeature.SaveHoldBack"/>; a failure is logged as an error.
        /// </summary>
        internal static bool Apply(Harmony harmony)
        {
            var holdBack = MasterDataFeature.SaveHoldBack;
            if (holdBack.Installed)
            {
                return true;
            }

            string problem;
            var report = PatchTargetChecker.Check(MasterDataPatchTargets.All, new ReflectionMemberResolver());
            var target = report.AllFound
                ? AccessTools.Method(typeof(Game.SaveDataManager), MasterDataPatchTargets.SaveScenarioProgressMethod, Type.EmptyTypes)
                : null;
            if (!report.AllFound)
            {
                problem = "PATCH TARGET MISSING: " + report.MissingResults[0].Target.Id + " (" + report.MissingResults[0].Detail + ")";
            }
            else if (target is null)
            {
                problem = "PATCH TARGET MISSING: Bulbul.SaveDataManager.SaveScenarioProgressData()";
            }
            else
            {
                try
                {
                    harmony.Patch(
                        target,
                        prefix: new HarmonyMethod(typeof(MasterDataSaveFilter), nameof(SavePrefix)),
                        finalizer: new HarmonyMethod(typeof(MasterDataSaveFilter), nameof(SaveFinalizer)));
                    holdBack.MarkInstalled();
                    Detail = "prefix + finalizer on Bulbul.SaveDataManager.SaveScenarioProgressData()";
                    return true;
                }
                catch (Exception ex)
                {
                    problem = "patching Bulbul.SaveDataManager.SaveScenarioProgressData() failed: " + ex.GetType().Name + ": " + ex.Message;
                }
            }

            holdBack.MarkMissing(problem);
            Detail = problem;
            CwyLsModLoader.Log.LogError(
                "MASTERDATA the save filter is OFF (" + problem + "): nothing could keep a pack id out of the save, so pack "
                + "small talk is NOT injected and pack lines stay off");
            ConsoleEvents.File("masterdata", "saveFilterOff", ("reason", problem));
            return false;
        }

        private static void SavePrefix()
        {
            try
            {
                var holdBack = MasterDataFeature.SaveHoldBack;
                var held = holdBack.HoldBack(PlayedIds());
                if (held.Count > 0 && !holdBack.PackLinesJoined)
                {
                    // With pack lines on, their callback logs packlines/saveFilter for the same save.
                    ConsoleEvents.File("masterdata", "saveFilter", ("heldBack", held.Count));
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("MASTERDATA save filter failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void SaveFinalizer()
        {
            try
            {
                MasterDataFeature.SaveHoldBack.Restore(PlayedIds());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("MASTERDATA save filter restore failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The game's played scene-group list, or null when the save is not loaded.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static List<string>? PlayedIds() =>
            Game.SaveDataManager.HasInstance ? Game.SaveDataManager.Instance.ScenarioProgressData?.PlayedScenarioGroupIDs : null;
    }
}
