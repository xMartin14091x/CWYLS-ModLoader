using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Mods
{
    /// <summary>
    /// One code mod the loader will try, in the order the pack resolver placed it, with the code
    /// mods it depends on split into required and optional.
    /// </summary>
    public sealed class ModCandidate
    {
        /// <summary>Creates a candidate.</summary>
        /// <param name="pack">The resolved pack whose manifest has an <c>entryDll</c>.</param>
        /// <param name="requiredCodeMods">Ids of the code mods it requires.</param>
        /// <param name="optionalCodeMods">Ids of the code mods it optionally depends on (present ones only).</param>
        /// <exception cref="ArgumentNullException"><paramref name="pack"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="pack"/> is not a code mod.</exception>
        public ModCandidate(ResolvedPack pack, IReadOnlyList<string>? requiredCodeMods, IReadOnlyList<string>? optionalCodeMods)
        {
            Pack = pack ?? throw new ArgumentNullException(nameof(pack));
            if (!pack.Manifest.IsCodeMod)
            {
                throw new ArgumentException(pack.Id + " has no entryDll", nameof(pack));
            }

            RequiredCodeMods = requiredCodeMods ?? Array.Empty<string>();
            OptionalCodeMods = optionalCodeMods ?? Array.Empty<string>();
        }

        /// <summary>The resolved pack.</summary>
        public ResolvedPack Pack { get; }

        /// <summary>Shorthand for the manifest id.</summary>
        public string Id => Pack.Id;

        /// <summary>Shorthand for the manifest.</summary>
        public PackManifest Manifest => Pack.Manifest;

        /// <summary>The normalised entry DLL path inside the pack folder.</summary>
        public string EntryDll => Pack.Manifest.EntryDll!;

        /// <summary>Ids of the code mods this one requires; each must reach <see cref="ModState.Loaded"/> first.</summary>
        public IReadOnlyList<string> RequiredCodeMods { get; }

        /// <summary>Ids of installed code mods this one optionally depends on; they load first, and their failure is ignored.</summary>
        public IReadOnlyList<string> OptionalCodeMods { get; }

        /// <inheritdoc />
        public override string ToString() => Pack.ToString();
    }

    /// <summary>
    /// Picks the code mods out of a <see cref="PackResolution"/>. Pure: the order is the
    /// resolver's, so dependencies (packs and code mods in one graph) always come first.
    /// </summary>
    public static class ModLoadPlanner
    {
        /// <summary>The code mods of a resolution, in load order.</summary>
        /// <param name="resolution">The resolver's result; null plans nothing.</param>
        public static IReadOnlyList<ModCandidate> Plan(PackResolution? resolution)
        {
            if (resolution is null || resolution.Loaded.Count == 0)
            {
                return Array.Empty<ModCandidate>();
            }

            var codeMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in resolution.Loaded)
            {
                if (pack.Manifest.IsCodeMod)
                {
                    codeMods.Add(pack.Id);
                }
            }

            var plan = new List<ModCandidate>(codeMods.Count);
            foreach (var pack in resolution.Loaded)
            {
                if (!pack.Manifest.IsCodeMod)
                {
                    continue;
                }

                List<string>? required = null;
                List<string>? optional = null;
                foreach (var dependency in pack.Manifest.AllDependencies)
                {
                    if (!codeMods.Contains(dependency.Id))
                    {
                        continue;
                    }

                    if (dependency.IsOptional)
                    {
                        (optional ??= new List<string>()).Add(dependency.Id);
                    }
                    else
                    {
                        (required ??= new List<string>()).Add(dependency.Id);
                    }
                }

                plan.Add(new ModCandidate(pack, required, optional));
            }

            return plan;
        }
    }

    /// <summary>
    /// The one runtime gate between the resolver and a mod's code: every code mod it requires
    /// must have loaded. Pure, so the cascade is unit-tested.
    /// </summary>
    public static class ModLoadGate
    {
        /// <summary>
        /// Decides whether a candidate may load, given what happened to the mods before it.
        /// </summary>
        /// <param name="candidate">The candidate.</param>
        /// <param name="statuses">The outcome of every mod handled so far, by id.</param>
        /// <param name="detail">Why not, naming the dependency and what happened to it; empty when it may load.</param>
        /// <returns>True when every required code mod is <see cref="ModState.Loaded"/>.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static bool CanLoad(
            ModCandidate candidate,
            IReadOnlyDictionary<string, ModStatus> statuses,
            out string detail)
        {
            if (candidate is null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (statuses is null)
            {
                throw new ArgumentNullException(nameof(statuses));
            }

            StringBuilder? problems = null;
            foreach (var id in candidate.RequiredCodeMods)
            {
                if (statuses.TryGetValue(id, out var status) && status.State == ModState.Loaded)
                {
                    continue;
                }

                problems ??= new StringBuilder("requires ");
                if (problems.Length > "requires ".Length)
                {
                    problems.Append("; ");
                }

                problems.Append(id).Append(", which ").Append(Describe(status));
            }

            detail = problems?.ToString() ?? string.Empty;
            return problems is null;
        }

        private static string Describe(ModStatus? status)
        {
            if (status is null)
            {
                return "has not loaded";
            }

            switch (status.State)
            {
                case ModState.Skipped:
                    return "was skipped (" + status.ReasonText + ")";
                case ModState.Failed:
                    return "failed in " + status.ReasonText;
                default:
                    return "is " + status.State;
            }
        }
    }
}
