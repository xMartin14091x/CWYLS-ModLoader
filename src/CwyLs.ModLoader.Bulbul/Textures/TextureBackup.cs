using System;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>Where one backed-up texture slot lives.</summary>
    internal enum TextureTarget
    {
        /// <summary><c>renderer.materials[i]</c> - the renderer's own copy.</summary>
        Instance = 0,

        /// <summary><c>renderer.sharedMaterials[i]</c> - the loaded material asset, in memory.</summary>
        Shared,

        /// <summary>A <c>MaterialPropertyBlock</c> on the renderer, which overrides the material.</summary>
        PropertyBlock,
    }

    /// <summary>
    /// What one slot held before a pack wrote to it, so <see cref="TextureFeature.RestoreAll"/> can
    /// put the game back exactly as it was - and what we wrote, so a later pass can tell whether
    /// the game reverted it behind our back.
    /// <para>
    /// A null <see cref="Original"/> is a real value - most lilToon slots ship empty - and is
    /// restored as null on a material. A property block cannot have a property removed, only
    /// overwritten, which is why we only ever touch a block that already carried the property.
    /// </para>
    /// </summary>
    internal sealed class TextureBackup
    {
        /// <summary>Records what a slot held.</summary>
        /// <param name="target">Which of the three places this slot is.</param>
        /// <param name="renderer">The renderer; required for <see cref="TextureTarget.PropertyBlock"/>.</param>
        /// <param name="material">The material written, or null for a property block.</param>
        /// <param name="materialIndex">Index of the material in the renderer.</param>
        /// <param name="property">The shader property written.</param>
        /// <param name="original">What the property held.</param>
        /// <param name="written">What we put there, for the reversion audit.</param>
        /// <exception cref="ArgumentException"><paramref name="property"/> is blank, or the target's required object is missing.</exception>
        internal TextureBackup(
            TextureTarget target,
            Renderer? renderer,
            Material? material,
            int materialIndex,
            string property,
            Texture? original,
            Texture? written)
        {
            if (string.IsNullOrWhiteSpace(property))
            {
                throw new ArgumentException("a shader property name is required", nameof(property));
            }

            if (target == TextureTarget.PropertyBlock)
            {
                if (renderer == null)
                {
                    throw new ArgumentException("a property block needs its renderer", nameof(renderer));
                }
            }
            else if (material == null)
            {
                throw new ArgumentException("a material target needs its material", nameof(material));
            }

            Target = target;
            Renderer = renderer;
            Material = material;
            MaterialIndex = materialIndex;
            Property = property;
            Original = original;
            Written = written;
        }

        /// <summary>Which of the three places this slot is.</summary>
        internal TextureTarget Target { get; }

        /// <summary>
        /// A renderer the slot belongs to, for the log line only. Null for a material rule, which
        /// is keyed on the material asset and may have no renderer using it yet.
        /// </summary>
        internal Renderer? Renderer { get; }

        /// <summary>The material written, or null for a property block.</summary>
        internal Material? Material { get; }

        /// <summary>Index of the material in the renderer.</summary>
        internal int MaterialIndex { get; }

        /// <summary>The shader property written.</summary>
        internal string Property { get; }

        /// <summary>What the property held before we wrote it.</summary>
        internal Texture? Original { get; }

        /// <summary>What we wrote. Compared against the live value to detect a revert.</summary>
        internal Texture? Written { get; }

        /// <summary>
        /// What makes two backups the same slot. The object's instance id, so the same material
        /// reached through two renderers is backed up once, and an instance material and the shared
        /// asset it was copied from stay separate.
        /// </summary>
        /// <remarks>
        /// Deliberately keyed on the OBJECT's instance id and not on the renderer or the material
        /// index: the same asset reached through ten renderers is one slot, backed up once and
        /// restored once. Two indices on one renderer are two distinct Material objects anyway.
        /// </remarks>
        internal string Key =>
            Target + ":" +
            (Target == TextureTarget.PropertyBlock
                ? (Renderer == null ? "0" : Renderer!.GetInstanceID().ToString())
                : (Material == null ? "0" : Material!.GetInstanceID().ToString())) +
            "." + Property;

        /// <summary>
        /// The live value of this slot right now, and whether the object holding it still exists.
        /// </summary>
        /// <param name="alive">False when the material, renderer or block is gone.</param>
        internal Texture? ReadLive(out bool alive)
        {
            alive = false;

            if (Target == TextureTarget.PropertyBlock)
            {
                if (Renderer == null || !Renderer!.HasPropertyBlock())
                {
                    return null;
                }

                var block = new MaterialPropertyBlock();
                Renderer.GetPropertyBlock(block);
                alive = true;
                return block.GetTexture(Property);
            }

            if (Material == null)
            {
                return null;
            }

            alive = true;
            return Material!.GetTexture(Property);
        }

        /// <summary>
        /// Puts the original back.
        /// </summary>
        /// <returns>
        /// False when the object has been destroyed since - a scene reload, a costume swap - which
        /// is not an error: there is nothing left to restore.
        /// </returns>
        internal bool Restore()
        {
            if (Target == TextureTarget.PropertyBlock)
            {
                if (Renderer == null || !Renderer!.HasPropertyBlock())
                {
                    return false;
                }

                var block = new MaterialPropertyBlock();
                Renderer.GetPropertyBlock(block);

                // A block cannot have a property removed, only overwritten - which is why we only
                // ever write a block that already carried this property, so Original is non-null.
                if (Original == null)
                {
                    return false;
                }

                block.SetTexture(Property, Original!);
                Renderer.SetPropertyBlock(block);
                return true;
            }

            if (Material == null)
            {
                return false;
            }

            Material!.SetTexture(Property, Original);
            return true;
        }

        /// <inheritdoc />
        public override string ToString() =>
            RendererLookup.PathOf(Renderer) + "[" + MaterialIndex + "]." + Property + " (" + Target + ")";
    }
}
