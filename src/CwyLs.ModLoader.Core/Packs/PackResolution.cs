using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// The outcome of one pass of <see cref="PackResolver.Resolve"/>: what will load, in what
    /// order, and what will not, with reasons.
    /// </summary>
    public sealed class PackResolution
    {
        private static readonly ResolvedPack[] NoPacks = new ResolvedPack[0];
        private static readonly SkippedPack[] NoSkips = new SkippedPack[0];
        private static readonly PackWarning[] NoWarnings = new PackWarning[0];

        /// <summary>Creates a resolution.</summary>
        /// <param name="loaded">Packs to load, already in dependency order.</param>
        /// <param name="skipped">Packs that were refused, with reasons.</param>
        public PackResolution(IReadOnlyList<ResolvedPack>? loaded, IReadOnlyList<SkippedPack>? skipped)
        {
            Loaded = loaded ?? NoPacks;
            Skipped = skipped ?? NoSkips;
            Warnings = CollectWarnings(Loaded);
        }

        /// <summary>An outcome with nothing found at all.</summary>
        public static PackResolution Empty { get; } = new PackResolution(NoPacks, NoSkips);

        /// <summary>
        /// Packs to load, ordered so that every pack comes after the packs it depends on.
        /// Packs that do not depend on each other keep discovery order.
        /// </summary>
        public IReadOnlyList<ResolvedPack> Loaded { get; }

        /// <summary>Packs that were found but refused, in discovery order.</summary>
        public IReadOnlyList<SkippedPack> Skipped { get; }

        /// <summary>
        /// Packs that loaded but deserve one warning line each, in load order - today, a pack made
        /// for another game version (<see cref="ResolvedPack.GameVersionCheck"/>). Only loaded packs
        /// appear: a skipped pack is already reported by its skip line. Never null.
        /// </summary>
        public IReadOnlyList<PackWarning> Warnings { get; }

        /// <summary>
        /// The one line the framework logs at startup:
        /// <c>PACKS: 1 loaded (cwy.example@0.1.0); 0 skipped</c>. A list is omitted when empty, so
        /// a clean start is a short line and a problem is never hidden behind a count.
        /// </summary>
        public string Summary
        {
            get
            {
                var sb = new StringBuilder("PACKS: ");
                sb.Append(Loaded.Count.ToString(CultureInfo.InvariantCulture)).Append(" loaded");

                if (Loaded.Count > 0)
                {
                    sb.Append(" (");
                    for (var i = 0; i < Loaded.Count; i++)
                    {
                        if (i > 0) { sb.Append(", "); }

                        sb.Append(Loaded[i].Manifest.Id).Append('@').Append(Loaded[i].Manifest.Version);
                    }

                    sb.Append(')');
                }

                sb.Append("; ").Append(Skipped.Count.ToString(CultureInfo.InvariantCulture)).Append(" skipped");

                if (Skipped.Count > 0)
                {
                    sb.Append(" (");
                    for (var i = 0; i < Skipped.Count; i++)
                    {
                        if (i > 0) { sb.Append(", "); }

                        sb.Append(Skipped[i].Id).Append(": ").Append(Skipped[i].Reason);
                    }

                    sb.Append(')');
                }

                return sb.ToString();
            }
        }

        /// <summary>The resolved pack with this id, or null when it did not load.</summary>
        /// <param name="id">A pack id.</param>
        public ResolvedPack? Find(string? id)
        {
            if (id is null)
            {
                return null;
            }

            foreach (var pack in Loaded)
            {
                if (string.Equals(pack.Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }

            return null;
        }

        /// <summary>The skip record for this id, or null when it was not skipped.</summary>
        /// <param name="id">A pack id, or the folder name of a pack whose manifest never parsed.</param>
        public SkippedPack? FindSkipped(string? id)
        {
            if (id is null)
            {
                return null;
            }

            foreach (var skip in Skipped)
            {
                if (string.Equals(skip.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return skip;
                }
            }

            return null;
        }

        /// <inheritdoc />
        public override string ToString() => Summary;

        private static IReadOnlyList<PackWarning> CollectWarnings(IReadOnlyList<ResolvedPack> loaded)
        {
            List<PackWarning>? warnings = null;
            foreach (var pack in loaded)
            {
                var warning = pack?.GameVersionCheck?.Warning;
                if (warning is null)
                {
                    continue;
                }

                warnings ??= new List<PackWarning>();
                warnings.Add(new PackWarning(pack!.Id, pack.Source.FolderName, warning));
            }

            return warnings ?? (IReadOnlyList<PackWarning>)NoWarnings;
        }
    }
}
