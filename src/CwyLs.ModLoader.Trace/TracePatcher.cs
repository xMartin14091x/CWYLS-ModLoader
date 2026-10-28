using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using CwyLs.ModLoader.Core.Trace;
using HarmonyLib;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Logging;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>Result of one patching pass, reported in the start-up header and the console.</summary>
    internal sealed class PatchReport
    {
        /// <summary>Methods successfully given a prefix.</summary>
        internal int Patched { get; set; }

        /// <summary>Methods Harmony refused.</summary>
        internal int Failed { get; set; }

        /// <summary>Distinct classes touched.</summary>
        internal int Classes { get; set; }

        /// <summary>Curated methods that also got a return-value postfix.</summary>
        internal int Postfixed { get; set; }

        /// <summary>Methods traced without their arguments because Harmony refused to capture them.</summary>
        internal int Degraded { get; set; }

        /// <summary>Wall-clock milliseconds spent patching.</summary>
        internal double Milliseconds { get; set; }

        /// <summary>One line per failure: the method and Harmony's reason.</summary>
        internal List<string> Failures { get; } = new List<string>();

        /// <summary>Assemblies that could not be found or enumerated.</summary>
        internal List<string> MissingAssemblies { get; } = new List<string>();

        /// <summary>
        /// One line per type left alone by a declared <see cref="TraceExclusion"/>, naming the methods
        /// that would otherwise have been patched. Printed at Info: a declared skip is not a failure.
        /// </summary>
        internal List<string> Skipped { get; } = new List<string>();
    }

    /// <summary>
    /// Walks the game assemblies, asks <see cref="TraceFilter"/> about every method and installs the
    /// shared prefix on the ones that pass. Every step is individually guarded: one type that fails
    /// to load must cost us that type, not the whole trace.
    /// </summary>
    internal sealed class TracePatcher
    {
        /// <summary>Failures written to the console in full before the rest are only counted.</summary>
        private const int MaxLoggedFailures = 40;

        /// <summary>Return types whose postfix value is an unfinished awaitable, so not worth recording.</summary>
        private static readonly string[] AwaitableTypeNames =
        {
            "UniTask", "UniTaskVoid", "Task", "ValueTask", "IEnumerator", "Coroutine", "Awaitable",
        };

        private readonly Harmony _harmony;
        private readonly TraceFilter _filter;
        private readonly TracePlatforms _platform;
        private readonly IReadOnlyList<TraceExclusion> _exclusions;

        /// <summary>Creates a patcher bound to one Harmony instance, one filter and the running platform.</summary>
        /// <param name="harmony">The trace's Harmony instance.</param>
        /// <param name="filter">Decides which methods get a prefix.</param>
        /// <param name="platform">The running platform; decides which <see cref="TraceExclusions.Declared"/> entries apply.</param>
        internal TracePatcher(Harmony harmony, TraceFilter filter, TracePlatforms platform)
            : this(harmony, filter, platform, TraceExclusions.Declared)
        {
        }

        /// <summary>Creates a patcher with an explicit exclusion table.</summary>
        /// <param name="harmony">The trace's Harmony instance.</param>
        /// <param name="filter">Decides which methods get a prefix.</param>
        /// <param name="platform">The running platform.</param>
        /// <param name="exclusions">Types never to patch, per platform.</param>
        internal TracePatcher(Harmony harmony, TraceFilter filter, TracePlatforms platform, IReadOnlyList<TraceExclusion> exclusions)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            _platform = platform;
            _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        }

        /// <summary>Patches every accepted method in the named assemblies.</summary>
        internal PatchReport PatchAssemblies(IEnumerable<string> assemblyNames)
        {
            var report = new PatchReport();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var classes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in assemblyNames)
            {
                var assembly = FindAssembly(name);
                if (assembly is null)
                {
                    report.MissingAssemblies.Add(name);
                    continue;
                }

                foreach (var type in SafeGetTypes(assembly, report))
                {
                    PatchType(type, report, classes);
                }
            }

            stopwatch.Stop();
            report.Classes = classes.Count;
            report.Milliseconds = stopwatch.Elapsed.TotalMilliseconds;
            return report;
        }

        /// <summary>Finds a loaded assembly by simple name, loading it on demand if the game has not yet.</summary>
        private static Assembly? FindAssembly(string simpleName)
        {
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.Ordinal))
                {
                    return loaded;
                }
            }

            try
            {
                return Assembly.Load(new AssemblyName(simpleName));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: assembly {simpleName} is not loaded and could not be loaded: {ex.Message}");
                return null;
            }
        }

        /// <summary>Enumerates a type list, keeping whatever loaded when some types did not.</summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly assembly, PatchReport report)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: {assembly.GetName().Name} has {ex.Types.Length - loaded.Length} types that would not load; tracing the other {loaded.Length}");
                report.Failures.Add($"{assembly.GetName().Name}: {ex.Types.Length - loaded.Length} types failed to load");
                return loaded;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: could not enumerate {assembly.GetName().Name}: {ex.Message}");
                report.MissingAssemblies.Add(assembly.GetName().Name);
                return Array.Empty<Type>();
            }
        }

        private void PatchType(Type type, PatchReport report, HashSet<string> classes)
        {
            if (!TraceCandidates.IsTraceableType(type))
            {
                return;
            }

            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(TraceCandidates.DeclaredMethods);
            }
            catch (Exception ex)
            {
                // Typically an obfuscated or half-loaded type. Skip it and say so once.
                CwyLsModLoader.Log.LogWarning($"TRACE: could not list methods of {type.FullName}: {ex.Message}");
                return;
            }

            var interaction = TraceCandidates.IsInteractionType(type, methods);

            // Decided before the first Harmony call on this type: patching compiles the original, and
            // on Mono compiling runs the type's static constructor, so an excluded type must never
            // reach _harmony.Patch. Listing its methods, interfaces and IL bodies is metadata only.
            var exclusion = TraceExclusions.Find(_exclusions, _platform, type.FullName);
            if (exclusion != null)
            {
                RecordSkipped(type, methods, interaction, exclusion, report);
                return;
            }

            foreach (var method in methods)
            {
                if (!_filter.ShouldPatch(TraceCandidates.Describe(method, type, interaction)))
                {
                    continue;
                }

                if (TraceCandidates.HasUnpatchableSignature(method))
                {
                    report.Failed++;
                    AddFailure(report, method, "signature uses a pointer type");
                    continue;
                }

                TryPatch(type, method, report, classes);
            }
        }

        /// <summary>Names the methods an exclusion kept us from patching, for the one INFO line.</summary>
        private void RecordSkipped(
            Type type,
            MethodInfo[] methods,
            bool interaction,
            TraceExclusion exclusion,
            PatchReport report)
        {
            var wouldPatch = new List<string>();
            foreach (var method in methods)
            {
                if (_filter.ShouldPatch(TraceCandidates.Describe(method, type, interaction)))
                {
                    wouldPatch.Add(method.Name);
                }
            }

            var line = TraceExclusions.FormatSkipped(exclusion, _platform, wouldPatch);
            if (line != null)
            {
                report.Skipped.Add(line);
            }
        }

        private void TryPatch(Type type, MethodInfo method, PatchReport report, HashSet<string> classes)
        {
            var curated = CuratedEvents.IsCurated(type.Name, method.Name);
            var prefix = AccessTools.Method(
                typeof(TraceRuntime),
                method.IsStatic ? nameof(TraceRuntime.PrefixStatic) : nameof(TraceRuntime.PrefixInstance));
            var postfix = IsClickPoll(type, method)
                ? AccessTools.Method(typeof(TraceRuntime), nameof(TraceRuntime.PostfixInputClick))
                : curated ? ChoosePostfix(method) : null;
            var degraded = false;

            try
            {
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(prefix),
                    postfix: postfix is null ? null : new HarmonyMethod(postfix));
            }
            catch (Exception ex)
            {
                // Capturing arguments is the part of the signature Harmony is most likely to refuse
                // (by-ref-like parameters, exotic signatures). A call with no arguments recorded is
                // still worth far more than a hole in the trace, so try once without them.
                if (!TryPatchWithoutArguments(method, ex, report))
                {
                    return;
                }

                degraded = true;
                postfix = null;
            }

            TraceRuntime.Register(method, new TraceEntry(type.Namespace, type.Name, method.Name, curated));
            report.Patched++;
            if (degraded)
            {
                report.Degraded++;
            }

            if (postfix != null)
            {
                report.Postfixed++;
            }

            classes.Add(type.FullName ?? type.Name);
        }

        /// <summary>
        /// The one polled method whose <em>result</em> is an event: a frame where
        /// <c>InputController.GetClickDown</c> returns true is a click the user made.
        /// </summary>
        private static bool IsClickPoll(Type type, MethodInfo method) =>
            string.Equals(type.Name, "InputController", StringComparison.Ordinal)
            && string.Equals(method.Name, "GetClickDown", StringComparison.Ordinal)
            && method.ReturnType == typeof(bool);

        /// <summary>Second attempt with a prefix that takes no arguments. True when it worked.</summary>
        private bool TryPatchWithoutArguments(MethodInfo method, Exception firstFailure, PatchReport report)
        {
            try
            {
                _harmony.Unpatch(method, HarmonyPatchType.All, _harmony.Id);
            }
            catch (Exception ex)
            {
                // Nothing to undo, or Harmony never got far enough to record one. Carry on.
                TraceDiagnostics.Report(
                    $"TRACE: could not clean up the failed patch of {method.DeclaringType?.FullName}.{method.Name}", ex);
            }

            var fallback = AccessTools.Method(
                typeof(TraceRuntime),
                method.IsStatic ? nameof(TraceRuntime.PrefixStaticNoArgs) : nameof(TraceRuntime.PrefixInstanceNoArgs));

            try
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(fallback));
                AddFailure(report, method, $"traced without arguments ({TraceFailureText.Chain(firstFailure)})");
                return true;
            }
            catch (Exception ex)
            {
                report.Failed++;
                AddFailure(report, method, TraceFailureText.Chain(ex));
                return false;
            }
        }

        /// <summary>
        /// Picks the postfix whose <c>__result</c> parameter matches the return type exactly.
        /// A single <c>object __result</c> postfix would need Harmony to box value-type returns,
        /// which HarmonyX 2.9 does not promise, so the common value types get their own.
        /// </summary>
        private static MethodInfo? ChoosePostfix(MethodInfo method)
        {
            var returnType = method.ReturnType;
            if (returnType == typeof(void) || returnType.IsByRef || returnType.IsPointer)
            {
                return null;
            }

            var simpleName = returnType.Name;
            var tick = simpleName.IndexOf('`');
            if (tick >= 0)
            {
                simpleName = simpleName.Substring(0, tick);
            }

            if (Array.IndexOf(AwaitableTypeNames, simpleName) >= 0)
            {
                return null;
            }

            if (returnType == typeof(bool))
            {
                return AccessTools.Method(typeof(TraceRuntime), nameof(TraceRuntime.PostfixBool));
            }

            if (returnType == typeof(int))
            {
                return AccessTools.Method(typeof(TraceRuntime), nameof(TraceRuntime.PostfixInt));
            }

            if (returnType == typeof(float))
            {
                return AccessTools.Method(typeof(TraceRuntime), nameof(TraceRuntime.PostfixFloat));
            }

            return returnType.IsValueType
                ? null
                : AccessTools.Method(typeof(TraceRuntime), nameof(TraceRuntime.PostfixObject));
        }

        private static void AddFailure(PatchReport report, MethodInfo method, string reason)
        {
            if (report.Failures.Count >= MaxLoggedFailures)
            {
                return;
            }

            report.Failures.Add(
                $"{method.DeclaringType?.FullName}.{method.Name}: {reason}");
        }

        /// <summary>Writes the patch report to the BepInEx console.</summary>
        internal static void LogReport(PatchReport report, TraceLevel level)
        {
            ConsoleMirror.Print(TraceConsoleLines.Status(TraceRuntime.Now, string.Format(
                CultureInfo.InvariantCulture,
                "patched {0} methods across {1} classes in {2:F0} ms; {3} failed (level {4}, {5} with return values, {6} without arguments)",
                report.Patched,
                report.Classes,
                report.Milliseconds,
                report.Failed,
                level,
                report.Postfixed,
                report.Degraded)));

            // A declared skip is expected, so it is an Info line, not a warning.
            foreach (var skipped in report.Skipped)
            {
                ConsoleMirror.Print(TraceConsoleLines.Status(TraceRuntime.Now, skipped));
            }

            foreach (var assembly in report.MissingAssemblies)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: assembly not traced: {assembly}");
            }

            foreach (var failure in report.Failures)
            {
                CwyLsModLoader.Log.LogWarning("TRACE: " + failure);
            }

            if (report.Failed > report.Failures.Count)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: {report.Failed - report.Failures.Count} further failures were counted but not listed");
            }
        }
    }
}
