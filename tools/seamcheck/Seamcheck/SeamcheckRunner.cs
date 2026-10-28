using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Trace;

namespace CWY.Seamcheck
{
    /// <summary>The outcome of one control target.</summary>
    /// <param name="Id">The control's id.</param>
    /// <param name="Expectation">What it must do: <c>missing</c> or <c>ok</c>.</param>
    /// <param name="Passed">Whether it behaved as designed.</param>
    /// <param name="Detail">The resolver's detail text.</param>
    internal sealed record ControlOutcome(string Id, string Expectation, bool Passed, string Detail);

    /// <summary>Everything found about the registrations, independent of any platform.</summary>
    /// <param name="PluginAssemblies">The plugin assemblies scanned.</param>
    /// <param name="Loaded">Registrations whose table was read, in scan order.</param>
    /// <param name="Forwarders">Register calls that forward a parameter (registration APIs).</param>
    /// <param name="Targets">Every registered target once, first registration wins (as in game).</param>
    /// <param name="OwnerById">The owner label of each target.</param>
    /// <param name="Warnings">Duplicate ids and similar, printed as warnings.</param>
    /// <param name="Notes">Warnings and errors plugin code logged while its tables were read.</param>
    /// <param name="Errors">Tool errors: unreadable tables, unenumerable registrations.</param>
    internal sealed record RegistrationSummary(
        IReadOnlyList<string> PluginAssemblies,
        IReadOnlyList<LoadedRegistration> Loaded,
        IReadOnlyList<RegistrationSite> Forwarders,
        IReadOnlyList<PatchTarget> Targets,
        IReadOnlyDictionary<string, string> OwnerById,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Notes,
        IReadOnlyList<string> Errors);

    /// <summary>The check of every registered target against one platform's assemblies.</summary>
    /// <param name="Spec">The platform.</param>
    /// <param name="AssemblyCount">How many assemblies its Managed/ folder has.</param>
    /// <param name="GameAssemblySha256">SHA-256 of its Assembly-CSharp.dll, to name the build; null if absent.</param>
    /// <param name="Report">The same report the game builds (<see cref="PatchTargetChecker"/>).</param>
    /// <param name="Controls">The negative and positive controls.</param>
    /// <param name="Trace">The trace preflight, or null when it could not run.</param>
    /// <param name="Errors">Tool errors for this platform.</param>
    internal sealed record PlatformResult(
        PlatformSpec Spec,
        int AssemblyCount,
        string? GameAssemblySha256,
        PatchTargetReport Report,
        IReadOnlyList<ControlOutcome> Controls,
        TracePreflightResult? Trace,
        IReadOnlyList<string> Errors);

    /// <summary>A target whose outcome is not the same on every platform checked.</summary>
    /// <param name="Id">The target id.</param>
    /// <param name="Outcomes">Per platform key: found, and the detail text.</param>
    internal sealed record PlatformDifference(string Id, IReadOnlyList<(string Platform, bool Found, string Detail)> Outcomes);

    /// <summary>A whole run.</summary>
    /// <param name="Registrations">What the plugins register.</param>
    /// <param name="Platforms">One result per platform checked.</param>
    /// <param name="Differences">Targets whose outcome differs between platforms (only with two or more).</param>
    internal sealed record SeamcheckResult(
        RegistrationSummary Registrations,
        IReadOnlyList<PlatformResult> Platforms,
        IReadOnlyList<PlatformDifference> Differences)
    {
        /// <summary>
        /// Exit code 0 everything resolved, 1 a target is missing or a trace hazard is not excluded,
        /// 2 the tool could not vouch for the result.
        /// </summary>
        internal int ExitCode =>
            Registrations.Errors.Count > 0 || Platforms.Any(p => p.Errors.Count > 0) ? 2
            : Platforms.Any(p => !p.Report.AllFound || p.Trace?.UncoveredHazards.Count > 0) ? 1
            : 0;
    }

    /// <summary>
    /// The built-in controls. They go through the same resolver and checker as the real table, so a
    /// broken metadata setup (every lookup "missing", or every lookup "ok") cannot pass unnoticed.
    /// </summary>
    internal static class Controls
    {
        /// <summary>A method that does not exist on a game type: must report missing, at the method level.</summary>
        internal static readonly PatchTarget Negative = new PatchTarget(
            "seamcheck.control.negative", "Assembly-CSharp", "Bulbul.HeroineService",
            MemberKind.Method, "SeamcheckNegativeControlNoSuchMethod");

