using System;
using System.Reflection;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Grading checks for the final in-game pass. Quiet: URP was found; when a preset is
    /// applied, our volume exists, is global, outranks the game's volumes, sits on a layer the main
    /// camera's volume mask includes, and the camera renders post-processing. Loud: <c>grade ab</c>
    /// flips the weight to 0 and back.
    /// </summary>
    internal static class GradingSmokeChecks
    {
        /// <summary>Phase label of the checks.</summary>
        internal const string Phase = "D";

        internal const string BackendId = "d.grading.backend";
        internal const string VolumeId = "d.grading.volume";
        internal const string CameraId = "d.grading.camera";
        internal const string AbId = "d.grading.ab";

        /// <summary>Registers the checks.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(BackendId, Phase, Backend));
            SmokeRegistry.Register(new SmokeCheck(VolumeId, Phase, Volume));
            SmokeRegistry.Register(new SmokeCheck(CameraId, Phase, Camera));

            // Loud: the picture flips to the game's own look for a moment.
            SmokeRegistry.Register(new SmokeCheck(AbId, Phase, Ab), loud: true);
        }

        private static SmokeCheckResult Backend() =>
            GradingFeature.IsAvailable
                ? SmokeCheckResult.Pass(BackendId, "URP volume machinery bound; " + GradingFeature.Status)
                : SmokeCheckResult.Fail(BackendId, GradingFeature.UnavailableReason ?? "not initialised");

        private static SmokeCheckResult Volume()
        {
            if (GradingFeature.ActivePreset is null)
            {
                return SmokeCheckResult.Pass(VolumeId, "no preset applied (vacuous pass); " + GradingFeature.Status);
            }

            var state = GradingFeature.VolumeState;
            var go = GameObject.Find(GradingVolume.ObjectName);
            if (go == null)
            {
                return SmokeCheckResult.Fail(VolumeId, "preset " + GradingFeature.ActivePreset + " applied but no " + GradingVolume.ObjectName + " object; " + state);
            }

            var plan = GradingFeature.LastPlan;
            if (plan is null || plan.IsEmpty)
            {
                return SmokeCheckResult.Fail(VolumeId, "the applied preset rendered nothing; " + state);
            }

            return SmokeCheckResult.Pass(VolumeId, GradingFeature.LastSummary + "; " + state);
        }

        private static SmokeCheckResult Camera()
        {
            var camera = UnityEngine.Camera.main;
            if (camera == null)
            {
                return SmokeCheckResult.NotYetObserved(CameraId, "no main camera yet (open the room first)");
            }

            // UniversalAdditionalCameraData, by reflection like the rest of the grading layer.
            Component? data = null;
            foreach (var component in camera.GetComponents<Component>())
            {
                if (component != null && component.GetType().Name == "UniversalAdditionalCameraData")
                {
                    data = component;
                    break;
                }
            }

            if (data == null)
            {
                return SmokeCheckResult.Fail(CameraId, camera.name + " has no UniversalAdditionalCameraData");
            }

            try
            {
                var type = data.GetType();
                var post = (bool)type.GetProperty("renderPostProcessing", BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);
                var mask = (LayerMask)type.GetProperty("volumeLayerMask", BindingFlags.Public | BindingFlags.Instance)!.GetValue(data);
                var onLayer = (mask.value & (1 << GradingVolume.Layer)) != 0;

                var detail = camera.name + " renderPostProcessing=" + post + " volumeLayerMask=" + mask.value + " (our layer " + GradingVolume.Layer + (onLayer ? " included" : " NOT included") + ")";
                return post && onLayer ? SmokeCheckResult.Pass(CameraId, detail) : SmokeCheckResult.Fail(CameraId, detail);
            }
            catch (Exception ex)
            {
                return SmokeCheckResult.Fail(CameraId, "could not read the camera's URP data: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static SmokeCheckResult Ab()
        {
            if (GradingFeature.ActivePreset is null)
            {
                return SmokeCheckResult.Pass(AbId, "no preset applied (vacuous pass); apply one with 'grade apply warm-film' and rerun loud");
            }

            var before = GradingFeature.ShowingOriginal;
            try
            {
                var original = GradingFeature.ShowOriginal(true);
                var originalState = GradingFeature.VolumeState;
                var graded = GradingFeature.ShowOriginal(false);
                var gradedState = GradingFeature.VolumeState;

                var ok = original.Ok && graded.Ok && originalState.Contains("weight=0 ") && gradedState.Contains("weight=1 ");
                var detail = "original: " + originalState + " | graded: " + gradedState;
                return ok ? SmokeCheckResult.Pass(AbId, detail) : SmokeCheckResult.Fail(AbId, detail);
            }
            finally
            {
                GradingFeature.ShowOriginal(before);
            }
        }
    }
}
