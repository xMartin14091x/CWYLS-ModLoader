using System;
using System.Collections.Generic;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Data;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The change-detection half of the bus: two instance-capture postfixes on the save manager's
    /// load path, and a listener on the adapter's shared save hook that maps each
    /// <c>Save*</c>/<c>Delete*</c> call to a <see cref="GameDataSection"/> and publishes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The data bus does not patch the save family itself. <c>SaveDataHook</c> (in the game
    /// adapter) puts one postfix on each method and both buses listen to it, so a save runs one CWY
    /// postfix instead of two. The method-to-section table is Core's
    /// <see cref="SaveMethodRules.TryDataSection"/>, unit-tested against the game's method list.
    /// </para>
    /// <para>
    /// A game update that adds a save method is still covered automatically (reported as
    /// <see cref="GameDataSection.Unknown"/> until the table names it) and one that removes a method
    /// is a logged miss rather than a crash. These patches are static, and so is the Harmony instance
    /// that owns them - the plugin component itself can be destroyed moments after startup.
    /// </para>
    /// </remarks>
    internal static class SaveDataPatches
    {
        private static readonly HashSet<string> UnmappedReported = new HashSet<string>(StringComparer.Ordinal);

        private static IDisposable? _subscription;

        /// <summary>How many save/delete methods carry the shared hook's postfix.</summary>
        internal static int PatchedCount => SaveDataHook.PatchedCount;

        /// <summary>Names the reflection scan found but Harmony refused to patch.</summary>
        internal static IReadOnlyList<string> Failures => SaveDataHook.Failures;

        /// <summary>
        /// Applies the two instance-capture hooks and listens to the shared save hook, installing it
        /// first if the event bus did not (it normally has). Safe to call once; the plugin does so
        /// from Awake.
        /// </summary>
        /// <param name="harmony">The data plugin's Harmony instance.</param>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static void Apply(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            var type = typeof(Game.SaveDataManager);
            PatchInstanceCapture(harmony, type);

            // Idempotent: the event bus installed it in the adapter's Awake unless it failed to start.
            SaveDataHook.Install(harmony);
            _subscription ??= SaveDataHook.Subscribe(OnSaved);
        }

        private static void PatchInstanceCapture(Harmony harmony, Type type)
        {
            // Load() is async: this postfix runs when the method hands back its UniTask, so it is
            // the earliest point the instance exists - but not a signal that data has loaded.
            TryPatch(harmony, type, "Load", nameof(CapturePostfix));

            // LoadAllPlatformSetting() is called from exactly one place, the middle of Load()'s
            // synchronous block, after player/pomodoro/todo/habit/note/setting are in memory.
            // That makes it the readiness signal. (LoadSetting() would be wrong: EntryBehavior
            // calls it long before any save data is read.)
            TryPatch(harmony, type, "LoadAllPlatformSetting", nameof(LoadedPostfix));
        }

        private static void TryPatch(Harmony harmony, Type type, string methodName, string postfixName)
        {
            try
            {
                var target = AccessTools.Method(type, methodName);
                if (target is null)
                {
                    CwyLsModLoader.Log.LogError(
                        $"DATA PATCH TARGET MISSING: {type.FullName}.{methodName} - the bus will fall back " +
                        "to SaveDataManager.Instance and the RoomScene load for readiness");
                    return;
                }

                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SaveDataPatches), postfixName));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA could not patch {type.Name}.{methodName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Harmony postfix: remembers the instance the game built.</summary>
        internal static void CapturePostfix(Game.SaveDataManager __instance) =>
            SaveDataAccess.CaptureFromPatch(__instance);

        /// <summary>Harmony postfix: remembers the instance and marks the save data loaded.</summary>
        internal static void LoadedPostfix(Game.SaveDataManager __instance)
        {
            SaveDataAccess.CaptureFromPatch(__instance);
            SaveDataAccess.MarkLoaded();
        }

        /// <summary>
        /// The data bus's listener on the shared save hook: publishes the section the method
        /// touches. Runs inside the game's save call; never throws into it.
        /// </summary>
        /// <param name="call">The hooked call.</param>
        private static void OnSaved(SaveCall call)
        {
            try
            {
                SaveDataAccess.CaptureFromPatch(call.Instance);
                GameData.Raise(SectionFor(call.MethodName), "save:" + call.MethodName);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA change notification failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static GameDataSection SectionFor(string methodName)
        {
            if (SaveMethodRules.TryDataSection(methodName, out var section))
            {
                return section;
            }

            lock (UnmappedReported)
            {
                if (UnmappedReported.Add(methodName))
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"DATA {methodName} is not in the section table; reported as Unknown " +
                        "(a game update probably added it)");
                }
            }

            return GameDataSection.Unknown;
        }
    }
}
