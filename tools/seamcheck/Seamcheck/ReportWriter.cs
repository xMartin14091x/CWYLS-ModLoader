using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Trace;

namespace CWY.Seamcheck
{
    /// <summary>
    /// Writes a <see cref="SeamcheckResult"/> for people (standard output) and for tools (JSON).
    /// Each platform section ends with the game's own summary line, <c>targets: N ok, M missing</c>,
    /// and lists missing targets with the game's own <c>PATCH TARGET MISSING</c> lines.
    /// </summary>
    internal static class ReportWriter
    {
        /// <summary>JSON schema id; bump on any breaking change to the JSON shape.</summary>
        internal const string Schema = "cwy.seamcheck/1";

        /// <summary>Writes the text report.</summary>
        /// <param name="result">The run.</param>
        /// <param name="output">Where to write.</param>
        internal static void WriteText(SeamcheckResult result, TextWriter output)
        {
            var registrations = result.Registrations;
            output.WriteLine(
                "seamcheck: " + registrations.Loaded.Count + " registrations, " + registrations.Targets.Count
                + " targets, from " + registrations.PluginAssemblies.Count + " plugin assemblies ("
                + string.Join(", ", registrations.PluginAssemblies) + ")");

            foreach (var registration in registrations.Loaded)
            {
                output.WriteLine(
                    "  " + registration.OwnerLabel.PadRight(34) + " " + registration.Targets.Count.ToString().PadLeft(3)
                    + "  " + registration.Site.TableName);
            }

            foreach (var forwarder in registrations.Forwarders)
            {
                output.WriteLine("  registration API (forwards its argument): " + forwarder.Location);
            }

            foreach (var note in registrations.Notes)
            {
                output.WriteLine("  note: plugin code logged while its table was read: " + note);
            }

            foreach (var warning in registrations.Warnings)
            {
                output.WriteLine("WARN " + warning);
            }

            foreach (var error in registrations.Errors)
            {
                output.WriteLine("ERROR " + error);
            }

            foreach (var platform in result.Platforms)
            {
                output.WriteLine();
                WritePlatform(platform, output);
            }

            if (result.Platforms.Count > 1)
            {
                output.WriteLine();
                output.WriteLine("platform differences: " + (result.Differences.Count == 0 ? "none" : result.Differences.Count + " target(s)"));
                foreach (var difference in result.Differences)
                {
                    output.WriteLine("  " + difference.Id);
                    foreach (var (platform, found, detail) in difference.Outcomes)
                    {
                        output.WriteLine("    " + platform + ": " + (found ? "ok " : "MISSING ") + detail);
                    }
                }
            }

            output.WriteLine();
            output.WriteLine("seamcheck: exit " + result.ExitCode + " (" + ExitMeaning(result.ExitCode) + ")");
        }

        private static void WritePlatform(PlatformResult platform, TextWriter output)
        {
            var spec = platform.Spec;
            output.WriteLine(
                "== " + spec.Key + " (" + TraceExclusions.Name(spec.TracePlatform) + "): " + spec.ManagedDirectory);
            if (platform.AssemblyCount > 0)
            {
                output.WriteLine(
                    "   " + platform.AssemblyCount + " assemblies; Assembly-CSharp sha256 "
                    + (platform.GameAssemblySha256 ?? "(absent)"));
            }

            foreach (var control in platform.Controls)
            {
                output.WriteLine(
                    "control " + control.Id + ": " + (control.Passed ? "ok" : "FAILED")
                    + " (expected " + control.Expectation + "; " + control.Detail + ")");
            }

            if (platform.Trace is { } trace)
            {
                output.WriteLine(
                    "trace preflight: " + trace.Methods + " methods in " + trace.Classes
                    + " classes would be patched (default config); static-constructor hazards: "
                    + (trace.Hazards.Count == 0 ? "none" : trace.Hazards.Count.ToString()));
                foreach (var hazard in trace.Hazards.Where(h => h.ExcludedHere))
                {
                    output.WriteLine(
                        "  excluded " + hazard.Type
                        + " (" + hazard.Init + " .cctor calls " + string.Join(", ", hazard.Calls) + "; "
                        + hazard.PatchedMethods + " method(s) would be patched)");
                }

                // An uncovered hazard fails the run (exit 1), so it gets the one failure line.
                foreach (var hazard in trace.UncoveredHazards)
                {
                    output.WriteLine(TracePreflight.UncoveredLine(hazard, platform.Spec.TracePlatform));
                }

                foreach (var exclusion in trace.Exclusions)
                {
                    var warn = exclusion.Verdict != "needed" && exclusion.Verdict != "not-applicable";
                    output.WriteLine(
                        (warn ? "WARN " : "  ") + "trace exclusion " + exclusion.Type + " [" + exclusion.Platforms + "]: "
                        + exclusion.Verdict + " (type " + (exclusion.TypePresent ? "present" : "absent")
                        + ", " + exclusion.WouldPatch + " method(s) the filter accepts, hazard "
                        + (exclusion.HazardPresent ? "present" : "absent") + ")");
                }
            }

            foreach (var line in platform.Report.MissingLines())
            {
                output.WriteLine(line);
            }

            foreach (var error in platform.Errors)
            {
                output.WriteLine("ERROR " + error);
            }

            output.WriteLine(platform.Report.Summarize());
        }

