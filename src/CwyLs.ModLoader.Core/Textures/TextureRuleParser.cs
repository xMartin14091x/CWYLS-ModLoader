using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Turns a pack's texture files into <see cref="TextureRule"/>s. Two sources, in this order:
    /// <list type="number">
    ///   <item><description>
    ///     the folder convention <c>textures/&lt;RendererName&gt;/&lt;slot&gt;.png</c>, where
    ///     <c>slot</c> is a shader property (<c>_MainTex</c>) or an alias
    ///     (<see cref="TextureSlotAliases"/>);
    ///   </description></item>
    ///   <item><description>
    ///     the optional <c>textures/_rules.json</c>, for the cases the folder layout cannot say:
    ///     a material index other than 0, or a file whose name is not the slot.
    ///   </description></item>
    /// </list>
    /// The rules file is merged <em>after</em> the convention, so an explicit entry wins over a
    /// file that happens to sit in the matching folder.
    /// <para>
    /// A PNG that is not at exactly <c>textures/&lt;folder&gt;/&lt;file&gt;.png</c>, any file that
    /// is not a PNG, and anything under a folder whose name starts with
    /// <see cref="PrivateFolderPrefix"/>, are all passed over in silence: packs legitimately keep
    /// source art, notes and the rules file itself under <c>textures/</c>. Only a file that clearly
    /// <em>meant</em> to be a rule and failed - an unknown slot name - becomes a
    /// <see cref="TextureRuleProblem"/>. The count of files seen versus rules made is logged, so a
    /// silent miss is still visible.
    /// </para>
    /// </summary>
    public static class TextureRuleParser
    {
        /// <summary>Folder every texture rule path starts with, with its trailing slash.</summary>
        public const string TexturesFolder = PackContentIndex.TexturesFolder;

        /// <summary>Extension a texture file must have; it is the only format the loader decodes.</summary>
        public const string TextureExtension = ".png";

        /// <summary>Path of the optional advanced-mapping file, relative to the pack root.</summary>
        public const string RulesPath = TexturesFolder + "_rules.json";

        /// <summary>
        /// A folder directly under <c>textures/</c> whose name starts with this is the pack
        /// author's own - source art, work in progress - and is never read as a renderer name.
        /// </summary>
        public const char PrivateFolderPrefix = '_';

        private const string KeyRenderer = "renderer";
        private const string KeyMaterial = "material";
        private const string KeyProperty = "property";
        private const string KeyFile = "file";
        private const string KeyMaterialIndex = "materialIndex";

        private static readonly string[] KnownKeys =
            { KeyRenderer, KeyMaterial, KeyProperty, KeyFile, KeyMaterialIndex };

        /// <summary>
        /// Reads the folder convention out of a pack's file listing.
        /// </summary>
        /// <param name="packId">The pack's id; stamped on every rule.</param>
        /// <param name="files">
        /// The pack's files - <c>LoadedPack.Index.Files</c>, or just its <c>Textures</c>. Entries
        /// outside <c>textures/</c> are skipped, so either works. Null yields <see cref="TextureRuleSet.Empty"/>.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public static TextureRuleSet FromIndex(string packId, IEnumerable<PackFile?>? files)
        {
            RequirePackId(packId);

            if (files is null)
            {
                return TextureRuleSet.Empty;
            }

            var rules = new List<TextureRule>();
            var problems = new List<TextureRuleProblem>();

            foreach (var file in files)
            {
                if (file is null)
                {
                    continue;
                }

                var rule = FromPath(packId, file.Path, out var problem);
                if (rule != null)
                {
                    rules.Add(rule);
                }
                else if (problem != null)
                {
                    problems.Add(problem);
                }
            }

            rules.Sort(ByPath);
            return Deduplicate(rules, problems);
        }

        /// <summary>
        /// Reads one path as a folder-convention rule.
        /// </summary>
        /// <param name="packId">The pack's id.</param>
        /// <param name="relativePath">A pack-relative path, any separator style.</param>
        /// <param name="problem">
        /// Why the path was rejected when it looked like a rule and was not one; null when the path
        /// simply is not a texture rule (not a PNG, wrong depth, outside <c>textures/</c>).
        /// </param>
        /// <returns>The rule, or null.</returns>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public static TextureRule? FromPath(string packId, string? relativePath, out TextureRuleProblem? problem)
        {
            RequirePackId(packId);
            problem = null;

            var path = PackContentIndex.Normalise(relativePath);
            if (path is null ||
                !path.StartsWith(TexturesFolder, StringComparison.OrdinalIgnoreCase) ||
                !path.EndsWith(TextureExtension, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Exactly textures/<renderer>/<file>.png: one separator after the folder, none after it.
            var rendererStart = TexturesFolder.Length;
            var slash = path.IndexOf('/', rendererStart);
            if (slash < 0 || path.IndexOf('/', slash + 1) >= 0)
            {
                return null;
            }

            var renderer = path.Substring(rendererStart, slash - rendererStart);
            var slot = path.Substring(slash + 1, path.Length - slash - 1 - TextureExtension.Length);

            if (renderer.Length == 0 || slot.Length == 0)
            {
                return null;
            }

            // A folder whose name starts with "_" is the pack author's own - source art, work in
            // progress - and is never read as a renderer. Without it, textures/src/wip.png is
            // indistinguishable from a rule for a renderer called "src", and the author gets a
            // warning about a slot they never meant to write. Same prefix as _rules.json, which
            // sits one level up for the same reason.
            if (renderer[0] == PrivateFolderPrefix)
            {
                return null;
            }

            if (!TextureSlotAliases.TryResolve(slot, out var property, out var reason))
            {
                problem = new TextureRuleProblem(packId, path, reason ?? "the slot name is not understood");
                return null;
            }

            // Paid content is never re-skinned (PaidContentGuard), whatever the folder says.
            if (PaidContentGuard.IsRefused(renderer.TrimStart(TextureRule.MaterialPrefix), out var paid))
            {
                problem = new TextureRuleProblem(packId, path, paid!);
                return null;
            }

            // "@name" is a material asset, not a renderer: textures/@mt_Tee_A/_MainTex.png.
            if (renderer[0] == TextureRule.MaterialPrefix)
            {
                var material = renderer.Substring(1);
                if (material.Length == 0)
                {
                    problem = new TextureRuleProblem(
                        packId, path, "\"" + TextureRule.MaterialPrefix + "\" must be followed by a material name");
                    return null;
                }

                return TextureRule.ForMaterial(packId, material, property!, path);
            }

            return new TextureRule(packId, renderer, property!, path, TextureRule.DefaultMaterialIndex);
        }

        /// <summary>
        /// Reads the optional <c>textures/_rules.json</c>: an array of
        /// <c>{"renderer":"Body","materialIndex":0,"property":"_MainTex","file":"my.png"}</c>.
        /// <para>
        /// <c>file</c> is relative to the <c>textures/</c> folder, so <c>"my.png"</c> is
        /// <c>textures/my.png</c> and <c>"Body/alt.png"</c> is <c>textures/Body/alt.png</c>.
        /// <c>property</c> accepts an alias just as a file stem does. <c>materialIndex</c> is
        /// optional and defaults to 0. A member the framework does not know is an error unless it
        /// starts with <c>x-</c>, which is reserved for the pack author.
        /// </para>
        /// </summary>
        /// <param name="packId">The pack's id.</param>
        /// <param name="json">The whole text of the file; null or blank yields <see cref="TextureRuleSet.Empty"/>.</param>
        /// <returns>
        /// The rules, plus a problem per rejected entry. Malformed JSON is one problem and no rules;
        /// a bad entry costs only that entry.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public static TextureRuleSet FromRulesJson(string packId, string? json)
        {
            RequirePackId(packId);

            if (string.IsNullOrWhiteSpace(json))
            {
                return TextureRuleSet.Empty;
            }

            if (!JsonReader.TryParse(json!, out var root, out var error))
            {
                return Problem(packId, RulesPath, "invalid JSON at " + error);
            }

            if (!(root is List<object?> entries))
            {
                return Problem(
                    packId,
                    RulesPath,
                    "the file must be a JSON array of rules, not " + JsonReader.TypeName(root));
            }

            var rules = new List<TextureRule>();
            var problems = new List<TextureRuleProblem>();

            for (var i = 0; i < entries.Count; i++)
            {
                var at = RulesPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";

                if (!(entries[i] is JsonObject entry))
                {
                    problems.Add(new TextureRuleProblem(
                        packId, at, "a rule must be an object, not " + JsonReader.TypeName(entries[i])));
                    continue;
                }

                // An entry with a value (color, tint, float, vector) is a material property
                // rule, read by MaterialPropertyRuleParser from the same file.
                if (MaterialPropertyRuleParser.IsPropertyEntry(entry))
                {
                    continue;
                }

                var rule = ReadEntry(packId, entry, at, out var problem);
                if (rule != null)
                {
                    rules.Add(rule);
                }
                else if (problem != null)
                {
                    problems.Add(problem);
                }
            }

            return Deduplicate(rules, problems);
        }

        /// <summary>
        /// The pack's complete rule set: the folder convention with <c>_rules.json</c> layered on
        /// top, so an explicit entry overrides a conventional file for the same target.
        /// </summary>
        /// <param name="packId">The pack's id.</param>
        /// <param name="files">The pack's files; see <see cref="FromIndex"/>.</param>
        /// <param name="rulesJson">
        /// Text of <c>textures/_rules.json</c>, or null when the pack has none. The caller reads the
        /// file - Core never touches a filesystem.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> is blank.</exception>
        public static TextureRuleSet Build(string packId, IEnumerable<PackFile?>? files, string? rulesJson) =>
            TextureRuleSet.Merge(FromIndex(packId, files), FromRulesJson(packId, rulesJson));

        private static TextureRule? ReadEntry(
            string packId, JsonObject entry, string at, out TextureRuleProblem? problem)
        {
            problem = null;

            foreach (var member in entry)
            {
                if (member.Key.StartsWith(PackManifestParser.ExtensionPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (Array.IndexOf(KnownKeys, member.Key) < 0)
                {
                    problem = new TextureRuleProblem(
                        packId,
                        at,
                        "unknown member \"" + member.Key + "\"; expected one of " + string.Join(", ", KnownKeys) +
                        " (prefix your own fields with \"" + PackManifestParser.ExtensionPrefix + "\")");
                    return null;
                }
            }

            var hasRenderer = entry.ContainsKey(KeyRenderer);
            var hasMaterial = entry.ContainsKey(KeyMaterial);

            if (hasRenderer == hasMaterial)
            {
                problem = new TextureRuleProblem(
                    packId,
                    at,
                    hasRenderer
                        ? "\"" + KeyRenderer + "\" and \"" + KeyMaterial + "\" cannot both be set; a rule " +
                          "names a renderer or a material, not both"
                        : "\"" + KeyRenderer + "\" or \"" + KeyMaterial + "\" is required");
                return null;
            }

            var targetKey = hasMaterial ? KeyMaterial : KeyRenderer;
            var target = RequiredString(entry, targetKey, packId, at, ref problem);
            if (target is null)
            {
                return null;
            }

            if (hasMaterial && entry.ContainsKey(KeyMaterialIndex))
            {
                problem = new TextureRuleProblem(
                    packId,
                    at,
                    "\"" + KeyMaterialIndex + "\" has no meaning with \"" + KeyMaterial +
                    "\": a material rule writes the material wherever it is used");
                return null;
            }

            var slot = RequiredString(entry, KeyProperty, packId, at, ref problem);
            if (slot is null)
            {
                return null;
            }

            if (!entry.ContainsKey(KeyFile))
            {
                problem = new TextureRuleProblem(
                    packId,
                    at,
                    "\"" + KeyFile + "\" is required for a texture rule (a material property rule gives one of " +
                    "\"color\", \"tint\", \"float\" or \"vector\" instead)");
                return null;
            }

            var file = RequiredString(entry, KeyFile, packId, at, ref problem);
            if (file is null)
            {
                return null;
            }

            if (PaidContentGuard.IsRefused(target, out var paid))
            {
                problem = new TextureRuleProblem(packId, at, paid!);
                return null;
            }

            if (!TextureSlotAliases.TryResolve(slot, out var property, out var reason))
            {
                problem = new TextureRuleProblem(packId, at, "\"" + KeyProperty + "\": " + reason);
                return null;
            }

            var relative = PackContentIndex.Normalise(file);
            if (relative is null)
            {
                problem = new TextureRuleProblem(
                    packId, at, "\"" + KeyFile + "\": \"" + file + "\" is not a path inside the pack");
                return null;
            }

            if (!relative.EndsWith(TextureExtension, StringComparison.OrdinalIgnoreCase))
            {
                problem = new TextureRuleProblem(
                    packId, at, "\"" + KeyFile + "\": \"" + file + "\" must be a " + TextureExtension + " file");
                return null;
            }

            if (hasMaterial)
            {
                return TextureRule.ForMaterial(
                    packId, target, property!, TexturesFolder + relative, TextureRuleSource.RulesFile);
            }

            if (!TryReadMaterialIndex(entry, packId, at, out var materialIndex, ref problem))
            {
                return null;
            }

            return new TextureRule(
                packId,
                target,
                property!,
                TexturesFolder + relative,
                materialIndex,
                TextureRuleSource.RulesFile);
        }

        private static bool TryReadMaterialIndex(
            JsonObject entry, string packId, string at, out int materialIndex, ref TextureRuleProblem? problem)
        {
            materialIndex = TextureRule.DefaultMaterialIndex;

            if (!entry.TryGetValue(KeyMaterialIndex, out var raw) || raw is null)
            {
                return true;
            }

            long value;
            if (raw is long whole)
            {
                value = whole;
            }
            else if (raw is double real && real == Math.Floor(real) && !double.IsInfinity(real))
            {
                value = (long)real;
            }
            else
            {
                problem = new TextureRuleProblem(
                    packId,
                    at,
                    "\"" + KeyMaterialIndex + "\": expected a whole number, not " + JsonReader.TypeName(raw));
                return false;
            }

            if (value < 0 || value > int.MaxValue)
            {
                problem = new TextureRuleProblem(
                    packId,
                    at,
                    "\"" + KeyMaterialIndex + "\": " + value.ToString(CultureInfo.InvariantCulture) +
                    " is not a material index");
                return false;
            }

            materialIndex = (int)value;
            return true;
        }

        private static string? RequiredString(
            JsonObject entry, string key, string packId, string at, ref TextureRuleProblem? problem)
        {
            if (!entry.TryGetValue(key, out var raw))
            {
                problem = new TextureRuleProblem(packId, at, "\"" + key + "\" is required");
                return null;
            }

            if (!(raw is string text) || text.Trim().Length == 0)
            {
                problem = new TextureRuleProblem(
                    packId, at, "\"" + key + "\": expected a non-empty string, not " + JsonReader.TypeName(raw));
                return null;
            }

            return text.Trim();
        }

        /// <summary>
        /// Within one pack, two files can still collide (<c>Body/base.png</c> and
        /// <c>Body/_MainTex.png</c> name the same property). The later one in path order wins,
        /// which is arbitrary but stable, and the loser is reported so the author can delete it.
        /// </summary>
        private static TextureRuleSet Deduplicate(List<TextureRule> rules, List<TextureRuleProblem> problems)
        {
            var merged = TextureRuleSet.Merge(new TextureRuleSet(rules, problems));

            foreach (var loser in merged.Overridden)
            {
                problems.Add(new TextureRuleProblem(
                    loser.PackId,
                    loser.RelativePath,
                    "another file in the same pack also sets " + loser.Slot + "; this one is not used"));
            }

            // The within-pack losers are reported as problems, not carried as Overridden: that list
            // is for cross-pack overrides, which are legitimate and not a mistake.
            return new TextureRuleSet(merged.Rules, problems);
        }

        private static TextureRuleSet Problem(string packId, string path, string reason) =>
            new TextureRuleSet(null, new[] { new TextureRuleProblem(packId, path, reason) });

        private static int ByPath(TextureRule a, TextureRule b) => string.CompareOrdinal(a.RelativePath, b.RelativePath);

        private static void RequirePackId(string packId)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }
        }
    }
}
