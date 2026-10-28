using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Trace;

namespace CWY.Seamcheck
{
    /// <summary>
    /// A type the trace would patch whose static constructor reaches game state that may not exist
    /// at plugin load. On Mono, patching a method compiles it and compiling runs the type's static
    /// constructor, so such a constructor runs hours early; if it throws, the type is broken for the
    /// session (seen with the Windows <c>ScreenSystem</c>).
    /// </summary>
    /// <param name="Type">Reflection full name.</param>
    /// <param name="Init"><c>precise</c> (no beforefieldinit: runs exactly at patch time) or <c>beforefieldinit</c>.</param>
    /// <param name="Calls">The game-state calls in the constructor, e.g. <c>ProjectLifetimeScope.get_GameObject</c>.</param>
    /// <param name="PatchedMethods">How many of its methods the trace would patch.</param>
    /// <param name="ExcludedHere">True when a declared exclusion already keeps the trace off it on this platform.</param>
    internal sealed record TraceHazard(string Type, string Init, IReadOnlyList<string> Calls, int PatchedMethods, bool ExcludedHere);

    /// <summary>What the preflight found for one declared <see cref="TraceExclusion"/> on one platform.</summary>
    /// <param name="Type">The excluded type.</param>
    /// <param name="Platforms">The platforms the exclusion is declared for.</param>
    /// <param name="Applies">Whether it applies on the platform checked.</param>
    /// <param name="TypePresent">Whether the type exists in this platform's traced assemblies.</param>
    /// <param name="WouldPatch">Methods the trace filter would patch without the exclusion.</param>
    /// <param name="HazardPresent">Whether the static-constructor hazard is present on this platform.</param>
    internal sealed record ExclusionCheck(string Type, string Platforms, bool Applies, bool TypePresent, int WouldPatch, bool HazardPresent)
    {
        /// <summary>One word for the report.</summary>
        internal string Verdict =>
            !Applies ? (HazardPresent ? "NOT-APPLIED-BUT-HAZARD" : "not-applicable")
            : !TypePresent ? "stale-type-missing"
            : !HazardPresent ? "stale-no-hazard"
            : "needed";
    }

    /// <summary>The trace preflight for one platform.</summary>
    /// <param name="Methods">Methods the in-game trace would patch (default config, Level=Trace).</param>
    /// <param name="Classes">Distinct classes those methods are in.</param>
    /// <param name="Hazards">Static-constructor hazards among those classes.</param>
    /// <param name="Exclusions">Every declared exclusion, checked.</param>
    internal sealed record TracePreflightResult(
        int Methods,
        int Classes,
        IReadOnlyList<TraceHazard> Hazards,
        IReadOnlyList<ExclusionCheck> Exclusions)
    {
        /// <summary>
        /// Hazards no declared exclusion covers on this platform. Each one fails the run (exit 1,
        /// by design): the in-game trace would run that constructor at plugin load.
        /// </summary>
        internal IReadOnlyList<TraceHazard> UncoveredHazards => Hazards.Where(h => !h.ExcludedHere).ToList();
    }

    /// <summary>
    /// Runs the in-game trace's own candidate rules (<see cref="TraceCandidates"/>, compiled in from
    /// the Trace plugin) and <see cref="TraceFilter"/> over a platform's assemblies, then reads the
    /// static constructor of every class it would touch.
    /// </summary>
    /// <remarks>
    /// "Reaches game state" is a heuristic over the constructor's own IL: a call to a member of a
    /// type whose name ends in <c>LifetimeScope</c> (the game's VContainer scopes), to any
    /// <c>get_Instance</c> (the game's singletons), or to <c>UnityEngine.Object/GameObject.Find*</c>.
    /// It sees direct calls only, not calls made by helpers the constructor calls.
    /// </remarks>
    internal static class TracePreflight
    {
        /// <summary>Prefix of the failure line for an uncovered hazard; grep-able like <c>PATCH TARGET MISSING</c>.</summary>
        internal const string HazardPrefix = "TRACE HAZARD";

        /// <summary>Where a new exclusion is declared, as named in the failure line.</summary>
        internal const string ExclusionFile = "src/CwyLs.ModLoader.Trace/TraceExclusions.cs";

