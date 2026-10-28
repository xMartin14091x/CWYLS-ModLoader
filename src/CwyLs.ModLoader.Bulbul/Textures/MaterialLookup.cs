using System;
using System.Collections.Generic;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Finds material assets by name, so a rule can target <c>mt_Tee_A</c> instead of a renderer it
    /// has no way to name.
    /// <para>
    /// Two sources, merged: every material any scene renderer currently uses
    /// (<c>sharedMaterials</c>, inactive renderers included), and every loaded <c>Material</c>
    /// object. The second matters because a costume Satone is not wearing yet still has its material
    /// loaded - writing it now means the texture is already there when the game instantiates the
    /// prefab.
    /// </para>
    /// </summary>
    internal sealed class MaterialLookup
    {
        /// <summary>The suffix Unity appends when a renderer instantiates a material.</summary>
        internal const string InstanceSuffix = " (Instance)";

        private static readonly Material[] NoMaterials = new Material[0];

        private readonly Dictionary<string, List<Material>> _byName =
            new Dictionary<string, List<Material>>(StringComparer.OrdinalIgnoreCase);

        private readonly HashSet<int> _seen = new HashSet<int>();

        private MaterialLookup()
        {
        }

        /// <summary>How many distinct material objects the scan saw.</summary>
        public int Scanned => _seen.Count;

        /// <summary>How many distinct base names those materials had.</summary>
        public int NameCount => _byName.Count;

        /// <summary>
        /// Scans the renderers a pass already indexed, then every loaded material.
        /// </summary>
        /// <param name="renderers">The pass's renderer index, scanned first so scene materials win the order.</param>
        public static MaterialLookup Scan(RendererLookup renderers)
        {
            var lookup = new MaterialLookup();

            foreach (var renderer in renderers.AllRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                // sharedMaterials, never materials: reading the instance array would instantiate a
                // copy and rebind the renderer to it, which is precisely the trap this whole class
                // exists to avoid.
                foreach (var material in SafeShared(renderer!))
                {
                    lookup.Add(material);
                }
            }

            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
            {
                lookup.Add(material);
            }

            return lookup;
        }

        /// <summary>
        /// Every loaded material whose base name matches, in scan order. Never null.
        /// </summary>
        /// <param name="name">A material asset name, e.g. <c>mt_Tee_A</c>. No <c>@</c> prefix.</param>
        public IReadOnlyList<Material> Find(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || !_byName.TryGetValue(BaseNameOf(name!), out var bucket))
            {
                return NoMaterials;
            }

            return bucket;
        }

        /// <summary>
        /// Every loaded material whose base name satisfies <paramref name="match"/> - how a wildcard
        /// rule (<c>mt_env_*</c>) finds its materials. Never null.
        /// </summary>
        /// <param name="match">Takes a base name; true to include that name's materials.</param>
        public IReadOnlyList<Material> FindMatching(Func<string, bool> match)
        {
            if (match is null)
            {
                return NoMaterials;
            }

            var found = new List<Material>();
            foreach (var pair in _byName)
            {
                if (match(pair.Key))
                {
                    found.AddRange(pair.Value);
                }
            }

            return found;
        }

        /// <summary>Every base name the scan saw, with its materials. For <c>tex scan</c>.</summary>
        public IEnumerable<KeyValuePair<string, List<Material>>> ByName => _byName;

        /// <summary>
        /// A material's name with every <c>" (Instance)"</c> suffix stripped, so an instance is
        /// matched by the asset it was copied from.
        /// </summary>
        /// <param name="name">A <c>Material.name</c>, or a rule's material name.</param>
        /// <remarks>
        /// The suffix can repeat - an instance of an instance is
        /// <c>mt_Body (Instance) (Instance)</c> - so this strips until none is left.
        /// </remarks>
        public static string BaseNameOf(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var text = name!.Trim();
            while (text.EndsWith(InstanceSuffix, StringComparison.Ordinal))
            {
                text = text.Substring(0, text.Length - InstanceSuffix.Length).TrimEnd();
            }

            return text;
        }

        private void Add(Material? material)
        {
            if (material == null || !_seen.Add(material!.GetInstanceID()))
            {
                return;
            }

            var name = BaseNameOf(material.name);
            if (name.Length == 0)
            {
                return;
            }

            if (!_byName.TryGetValue(name, out var bucket))
            {
                bucket = new List<Material>(1);
                _byName.Add(name, bucket);
            }

            bucket.Add(material);
        }

        private static Material[] SafeShared(Renderer renderer)
        {
            try
            {
                return renderer.sharedMaterials ?? NoMaterials;
            }
            catch (Exception ex)
            {
                // A half-destroyed renderer must not abort the scan for every other rule.
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not read shared materials of {RendererLookup.PathOf(renderer)}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return NoMaterials;
            }
        }
    }
}
