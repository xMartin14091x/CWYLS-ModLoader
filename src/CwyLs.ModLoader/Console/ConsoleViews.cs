using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Smoke;
using CwyLs.ModLoader.Input;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Smoke;
using UnityEngine;

namespace CwyLs.ModLoader.Console
{
    /// <summary>
    /// The text behind the overlay's built-in tabs. One implementation per view, so the
    /// <c>packs</c> command and the Packs tab can never drift apart: both call the same method.
    /// </summary>
    internal static class ConsoleViews
    {
        /// <summary>
        /// Marks a loaded pack's warning line in the Packs view. A glyph the game's console font
        /// (<c>Base_JP</c>) has in its static atlas; U+26A0 is not in it and would draw as a box.
        /// </summary>
        internal const string WarningMarker = "▲";

        /// <summary>
        /// What loaded (with one line per load warning, e.g. made for another game version), what
        /// was refused, and why.
        /// </summary>
        internal static string Packs()
        {
            var sb = new StringBuilder();
            var loaded = PackManager.Loaded;
            var skipped = PackManager.Skipped;
            var warnings = PackManager.Warnings;

            sb.Append("PACKS: ").Append(loaded.Count).Append(" loaded, ")
                .Append(skipped.Count).Append(" skipped");

            if (!PackManager.Enabled)
            {
                sb.Append("  (pack loading is off: ")
                    .Append(PackManager.ConfigSection).Append('.')
                    .Append(PackManager.ConfigKeyEnabled).Append("=false)");
            }

            if (!string.IsNullOrEmpty(PackManager.LastScanError))
            {
                sb.Append("\nlast scan failed: ").Append(PackManager.LastScanError);
            }

            sb.Append("\nroot: ").Append(ModLoaderPaths.PacksRoot);

            foreach (var pack in loaded)
            {
                sb.Append("\n  ").Append(pack.Manifest.Id).Append('@').Append(pack.Manifest.Version)
                    .Append("  ").Append(pack.Index.Files.Count).Append(" file(s)");
                if (!string.IsNullOrEmpty(pack.Manifest.Name))
                {
                    sb.Append("  \"").Append(pack.Manifest.Name).Append('"');
                }
            }

            // PACK-VERSION: loaded anyway, but the author should know (PackWarning.ToString is
            // "id: message", the same text as the startup PACKS warning line).
            foreach (var warning in warnings)
            {
                sb.Append("\n  ").Append(WarningMarker).Append(' ').Append(warning.ToString());
            }

            foreach (var skip in skipped)
            {
                sb.Append("\n  ! ").Append(skip.Id).Append(" (").Append(skip.Reason).Append(") - ")
                    .Append(skip.Detail);
            }

            if (loaded.Count == 0 && skipped.Count == 0)
            {
                sb.Append("\n  (no pack folders found)");
            }

            return sb.ToString();
        }

