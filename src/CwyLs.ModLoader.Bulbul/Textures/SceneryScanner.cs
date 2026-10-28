using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Textures;
using UnityEngine;
using UnityEngine.Rendering;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// The Unity half of <c>tex scan</c> / <c>tex export</c>: reads every loaded material whose name
    /// matches the filter - shader, render queue, the renderers using it, and every shader property
    /// with its live value (via <c>Shader.GetPropertyCount/Name/Type</c>) - into Core's
    /// <see cref="MaterialScanEntry"/>. Read-only; touches nothing.
    /// </summary>
    internal static class SceneryScanner
    {
        /// <summary>Most entries a scan returns; a filter of <c>*</c> on a full scene stays bounded.</summary>
        internal const int MaxEntries = 500;

        /// <summary>Scans the loaded materials.</summary>
        /// <param name="patterns">Name patterns; empty means <see cref="SceneryMaterials.DefaultScanPatterns"/>.</param>
        /// <param name="withProperties">False for a fast console listing (summary properties only).</param>
        /// <param name="truncated">True when <see cref="MaxEntries"/> cut the list.</param>
        /// <returns>The entries, sorted by name.</returns>
        internal static List<MaterialScanEntry> Scan(IReadOnlyList<string> patterns, bool withProperties, out bool truncated)
        {
            truncated = false;
            var filter = patterns.Count == 0 ? SceneryMaterials.DefaultScanPatterns : patterns;
            var renderers = RendererLookup.Scan();
            var materials = MaterialLookup.Scan(renderers);
            var users = UsersByMaterial(renderers);
            var entries = new List<MaterialScanEntry>();

            foreach (var pair in materials.ByName)
            {
                if (!SceneryMaterials.AnyMatch(filter, pair.Key))
                {
                    continue;
                }

                foreach (var material in pair.Value)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    if (entries.Count >= MaxEntries)
                    {
                        truncated = true;
                        break;
                    }

                    entries.Add(Describe(material, pair.Key, users, withProperties));
                }
            }

            entries.Sort((a, b) =>
            {
                var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : a.IsInstance.CompareTo(b.IsInstance);
            });
            return entries;
        }

        /// <summary>One pass over the renderers: material instance id to the renderers using it.</summary>
        private static Dictionary<int, List<MaterialScanUser>> UsersByMaterial(RendererLookup renderers)
        {
            var users = new Dictionary<int, List<MaterialScanUser>>();
            foreach (var renderer in renderers.AllRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                Material[] shared;
                try
                {
                    shared = renderer.sharedMaterials;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug($"TEXTURES scan: could not read {RendererLookup.PathOf(renderer)}: {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                var user = new MaterialScanUser(
                    RendererLookup.PathOf(renderer), renderer.enabled && renderer.gameObject.activeInHierarchy);
                foreach (var material in shared)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    var id = material.GetInstanceID();
                    if (!users.TryGetValue(id, out var list))
                    {
                        list = new List<MaterialScanUser>(1);
                        users.Add(id, list);
                    }

                    list.Add(user);
                }
            }

            return users;
        }

        private static MaterialScanEntry Describe(
            Material material, string baseName, Dictionary<int, List<MaterialScanUser>> usersByMaterial, bool withProperties)
        {
            usersByMaterial.TryGetValue(material.GetInstanceID(), out var users);

            var shader = material.shader;
            return new MaterialScanEntry(
                baseName,
                material.name.EndsWith(MaterialLookup.InstanceSuffix, StringComparison.Ordinal),
                material.GetInstanceID(),
                shader == null ? "(none)" : shader.name,
                material.renderQueue,
                users,
                ReadProperties(material, shader, withProperties));
        }

        private static List<MaterialScanProperty> ReadProperties(Material material, Shader? shader, bool all)
        {
            var properties = new List<MaterialScanProperty>();
            if (shader == null)
            {
                return properties;
            }

            var count = shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var name = shader.GetPropertyName(i);
                if (!all && !Contains(MaterialScan.SummaryProperties, name))
                {
                    continue;
                }

                try
                {
                    properties.Add(Read(material, name, shader.GetPropertyType(i)));
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug($"TEXTURES scan: {material.name}.{name}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return properties;
        }

        private static MaterialScanProperty Read(Material material, string name, ShaderPropertyType type)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    var c = material.GetColor(name);
                    return new MaterialScanProperty(name, MaterialValueType.Color, c.r, c.g, c.b, c.a);
                case ShaderPropertyType.Vector:
                    var v = material.GetVector(name);
                    return new MaterialScanProperty(name, MaterialValueType.Vector, v.x, v.y, v.z, v.w);
                case ShaderPropertyType.Range:
                    return new MaterialScanProperty(name, MaterialValueType.Range, material.GetFloat(name));
                case ShaderPropertyType.Int:
                    return new MaterialScanProperty(name, MaterialValueType.Int, material.GetInteger(name));
                case ShaderPropertyType.Texture:
                    var t = material.GetTexture(name);
                    return t == null
                        ? new MaterialScanProperty(name, null, 0, 0)
                        : new MaterialScanProperty(name, t.name, t.width, t.height);
                default:
                    return new MaterialScanProperty(name, MaterialValueType.Float, material.GetFloat(name));
            }
        }

        private static bool Contains(IReadOnlyList<string> list, string value)
        {
            foreach (var item in list)
            {
                if (string.Equals(item, value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