        /// <summary>The exact detail the negative control must produce: the type resolved, the method did not.</summary>
        internal const string NegativeDetail =
            "instance method 'SeamcheckNegativeControlNoSuchMethod' not found on Bulbul.HeroineService";

        /// <summary>A core-library method every Unity Managed folder has: must resolve, signature and all.</summary>
        internal static readonly PatchTarget Positive = new PatchTarget(
            "seamcheck.control.positive", ManagedAssemblySet.UnityCoreAssembly, "System.String",
            MemberKind.Method, "Concat", new[] { "System.String", "System.String" },
            isStatic: true, expectedSignature: "static string Concat(string, string)");

        /// <summary>Runs both controls.</summary>
        /// <param name="resolver">The platform's resolver.</param>
        internal static IReadOnlyList<ControlOutcome> Run(IMemberResolver resolver)
        {
            var report = PatchTargetChecker.Check(new[] { Negative, Positive }, resolver);
            var negative = report.Results[0];
            var positive = report.Results[1];
            return new[]
            {
                new ControlOutcome(
                    Negative.Id, "missing",
                    !negative.Found && string.Equals(negative.Detail, NegativeDetail, StringComparison.Ordinal),
                    negative.Detail),
                new ControlOutcome(Positive.Id, "ok", positive.Found, positive.Detail),
            };
        }
    }

    /// <summary>Runs a whole check: registrations once, then every requested platform.</summary>
    internal static class SeamcheckRunner
    {
        /// <summary>Runs the check.</summary>
        /// <param name="options">The parsed command line.</param>
        /// <param name="pluginDirectory">Where the plugin assemblies are (the tool's own folder).</param>
        /// <param name="excludedAssemblies">Assemblies in that folder that are not plugins.</param>
        /// <param name="log">Diagnostics.</param>
        internal static SeamcheckResult Run(
            SeamcheckOptions options,
            string pluginDirectory,
            IReadOnlyCollection<string> excludedAssemblies,
            ToolLog log)
        {
            var plugins = RegistrationScanner.DiscoverPluginAssemblies(pluginDirectory, excludedAssemblies, log);
            var registrations = LoadRegistrations(plugins, log);

            var platforms = new List<PlatformResult>();
            foreach (var spec in options.Platforms)
            {
                platforms.Add(CheckPlatform(spec, registrations.Targets, TraceExclusions.Declared, log));
            }

            return new SeamcheckResult(registrations, platforms, Differences(platforms));
        }

        /// <summary>Scans the plugin assemblies and reads every registered table.</summary>
        /// <param name="pluginPaths">Plugin assembly files.</param>
        /// <param name="log">Diagnostics.</param>
        internal static RegistrationSummary LoadRegistrations(IReadOnlyList<string> pluginPaths, ToolLog log)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            var loaded = new List<LoadedRegistration>();
            var forwarders = new List<RegistrationSite>();

            if (pluginPaths.Count == 0)
            {
                errors.Add("no plugin assembly (one referencing " + RegistrationScanner.FrameworkAssemblyName + ") was found next to the tool");
            }

            using var capture = PluginLogCapture.Install(log);
            foreach (var path in pluginPaths)
            {
                IReadOnlyList<RegistrationSite> sites;
                try
                {
                    sites = RegistrationScanner.Scan(path);
                }
                catch (SeamcheckException ex)
                {
                    errors.Add(ex.Message);
                    continue;
                }

                foreach (var site in sites)
                {
                    switch (site.Kind)
                    {
                        case TableSourceKind.Forwarded:
                            forwarders.Add(site);
                            log.Debug("registration API at " + site.Location + " forwards its " + site.Instruction + "; its callers' tables are what count");
                            break;
                        case TableSourceKind.Unenumerable:
                            errors.Add("registration at " + site.Location + " passes " + site.Instruction
                                + ", which cannot be read offline; pass a static table (e.g. SomeTargets.All) instead");
                            break;
                        default:
                            try
                            {
                                loaded.Add(new LoadedRegistration(site, TableLoader.Load(site)));
                            }
                            catch (SeamcheckException ex)
                            {
                                errors.Add(ex.Message);
                            }

                            break;
                    }
                }
            }

