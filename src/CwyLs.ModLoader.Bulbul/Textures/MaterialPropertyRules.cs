using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Core.Textures;
using UnityEngine;
using UnityEngine.Rendering;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>One material property rule that reached a live material in the last pass.</summary>
    public sealed class AppliedMaterialProperty
    {
        internal AppliedMaterialProperty(MaterialPropertyRule rule, int materials, string detail)
        {
            Rule = rule;
            Materials = materials;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The rule.</summary>
        public MaterialPropertyRule Rule { get; }

        /// <summary>How many material objects were written.</summary>
        public int Materials { get; }

        /// <summary>Which materials and properties, e.g. <c>mt_desk_1{_Color+_BaseColor}</c>.</summary>
        public string Detail { get; }

        /// <inheritdoc />
        public override string ToString() => Rule + " -> " + Detail;
    }

    /// <summary>
    /// The Unity half of material property rules (scenery): writes colours, tints, floats and
    /// vectors onto the shared material assets the rules name, remembers every original value the
    /// first time a slot is written, and puts them all back on <see cref="RestoreAll"/>.
    /// <para>
    /// Called from <see cref="TextureFeature"/>'s apply pass, after its restore, so a tint is always
    /// computed from the game's own colour and two passes never compound. The write goes to the
    /// shared asset in memory (as <see cref="TextureWriteMode.Shared"/> does), which is what reaches
    /// every renderer using it - including window-view objects that are switched off right now and
    /// desk models the decoration service swaps in later. No game file is written.
    /// </para>
    /// </summary>
    internal static class MaterialPropertyRules
    {
        private static readonly object Gate = new object();
        private static readonly List<Backup> Backups = new List<Backup>();
        private static readonly HashSet<string> BackedUp = new HashSet<string>(StringComparer.Ordinal);
        private static readonly AppliedMaterialProperty[] NoneApplied = new AppliedMaterialProperty[0];

        private static IReadOnlyList<AppliedMaterialProperty> _applied = NoneApplied;

        /// <summary>The rules that reached a material in the last pass. Never null.</summary>
        internal static IReadOnlyList<AppliedMaterialProperty> Applied
        {
            get { lock (Gate) { return _applied; } }
        }

        /// <summary>How many slots are currently backed up (written by us).</summary>
        internal static int BackedUpCount
        {
            get { lock (Gate) { return Backups.Count; } }
        }

        /// <summary>
        /// Applies every rule. The caller has restored first.
        /// </summary>
        /// <param name="rules">The merged rules of every loaded pack.</param>
        /// <param name="materials">The pass's material index.</param>
        /// <param name="warnOnce">The feature's warn-once sink: (key, message).</param>
        /// <param name="failed">How many rules reached no material.</param>
        /// <returns>How many rules were applied.</returns>
        internal static int Apply(
            MaterialPropertyRuleSet rules, MaterialLookup materials, Action<string, string> warnOnce, out int failed)
        {
            failed = 0;
            var applied = new List<AppliedMaterialProperty>(rules.Count);

            foreach (var rule in rules.Rules)
            {
                try
                {
                    var result = ApplyRule(rule, materials, warnOnce);
                    if (result is null)
                    {
                        failed++;
                        continue;
                    }

                    applied.Add(result);
                    CwyLsModLoader.Log.LogDebug("TEXTURE property " + result);
                }
                catch (Exception ex)
                {
                    failed++;
                    warnOnce(
                        "propfail:" + rule.Key,
                        "TEXTURES: property rule " + rule + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            lock (Gate)
            {
                _applied = applied.Count == 0 ? NoneApplied : applied;
            }

            return applied.Count;
        }

        /// <summary>Puts every original value back, newest first, and forgets the backups.</summary>
        /// <returns>How many slots were restored (destroyed materials are skipped).</returns>
        internal static int RestoreAll()
        {
            List<Backup> backups;
            lock (Gate)
            {
                _applied = NoneApplied;
                if (Backups.Count == 0)
                {
                    return 0;
                }

                backups = new List<Backup>(Backups);
                Backups.Clear();
                BackedUp.Clear();
            }

            var restored = 0;
            for (var i = backups.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (backups[i].Restore())
                    {
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: could not restore {backups[i]}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return restored;
        }

        /// <summary>
        /// For the smoke check: every applied rule's value read back off one of its materials.
        /// </summary>
        /// <param name="mismatches">Slots whose live value is not what we wrote.</param>
        /// <returns>How many backed-up slots still hold what we wrote.</returns>
        internal static int VerifyLive(List<string> mismatches)
        {
            List<Backup> backups;
            lock (Gate)
            {
                backups = new List<Backup>(Backups);
            }

            var ok = 0;
            foreach (var backup in backups)
            {
                if (backup.HoldsWritten(out var live))
                {
                    ok++;
                }
                else
                {
                    mismatches?.Add(backup + " live=" + live);
                }
            }

            return ok;
        }

        private static AppliedMaterialProperty? ApplyRule(
            MaterialPropertyRule rule, MaterialLookup lookup, Action<string, string> warnOnce)
        {
            var targets = MaterialNamePattern.IsPattern(rule.MaterialPattern)
                ? lookup.FindMatching(rule.Matches)
                : lookup.Find(rule.MaterialPattern);

            if (targets.Count == 0)
            {
                warnOnce(
                    "propmaterial:" + rule.Key,
                    $"TEXTURES: no loaded material matches '{rule.MaterialPattern}' ({rule}); " +
                    $"scanned {lookup.Scanned} material(s) over {lookup.NameCount} name(s) - 'tex scan' lists them");
                return null;
            }

            var written = new List<string>();
            var count = 0;

            foreach (var material in targets)
            {
                if (material == null)
                {
                    continue;
                }

                var baseName = MaterialLookup.BaseNameOf(material.name);
                if (PaidContentGuard.IsRefused(baseName, out var paid))
                {
                    warnOnce("proppaid:" + baseName, "TEXTURES: " + paid + " (" + rule + ")");
                    continue;
                }

                var slots = new List<string>(2);
                WriteProperty(material, rule, rule.PropertyName, slots, warnOnce);
                foreach (var mirror in MaterialPropertyRuleParser.MirrorsOf(rule.PropertyName))
                {
                    if (material.HasProperty(mirror))
                    {
                        WriteProperty(material, rule, mirror, slots, warnOnce);
                    }
                }

                if (slots.Count == 0)
                {
                    continue;
                }

                count++;
                if (written.Count < 8)
                {
                    written.Add(baseName + "{" + string.Join("+", slots.ToArray()) + "}");
                }
            }

            if (count == 0)
            {
                warnOnce(
                    "propnowrite:" + rule.Key,
                    $"TEXTURES: nothing written for {rule}: none of the {targets.Count} matching material(s) " +
                    $"declares {rule.PropertyName} as a {Expected(rule.Kind)} property");
                return null;
            }

            var detail = string.Join(" ", written.ToArray()) + (count > written.Count ? " +" + (count - written.Count) + " more" : string.Empty);
            return new AppliedMaterialProperty(rule, count, detail);
        }

        private static void WriteProperty(
            Material material, MaterialPropertyRule rule, string property, List<string> slots, Action<string, string> warnOnce)
        {
            if (!material.HasProperty(property))
            {
                return;
            }

            var type = TypeOf(material, property);
            if (!Compatible(rule.Kind, type))
            {
                warnOnce(
                    "proptype:" + rule.Key + ":" + property,
                    $"TEXTURES: {rule.Slot} is a {Expected(rule.Kind)} rule but {MaterialLookup.BaseNameOf(material.name)}.{property} " +
                    $"is a {(type.HasValue ? type.Value.ToString() : "?")} property; skipped");
                return;
            }

            var original = Read(material, property, type);
            var value = rule.Compute(original);
            Remember(new Backup(material, property, type, original, value));
            Write(material, property, type, value);
            slots.Add(property);
        }

        private static ShaderPropertyType? TypeOf(Material material, string property)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return null;
            }

            var index = shader.FindPropertyIndex(property);
            return index < 0 ? (ShaderPropertyType?)null : shader.GetPropertyType(index);
        }

        private static bool Compatible(MaterialPropertyKind kind, ShaderPropertyType? type)
        {
            if (!type.HasValue)
            {
                return false;
            }

            switch (kind)
            {
                case MaterialPropertyKind.Float:
                    return type == ShaderPropertyType.Float || type == ShaderPropertyType.Range || type == ShaderPropertyType.Int;
                default:
                    return type == ShaderPropertyType.Color || type == ShaderPropertyType.Vector;
            }
        }

        private static string Expected(MaterialPropertyKind kind) =>
            kind == MaterialPropertyKind.Float ? "float" : kind == MaterialPropertyKind.Vector ? "vector" : "colour";

        private static Float4 Read(Material material, string property, ShaderPropertyType? type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    var c = material.GetColor(property);
                    return new Float4(c.r, c.g, c.b, c.a);
                case ShaderPropertyType.Vector:
                    var v = material.GetVector(property);
                    return new Float4(v.x, v.y, v.z, v.w);
                case ShaderPropertyType.Int:
                    return new Float4(material.GetInteger(property), 0, 0, 0);
                default:
                    return new Float4(material.GetFloat(property), 0, 0, 0);
            }
        }

        private static void Write(Material material, string property, ShaderPropertyType? type, Float4 value)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    material.SetColor(property, new Color((float)value.X, (float)value.Y, (float)value.Z, (float)value.W));
                    break;
                case ShaderPropertyType.Vector:
                    material.SetVector(property, new Vector4((float)value.X, (float)value.Y, (float)value.Z, (float)value.W));
                    break;
                case ShaderPropertyType.Int:
                    material.SetInteger(property, (int)Math.Round(value.X, MidpointRounding.AwayFromZero));
                    break;
                default:
                    material.SetFloat(property, (float)value.X);
                    break;
            }
        }

        private static void Remember(Backup backup)
        {
            lock (Gate)
            {
                if (BackedUp.Add(backup.Key))
                {
                    Backups.Add(backup);
                }
            }
        }

        /// <summary>What one material property held before a rule wrote it, and what was written.</summary>
        private sealed class Backup
        {
            internal Backup(Material material, string property, ShaderPropertyType? type, Float4 original, Float4 written)
            {
                Material = material;
                Name = MaterialLookup.BaseNameOf(material.name);
                Property = property;
                Type = type;
                Original = original;
                Written = written;
                Key = material.GetInstanceID().ToString(CultureInfo.InvariantCulture) + "." + property;
            }

            internal Material Material { get; }

            internal string Name { get; }

            internal string Property { get; }

            internal ShaderPropertyType? Type { get; }

            internal Float4 Original { get; }

            internal Float4 Written { get; }

            internal string Key { get; }

            internal bool Restore()
            {
                if (Material == null)
                {
                    return false;
                }

                Write(Material, Property, Type, Original);
                return true;
            }

            internal bool HoldsWritten(out string live)
            {
                if (Material == null)
                {
                    live = "destroyed";
                    return false;
                }

                var value = Read(Material, Property, Type);
                live = value.ToString();
                return Math.Abs(value.X - Written.X) < 1e-3 && Math.Abs(value.Y - Written.Y) < 1e-3 &&
                       Math.Abs(value.Z - Written.Z) < 1e-3 && Math.Abs(value.W - Written.W) < 1e-3;
            }

            public override string ToString() => "@" + Name + "." + Property;
        }
    }
}