        /// <summary>The last smoke report as it was written to disk, summarised.</summary>
        internal static string Smoke()
        {
            var path = SmokeHost.ReportPath;
            var sb = new StringBuilder("SMOKE: ")
                .Append(SmokeRegistry.Count).Append(" check(s) registered");

            var runner = SmokeHost.EnsureRunner();
            sb.Append(runner == null ? "; no runner" : "; session " + runner.Session);
            sb.Append("\nreport: ").Append(path);
            sb.Append("\ntrigger file: ").Append(SmokeHost.TriggerFilePath);

            string json;
            try
            {
                if (!File.Exists(path))
                {
                    return sb.Append("\n  no report yet - press the smoke.run key, or run 'smoke run'")
                        .ToString();
                }

                json = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                var message = "could not read the report: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning(ConsoleFeature.LogPrefix + " " + message);
                return sb.Append("\n  ").Append(message).ToString();
            }

            try
            {
                var report = JsonReader.ParseObject(json);
                sb.Append("\n  ok=").Append(Text(report, "ok"))
                    .Append(" passed=").Append(Text(report, "passed"))
                    .Append('/').Append(Text(report, "total"))
                    .Append("  at ").Append(Text(report, "time"));

                var detail = Text(report, "detail");
                if (detail.Length > 0)
                {
                    sb.Append("\n  ").Append(detail);
                }

                if (report.TryGetValue("checks", out var value) && value is List<object?> checks)
                {
                    foreach (var entry in checks)
                    {
                        if (entry is not JsonObject check)
                        {
                            continue;
                        }

                        var ok = check.TryGetValue("ok", out var okValue) && okValue is bool flag && flag;
                        sb.Append("\n  ").Append(ok ? "  " : "! ")
                            .Append(Text(check, "id")).Append("  ").Append(Text(check, "detail"));
                    }
                }
            }
            catch (Exception ex)
            {
                // A half-written report is normal if a run is in flight; say so rather than
                // pretending there is no report at all.
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix} the smoke report did not parse: {ex.GetType().Name}: {ex.Message}");
                sb.Append("\n  the report did not parse (a run may be in progress): ")
                    .Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>The registered patch targets, optionally re-verified against the running game.</summary>
        /// <param name="verify">True to resolve every target again and report what is missing.</param>
        internal static string Targets(bool verify)
        {
            var all = PatchTargetRegistry.All;
            var sb = new StringBuilder("TARGETS: ").Append(all.Count).Append(" registered by ")
                .Append(string.Join(", ", Owners()));

            if (verify)
            {
                var report = PatchTargetVerifier.VerifyAll();
                sb.Append("\n  ").Append(report.Summarize());
                foreach (var line in report.MissingLines())
                {
                    sb.Append("\n  ").Append(line);
                }

                return sb.ToString();
            }

            var assembly = string.Empty;
            foreach (var target in all)
            {
                if (!string.Equals(assembly, target.Assembly, StringComparison.Ordinal))
                {
                    assembly = target.Assembly;
                    sb.Append("\n[").Append(assembly).Append(']');
                }

                sb.Append("\n  ").Append(target.Id).Append("  ").Append(target.Describe());
            }

            sb.Append("\n('targets verify' resolves every one of them again)");
            return sb.ToString();
        }

        /// <summary>Every bound hotkey and its two chords.</summary>
        internal static string Keys()
        {
            var keys = Hotkeys.List();
            if (keys.Count == 0)
            {
                return "KEYS: none registered";
            }

            var sb = new StringBuilder("KEYS: ").Append(keys.Count).Append(" bound");
            foreach (var key in keys)
            {
                sb.Append("\n  ").Append(key.Format());
                if (key.Description.Length > 0)
                {
                    sb.Append("\n      ").Append(key.Description);
                }
            }

            return sb.ToString();
        }

        /// <summary>Who we are, what we are running on, and what is loaded.</summary>
        internal static string Version()
        {
            var sb = new StringBuilder();
            sb.Append(CwyLsModLoader.PluginName).Append(" v").Append(CwyLsModLoader.PluginVersion)
                .Append("  |  Unity ").Append(Application.unityVersion)
                .Append("  |  game v").Append(Application.version);

            sb.Append("\nplugins: ");
            try
            {
                var names = new List<string>();
                foreach (var pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
                {
                    names.Add(pair.Value.Metadata.GUID);
                }

                names.Sort(StringComparer.Ordinal);
                sb.Append(names.Count).Append(" (").Append(string.Join(", ", names.ToArray())).Append(')');
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"{ConsoleFeature.LogPrefix} could not list the plugins: {ex.GetType().Name}: {ex.Message}");
                sb.Append("could not be listed: ").Append(ex.GetType().Name);
            }

            sb.Append("\npacks: ").Append(PackManager.Loaded.Count).Append(" loaded, ")
                .Append(PackManager.Skipped.Count).Append(" skipped");
            sb.Append("\ncommands: ").Append(ConsoleFeature.Commands.Count)
                .Append("  panels: ").Append(ConsolePanels.Count)
                .Append("  keys: ").Append(Hotkeys.Count);
            return sb.ToString();
        }

        /// <summary>Where everything the framework writes lives.</summary>
        internal static string Paths()
        {
            var sb = new StringBuilder("PATHS");
            sb.Append("\n  packs:   ").Append(ModLoaderPaths.PacksRoot);
            sb.Append("\n  trace:   ").Append(ModLoaderPaths.TraceRoot);
            sb.Append("\n  data:    ").Append(ModLoaderPaths.DataRoot);
            sb.Append("\n  report:  ").Append(SmokeHost.ReportPath);
            sb.Append("\n  trigger: ").Append(SmokeHost.TriggerFilePath);
            sb.Append("\n  config:  ").Append(BepInEx.Paths.ConfigPath);
            return sb.ToString();
        }

        /// <summary>Frame timing, screen, and how much the console itself is holding.</summary>
        internal static string Stats()
        {
            var delta = Time.smoothDeltaTime;
            var fps = delta > 0.0001f ? 1f / delta : 0f;
            var sb = new StringBuilder("STATS");
            sb.Append("\n  fps ").Append(fps.ToString("0.0", CultureInfo.InvariantCulture))
                .Append("  frame ").Append(Time.frameCount)
                .Append("  up ").Append(Time.realtimeSinceStartup.ToString("0", CultureInfo.InvariantCulture)).Append('s');
            sb.Append("\n  screen ").Append(Screen.width).Append('x').Append(Screen.height)
                .Append("  scene ").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            sb.Append("\n  console ").Append(ConsoleFeature.Output.Count).Append('/')
                .Append(ConsoleFeature.Output.Capacity).Append(" lines, ")
                .Append(ConsoleFeature.Output.TotalWritten).Append(" written this session");
            sb.Append("\n  bus emitted ").Append(Core.Bus.TraceBus.Emitted)
                .Append(", buffered ").Append(Core.Bus.TraceBus.Buffered)
                .Append(", dropped ").Append(Core.Bus.TraceBus.Dropped)
                .Append(", consumer ").Append(Core.Bus.TraceBus.HasConsumer ? "attached" : "none");
            return sb.ToString();
        }

        private static IReadOnlyList<string> Owners()
        {
            var owners = PatchTargetRegistry.Owners;
            return owners.Count == 0 ? new[] { "nobody" } : owners;
        }

        private static string Text(JsonObject obj, string key) =>
            obj.TryGetValue(key, out var value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
    }
}
