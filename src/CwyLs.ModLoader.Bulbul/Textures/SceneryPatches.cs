using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Re-applies pack textures and material property rules after the game changes a decoration.
    /// <para>
    /// <c>DecorationModelChangerCommon.Apply</c> points the desk's renderers at another shared material
    /// and sets its <c>mainTexture</c>; <c>DecorationModelChangerMatTexture.Apply</c> sets
    /// <c>_material.mainTexture</c> on a shared material. Either one overwrites a texture rule on that
    /// material, and a swapped-in material may never have been part of the pass. All three seams
    /// are synchronous and only <em>arm</em> a deferred pass (<see cref="TextureHost.Request"/>), which
    /// coalesces: one decoration change that runs several changers is one pass a frame later.
    /// </para>
    /// <para>Same pattern as <see cref="CostumePatches"/>: a missing seam is logged and skipped.</para>
    /// </summary>
    internal static class SceneryPatches
    {
        /// <summary>Harmony id for these patches.</summary>
        internal const string HarmonyId = "cwyls.modloader.bulbul.scenery";

        /// <summary>Trigger name prefix for the log line.</summary>
        internal const string Trigger = "decoration change";

        private static readonly object Gate = new object();

        private static Harmony? _harmony;

        /// <summary>How many of the three seams were patched.</summary>
        internal static int Patched { get; private set; }

        /// <summary>What could not be patched.</summary>
        internal static string Failures { get; private set; } = string.Empty;

        /// <summary>Patches the decoration seams. Idempotent.</summary>
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

            var failures = new List<string>();
            var postfix = new HarmonyMethod(
                typeof(SceneryPatches).GetMethod(nameof(OnDecorationChanged), BindingFlags.NonPublic | BindingFlags.Static));

            Patch(typeof(global::DecorationService), SceneryPatchTargets.ChangeDecorationMethod, postfix, failures);
            Patch(typeof(global::Bulbul.DecorationModelChangerMatTexture), SceneryPatchTargets.ApplyMethod, postfix, failures);
            Patch(typeof(global::Bulbul.DecorationModelChangerCommon), SceneryPatchTargets.ApplyMethod, postfix, failures);

            Failures = string.Join("; ", failures.ToArray());
            CwyLsModLoader.Log.LogDebug(
                $"TEXTURES: decoration seams patched {Patched}/3" + (failures.Count == 0 ? string.Empty : " - MISSING: " + Failures));
        }

        private static void Patch(Type type, string method, HarmonyMethod postfix, List<string> failures)
        {
            var name = type.Name + "." + method;
            try
            {
                var target = AccessTools.DeclaredMethod(type, method);
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
                // A seam that will not patch costs the decoration re-apply; scene and window passes still run.
                failures.Add(name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Postfix body: arms a deferred pass and returns.</summary>
        private static void OnDecorationChanged(MethodBase __originalMethod)
        {
            try
            {
                if (!SceneryFeature.Enabled || !TextureFeature.HasApplied)
                {
                    return;
                }

                TextureHost.Request(Trigger + " (" + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + ")");
            }
            catch (Exception ex)
            {
                // A throw here would propagate into the game's decoration code.
                CwyLsModLoader.Log.LogError($"TEXTURES: decoration postfix failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
