using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Core.Textures;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The Phase-1 texture checks, registered by <see cref="TextureFeature.Initialize"/> so they
    /// exist whether or not the smoke plugin is installed; the runner in CwyLs.ModLoader.Smoke picks
    /// them up from the shared <see cref="SmokeRegistry"/>.
    /// <para>
    /// They answer what a launch has to settle: did the packs produce rules, is every rule's texture
    /// on something the player can actually see, did a <c>@material</c> rule reach a worn costume,
    /// and can we put the game back.
    /// </para>
    /// <para>
    /// "On an active renderer" is the property that matters, and these checks are deliberately
    /// <em>rule-agnostic</em> about it. An earlier version read one hard-coded slot
    /// (<c>Body._MainTex</c>) back off the renderer the rule named. That passed while the player saw
    /// nothing - <c>CostumeChangeService</c> had deactivated that renderer - and then failed once
    /// the pack switched to a <c>@material</c> rule that never mentions <c>Body</c> at all. Neither
    /// failure was about the texture; both were about the check naming a renderer instead of asking
    /// the scene.
    /// </para>
    /// </summary>
    internal static class TextureSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "P1";

        /// <summary>Phase label of the live-reload check, which is reported under P2.</summary>
        internal const string ReloadPhase = "P2";

        /// <summary>How much of a diagnostic or a long list is folded into a smoke detail.</summary>
        private const int DetailLimit = 1200;

        private const string RulesId = "p1.textures.rules";
        private const string AppliedId = "p1.textures.applied";
        private const string RestoreId = "p1.textures.restore";
        private const string MaterialRuleId = "p1.textures.material_rule";
        private const string ReloadId = "p2.textures.reload";

        /// <summary>Registers every texture check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(RulesId, Phase, Rules));
            SmokeRegistry.Register(new SmokeCheck(AppliedId, Phase, Applied));
            SmokeRegistry.Register(new SmokeCheck(MaterialRuleId, Phase, MaterialRule));

            // Loud: both of these change what is on screen while they run - restore strips the
            // pack's textures off the character and puts them back, reload re-reads every file and
            // re-applies. They belong to an explicit smoke trigger, not to a pass the player did
            // not ask for.
            SmokeRegistry.Register(new SmokeCheck(RestoreId, Phase, Restore), loud: true);
            SmokeRegistry.Register(new SmokeCheck(ReloadId, ReloadPhase, Reload), loud: true);
        }

        /// <summary>At least one rule came out of the deployed packs.</summary>
        private static SmokeCheckResult Rules()
        {
            if (!TextureFeature.HasApplied)
            {
                return SmokeCheckResult.Fail(
                    RulesId, $"no apply pass has run yet (trigger is '{TextureFeature.SceneName}' + one frame)");
            }

            var rules = TextureFeature.Rules;
            if (rules.Count == 0)
            {
                return SmokeCheckResult.Fail(
                    RulesId,
                    "no texture rules; deploy a pack with textures/<renderer>/<slot>.png or " +
                    $"textures/@<material>/<slot>.png (./deploy-packs.sh). {rules.Summarize()}");
            }

            var names = new List<string>(rules.Count);
            foreach (var rule in rules.Rules)
            {
                names.Add(rule.ToString());
            }

            return SmokeCheckResult.Pass(RulesId, rules.Summarize() + ": " + Join("; ", names));
        }

        /// <summary>
        /// Every rule the loaded packs produced has its texture on at least one active renderer -
        /// which is the only form of "applied" the player can see. Works the same for a renderer
        /// rule and a <c>@material</c> rule, because it asks the scene rather than a name.
        /// </summary>
        private static SmokeCheckResult Applied()
        {
            if (!TextureFeature.HasApplied)
            {
                return SmokeCheckResult.Fail(
                    AppliedId, $"no apply pass has run yet (trigger is '{TextureFeature.SceneName}' + one frame)");
            }

            var rules = TextureFeature.Rules;
            if (rules.Count == 0)
            {
                // Vacuous, and it says so: there is nothing to see, which is not the same as a pack
                // whose textures failed to show.
                return SmokeCheckResult.Pass(
                    AppliedId,
                    "no texture rules in the loaded packs, so nothing to show. " + TextureFeature.LastSummary);
            }

            var lookup = RendererLookup.Scan();
            var visible = new List<string>();
            var hidden = new List<string>();
            var notWorn = new List<string>();

            foreach (var rule in rules.Rules)
            {
                var holders = HoldersFor(rule, lookup, out var applied);

                if (holders.Count > 0)
                {
                    visible.Add(rule.Slot + " on " + Join(", ", holders));
                    continue;
                }

                if (applied is null)
                {
                    hidden.Add(rule.Slot + " (never applied)");
                    continue;
                }

                // The texture IS on the material - the outfit it belongs to is simply not
                // being worn. That is the player's wardrobe choice, not a broken seam, so it is
                // reported as not-yet-observed rather than as a failure that paints the run red.
                notWorn.Add(
                    $"{rule.Slot} not worn (no active renderer uses {NameOf(rule)}; applied as " +
                    $"'{applied.TextureName}' {applied.Width}x{applied.Height})");
            }

            var total = visible.Count + hidden.Count + notWorn.Count;

            if (hidden.Count > 0)
            {
                return SmokeCheckResult.Fail(
                    AppliedId,
                    $"{visible.Count}/{total} rule(s) on an active renderer. NOT VISIBLE: " +
                    Join("; ", hidden) +
                    (notWorn.Count == 0 ? string.Empty : ". Not worn: " + Join("; ", notWorn)) +
                    (visible.Count == 0 ? string.Empty : ". Visible: " + Join("; ", visible)) +
                    $". {TextureFeature.LastSummary}; mode={TextureFeature.WriteMode}; " +
                    $"costume seams {CostumePatches.Patched}/4");
            }

            if (notWorn.Count > 0)
            {
                return SmokeCheckResult.NotYetObserved(
                    AppliedId,
                    $"{visible.Count}/{total} rule(s) on an active renderer; " + Join("; ", notWorn) +
                    (visible.Count == 0 ? string.Empty : ". Visible: " + Join("; ", visible)) +
                    $". {TextureFeature.LastSummary}; mode={TextureFeature.WriteMode}");
            }

            return SmokeCheckResult.Pass(
                AppliedId,
                $"{total}/{total} rule(s) on an active renderer: " + Join("; ", visible) +
                $". {TextureFeature.LastSummary}; mode={TextureFeature.WriteMode}");
        }

        /// <summary>
        /// A <c>@material</c> rule reached a material that an active renderer is actually using -
        /// which is the only way to repaint a costume the game instantiated, because its renderers
        /// (<c>Top2</c>, <c>Top3</c>, <c>Body1</c>) have names no rule can predict.
        /// </summary>
        private static SmokeCheckResult MaterialRule()
        {
            var rules = TextureFeature.Rules;
            if (rules.MaterialNames.Count == 0)
            {
                return SmokeCheckResult.Pass(
                    MaterialRuleId,
                    "no @material rules in the loaded packs (nothing to prove); " +
                    $"costume seams patched {CostumePatches.Patched}/4");
            }

            var lookup = RendererLookup.Scan();
            var found = new List<string>();
            var missing = new List<string>();
            var notWorn = new List<string>();

            foreach (var rule in rules.Rules)
            {
                if (rule.Target != TextureRuleTarget.Material)
                {
                    continue;
                }

                var holders = HoldersFor(rule, lookup, out var applied);

                if (holders.Count > 0)
                {
                    found.Add(rule.Slot + " on " + Join(", ", holders));
                }
                else if (applied is null)
                {
                    missing.Add(rule.Slot + " (never applied)");
                }
                else
                {
                    // Same distinction as p1.textures.applied: the material took the texture, the
                    // costume that uses it is not on the character right now.
                    notWorn.Add(rule.Slot + $" not worn (no active renderer uses {NameOf(rule)})");
                }
            }

            if (found.Count == 0 && missing.Count > 0)
            {
                return SmokeCheckResult.Fail(
                    MaterialRuleId,
                    $"no @material rule reached an active renderer: {Join("; ", missing)}. " +
                    $"Costume seams patched {CostumePatches.Patched}/4" +
                    (CostumePatches.Failures.Length == 0 ? string.Empty : " - " + CostumePatches.Failures) + ". " +
                    Fold(TextureFeature.DiagnosticFor(TextureRule.MaterialPrefix + rules.MaterialNames[0])));
            }

            if (found.Count == 0)
            {
                return SmokeCheckResult.NotYetObserved(
                    MaterialRuleId,
                    Join("; ", notWorn) +
                    $". Costume seams patched {CostumePatches.Patched}/4");
            }

            return SmokeCheckResult.Pass(
                MaterialRuleId,
                $"{found.Count} of {found.Count + missing.Count + notWorn.Count} @material rule(s) on an " +
                "active renderer: " + Join("; ", found) +
                (missing.Count == 0 ? string.Empty : ". Not visible: " + Join("; ", missing)) +
                (notWorn.Count == 0 ? string.Empty : ". " + Join("; ", notWorn)) +
                $". Costume seams patched {CostumePatches.Patched}/4");
        }

        /// <summary>
        /// <see cref="TextureFeature.RestoreAll"/> really takes every pack texture back off the
        /// scene - and then re-applies, so a smoke run leaves the player looking at the modded
        /// character and not at whatever the check happened to finish on.
        /// </summary>
        private static SmokeCheckResult Restore()
        {
            var rules = TextureFeature.Rules;
            if (rules.Count == 0)
            {
                return SmokeCheckResult.Pass(RestoreId, "no texture rules in the loaded packs, so nothing to restore");
            }

            var before = CountHolders(rules, out var beforeDetail);
            if (before == 0)
            {
                return SmokeCheckResult.Fail(
                    RestoreId,
                    "no pack texture is on an active renderer before restoring, so restoring proves " +
                    $"nothing (see {AppliedId})");
            }

            try
            {
                var restored = TextureFeature.RestoreAll();
                var after = CountHolders(rules, out var afterDetail);

                if (after > 0)
                {
                    return SmokeCheckResult.Fail(
                        RestoreId,
                        $"RestoreAll restored {restored} slot(s) but {after} active renderer(s) still " +
                        $"hold a pack texture: {afterDetail}");
                }

                return SmokeCheckResult.Pass(
                    RestoreId,
                    $"restored {restored} slot(s): active renderers holding a pack texture went " +
                    $"{before} -> 0, then re-applied. Was: {beforeDetail}");
            }
            finally
            {
                // Whatever happened above, the run must end with the pack's textures on: the point
                // of the launch is that the user sees the modded character.
                TextureFeature.Apply();
            }
        }

        /// <summary>
        /// <see cref="TextureFeature.ReloadAll()"/> - the live-reload key - runs and leaves the
        /// scene exactly as it was: same rules applied, same failures, nothing lost.
        /// </summary>
        /// <remarks>
        /// The rule count is the assertion because a reload is meant to be invisible unless a file
        /// changed: it drops the asset cache, rescans the packs and applies again. A count that
        /// moved means a file appeared or went away between the two passes, which is either a real
        /// editing session (fine, and named in the detail) or a pack that is losing files.
        /// </remarks>
        private static SmokeCheckResult Reload()
        {
            if (!TextureFeature.HasApplied)
            {
                return SmokeCheckResult.Fail(
                    ReloadId, $"no apply pass has run yet (trigger is '{TextureFeature.SceneName}' + one frame)");
            }

            var before = TextureFeature.AppliedRules.Count;
            var applied = TextureFeature.ReloadAll();

            if (applied < 0)
            {
                return SmokeCheckResult.Fail(ReloadId, Cap(TextureFeature.LastReloadSummary));
            }

            if (applied != before)
            {
                return SmokeCheckResult.Fail(
                    ReloadId,
                    $"the reload changed the applied rule count {before} -> {applied}; " +
                    Cap(TextureFeature.LastReloadSummary));
            }

            return SmokeCheckResult.Pass(
                ReloadId,
                Cap($"{TextureFeature.LastReloadSummary}; reload #{TextureFeature.ReloadCount}, " +
                    $"watching {TextureReload.Mode}"));
        }

        /// <summary>
        /// Active renderers currently showing this rule's texture.
        /// </summary>
        /// <param name="rule">The rule, in either form.</param>
        /// <param name="lookup">A fresh renderer scan.</param>
        /// <param name="applied">The rule's applied record, or null when it never applied.</param>
        /// <remarks>
        /// A texture is identified by its name and pixel size, not by reference: the feature does not
        /// hold references to what it wrote. Two rules whose files share a stem and a size would
        /// therefore vouch for each other - acceptable here, and visible in the detail, which names
        /// the renderer and the material.
        /// </remarks>
        /// <summary>What a rule targets, for the "not worn" wording: a material or a renderer.</summary>
        /// <param name="rule">The rule.</param>
        private static string NameOf(TextureRule rule) =>
            rule.Target == TextureRuleTarget.Material
                ? "material '" + rule.MaterialName + "'"
                : "renderer '" + rule.RendererName + "'";

        private static List<string> HoldersFor(TextureRule rule, RendererLookup lookup, out AppliedTexture? applied)
        {
            applied = FindApplied(rule);
            if (applied is null)
            {
                return new List<string>();
            }

            return rule.Target == TextureRuleTarget.Material
                ? ActiveHolders(lookup, applied, rule.MaterialName)
                : ActiveHolders(lookup, applied, materialName: null);
        }

        /// <summary>How many active renderers show any rule's texture, plus a one-line description.</summary>
        private static int CountHolders(TextureRuleSet rules, out string detail)
        {
            var lookup = RendererLookup.Scan();
            var lines = new List<string>();
            var count = 0;

            foreach (var rule in rules.Rules)
            {
                var holders = HoldersFor(rule, lookup, out _);
                if (holders.Count == 0)
                {
                    continue;
                }

                count += holders.Count;
                lines.Add(rule.Slot + " on " + Join(", ", holders));
            }

            detail = lines.Count == 0 ? "<none>" : Join("; ", lines);
            return count;
        }

        /// <summary>The applied record for one rule, or null when it did not apply.</summary>
        private static AppliedTexture? FindApplied(TextureRule rule)
        {
            foreach (var applied in TextureFeature.AppliedRules)
            {
                if (string.Equals(applied.Rule.Key, rule.Key, StringComparison.Ordinal))
                {
                    return applied;
                }
            }

            return null;
        }

        /// <summary>
        /// Every active, enabled renderer whose shared material holds this texture in an albedo slot.
        /// </summary>
        /// <param name="lookup">A fresh renderer scan.</param>
        /// <param name="applied">The applied record naming the texture and its size.</param>
        /// <param name="materialName">
        /// When given, only materials with this base name count - which is what a <c>@material</c>
        /// rule actually claims. Null accepts any material.
        /// </param>
        private static List<string> ActiveHolders(
            RendererLookup lookup, AppliedTexture applied, string? materialName)
        {
            var holders = new List<string>();

            foreach (var renderer in lookup.AllRenderers)
            {
                if (!RendererDiagnostics.IsActive(renderer) || !renderer!.enabled)
                {
                    continue;
                }

                var slot = FindSlot(renderer, applied, materialName);
                if (slot != null)
                {
                    holders.Add(RendererLookup.PathOf(renderer) + slot);
                }
            }

            return holders;
        }

        /// <summary>The first matching slot on this renderer, e.g. <c>[0]._MainTex ('&lt;texture name&gt;')</c>, or null.</summary>
        private static string? FindSlot(Renderer renderer, AppliedTexture applied, string? materialName)
        {
            try
            {
                // sharedMaterials, not materials: reading .materials would instantiate a copy on
                // every renderer in the scene and rebind it - the very trap these checks exist to
                // catch. An instance the feature created is already in this array.
                var materials = renderer.sharedMaterials;
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null)
                    {
                        continue;
                    }

                    if (materialName != null &&
                        !string.Equals(
                            MaterialLookup.BaseNameOf(material!.name), materialName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var property = SlotHolding(material!, applied.TextureName, applied.Width, applied.Height);
                    if (property != null)
                    {
                        return "[" + i + "]." + property + " ('" + material.name + "')";
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not inspect {RendererLookup.PathOf(renderer)}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        /// <summary>The first albedo property of this material holding that texture, or null.</summary>
        /// <param name="material">A live material.</param>
        /// <param name="textureName">The <c>Texture.name</c> to look for.</param>
        /// <param name="width">Expected width, so a same-named game texture cannot pass for ours.</param>
        /// <param name="height">Expected height.</param>
        private static string? SlotHolding(Material material, string textureName, int width, int height)
        {
            foreach (var property in RendererDiagnostics.AlbedoProperties)
            {
                if (!material.HasProperty(property))
                {
                    continue;
                }

                var texture = material.GetTexture(property);
                if (texture != null &&
                    string.Equals(texture!.name, textureName, StringComparison.Ordinal) &&
                    texture.width == width &&
                    texture.height == height)
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>Joins and caps a list, so one pack with forty rules cannot bloat the report.</summary>
        private static string Join(string separator, List<string> parts) =>
            Cap(string.Join(separator, parts.ToArray()));

        /// <summary>Folds a multi-line diagnostic onto one line and caps it, for a JSON report field.</summary>
        private static string Fold(string? diagnostic)
        {
            if (string.IsNullOrEmpty(diagnostic))
            {
                return "(no diagnostic; set Textures.Diagnose=true)";
            }

            return Cap(diagnostic!.Replace("\r\n", " | ").Replace("\n", " | "));
        }

        private static string Cap(string text) =>
            text.Length <= DetailLimit ? text : text.Substring(0, DetailLimit) + " ...(see LogOutput.log)";
    }
}