            if (pluginPaths.Count > 0 && loaded.Count == 0 && errors.Count == 0)
            {
                errors.Add("no PatchTargetRegistry.Register call was found in " + pluginPaths.Count + " plugin assemblies; the scan is broken");
            }

            // First registration of an id wins and later copies are reported, exactly as
            // PatchTargetRegistry.Register does in game.
            var targets = new List<PatchTarget>();
            var ownerById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var registration in loaded)
            {
                foreach (var target in registration.Targets)
                {
                    if (ownerById.TryGetValue(target.Id, out var first))
                    {
                        warnings.Add("patch target id '" + target.Id + "' is already registered by " + first + "; "
                            + registration.OwnerLabel + "'s copy (" + target.Describe() + ") is ignored");
                        continue;
                    }

                    ownerById.Add(target.Id, registration.OwnerLabel);
                    targets.Add(target);
                }
            }

            return new RegistrationSummary(
                pluginPaths.Select(Path.GetFileNameWithoutExtension).Select(n => n!).ToList(),
                loaded,
                forwarders,
                targets,
                ownerById,
                warnings,
                capture.Notes.ToList(),
                errors);
        }

        /// <summary>Checks the targets, the controls and the trace preflight against one platform.</summary>
        /// <param name="spec">The platform.</param>
        /// <param name="targets">The registered targets.</param>
        /// <param name="exclusions">The Trace plugin's declared exclusions.</param>
        /// <param name="log">Diagnostics.</param>
        internal static PlatformResult CheckPlatform(
            PlatformSpec spec,
            IReadOnlyList<PatchTarget> targets,
            IReadOnlyList<TraceExclusion> exclusions,
            ToolLog log)
        {
            var errors = new List<string>();
            ManagedAssemblySet assemblies;
            try
            {
                assemblies = ManagedAssemblySet.FromManagedDirectory(spec.ManagedDirectory, log);
            }
            catch (SeamcheckException ex)
            {
                errors.Add(ex.Message);
                return new PlatformResult(
                    spec, 0, null, new PatchTargetReport(Array.Empty<PatchTargetResult>()),
                    Array.Empty<ControlOutcome>(), null, errors);
            }

            using (assemblies)
            {
                var resolver = new MetadataMemberResolver(assemblies, log);
                var report = PatchTargetChecker.Check(targets, resolver);

                var controls = Controls.Run(resolver);
                foreach (var control in controls.Where(c => !c.Passed))
                {
                    errors.Add("control " + control.Id + " failed: expected " + control.Expectation + ", got: " + control.Detail);
                }

                TracePreflightResult? trace = null;
                try
                {
                    trace = TracePreflight.Run(assemblies, spec.TracePlatform, exclusions, log);
                }
                catch (SeamcheckException ex)
                {
                    errors.Add(ex.Message);
                }

                return new PlatformResult(
                    spec,
                    assemblies.Count,
                    Sha256(assemblies.PathOf("Assembly-CSharp"), log),
                    report,
                    controls,
                    trace,
                    errors);
            }
        }

        /// <summary>Targets whose found/detail is not identical on every platform.</summary>
        /// <param name="platforms">The per-platform results.</param>
        internal static IReadOnlyList<PlatformDifference> Differences(IReadOnlyList<PlatformResult> platforms)
        {
            var differences = new List<PlatformDifference>();
            var usable = platforms.Where(p => p.Report.Results.Count > 0).ToList();
            if (usable.Count < 2)
            {
                return differences;
            }

            foreach (var first in usable[0].Report.Results)
            {
                var outcomes = new List<(string Platform, bool Found, string Detail)>();
                foreach (var platform in usable)
                {
                    var match = platform.Report.Results.FirstOrDefault(r => r.Target.Id == first.Target.Id);
                    if (match != null)
                    {
                        outcomes.Add((platform.Spec.Key, match.Found, match.Detail));
                    }
                }

                if (outcomes.Select(o => (o.Found, o.Detail)).Distinct().Count() > 1)
                {
                    differences.Add(new PlatformDifference(first.Target.Id, outcomes));
                }
            }

            return differences;
        }

        private static string? Sha256(string? path, ToolLog log)
        {
            if (path == null)
            {
                return null;
            }

            try
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
            catch (IOException ex)
            {
                log.Warn("could not hash " + path + ": " + ex.Message);
                return null;
            }
        }
    }
}
