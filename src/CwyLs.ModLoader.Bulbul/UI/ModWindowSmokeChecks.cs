using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Core.Ui;
using CwyLs.ModLoader.Mods;
using TMPro;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The mod windows' in-game evidence. Quiet checks build nothing visible: the backend and the Escape patch
    /// are in place, every mod window is named and parented as it should be, and Thai renders in a
    /// mod window's font with every glyph present and every stacked tone mark above its vowel (an
    /// invisible label, destroyed at once). The loud check opens the demo window for the eyes.
    /// </summary>
    internal static class ModWindowSmokeChecks
    {
        /// <summary>Phase label the checks are reported under.</summary>
        internal const string Phase = "S";

        /// <summary>Backend, feature switch, Escape patch, game assets.</summary>
        internal const string BackendId = "s.ui.backend";

        /// <summary>Every live mod window: object name, parent canvas, unique key.</summary>
        internal const string RegistryId = "s.ui.registry";

        /// <summary>Thai glyphs and tone-mark placement in the windows' font.</summary>
        internal const string ThaiId = "s.ui.thai";

        /// <summary>Opens the demo window (loud).</summary>
        internal const string DemoId = "s.ui.demo";

        /// <summary>The fonts Thai must come from (the game's own, with the pack's mark records).</summary>
        internal static readonly string[] ThaiFonts = { "Base_TH", "Dynamic_TH" };

        private const string RoomScene = "RoomScene";

        /// <summary>Registers the checks. Idempotent.</summary>
        internal static void Register()
        {
            SmokeRegistry.Register(new SmokeCheck(BackendId, Phase, Backend));
            SmokeRegistry.Register(new SmokeCheck(RegistryId, Phase, Registry));
            SmokeRegistry.Register(new SmokeCheck(ThaiId, Phase, Thai));
            SmokeRegistry.Register(new SmokeCheck(DemoId, Phase, Demo), loud: true);
        }

        private static SmokeCheckResult Backend()
        {
            if (!ModWindows.Enabled)
            {
                return SmokeCheckResult.Skipped(BackendId, ModWindows.Status);
            }

            if (ModFacetBackends.Get<IModWindowBackend>() == null)
            {
                return SmokeCheckResult.Fail(BackendId, "no window backend installed: helper.Ui.CreateWindow refuses every spec");
            }

            if (!global::CwyLs.ModLoader.Bulbul.Console.ConsoleEscapePatch.Applied)
            {
                return SmokeCheckResult.Fail(
                    BackendId, "the Escape patch on SettingService.ChangeWindowMode is not applied: Escape in a mod window also leaves fullscreen");
            }

            var assets = GameStyleAssets.TryResolve(out var problem);
            if (assets == null)
            {
                return InRoom()
                    ? SmokeCheckResult.Fail(BackendId, "game UI assets missing in the room: " + problem)
                    : SmokeCheckResult.NotYetObserved(BackendId, "the game's UI assets load with " + RoomScene + ": " + problem);
            }

            return SmokeCheckResult.Pass(BackendId, ModWindows.Status + "; escape patch on; " + assets.Report.Describe());
        }

        private static SmokeCheckResult Registry()
        {
            var windows = ModWindows.Snapshot();
            if (windows.Count == 0)
            {
                return SmokeCheckResult.Pass(RegistryId, "no mod windows open");
            }

            var canvas = GameStyleCanvas.Root;
            var problems = new List<string>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in windows)
            {
                if (!keys.Add(window.Key))
                {
                    problems.Add(window.Key + " registered twice");
                }

                if (!window.ObjectName.StartsWith(CwyWindowSpecRules.RootPrefix, StringComparison.Ordinal))
                {
                    problems.Add(window.Key + " object name " + window.ObjectName + " lacks " + CwyWindowSpecRules.RootPrefix);
                }

                var root = window.Frame.Root;
                if (root == null || !string.Equals(root.name, window.ObjectName, StringComparison.Ordinal))
                {
                    problems.Add(window.Key + " root object is " + (root == null ? "gone" : root.name));
                }
                else if (canvas == null || root.parent != canvas)
                {
                    problems.Add(window.Key + " is not on " + GameStyleCanvas.ObjectName);
                }
            }

            return problems.Count == 0
                ? SmokeCheckResult.Pass(RegistryId, windows.Count + " mod window(s) named CWY.Mod.<mod>.<window> on " + GameStyleCanvas.ObjectName)
                : SmokeCheckResult.Fail(RegistryId, string.Join("; ", problems));
        }

        private static SmokeCheckResult Thai()
        {
            var assets = GameStyleAssets.TryResolve(out var problem);
            if (assets == null)
            {
                return InRoom()
                    ? SmokeCheckResult.Fail(ThaiId, "cannot build a label: " + problem)
                    : SmokeCheckResult.NotYetObserved(ThaiId, "the game's UI assets load with " + RoomScene + ": " + problem);
            }

            var canvas = GameStyleCanvas.Ensure(out var canvasProblem);
            if (canvas == null)
            {
                return SmokeCheckResult.Fail(ThaiId, "cannot build a label: " + canvasProblem);
            }

            GameObject? probe = null;
            try
            {
                var rect = GameStyleUi.Child(canvas, "CWY.UiSmoke.Thai");
                probe = rect.gameObject;
                probe.SetActive(false);
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.sizeDelta = new Vector2(900f, 80f);

                // Fully transparent: nothing is ever drawn, but TextMeshPro lays the line out.
                var label = GameStyleUi.AddLabel(rect, assets.Font, GameStyleUi.BodySize, new Color(1f, 1f, 1f, 0f), TextAlignmentOptions.TopLeft);
                label.overflowMode = TextOverflowModes.Overflow;
                probe.SetActive(true);
                label.text = ThaiMarkCheck.Sample;
                label.ForceMeshUpdate(true, true);

                var info = label.textInfo;
                var glyphs = new List<GlyphBox>(info.characterCount);
                for (var i = 0; i < info.characterCount; i++)
                {
                    var c = info.characterInfo[i];
                    var element = c.textElement;

                    // characterInfo's corners are the drawn quad, which TMP grows by the material's
                    // padding on every side (in TextMeshProUGUI). Two marks whose ink
                    // just clears each other still overlap by twice that, so measure the ink: the
                    // quad shrunk back to the glyph's own height (metrics.height x the char's scale).
                    // Before this, the fixed layout read as "overlaps by 1.7".
                    var inkPad = 0f;
                    if (element?.glyph != null)
                    {
                        var ink = element.glyph.metrics.height * c.scale;
                        inkPad = Mathf.Max(0f, (c.topRight.y - c.bottomLeft.y - ink) / 2f);
                    }

                    glyphs.Add(new GlyphBox(
                        c.character,
                        c.fontAsset == null ? "<none>" : c.fontAsset.name,
                        element == null || element.glyphIndex == 0,
                        c.bottomLeft.x,
                        c.topRight.x,
                        c.bottomLeft.y + inkPad,
                        c.topRight.y - inkPad));
                }

                var report = ThaiMarkCheck.Check(glyphs);
                var outside = new StringBuilder();
                foreach (var font in report.Fonts.Keys)
                {
                    if (Array.IndexOf(ThaiFonts, font) < 0)
                    {
                        outside.Append(outside.Length == 0 ? string.Empty : ", ").Append(font);
                    }
                }

                var detail = report.Describe() + "; mark records installed: "
                    + global::CwyLs.ModLoader.Bulbul.Language.LanguageFonts.RecordCount;
                if (outside.Length > 0)
                {
                    return SmokeCheckResult.Fail(ThaiId, "Thai drawn by " + outside + " instead of Base_TH/Dynamic_TH; " + detail);
                }

                return report.Ok ? SmokeCheckResult.Pass(ThaiId, detail) : SmokeCheckResult.Fail(ThaiId, detail);
            }
            finally
            {
                if (probe != null)
                {
                    UnityEngine.Object.Destroy(probe);
                }
            }
        }

        private static SmokeCheckResult Demo()
        {
            var message = ModWindowDemo.Open();
            return ModWindowDemo.IsOpen
                ? SmokeCheckResult.Pass(DemoId, message)
                : SmokeCheckResult.Fail(DemoId, message);
        }

        private static bool InRoom()
        {
            try
            {
                return string.Equals(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name, RoomScene, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(GameStyleUi.LogPrefix + " smoke could not read the scene: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