        /// <summary>Writes the JSON report.</summary>
        /// <param name="result">The run.</param>
        /// <param name="path">The output file; its folder is created if needed.</param>
        internal static void WriteJson(SeamcheckResult result, string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var registrations = result.Registrations;
            var document = new
            {
                schema = Schema,
                tool = typeof(ReportWriter).Assembly.GetName().Version?.ToString(3),
                exitCode = result.ExitCode,
                registrations = new
                {
                    pluginAssemblies = registrations.PluginAssemblies,
                    tables = registrations.Loaded.Select(r => new
                    {
                        owner = r.OwnerLabel,
                        table = r.Site.TableName,
                        callSite = r.Site.Location,
                        count = r.Targets.Count,
                    }),
                    forwarders = registrations.Forwarders.Select(f => f.Location),
                    targetCount = registrations.Targets.Count,
                    notes = registrations.Notes,
                    warnings = registrations.Warnings,
                    errors = registrations.Errors,
                },
                platforms = result.Platforms.Select(p => new
                {
                    platform = p.Spec.Key,
                    managedDirectory = p.Spec.ManagedDirectory,
                    assemblyCount = p.AssemblyCount,
                    assemblyCSharpSha256 = p.GameAssemblySha256,
                    summary = p.Report.Summarize(),
                    ok = p.Report.OkCount,
                    missing = p.Report.MissingCount,
                    controls = p.Controls,
                    errors = p.Errors,
                    trace = p.Trace is null ? null : new
                    {
                        methods = p.Trace.Methods,
                        classes = p.Trace.Classes,
                        hazards = p.Trace.Hazards,
                        uncoveredHazards = p.Trace.UncoveredHazards.Select(h => TracePreflight.UncoveredLine(h, p.Spec.TracePlatform)),
                        exclusions = p.Trace.Exclusions.Select(e => new
                        {
                            type = e.Type,
                            platforms = e.Platforms,
                            applies = e.Applies,
                            typePresent = e.TypePresent,
                            wouldPatch = e.WouldPatch,
                            hazardPresent = e.HazardPresent,
                            verdict = e.Verdict,
                        }),
                    },
                    results = p.Report.Results.Select(r => new
                    {
                        id = r.Target.Id,
                        owner = registrations.OwnerById.TryGetValue(r.Target.Id, out var owner) ? owner : null,
                        target = r.Target.Describe(),
                        kind = r.Target.MemberKind.ToString(),
                        found = r.Found,
                        detail = r.Detail,
                    }),
                }),
                differences = result.Differences.Select(d => new
                {
                    id = d.Id,
                    outcomes = d.Outcomes.Select(o => new { platform = o.Platform, found = o.Found, detail = o.Detail }),
                }),
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(document, options) + Environment.NewLine);
        }

        private static string ExitMeaning(int code) => code switch
        {
            0 => "all targets resolved, no uncovered trace hazard",
            1 => "a registered target is missing, or a trace hazard is not excluded",
            _ => "tool error; the result above cannot be trusted as a whole",
        };
    }
}
