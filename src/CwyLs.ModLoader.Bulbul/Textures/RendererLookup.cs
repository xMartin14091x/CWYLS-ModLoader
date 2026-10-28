using System;
using System.Collections.Generic;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Finds the renderers a rule names, by GameObject name, across the loaded scenes.
    /// <para>
    /// Built once per apply pass rather than searched per rule: <c>RoomScene</c> holds thousands of
    /// objects and a pack may carry dozens of rules.
    /// </para>
    /// <para>
    /// Inactive objects are included on purpose. Satone's outfit pieces (<c>Top1</c>, <c>Paka1</c>,
    /// the hairpins) are switched on and off as costumes change, so a texture applied only to what
    /// happens to be visible at scene load would silently miss half the character.
    /// </para>
    /// </summary>
    internal sealed class RendererLookup
    {
        /// <summary>
        /// Substrings that mark the character's own hierarchy. When one name matches more than one
        /// renderer, a candidate whose scene path contains one of these is preferred - in the
        /// game's RoomScene (1.17.x) Satone's renderers sit under <c>Character/Character/</c>.
        /// </summary>
        internal static readonly string[] PreferredPathMarkers = { "Heroine", "Character" };

        private static readonly Renderer[] NoRenderers = new Renderer[0];

        private readonly Dictionary<string, List<Renderer>> _byName =
            new Dictionary<string, List<Renderer>>(StringComparer.OrdinalIgnoreCase);

        private readonly List<Renderer> _all = new List<Renderer>();

        private RendererLookup(int scanned)
        {
            Scanned = scanned;
        }

        /// <summary>How many live scene renderers the scan saw.</summary>
        public int Scanned { get; }

        /// <summary>How many distinct GameObject names those renderers had.</summary>
        public int NameCount => _byName.Count;

        /// <summary>Every scene renderer the scan kept, in scan order. Never null.</summary>
        public IReadOnlyList<Renderer> AllRenderers => _all;

        /// <summary>
        /// Scans every loaded scene, including inactive objects, and indexes the renderers by
        /// GameObject name.
        /// </summary>
        /// <remarks>
        /// <c>Resources.FindObjectsOfTypeAll</c> also returns renderers that belong to assets and to
        /// objects hidden from the hierarchy - prefabs the game keeps loaded, for one - so the scan
        /// keeps only objects that belong to a real scene.
        /// </remarks>
        public static RendererLookup Scan()
        {
            var all = Resources.FindObjectsOfTypeAll<Renderer>();
            var lookup = new RendererLookup(all.Length);

            foreach (var renderer in all)
            {
                if (renderer == null)
                {
                    continue;
                }

                var go = renderer.gameObject;
                if (go == null || !go.scene.IsValid())
                {
                    continue;
                }

                if (!lookup._byName.TryGetValue(go.name, out var bucket))
                {
                    bucket = new List<Renderer>(1);
                    lookup._byName.Add(go.name, bucket);
                }

                bucket.Add(renderer);
                lookup._all.Add(renderer);
            }

            return lookup;
        }

        /// <summary>
        /// Every renderer that is actually being drawn and uses <paramref name="material"/> as one
        /// of its shared materials.
        /// <para>
        /// This is how a costume swap becomes visible in the log. <c>CostumeChangeService</c>
        /// deactivates <c>Body</c> and instantiates a prefab whose skin renderer is called
        /// <c>Body1</c> - a different name, so no rule can name it - but both use the same
        /// <c>mt_Body</c> asset, so this finds it.
        /// </para>
        /// </summary>
        /// <param name="material">The material to look for.</param>
        /// <param name="except">A renderer to leave out, normally the one already chosen.</param>
        public IReadOnlyList<Renderer> ActiveUsersOf(Material material, Renderer? except = null)
        {
            var users = new List<Renderer>();
            if (material == null)
            {
                return users;
            }

            var id = material!.GetInstanceID();

            foreach (var renderer in _all)
            {
                if (renderer == null || renderer == except || !renderer!.gameObject.activeInHierarchy)
                {
                    continue;
                }

                foreach (var shared in renderer.sharedMaterials)
                {
                    if (shared != null && shared!.GetInstanceID() == id)
                    {
                        users.Add(renderer);
                        break;
                    }
                }
            }

            return users;
        }

        /// <summary>Any renderer using this material, active or not, or null when none does.</summary>
        /// <param name="material">The material to look for.</param>
        /// <remarks>Only for a log line - a material rule is keyed on the material, not a renderer.</remarks>
        public Renderer? AnyUserOf(Material material)
        {
            if (material == null)
            {
                return null;
            }

            var id = material!.GetInstanceID();

            foreach (var renderer in _all)
            {
                if (renderer == null)
                {
                    continue;
                }

                foreach (var shared in renderer!.sharedMaterials)
                {
                    if (shared != null && shared!.GetInstanceID() == id)
                    {
                        return renderer;
                    }
                }
            }

            return null;
        }

        /// <summary>Every renderer with this GameObject name, in scan order. Never null.</summary>
        /// <param name="name">A GameObject name; matched case-insensitively.</param>
        public IReadOnlyList<Renderer> All(string? name)
        {
            if (string.IsNullOrEmpty(name) || !_byName.TryGetValue(name!, out var bucket))
            {
                return NoRenderers;
            }

            return bucket;
        }

        /// <summary>
        /// The renderer a rule should use.
        /// </summary>
        /// <param name="name">A GameObject name, e.g. <c>Body</c>.</param>
        /// <param name="ambiguity">
        /// When more than one renderer carries the name, a message listing every candidate path and
        /// naming the one that was chosen; null when the name was unique or unknown. The caller
        /// logs it as a warning - it is a real risk of patching the wrong object, not an error.
        /// </param>
        /// <returns>The chosen renderer, or null when nothing in the scene has that name.</returns>
        public Renderer? Find(string? name, out string? ambiguity)
        {
            ambiguity = null;

            var candidates = All(name);
            if (candidates.Count == 0)
            {
                return null;
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // In order: an active renderer under the character, then any renderer under the
            // character, then whatever came first. RoomScene has two GameObjects called
            // "m_Headphone" - one parented to Character_Head and one to Character_Spine2, only the
            // first of them active - and both sit under "Character", so the marker alone does not
            // separate them and the active one is the one the player sees.
            var chosen = Prefer(candidates, requireActive: true)
                         ?? Prefer(candidates, requireActive: false)
                         ?? candidates[0];

            var paths = new List<string>(candidates.Count);
            foreach (var candidate in candidates)
            {
                paths.Add(PathOf(candidate) + (IsActive(candidate) ? string.Empty : " (inactive)"));
            }

            ambiguity =
                $"'{name}' matches {candidates.Count} renderers: {string.Join(" | ", paths.ToArray())}; " +
                $"using {PathOf(chosen)}";

            return chosen;
        }

        /// <summary>
        /// The object's path in its scene, e.g. <c>Character/Character/Body</c>. Cheap enough for a
        /// log line; not called per frame.
        /// </summary>
        /// <param name="renderer">Any renderer; a destroyed one yields <c>&lt;destroyed&gt;</c>.</param>
        public static string PathOf(Renderer? renderer)
        {
            if (renderer == null)
            {
                return "<destroyed>";
            }

            var transform = renderer!.transform;
            var parts = new List<string>(8);

            // Bounded so a hierarchy that somehow contains a cycle cannot hang the log line.
            for (var depth = 0; transform != null && depth < 64; depth++)
            {
                parts.Add(transform!.name);
                transform = transform.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>The first candidate under the character's hierarchy, or null when there is none.</summary>
        /// <param name="candidates">The renderers sharing a name.</param>
        /// <param name="requireActive">True to consider only renderers that are actually rendering.</param>
        private static Renderer? Prefer(IReadOnlyList<Renderer> candidates, bool requireActive)
        {
            foreach (var candidate in candidates)
            {
                if (requireActive && !IsActive(candidate))
                {
                    continue;
                }

                if (IsPreferred(PathOf(candidate)))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static bool IsActive(Renderer? renderer) =>
            renderer != null && renderer!.gameObject.activeInHierarchy;

        private static bool IsPreferred(string path)
        {
            foreach (var marker in PreferredPathMarkers)
            {
                if (path.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
