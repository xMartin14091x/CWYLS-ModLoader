using System;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// The one Harmony patch this feature needs: a postfix on
    /// <c>Bulbul.MasterDataLoader.Load()</c> (game 1.17.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This postfix does not mean the data is loaded.</b> <c>Load</c> is an
    /// <c>async UniTask</c> method, so the compiler turns its body into a state machine and the
    /// method itself returns as soon as the first <c>await</c> is reached - the one on
    /// <c>Addressables.LoadAssetsAsync</c>. A Harmony postfix therefore runs <em>before</em>
    /// <c>NovelMasterList</c>, <c>LocalizationList</c> and <c>ScenarioGroupMasterList</c> have
    /// been assigned.
    /// </para>
    /// <para>
    /// So the postfix is used for exactly two things, both of which are correct at that moment:
    /// it captures the loader instance (there is only one, created by VContainer and resolved in
    /// <c>BootBehavior.LoadAsync</c>), and it tells the feature that a fresh load has begun so a
    /// previous injection is forgotten. The actual injection waits for
    /// <c>MasterDataLoader.IsLoaded</c> (line 87, set true at line 246 after all three assignments)
    /// and is driven by <see cref="MasterDataHost"/>.
    /// </para>
    /// </remarks>
    internal static class MasterDataPatches
    {
        private static Game.MasterDataLoader? _loader;

        /// <summary>True once the postfix is in place.</summary>
        internal static bool Applied { get; private set; }

        /// <summary>What was patched, or why it could not be. Never null.</summary>
        internal static string Detail { get; private set; } = "not attempted";

        /// <summary>How many times the game has started a master-data load since startup.</summary>
        internal static int LoadsSeen { get; private set; }

        /// <summary>
        /// The loader instance the postfix captured, or null when <c>Load</c> has not been called
        /// yet (or the patch never applied).
        /// </summary>
        internal static Game.MasterDataLoader? Loader => _loader;

        /// <summary>
        /// Applies the postfix. Idempotent: a second call reports the first result rather than
        /// patching twice.
        /// </summary>
        /// <param name="harmony">The static Harmony instance that will own the patch.</param>
        /// <returns>True when the postfix is applied.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static bool Apply(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            if (Applied)
            {
                return true;
            }

            var target = AccessTools.Method(typeof(Game.MasterDataLoader), nameof(Game.MasterDataLoader.Load));
            if (target is null)
            {
                Detail = "PATCH TARGET MISSING: Bulbul.MasterDataLoader.Load()";
                CwyLsModLoader.Log.LogError("MASTERDATA " + Detail);
                return false;
            }

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(MasterDataPatches), nameof(LoadPostfix)));

                Applied = true;
                Detail = "postfixed Bulbul.MasterDataLoader.Load() to capture the loader instance";
                return true;
            }
            catch (Exception ex)
            {
                Detail = "could not patch Bulbul.MasterDataLoader.Load(): "
                         + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError("MASTERDATA " + Detail);
                return false;
            }
        }

        /// <summary>
        /// Forgets the captured loader. For the smoke checks and for a clean re-arm; the next
        /// <c>Load</c>, or the project-scope fallback, finds it again.
        /// </summary>
        internal static void Forget() => _loader = null;

        /// <summary>
        /// Harmony postfix on <c>Load</c>. Runs at the method's first <c>await</c>, not at
        /// completion - see the remarks on this class.
        /// </summary>
        /// <param name="__instance">The loader the game is loading into.</param>
        private static void LoadPostfix(Game.MasterDataLoader __instance)
        {
            try
            {
                if (__instance is null)
                {
                    return;
                }

                _loader = __instance;
                LoadsSeen++;

                // A second load means the previous collections are being replaced, so whatever we
                // injected into them is gone and has to go in again.
                MasterDataFeature.OnLoadStarted(__instance, LoadsSeen);
            }
            catch (Exception ex)
            {
                // A throwing postfix would take the game's master-data load down with it.
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA Load postfix failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
