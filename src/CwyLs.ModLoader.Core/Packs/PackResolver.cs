using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Packs
{
    /// <summary>
    /// Decides which discovered packs and code mods load, in which order, and reports every
    /// refusal with a reason. Pure logic over <see cref="IPackSource"/>: no filesystem, no Unity,
    /// no logging - which is what lets the whole policy be unit-tested. Content packs and code
    /// mods (manifests with <c>entryDll</c>) share this one dependency graph.
    /// <para>The gates are applied in this order, and the first one that refuses wins:</para>
    /// <list type="number">
    ///   <item><description>manifest present, then valid</description></item>
    ///   <item><description>id not already claimed by an earlier folder</description></item>
    ///   <item><description>
    ///     the running game is inside the declared <c>minGameVersion</c>/<c>maxGameVersion</c>
    ///     range (<see cref="PackGameVersionCheck"/>); a different <c>gameVersion</c> alone is not
    ///     a gate - the pack loads and <see cref="PackResolution.Warnings"/> carries one line for it
    ///   </description></item>
    ///   <item><description>declared <c>loaderVersion</c> is not newer than this framework</description></item>
    ///   <item><description>a code mod's <c>entryDll</c> is in its folder (<see cref="PackSkipReason.EntryDllMissing"/>)</description></item>
    ///   <item><description>
    ///     every required dependency (<c>dependsOn</c> without <c>optional</c>, and
    ///     <c>contentPackFor</c>, which must name a code mod) survived, at a new enough version;
    ///     an optional dependency that is absent or skipped is ignored, one that is present but
    ///     too old refuses the dependent
    ///   </description></item>
    ///   <item><description>the dependency graph is acyclic</description></item>
    /// </list>
    /// A dependency skipped by an earlier gate takes its dependents down with it - reported as
    /// <see cref="PackSkipReason.MissingDependency"/> naming the reason the dependency went, so
    /// one broken pack does not produce a wall of unexplained skips.
    /// </summary>
    public static class PackResolver
    {
        /// <summary>Resolves discovered packs into a load order.</summary>
        /// <param name="sources">
        /// The candidates, in discovery order. Null entries are ignored; the order is preserved
        /// for packs that do not depend on each other, so the log is stable between runs.
        /// </param>
        /// <param name="currentGameVersion">
        /// The running game's version (<c>UnityEngine.Application.version</c>), or null when it
        /// could not be read. An unknown or unparseable version never skips a pack; a pack that
        /// declares a game version then loads with a warning.
        /// </param>
        /// <param name="coreVersion">
        /// This framework's API version (<c>CwyApi.Version</c>), compared against each manifest's
        /// <c>loaderVersion</c>. Null means "unknown", which refuses any pack that
        /// declares a minimum.
        /// </param>
        /// <param name="ignoreGameVersion">
        /// True to load packs even when the running game is outside their
        /// <c>minGameVersion</c>/<c>maxGameVersion</c> range (they warn instead), for testing a
        /// pack on a game build it does not claim to support.
        /// </param>
        public static PackResolution Resolve(
            IEnumerable<IPackSource>? sources,
            string? currentGameVersion,
            SemVer? coreVersion,
            bool ignoreGameVersion = false)
        {
            if (sources is null)
            {
                return PackResolution.Empty;
            }

            var skipped = new List<SkippedPack>();
            var skipReasons = new Dictionary<string, PackSkipReason>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<ResolvedPack>();
            var byId = new Dictionary<string, ResolvedPack>(StringComparer.OrdinalIgnoreCase);

            void Skip(string id, string folder, PackSkipReason reason, string detail)
            {
                skipped.Add(new SkippedPack(id, folder, reason, detail));
                skipReasons[id] = reason;
            }

            foreach (var source in sources)
            {
                if (source is null)
                {
                    continue;
                }

                var folder = source.FolderName ?? string.Empty;
                var fallbackId = string.IsNullOrWhiteSpace(source.Id) ? folder : source.Id;
                if (string.IsNullOrWhiteSpace(fallbackId))
                {
                    fallbackId = "<unnamed>";
                }

                if (string.IsNullOrWhiteSpace(source.ManifestText))
                {
                    Skip(fallbackId, folder, PackSkipReason.MissingManifest,
                        "no readable " + PackContentIndex.ManifestFileName + " in " + Describe(folder));
                    continue;
                }

                if (!PackManifestParser.TryParse(source.ManifestText, out var manifest, out var error))
                {
                    Skip(fallbackId, folder, PackSkipReason.InvalidManifest, error ?? "unreadable manifest");
                    continue;
                }

                var id = manifest!.Id;

                if (byId.TryGetValue(id, out var winner))
                {
                    Skip(id, folder, PackSkipReason.DuplicateId,
                        "id already provided by " + Describe(winner.Source.FolderName));
                    continue;
                }

                var gameVersionCheck = PackGameVersionCheck.Evaluate(manifest, currentGameVersion, ignoreGameVersion);
                if (gameVersionCheck.Skips)
                {
                    Skip(id, folder, gameVersionCheck.SkipReason!.Value, gameVersionCheck.Detail);
                    continue;
                }

                if (manifest.LoaderVersion != null &&
                    (coreVersion is null || coreVersion < manifest.LoaderVersion))
                {
                    Skip(id, folder, PackSkipReason.CoreVersionTooOld,
                        (manifest.IsCodeMod ? "mod" : "pack") + " needs CwyLs.ModLoader >= " + manifest.LoaderVersion +
                        ", running " + (coreVersion is null ? "an unknown version" : coreVersion.ToString()));
                    continue;
                }

                if (manifest.IsCodeMod && !EntryDllPath.IsListed(source.Files, manifest.EntryDll))
                {
                    Skip(id, folder, PackSkipReason.EntryDllMissing,
                        "entryDll \"" + manifest.EntryDll + "\" is not in " + Describe(folder));
                    continue;
                }

                var resolved = new ResolvedPack(
                    source, manifest, PackContentIndex.Build(source.Files), gameVersionCheck);
                candidates.Add(resolved);
                byId.Add(id, resolved);
            }

            RemoveUnsatisfiedDependencies(candidates, byId, skipReasons, Skip);

            var ordered = Order(candidates, byId, Skip);

            return new PackResolution(ordered, skipped);
        }

        /// <summary>
        /// Drops packs whose required dependencies are absent, too old or (for
        /// <c>contentPackFor</c>) not a code mod, and packs whose present optional dependency is
        /// too old - repeatedly, because dropping one pack can orphan another. Runs to a fixed
        /// point, so the answer does not depend on the order the packs happened to be discovered in.
        /// </summary>
        private static void RemoveUnsatisfiedDependencies(
            List<ResolvedPack> candidates,
            Dictionary<string, ResolvedPack> byId,
            Dictionary<string, PackSkipReason> skipReasons,
            Action<string, string, PackSkipReason, string> skip)
        {
            bool removedAny;
            do
            {
                removedAny = false;

                for (var i = candidates.Count - 1; i >= 0; i--)
                {
                    var pack = candidates[i];
                    foreach (var dependency in pack.Manifest.AllDependencies)
                    {
                        var reason = PackSkipReason.MissingDependency;
                        string detail;

                        if (!byId.TryGetValue(dependency.Id, out var provider))
                        {
                            if (dependency.IsOptional)
                            {
                                continue;
                            }

                            detail = "requires " + dependency.Id + WhyGone(dependency.Id, skipReasons);
                        }
                        else if (!dependency.IsSatisfiedBy(provider.Manifest.Version))
                        {
                            reason = PackSkipReason.DependencyVersionTooLow;
                            detail = "requires " + dependency.Id + " >= " + dependency.MinVersion +
                                ", found " + provider.Manifest.Version +
                                (dependency.IsOptional ? " (an optional dependency, but installed and too old)" : string.Empty);
                        }
                        else if (IsContentPackTarget(pack.Manifest, dependency) && !provider.Manifest.IsCodeMod)
                        {
                            detail = "contentPackFor names " + dependency.Id + ", which is installed but is not a code mod (no entryDll)";
                        }
                        else
                        {
                            continue;
                        }

                        skip(pack.Manifest.Id, pack.Source.FolderName, reason, detail);
                        candidates.RemoveAt(i);
                        byId.Remove(pack.Manifest.Id);
                        removedAny = true;
                        break;
                    }
                }
            }
            while (removedAny);
        }

        /// <summary>
        /// Orders the survivors so dependencies come first, keeping discovery order among packs
        /// that do not constrain each other. An optional dependency constrains the order only when
        /// its pack survived. When nothing more can be placed:
        /// <list type="number">
        ///   <item><description>the optional edges that lie on a cycle among the stuck packs are
        ///   ignored - only those, so every other installed optional dependency still loads first -
        ///   and placement resumes;</description></item>
        ///   <item><description>otherwise the stuck packs contain a cycle of required dependencies:
        ///   optional edges into that cycle (or into packs that require it) are ignored, since those
        ///   packs will never load, and placement resumes;</description></item>
        ///   <item><description>otherwise whatever is left is in, or behind, a required cycle and is skipped.</description></item>
        /// </list>
        /// </summary>
        private static List<ResolvedPack> Order(
            List<ResolvedPack> candidates,
            Dictionary<string, ResolvedPack> byId,
            Action<string, string, PackSkipReason, string> skip)
        {
            var ordered = new List<ResolvedPack>(candidates.Count);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remaining = new List<ResolvedPack>(candidates);
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Repeated scanning rather than a queue: with at most a few dozen packs the cost is
            // irrelevant, and scanning in discovery order makes the output order reproducible.
            while (remaining.Count > 0)
            {
                var placedAny = false;

                for (var i = 0; i < remaining.Count; i++)
                {
                    var pack = remaining[i];
                    if (!IsReady(pack, byId, placed, ignored))
                    {
                        continue;
                    }

                    ordered.Add(pack);
                    placed.Add(pack.Manifest.Id);
                    remaining.RemoveAt(i);
                    i--;
                    placedAny = true;
                }

                if (placedAny)
                {
                    continue;
                }

                if (IgnoreOptionalEdgesOnCycles(remaining, byId, ignored) ||
                    IgnoreOptionalEdgesIntoRequiredCycles(remaining, ignored))
                {
                    continue;
                }

                break;
            }

            foreach (var pack in remaining)
            {
                skip(
                    pack.Manifest.Id,
                    pack.Source.FolderName,
                    PackSkipReason.DependencyCycle,
                    DescribeCycle(pack, byId, remaining));
            }

            return ordered;
        }

        /// <summary>
        /// True when every dependency that constrains <paramref name="pack"/> is already placed:
        /// all required ones, and the optional ones whose pack survived, except optional edges
        /// ignored to break a deadlock.
        /// </summary>
        private static bool IsReady(
            ResolvedPack pack,
            Dictionary<string, ResolvedPack> byId,
            HashSet<string> placed,
            HashSet<string> ignoredEdges)
        {
            foreach (var dependency in pack.Manifest.AllDependencies)
            {
                if (dependency.IsOptional &&
                    (!byId.ContainsKey(dependency.Id) || ignoredEdges.Contains(Edge(pack.Manifest.Id, dependency.Id))))
                {
                    continue;
                }

                if (!placed.Contains(dependency.Id))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Ignores every optional edge <c>a → b</c> among the stuck packs where <c>b</c> can reach
        /// <c>a</c> again (through edges not yet ignored): exactly the optional edges on a cycle.
        /// </summary>
        /// <returns>True when at least one edge was newly ignored.</returns>
        private static bool IgnoreOptionalEdgesOnCycles(
            List<ResolvedPack> stuck,
            Dictionary<string, ResolvedPack> byId,
            HashSet<string> ignoredEdges)
        {
            var stuckIds = Ids(stuck);
            var onCycle = new List<string>();
            foreach (var pack in stuck)
            {
                foreach (var dependency in pack.Manifest.AllDependencies)
                {
                    var edge = Edge(pack.Manifest.Id, dependency.Id);
                    if (dependency.IsOptional && stuckIds.Contains(dependency.Id) && !ignoredEdges.Contains(edge) &&
                        Reaches(dependency.Id, pack.Manifest.Id, stuckIds, byId, ignoredEdges))
                    {
                        onCycle.Add(edge);
                    }
                }
            }

            // Collected first and ignored together, so the answer does not depend on scan order.
            foreach (var edge in onCycle)
            {
                ignoredEdges.Add(edge);
            }

            return onCycle.Count > 0;
        }

        /// <summary>
        /// Ignores optional edges into packs that can never load because they are in, or require
        /// their way into, a cycle of required dependencies.
        /// </summary>
        /// <returns>True when at least one edge was newly ignored.</returns>
        private static bool IgnoreOptionalEdgesIntoRequiredCycles(List<ResolvedPack> stuck, HashSet<string> ignoredEdges)
        {
            var doomed = Ids(stuck);

            // Peel off every stuck pack whose required dependencies (among the stuck) are all
            // peelable; what is left sits in or behind a required cycle.
            bool peeled;
            do
            {
                peeled = false;
                foreach (var pack in stuck)
                {
                    if (!doomed.Contains(pack.Manifest.Id))
                    {
                        continue;
                    }

                    var free = true;
                    foreach (var dependency in pack.Manifest.AllDependencies)
                    {
                        if (!dependency.IsOptional && doomed.Contains(dependency.Id))
                        {
                            free = false;
                            break;
                        }
                    }

                    if (free)
                    {
                        doomed.Remove(pack.Manifest.Id);
                        peeled = true;
                    }
                }
            }
            while (peeled);

            var any = false;
            foreach (var pack in stuck)
            {
                foreach (var dependency in pack.Manifest.AllDependencies)
                {
                    if (dependency.IsOptional && doomed.Contains(dependency.Id) &&
                        ignoredEdges.Add(Edge(pack.Manifest.Id, dependency.Id)))
                    {
                        any = true;
                    }
                }
            }

            return any;
        }

        /// <summary>True when <paramref name="from"/> reaches <paramref name="to"/> through stuck packs' edges that are not ignored.</summary>
        private static bool Reaches(
            string from,
            string to,
            HashSet<string> stuckIds,
            Dictionary<string, ResolvedPack> byId,
            HashSet<string> ignoredEdges)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from };
            var pending = new Stack<string>();
            pending.Push(from);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (string.Equals(current, to, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!byId.TryGetValue(current, out var pack))
                {
                    continue;
                }

                foreach (var dependency in pack.Manifest.AllDependencies)
                {
                    if (!stuckIds.Contains(dependency.Id) || ignoredEdges.Contains(Edge(current, dependency.Id)))
                    {
                        continue;
                    }

                    if (seen.Add(dependency.Id))
                    {
                        pending.Push(dependency.Id);
                    }
                }
            }

            return false;
        }

        private static string Edge(string from, string to) => from + "\n" + to;

        private static HashSet<string> Ids(List<ResolvedPack> packs)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in packs)
            {
                ids.Add(pack.Manifest.Id);
            }

            return ids;
        }

        private static bool IsContentPackTarget(PackManifest manifest, PackDependency dependency) =>
            manifest.ContentPackFor != null &&
            string.Equals(manifest.ContentPackFor.Id, dependency.Id, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Walks the required dependencies from <paramref name="start"/> inside the unplaceable set
        /// until a pack repeats, and renders that path - a modder needs to see which edge to cut.
        /// </summary>
        private static string DescribeCycle(
            ResolvedPack start,
            Dictionary<string, ResolvedPack> byId,
            List<ResolvedPack> remaining)
        {
            var stuck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pack in remaining)
            {
                stuck.Add(pack.Manifest.Id);
            }

            var path = new List<string>();
            var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = start;

            while (true)
            {
                path.Add(current.Manifest.Id);
                onPath.Add(current.Manifest.Id);

                ResolvedPack? next = null;
                foreach (var dependency in current.Manifest.AllDependencies)
                {
                    if (!dependency.IsOptional && stuck.Contains(dependency.Id) && byId.TryGetValue(dependency.Id, out var candidate))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    return "unresolvable dependency cycle among: " + string.Join(", ", ToArray(stuck));
                }

                if (onPath.Contains(next.Manifest.Id))
                {
                    path.Add(next.Manifest.Id);
                    var sb = new StringBuilder("dependency cycle: ");
                    for (var i = 0; i < path.Count; i++)
                    {
                        if (i > 0) { sb.Append(" -> "); }

                        sb.Append(path[i]);
                    }

                    return sb.ToString();
                }

                current = next;
            }
        }

        private static string WhyGone(string id, Dictionary<string, PackSkipReason> skipReasons) =>
            skipReasons.TryGetValue(id, out var reason)
                ? ", which was itself skipped (" + reason + ")"
                : ", which is not installed";

        private static string Describe(string? folder) =>
            string.IsNullOrEmpty(folder) ? "the pack folder" : "'" + folder + "'";

        private static string[] ToArray(HashSet<string> set)
        {
            var values = new string[set.Count];
            set.CopyTo(values);
            Array.Sort(values, StringComparer.Ordinal);
            return values;
        }
    }
}
