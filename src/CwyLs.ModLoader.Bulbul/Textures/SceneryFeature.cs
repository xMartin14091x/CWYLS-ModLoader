using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Textures;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Scenery, part one: scenery and desk materials.
    /// <list type="bullet">
    ///   <item><description>Material property rules (<c>color</c>/<c>tint</c>/<c>float</c>/<c>vector</c>
    ///   entries of <c>textures/_rules.json</c>) are applied by <see cref="TextureFeature"/>'s pass while
    ///   <c>[Scenery] Enabled</c> is on (<see cref="MaterialPropertyRules"/>).</description></item>
    ///   <item><description>The pass is re-armed when a window view or the time of day changes
    ///   (<see cref="WindowChanged"/>) and when a decoration changes (<see cref="SceneryPatches"/>).</description></item>
    ///   <item><description>Console: <c>scenery scan [pattern]</c>, <c>scenery export [pattern|all]</c>
    ///   (writes <c>BepInEx/data/materials.json</c>), <c>scenery list</c>, <c>scenery restore</c>,
    ///   <c>scenery apply</c>.</description></item>
    /// </list>
    /// Initialised from <see cref="TextureFeature.Initialize"/>; no plugin line of its own.
    /// </summary>
    public static class SceneryFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Scenery";

        /// <summary>Config key of the switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>The console command.</summary>
        public const string CommandName = "scenery";

        /// <summary>Name of the export file under <c>BepInEx/data/</c>.</summary>
        public const string ExportFileName = "materials.json";

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static ConfigEntry<bool>? _enabled;
        private static bool _initialised;

        /// <summary>True while scenery/desk property rules and their re-apply triggers are on (default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>Path of the last <c>materials.json</c> written, or empty.</summary>
        public static string LastExportPath { get; private set; } = string.Empty;

        /// <summary>Binds the switch, registers and patches the decoration seams, subscribes to window changes, registers the command and smoke checks. Idempotent.</summary>
        /// <param name="file">The config file the <c>Textures.*</c> entries live in.</param>
        internal static void Initialize(ConfigFile file)
        {
            lock (Gate)
            {
                if (_initialised)
                {
                    return;
                }

                _initialised = true;
            }

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Apply the material property rules (color, tint, float, vector entries of textures/_rules.json) " +
                "and re-apply pack textures when a window view, the time of day or a decoration changes. " +
                "Off restores every property rule at the next pass.");
            _enabled.SettingChanged += (_, __) => TextureHost.Request("scenery switch");

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (scenery)", SceneryPatchTargets.All);
            PatchTargetVerifier.VerifyAll(SceneryPatchTargets.All, new ReflectionMemberResolver());
            SceneryPatches.Apply();

            try
            {
                Subscriptions.Add(CWYEvents.Subscribe<WindowChanged>(OnWindowChanged));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: window-change re-apply is off: could not subscribe to WindowChanged: {ex.GetType().Name}: {ex.Message}");
            }

            Framework.ConsoleFeature.Register(
                CommandName,
                "Scenery and desk materials: scan, export materials.json, list property rules, restore, apply.",
                "scenery scan [pattern]|export [pattern|all]|list|restore|apply",
                "textures",
                Run);

            ScenerySmokeChecks.RegisterAll();

            ConsoleEvents.Setup(
                "scenery",
                "armed",
                ("enabled", Enabled),
                ("seams", SceneryPatches.Patched + "/3"),
                ("trigger", "window change + decoration change"));
        }

        /// <summary>
        /// The <c>scenery</c> console command (also reachable as <c>tex scan</c> / <c>tex export</c>
        /// once those commands are routed here). Main thread.
        /// </summary>
        /// <param name="args">Arguments after the command name.</param>
        /// <returns>What to print.</returns>
        public static string Run(IReadOnlyList<string> args)
        {
            var verb = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
            var rest = new List<string>();
            for (var i = 1; i < args.Count; i++)
            {
                rest.Add(args[i]);
            }

            switch (verb)
            {
                case "scan":
                    return Scan(rest);
                case "export":
                    return Export(rest);
                case "list":
                    return List();
                case "restore":
                    return "restored " + TextureFeature.RestoreAll() + " slot(s) (textures and material properties) to the game's own";
                case "apply":
                    TextureFeature.Apply();
                    return TextureFeature.LastSummary;
                default:
                    return "scenery scan [pattern]|export [pattern|all]|list|restore|apply";
            }
        }

        /// <summary><c>tex scan [pattern]</c>: one line per loaded scenery/desk material (or matching the patterns).</summary>
        /// <param name="patterns">Name patterns; none means the scenery, desk and window families.</param>
        public static string Scan(IReadOnlyList<string> patterns)
        {
            var entries = SceneryScanner.Scan(patterns, withProperties: false, out var truncated);
            var sb = new StringBuilder();
            sb.Append("SCENERY scan: ").Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(" material(s) matching ")
              .Append(string.Join(" ", (patterns.Count == 0 ? SceneryMaterials.DefaultScanPatterns : patterns)));
            if (truncated)
            {
                sb.Append(" (first ").Append(SceneryScanner.MaxEntries.ToString(CultureInfo.InvariantCulture)).Append(')');
            }

            foreach (var entry in entries)
            {
                sb.Append("\n  ").Append(MaterialScan.SummaryLine(entry));
            }

            ConsoleEvents.Emit(
                TextureFeature.TraceSys,
                "scan",
                ConsoleTags.Texture,
                "scenery scan " + entries.Count + " material(s)",
                "filter",
                ("count", entries.Count),
                ("filter", patterns.Count == 0 ? "default" : string.Join(" ", patterns)));

            return sb.ToString();
        }

        /// <summary><c>tex export [pattern|all]</c>: writes every matching material with all properties to <c>BepInEx/data/materials.json</c>.</summary>
        /// <param name="patterns">Name patterns; <c>all</c> means every loaded material; none means the default families.</param>
        public static string Export(IReadOnlyList<string> patterns)
        {
            var filter = patterns.Count == 1 && string.Equals(patterns[0], "all", StringComparison.OrdinalIgnoreCase)
                ? (IReadOnlyList<string>)new[] { "*" }
                : patterns;

            var entries = SceneryScanner.Scan(filter, withProperties: true, out var truncated);
            var used = filter.Count == 0 ? SceneryMaterials.DefaultScanPatterns : filter;
            var json = MaterialScan.ToJson(entries, DateTimeOffset.Now, used);

            try
            {
                ModLoaderPaths.EnsureCreated();
                var path = Path.Combine(ModLoaderPaths.DataRoot, ExportFileName);
                File.WriteAllText(path, json);
                LastExportPath = path;

                ConsoleEvents.Emit(
                    TextureFeature.TraceSys,
                    "export",
                    ConsoleTags.Texture,
                    "scenery export " + entries.Count + " material(s)",
                    "path",
                    ("count", entries.Count),
                    ("path", path),
                    ("truncated", truncated));

                return "wrote " + entries.Count + " material(s)" + (truncated ? " (truncated)" : string.Empty) + " to " + path;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TEXTURES: could not write {ExportFileName}: {ex.GetType().Name}: {ex.Message}");
                return "could not write " + ExportFileName + ": " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string List()
        {
            var rules = TextureFeature.PropertyRules;
            var applied = TextureFeature.AppliedProperties;
            var sb = new StringBuilder("SCENERY: ")
                .Append(Enabled ? "on" : "off (" + ConfigSection + "." + ConfigKeyEnabled + "=false)")
                .Append(", ").Append(rules.Summarize()).Append(", ").Append(applied.Count).Append(" applied")
                .Append(", decoration seams ").Append(SceneryPatches.Patched).Append("/3");

            foreach (var item in applied)
            {
                sb.Append("\n  ").Append(item);
            }

            foreach (var problem in rules.Problems)
            {
                sb.Append("\n  ! ").Append(problem);
            }

            return sb.ToString();
        }

        private static void OnWindowChanged(WindowChanged change)
        {
            try
            {
                if (!Enabled || !TextureFeature.HasApplied)
                {
                    return;
                }

                if (TextureFeature.Rules.Count == 0 && TextureFeature.PropertyRules.Count == 0)
                {
                    return;
                }

                TextureHost.Request("window " + change.Describe());
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TEXTURES: window-change re-apply failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
