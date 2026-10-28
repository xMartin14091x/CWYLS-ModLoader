using System;
using System.Reflection;
using HarmonyLib;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Re-applies the pack textures whenever the game changes Satone's costume.
    /// <para>
    /// Without this, a costume change silently undoes the texture feature: <c>CostumeChangeService</c> destroys the
    /// previous costume's objects and instantiates a new prefab, so every material instance we wrote
    /// is gone and the new renderers are on materials we have never seen.
    /// </para>
    /// <para>
    /// The seams, all in the <c>Bulbul</c> namespace (game 1.17.3) and all
    /// <b>synchronous</b> - which is what makes them patchable. <c>TryChangeModel</c> itself is an
    /// <c>async UniTask&lt;bool&gt;</c>, so a postfix on it would fire when the state machine is
    /// created rather than when the costume exists; these four fire at the right moments instead:
    /// </para>
    /// <list type="table">
    ///   <item>
    ///     <term><c>CostumeChangeService.AttachCostumeBonesToCharacter</c></term>
    ///     <description>The last step of the non-default branch of <c>TryChangeModel</c>, after
    ///     <c>InstantiateAsync</c> has completed and the prefab has been re-parented. This is the
    ///     first moment the new renderers exist in the hierarchy.</description>
    ///   </item>
    ///   <item>
    ///     <term><c>CostumeChangeService.ActivateDefaultCostume</c></term>
    ///     <description>The other branch: the five default objects (<c>Body</c>, <c>Bottom1</c>,
    ///     <c>Paka1</c>, <c>Socks</c>, <c>Top1</c>) come back on.</description>
    ///   </item>
    ///   <item>
    ///     <term><c>CostumeChangeService.TryChangeSkin</c></term>
    ///     <description>The last thing <c>TryChangeCostume</c> does, after the model swap has been
    ///     awaited and the badge set. The reliable "costume change finished" signal.</description>
    ///   </item>
    ///   <item>
    ///     <term><c>CostumeSkinChanger.ChangeSkin</c></term>
    ///     <description>Sets the renderer's shared material - the shirt renderer
    ///     (<c>Top3</c> on the Polo prefab) being pointed at a different material asset from
    ///     <c>CostumeSkinDB</c>. A material rule for the new material has to be re-applied here.</description>
    ///   </item>
    /// </list>
    /// <para>
    /// Every patch only <em>arms</em> a pass through <see cref="TextureHost.Request"/>; none of them
    /// walks the scene inside the game's own call stack.
    /// </para>
    /// </summary>
    internal static class CostumePatches
    {
        /// <summary>Harmony id for these patches; distinct from any other plugin's.</summary>
        internal const string HarmonyId = "cwyls.modloader.bulbul.textures";

        /// <summary>Trigger name prefix, so the log says a costume change caused the pass.</summary>
        internal const string Trigger = "costume change";

        private static readonly object Gate = new object();

        private static Harmony? _harmony;

        /// <summary>How many of the four seams were patched. 0 means costume changes will not re-apply.</summary>
        internal static int Patched { get; private set; }

        /// <summary>What could not be patched, for the startup log and the smoke detail.</summary>
        internal static string Failures { get; private set; } = string.Empty;

        /// <summary>
        /// Patches the costume seams. Idempotent: a second call does nothing.
        /// </summary>
        /// <remarks>
        /// A missing seam is logged and skipped, never thrown: a game update that renames one of
        /// these must cost us the re-apply, not the whole texture feature.
        /// </remarks>
        internal static void Apply()
        {
            lock (Gate)
            {
                if (_harmony != null)
                {
                    return;
                }

                _harmony = new Harmony(HarmonyId);
            }

            var failures = new System.Collections.Generic.List<string>();
            var postfix = new HarmonyMethod(
                typeof(CostumePatches).GetMethod(nameof(OnCostumeChanged), BindingFlags.NonPublic | BindingFlags.Static));

            // The same four members CostumePatchTargets declares, so the startup check and the
            // patches can never name different methods.
            Patch(typeof(global::Bulbul.CostumeChangeService), CostumePatchTargets.AttachBonesMethod, postfix, failures);
            Patch(typeof(global::Bulbul.CostumeChangeService), CostumePatchTargets.ActivateDefaultMethod, postfix, failures);
            Patch(typeof(global::Bulbul.CostumeChangeService), CostumePatchTargets.TryChangeSkinMethod, postfix, failures);
            Patch(typeof(global::Bulbul.CostumeSkinChanger), CostumePatchTargets.ChangeSkinMethod, postfix, failures);

            Failures = string.Join("; ", failures.ToArray());

            CwyLsModLoader.Log.LogDebug(
                $"TEXTURES: costume seams patched {Patched}/4" +
                (failures.Count == 0 ? string.Empty : " - MISSING: " + Failures));
        }

        private static void Patch(Type type, string method, HarmonyMethod postfix, System.Collections.Generic.List<string> failures)
        {
            var name = type.Name + "." + method;

            try
            {
                var target = AccessTools.Method(type, method);
                if (target is null)
                {
                    failures.Add(name + " not found");
                    return;
                }

                _harmony!.Patch(target, postfix: postfix);
                Patched++;
            }
            catch (Exception ex)
            {
                // A seam that will not patch costs us one trigger; the +2 s / +10 s passes and the
                // scene trigger still run, so the feature degrades rather than breaks.
                failures.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Postfix body for all four seams. Arms a deferred pass and returns immediately - the
        /// game is mid-costume-change and must not be made to wait for a scene walk.
        /// </summary>
        private static void OnCostumeChanged(MethodBase __originalMethod)
        {
            try
            {
                TextureHost.Request(Trigger + " (" + __originalMethod.Name + ")");
            }
            catch (Exception ex)
            {
                // A throw here would propagate into the game's costume code.
                CwyLsModLoader.Log.LogError(
                    $"TEXTURES: costume postfix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