        /// <summary>
        /// The one failure line for a hazard no exclusion covers, e.g.
        /// <c>TRACE HAZARD NestopiSystem.ScreenSystem on Windows: precise .cctor calls
        /// ProjectLifetimeScope.get_GameObject, ...; the trace would run it at plugin load (3 method(s)
        /// patched); declare an exclusion in src/CwyLs.ModLoader.Trace/TraceExclusions.cs</c>.
        /// </summary>
        /// <param name="hazard">The uncovered hazard.</param>
        /// <param name="platform">The platform it was found on.</param>
        internal static string UncoveredLine(TraceHazard hazard, TracePlatforms platform) =>
            HazardPrefix + " " + hazard.Type + " on " + TraceExclusions.Name(platform) + ": "
            + hazard.Init + " .cctor calls " + string.Join(", ", hazard.Calls)
            + "; the trace would run it at plugin load (" + hazard.PatchedMethods + " method(s) patched); "
            + "declare an exclusion in " + ExclusionFile;

        /// <summary>Runs the preflight.</summary>
        /// <param name="assemblies">The platform's game assemblies.</param>
        /// <param name="platform">The platform, for the exclusion table.</param>
        /// <param name="exclusions">The declared exclusions, normally <see cref="TraceExclusions.Declared"/>.</param>
        /// <param name="log">Diagnostics.</param>
        /// <exception cref="SeamcheckException">No traced assembly could be read, or no method passes the filter.</exception>
        internal static TracePreflightResult Run(
            ManagedAssemblySet assemblies,
            TracePlatforms platform,
            IReadOnlyList<TraceExclusion> exclusions,
            ToolLog log)
        {
            var filter = new TraceFilter(new TraceConfig { Level = TraceLevel.Trace });
            var patchedPerType = new Dictionary<string, int>(StringComparer.Ordinal);
            var presentTypes = new HashSet<string>(StringComparer.Ordinal);
            var wouldPatchIfIncluded = new Dictionary<string, int>(StringComparer.Ordinal);
            var constructorCalls = new Dictionary<string, (string Init, IReadOnlyList<string> Calls)>(StringComparer.Ordinal);
            var methods = 0;
            var readable = 0;

            foreach (var assemblyName in TraceCandidates.TracedAssemblies)
            {
                var assembly = assemblies.TryLoad(assemblyName);
                var path = assemblies.PathOf(assemblyName);
                if (assembly == null || path == null)
                {
                    log.Warn("trace preflight: " + assemblyName + " is not in " + assemblies.Description);
                    continue;
                }

                readable++;
                foreach (var entry in StaticConstructorCalls(path))
                {
                    constructorCalls[entry.Key] = entry.Value;
                }

                foreach (var type in SafeGetTypes(assembly, log))
                {
                    var name = type.FullName ?? type.Name;
                    presentTypes.Add(name);
                    if (!TraceCandidates.IsTraceableType(type))
                    {
                        continue;
                    }

                    var accepted = CountAccepted(type, filter, log);
                    if (accepted == 0)
                    {
                        continue;
                    }

                    wouldPatchIfIncluded[name] = accepted;
                    if (TraceExclusions.Find(exclusions, platform, name) != null)
                    {
                        continue;
                    }

                    patchedPerType[name] = accepted;
                    methods += accepted;
                }
            }

            if (readable == 0)
            {
                throw new SeamcheckException("trace preflight: none of the traced assemblies is in " + assemblies.Description);
            }

            if (methods == 0)
            {
                throw new SeamcheckException(
                    "trace preflight: no method passed the trace filter, so the metadata read is broken, not the game");
            }

            var hazards = new List<TraceHazard>();
            foreach (var pair in wouldPatchIfIncluded.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (constructorCalls.TryGetValue(pair.Key, out var constructor) && constructor.Calls.Count > 0)
                {
                    var excluded = TraceExclusions.Find(exclusions, platform, pair.Key) != null;
                    hazards.Add(new TraceHazard(pair.Key, constructor.Init, constructor.Calls, pair.Value, excluded));
                }
            }

            var checks = new List<ExclusionCheck>();
            foreach (var exclusion in exclusions)
            {
                var type = exclusion.TypeFullName;
                checks.Add(new ExclusionCheck(
                    type,
                    exclusion.Platforms.ToString(),
                    exclusion.AppliesTo(platform),
                    presentTypes.Contains(type),
                    wouldPatchIfIncluded.TryGetValue(type, out var count) ? count : 0,
                    hazards.Any(h => string.Equals(h.Type, type, StringComparison.Ordinal))));
            }

            return new TracePreflightResult(methods, patchedPerType.Count, hazards, checks);
        }

