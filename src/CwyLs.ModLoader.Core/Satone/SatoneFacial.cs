using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// One value of the <c>Facial</c> Animator integer: the expression
    /// <c>HeroineService.ChangeHeroineFacialAnimation(int)</c> takes, with a name attached.
    /// </summary>
    /// <remarks>
    /// Built from the <c>Facial</c> layer of the game's
    /// <c>Character_Animator_ForScript.controller</c> (game 1.17.3): each <c>Facial == n</c> transition names the
    /// state the expression plays, and the game's <c>FacialType</c> enum names seven of the twenty.
    /// </remarks>
    public sealed class SatoneFacial
    {
        private static readonly string[] NoAliases = new string[0];

        /// <summary>Creates a row. Only <paramref name="name"/> is required.</summary>
        /// <param name="id">The integer the game takes, 0-19 in v1.17.2.</param>
        /// <param name="name">Canonical name, e.g. <c>smile</c>. Required.</param>
        /// <param name="facialType">Matching <c>FacialType</c> member, or null when the enum does not cover this id.</param>
        /// <param name="state">Animator state in the <c>Facial</c> layer, or null.</param>
        /// <param name="clip">Animation clip the state plays, or null.</param>
        /// <param name="loopable">Whether that clip loops; null when there is no clip.</param>
        /// <param name="meshes">Blendshapes the clip drives; null becomes <see cref="SatoneFaceMeshes.Empty"/>.</param>
        /// <param name="usedByLines">How many <c>NovelMaster</c> rows use this id as <c>FacialMotion</c>.</param>
        /// <param name="aliases">Other names that resolve to this row; null becomes empty.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public SatoneFacial(
            int id,
            string name,
            string? facialType,
            string? state,
            string? clip,
            bool? loopable,
            SatoneFaceMeshes? meshes,
            int usedByLines,
            IReadOnlyList<string>? aliases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("a facial needs a name", nameof(name));
            }

            Id = id;
            Name = name;
            FacialType = facialType;
            State = state;
            Clip = clip;
            Loopable = loopable;
            Meshes = meshes ?? SatoneFaceMeshes.Empty;
            UsedByLines = usedByLines;
            Aliases = aliases ?? NoAliases;
        }

        /// <summary>The integer passed to <c>HeroineService.ChangeHeroineFacialAnimation</c>.</summary>
        public int Id { get; }

        /// <summary>Canonical name a mod types, e.g. <c>smile</c>.</summary>
        public string Name { get; }

        /// <summary>The game's own <c>FacialType</c> member, when it has one (only 0-6 and 18 do).</summary>
        public string? FacialType { get; }

        /// <summary>Animator state name, which is also what <c>IsCurrentAnimation</c> matches.</summary>
        public string? State { get; }

        /// <summary>Animation clip the state plays, e.g. <c>anim_face_shy_smile</c>.</summary>
        public string? Clip { get; }

        /// <summary>Whether the clip loops; null when the state has no clip.</summary>
        public bool? Loopable { get; }

        /// <summary>The blendshapes this expression drives.</summary>
        public SatoneFaceMeshes Meshes { get; }

        /// <summary>Rows in the game's own <c>NovelMaster</c> that select this expression.</summary>
        public int UsedByLines { get; }

        /// <summary>Other accepted names - the <c>FacialType</c> member, the clip, the state.</summary>
        public IReadOnlyList<string> Aliases { get; }

        /// <summary>e.g. <c>smile (id 10)</c>.</summary>
        public override string ToString() => Name + " (id " + Id + ")";
    }
}