        /// <summary>
        /// Methods of one type the trace would patch: the filter accepts them and Harmony can take
        /// the signature. Mirrors <c>TracePatcher.PatchType</c>.
        /// </summary>
        private static int CountAccepted(Type type, TraceFilter filter, ToolLog log)
        {
            MethodInfo[] declared;
            try
            {
                declared = type.GetMethods(TraceCandidates.DeclaredMethods);
            }
            catch (Exception ex)
            {
                log.Debug("trace preflight: could not list methods of " + type.FullName + ": " + ex.Message);
                return 0;
            }

            var interaction = TraceCandidates.IsInteractionType(type, declared);
            var accepted = 0;
            foreach (var method in declared)
            {
                if (filter.ShouldPatch(TraceCandidates.Describe(method, type, interaction))
                    && !TraceCandidates.HasUnpatchableSignature(method))
                {
                    accepted++;
                }
            }

            return accepted;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly, ToolLog log)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                log.Debug("trace preflight: " + assembly.GetName().Name + " has "
                    + (ex.Types.Length - loaded.Length) + " types that would not load");
                return loaded;
            }
        }

        /// <summary>
        /// For every type in an assembly file that has a static constructor: its init kind and the
        /// game-state calls in that constructor's IL (empty when there are none).
        /// </summary>
        /// <param name="assemblyPath">A game assembly.</param>
        internal static Dictionary<string, (string Init, IReadOnlyList<string> Calls)> StaticConstructorCalls(string assemblyPath)
        {
            var result = new Dictionary<string, (string Init, IReadOnlyList<string> Calls)>(StringComparer.Ordinal);
            try
            {
                using var stream = File.OpenRead(assemblyPath);
                using var pe = new PEReader(stream);
                var reader = pe.GetMetadataReader();
                var names = new MetadataNames(reader);
                foreach (var typeHandle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(typeHandle);
                    foreach (var methodHandle in type.GetMethods())
                    {
                        var method = reader.GetMethodDefinition(methodHandle);
                        if (!string.Equals(reader.GetString(method.Name), ".cctor", StringComparison.Ordinal)
                            || method.RelativeVirtualAddress == 0)
                        {
                            continue;
                        }

                        var calls = new List<string>();
                        foreach (var instruction in IlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress)))
                        {
                            if (!instruction.IsCall && instruction.OpCode != ILOpCode.Newobj)
                            {
                                continue;
                            }

                            if (names.Member(instruction.Token) is { } target && ReachesGameState(target))
                            {
                                var shortName = ShortTypeName(target.DeclaringType) + "." + target.Name;
                                if (!calls.Contains(shortName))
                                {
                                    calls.Add(shortName);
                                }
                            }
                        }

                        var init = (type.Attributes & TypeAttributes.BeforeFieldInit) != 0 ? "beforefieldinit" : "precise";
                        result[names.TypeDefinitionName(typeHandle)] = (init, calls);
                    }
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidDataException || ex is IOException)
            {
                throw new SeamcheckException("trace preflight: could not read " + assemblyPath + ": " + ex.Message, ex);
            }

            return result;
        }

        /// <summary>The heuristic: DI scopes, singletons, scene searches.</summary>
        /// <param name="target">A member a static constructor calls.</param>
        internal static bool ReachesGameState(MemberRefName target)
        {
            if (target.DeclaringType.EndsWith("LifetimeScope", StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(target.Name, "get_Instance", StringComparison.Ordinal))
            {
                return true;
            }

            return (string.Equals(target.DeclaringType, "UnityEngine.Object", StringComparison.Ordinal)
                    || string.Equals(target.DeclaringType, "UnityEngine.GameObject", StringComparison.Ordinal))
                && target.Name.StartsWith("Find", StringComparison.Ordinal);
        }

        private static string ShortTypeName(string fullName)
        {
            var dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }
    }
}
