using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Textures;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Packs;
using UnityEngine;
using UnityEngine.SceneManagement;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Textures
{
    /// <summary>
    /// Puts the PNGs in a content pack onto Satone's materials after <c>RoomScene</c> loads.
    /// <para>
    /// A pack says what it wants with folders - <c>textures/Body/_MainTex.png</c> - or with
    /// <c>textures/_rules.json</c> for the cases folders cannot express. Those become
    /// <see cref="TextureRule"/>s in <c>CwyLs.ModLoader.Core</c>, which is where the whole mapping is
    /// unit-tested; this class is the thin Unity half that finds the renderer, loads the file and
    /// writes the material.
    /// </para>
    /// <para>
    /// <b>Why it writes more than one place.</b> The first in-game run applied every rule, verified
    /// it off the live material, and changed nothing on screen.
    /// <c>Bulbul.CostumeChangeService</c> holds the five default costume objects - <c>Body</c>,
    /// <c>Bottom1</c>, <c>Paka1</c>, <c>Socks</c>, <c>Top1</c>, exactly the renderers a pack names -
    /// and on any non-default costume it <c>SetActive(false)</c>s all of them and instantiates a
    /// prefab whose renderers are called <c>Body1</c>, <c>Top3</c>, <c>Bottom2</c>. Different names,
    /// so no rule can reach them; but <c>Body1</c> uses the same <c>mt_Body</c> asset that
    /// <c>Body</c> does. The shared material is therefore the thing they have in common, which is
    /// what <see cref="TextureWriteMode.Shared"/> writes, and why that is the default. A rule can
    /// also name the material outright - <c>textures/@mt_Tee_A/_MainTex.png</c> - which is the only
    /// form that reaches a costume's own shirt. Two further overrides are handled the same way: lilToon's
    /// SRP mirrors (<c>_BaseMap</c>, <c>_BaseColorMap</c>) and a <c>MaterialPropertyBlock</c>, either
    /// of which can hide a correct material write.
    /// </para>
    /// <para>
    /// Everything it writes is undoable: the previous <c>Texture</c> of every slot it touches is
    /// kept, and <see cref="RestoreAll"/> puts the game back exactly as it was. <see cref="Apply"/>
    /// audits, restores and then applies, so a pack that is removed leaves the original texture
    /// behind and a slot something else reverted says so in the log.
    /// </para>
    /// <para>
    /// Static, with no MonoBehaviour of its own: this game destroys BepInEx plugin components about
    /// 0.2 s after boot. The frame of delay and the delayed re-passes come from
    /// <see cref="TextureHost"/>'s hidden <c>DontDestroyOnLoad</c> object.
    /// </para>
    /// </summary>
    public static class TextureFeature
    {
        /// <summary>Config section holding the texture settings.</summary>
        public const string ConfigSection = "Textures";

        /// <summary>Config key of the master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the per-apply log line.</summary>
        public const string ConfigKeyLogEachApply = "LogEachApply";

        /// <summary>Config key of the instance/shared choice.</summary>
        public const string ConfigKeyWriteMode = "WriteMode";

        /// <summary>Config key of the <c>_BaseMap</c>/<c>_BaseColorMap</c> mirror write.</summary>
        public const string ConfigKeyAlsoSetSrpMirrors = "AlsoSetSrpMirrors";

        /// <summary>Config key of the property-block override.</summary>
        public const string ConfigKeyClearPropertyBlockTextures = "ClearPropertyBlockTextures";

        /// <summary>Config key of the per-renderer diagnostic dump.</summary>
        public const string ConfigKeyDiagnose = "Diagnose";

        /// <summary>The scene whose load triggers an apply pass.</summary>
        public const string SceneName = "RoomScene";

        /// <summary>The <c>sys</c> tag this feature's diagnostic events are published under.</summary>
        public const string TraceSys = "textures";

        /// <summary><see cref="TextureRule.PackId"/> used for a texture handed to <see cref="Set"/> at runtime.</summary>
        public const string RuntimePackId = "(runtime)";

        private static readonly object Gate = new object();
        private static readonly AppliedTexture[] NoneApplied = new AppliedTexture[0];
        private static readonly LoadedPack[] NoPacks = new LoadedPack[0];

        private static readonly List<TextureBackup> Backups = new List<TextureBackup>();
        private static readonly HashSet<string> BackedUp = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, string> Diagnostics =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _logEachApply;
        private static ConfigEntry<TextureWriteMode>? _writeMode;
        private static ConfigEntry<bool>? _alsoSetSrpMirrors;
        private static ConfigEntry<bool>? _clearPropertyBlockTextures;
        private static ConfigEntry<bool>? _diagnose;
        private static ConfigFile? _ownConfig;

        private static IReadOnlyList<AppliedTexture> _applied = NoneApplied;
        private static TextureRuleSet _rules = TextureRuleSet.Empty;
        private static MaterialPropertyRuleSet _propertyRules = MaterialPropertyRuleSet.Empty;
        private static bool _initialised;
        private static bool _reloading;

        /// <summary>The last resolution a scan raised; the pack list every pass reads.</summary>
        private static PackResolution _resolution = PackResolution.Empty;

        /// <summary><see cref="_resolution"/> as folders on disk, rebuilt when the resolution changes.</summary>
        private static IReadOnlyList<LoadedPack>? _packs;

        /// <summary>True when texture replacement is switched on (<c>Textures.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>
        /// True when every applied rule gets its own <c>TEXTURE</c> line
        /// (<c>Textures.LogEachApply</c>, default true). The summary line is always logged.
        /// </summary>
        public static bool LogEachApply => _logEachApply?.Value ?? true;

        /// <summary>Which material a rule writes (<c>Textures.WriteMode</c>, default <c>Shared</c>).</summary>
        public static TextureWriteMode WriteMode => _writeMode?.Value ?? TextureWriteMode.Shared;

        /// <summary>
        /// True when a write to <c>_MainTex</c> also fills <c>_BaseMap</c> and <c>_BaseColorMap</c>
        /// where the material has them (<c>Textures.AlsoSetSrpMirrors</c>, default true).
        /// </summary>
        public static bool AlsoSetSrpMirrors => _alsoSetSrpMirrors?.Value ?? true;

        /// <summary>
        /// True when a <c>MaterialPropertyBlock</c> already carrying the property is re-set with our
        /// texture (<c>Textures.ClearPropertyBlockTextures</c>, default true). A block overrides the
        /// material, so leaving it alone would hide a correct write.
        /// </summary>
        public static bool ClearPropertyBlockTextures => _clearPropertyBlockTextures?.Value ?? true;

        /// <summary>True when each pass dumps a <c>TEXDIAG</c> block per target renderer (default true).</summary>
        public static bool Diagnose => _diagnose?.Value ?? true;

        /// <summary>
        /// The rules that reached a live material in the last apply pass, in apply order.
        /// Never null; empty before the first pass and after <see cref="RestoreAll"/>.
        /// </summary>
        public static IReadOnlyList<AppliedTexture> AppliedRules
        {
            get { lock (Gate) { return _applied; } }
        }

        /// <summary>
        /// The merged rule set of every loaded pack, as of the last apply pass. Never null.
        /// Carries the rejected files in <see cref="TextureRuleSet.Problems"/>.
        /// </summary>
        public static TextureRuleSet Rules
        {
            get { lock (Gate) { return _rules; } }
        }

        /// <summary>
        /// Scenery: the merged material <em>property</em> rules (colour, tint, float, vector entries
        /// of every pack's <c>textures/_rules.json</c>), as of the last pass. Never null.
        /// </summary>
        public static MaterialPropertyRuleSet PropertyRules
        {
            get { lock (Gate) { return _propertyRules; } }
        }

        /// <summary>
        /// Scenery: the property rules that reached a live material in the last pass. Never null;
        /// empty while <c>Scenery.Enabled=false</c>.
        /// </summary>
        public static IReadOnlyList<AppliedMaterialProperty> AppliedProperties => MaterialPropertyRules.Applied;

        /// <summary>How many rules failed to apply in the last pass.</summary>
        public static int FailedCount { get; private set; }

        /// <summary>True once an apply pass has run, whether or not it applied anything.</summary>
        public static bool HasApplied { get; private set; }

        /// <summary>
        /// The last summary line, e.g. <c>TEXTURES: applied 1 rule(s), 0 failed</c>. Empty before
        /// the first pass; also the detail the smoke checks report.
        /// </summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>
        /// The last <see cref="ReloadAll()"/> summary line, e.g.
        /// <c>TEXTURES: reloaded 3 file(s) (1 changed on disk, 0 new, 0 removed), applied 3 rule(s),
        /// 0 failed in 41 ms</c>. Empty before the first reload.
        /// </summary>
        public static string LastReloadSummary { get; private set; } = string.Empty;

        /// <summary>How many reloads have run this session, from any trigger.</summary>
        public static int ReloadCount { get; private set; }

        /// <summary>
        /// The last <c>TEXDIAG</c> block for a renderer name, or null when none was taken. Multi-line;
        /// this is the evidence a tester reads after pressing F9.
        /// </summary>
        /// <param name="rendererName">A renderer GameObject name, e.g. <c>Body</c>.</param>
        public static string? DiagnosticFor(string? rendererName)
        {
            if (string.IsNullOrEmpty(rendererName))
            {
                return null;
            }

            lock (Gate)
            {
                return Diagnostics.TryGetValue(rendererName!, out var text) ? text : null;
            }
        }

        /// <summary>
        /// Binds the config, registers the smoke checks, creates the hidden runner and arms the
        /// two triggers: <c>RoomScene</c> loading and <c>PackManager.PacksResolved</c>.
        /// </summary>
        /// <param name="config">
        /// Config file the <c>Textures.*</c> entries are bound in - pass the calling plugin's
        /// <c>Config</c>. Null is accepted and falls back to this framework's own .cfg, so a
        /// mis-wired call loses the settings file, not the feature.
        /// </param>
        /// <remarks>
        /// Idempotent: calling it twice re-arms nothing and double-registers nothing, so a plugin
        /// whose Awake runs twice cannot end up applying every rule twice.
        /// </remarks>
        public static void Initialize(ConfigFile config)
        {
            lock (Gate)
            {
                if (_initialised)
                {
                    return;
                }

                _initialised = true;

                var file = config;
                if (file is null)
                {
                    CwyLsModLoader.Log.LogWarning(
                        "TEXTURES: Initialize was given no ConfigFile; binding Textures.* in the framework's own .cfg");
                    file = OwnConfig();
                }

                _enabled = file.Bind(
                    ConfigSection,
                    ConfigKeyEnabled,
                    true,
                    "Apply the textures in <game root>/CWYLS Mods/*/textures to the scene after " +
                    SceneName + " loads. Off leaves the game's own textures alone.");

                _logEachApply = file.Bind(
                    ConfigSection,
                    ConfigKeyLogEachApply,
                    true,
                    "Log one TEXTURE line per applied rule. Off keeps only the summary line.");

                _writeMode = file.Bind(
                    ConfigSection,
                    ConfigKeyWriteMode,
                    TextureWriteMode.Shared,
                    "Which material a rule writes. Shared (default) = renderer.sharedMaterials[i], " +
                    "the loaded asset, in memory - this is what also reaches the costume prefab's " +
                    "renderers (Body1, Top3, ...), because they use the same material assets. " +
                    "Instance = renderer.materials[i], this renderer only; note that reading the " +
                    "instance array rebinds the renderer away from the asset. Both writes the asset " +
                    "first and then instantiates.");

                _alsoSetSrpMirrors = file.Bind(
                    ConfigSection,
                    ConfigKeyAlsoSetSrpMirrors,
                    true,
                    "When writing _MainTex, also write _BaseMap and _BaseColorMap where the material " +
                    "has them. The game is URP and its materials bind the same texture to all three, " +
                    "so the pass may sample a mirror rather than _MainTex.");

                _clearPropertyBlockTextures = file.Bind(
                    ConfigSection,
                    ConfigKeyClearPropertyBlockTextures,
                    true,
                    "If a MaterialPropertyBlock on the renderer already carries the property, re-set " +
                    "it with our texture. A block overrides the material, so leaving it would hide " +
                    "an otherwise correct write.");

                _diagnose = file.Bind(
                    ConfigSection,
                    ConfigKeyDiagnose,
                    true,
                    "Dump a TEXDIAG block per target renderer on every pass: active/enabled/visible, " +
                    "same-named renderers, shared and instance materials, shader keywords, property " +
                    "block, and the live values of _MainTex/_BaseMap/_BaseColorMap.");

                TextureReload.Configure(file);
            }

            TextureSmokeChecks.RegisterAll();
            TextureHost.EnsureRunner();

            // The costume seams are in the registry, so a game update that breaks one shows up
            // as PATCH TARGET MISSING at startup, not as textures silently not re-applying.
            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (textures)", CostumePatchTargets.All);
            PatchTargetVerifier.VerifyAll(CostumePatchTargets.All, new ReflectionMemberResolver());
            CostumePatches.Apply();

            // Scenery: [Scenery] switch, decoration seams, window-change re-apply, 'scenery' command.
            SceneryFeature.Initialize(config ?? OwnConfig());

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            // PacksResolved, not PacksChanged: it carries the resolution the scan just logged, so a
            // pass applies exactly the pack list the framework reported. LastResolution covers the
            // normal order, where ModLoaderPlugin scanned before this adapter's Awake ran.
            SetResolution(PackManager.LastResolution);
            PackManager.PacksResolved -= OnPacksResolved;
            PackManager.PacksResolved += OnPacksResolved;

            // The reload key and the file watcher: a pack author edits a PNG in place under
            // CWYLS Mods and sees it in the running game.
            TextureReload.Arm(PacksNow(), log: true);

            ConsoleEvents.Setup(
                "textures",
                "armed",
                ("enabled", Enabled),
                ("mode", WriteMode),
                ("trigger", SceneName + " + packs resolved + costume seams (" + CostumePatches.Patched + "/4)"),
                ("repass", string.Join("s/+", Array.ConvertAll(TextureHost.RepassSeconds, s => s.ToString("0.#"))) + "s"));

            // A plugin that loads after the scene is already up would otherwise wait for a
            // sceneLoaded that never comes again.
            if (SceneManager.GetActiveScene().name == SceneName)
            {
                TextureHost.Request("active-scene");
            }
        }

        /// <summary>
        /// Re-reads every loaded pack and applies its rules now, on the calling thread. The caller
        /// must be on the Unity main thread; the triggers go through
        /// <see cref="TextureHost.Request"/> instead, which defers a frame.
        /// </summary>
        /// <remarks>
        /// Audits and restores first, so removing a pack or renaming a file puts the game's own
        /// texture back rather than leaving the last one applied, and anything that reverted a slot
        /// behind our back is named in the log.
        /// </remarks>
        public static void Apply() => ApplyNow("manual");

        /// <summary>
        /// Re-reads every texture file in the installed packs from disk and applies them again -
        /// the "refresh the textures" button for someone editing a PNG while the game runs.
        /// </summary>
        /// <returns>
        /// How many rules ended up applied, or -1 when the reload itself failed (the reason is in
        /// <see cref="LastReloadSummary"/> and in the log).
        /// </returns>
        /// <remarks>
        /// <para>
        /// Does three things a plain <see cref="Apply"/> does not: drops the loader's asset cache,
        /// so a file whose contents changed but whose timestamp did not is still re-read; rescans
        /// the packs, so a PNG you have just added, deleted or renamed becomes a rule; and reports
        /// what moved on disk since the last reload.
        /// </para>
        /// <para>
        /// The cache is dropped wholesale: <c>AssetCache</c> has no per-path eviction, and adding
        /// one was not worth it. The cost is that pack audio is re-decoded the next time it
        /// is asked for - a development action, on demand, so that is an acceptable price.
        /// </para>
        /// <para>Main thread only. Never throws: a reload is on a key and on a file watcher.</para>
        /// </remarks>
        public static int ReloadAll() => ReloadAll("manual");

        /// <summary>
        /// Puts every texture this feature replaced back, newest first, and forgets the backups.
        /// </summary>
        /// <returns>
        /// How many slots were restored. Slots whose material has since been destroyed are skipped
        /// and not counted - there is nothing left to restore them to.
        /// </returns>
        public static int RestoreAll() => RestoreAll(quiet: false);

        /// <summary>
        /// Puts a texture a mod built at runtime onto a renderer, with the same backup, mirrors and
        /// logging as a pack rule. For generated content - an atlas, a recoloured outfit.
        /// </summary>
        /// <param name="rendererName">GameObject name of the renderer, e.g. <c>Body</c>.</param>
        /// <param name="property">
        /// Shader property (<c>_MainTex</c>) or a slot alias (<c>base</c>); see
        /// <see cref="TextureSlotAliases"/>.
        /// </param>
        /// <param name="texture">The texture to assign. Must be alive.</param>
        /// <param name="materialIndex">Which of the renderer's materials to write; 0 by default.</param>
        /// <returns>True when the texture was assigned.</returns>
        /// <remarks>
        /// The assignment lives until the next <see cref="Apply"/> or <see cref="RestoreAll"/> -
        /// including the automatic passes a scene load, a pack rescan or a delayed re-pass trigger -
        /// because those rebuild the whole set from the packs. Re-apply after one if it must survive.
        /// </remarks>
        public static bool Set(string rendererName, string property, Texture2D texture, int materialIndex = 0)
        {
            if (string.IsNullOrWhiteSpace(rendererName))
            {
                CwyLsModLoader.Log.LogWarning("TEXTURES: Set was given no renderer name");
                return false;
            }

            if (texture == null)
            {
                CwyLsModLoader.Log.LogWarning($"TEXTURES: Set('{rendererName}', '{property}') was given no texture");
                return false;
            }

            if (!TextureSlotAliases.TryResolve(property, out var resolved, out var reason))
            {
                CwyLsModLoader.Log.LogWarning($"TEXTURES: Set('{rendererName}'): {reason}");
                return false;
            }

            TextureRule rule;
            try
            {
                rule = new TextureRule(
                    RuntimePackId,
                    rendererName,
                    resolved!,
                    texture!.name.Length == 0 ? "<generated>" : texture.name,
                    materialIndex,
                    TextureRuleSource.Runtime);
            }
            catch (ArgumentException ex)
            {
                CwyLsModLoader.Log.LogWarning($"TEXTURES: Set('{rendererName}', '{property}') refused: {ex.Message}");
                return false;
            }

            try
            {
                var applied = ApplyRule(rule, RendererLookup.Scan(), texture!);
                if (applied is null)
                {
                    return false;
                }

                lock (Gate)
                {
                    var list = new List<AppliedTexture>(_applied) { applied };
                    _applied = list;
                }

                CwyLsModLoader.Log.LogDebug("TEXTURE " + applied);
                return true;
            }
            catch (Exception ex)
            {
                // A mod handing us a half-built texture must not take the game down.
                CwyLsModLoader.Log.LogError(
                    $"TEXTURES: Set('{rendererName}', '{resolved}') failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The reload pass. <see cref="ReloadAll()"/>, the reload key and the file watcher all land
        /// here.
        /// </summary>
        /// <param name="trigger">What asked for the reload, for the log line and the trace event.</param>
        /// <returns>Applied rule count, or -1 when the reload failed.</returns>
        internal static int ReloadAll(string trigger)
        {
            var started = Stopwatch.StartNew();
            var label = string.IsNullOrEmpty(trigger) ? "manual" : trigger;

            try
            {
                _reloading = true;

                var before = TextureReload.Snapshot;

                // No per-path eviction exists; see the note on the public overload.
                AssetCache.Clear();

                // Picks up files that were added, deleted or renamed: they are only rules once the
                // resolver has seen them. Raises PacksResolved, which _reloading keeps from
                // arming a second, deferred pass behind this one.
                var rescanned = PackManager.Rescan();

                var after = TextureReload.Refresh(PacksNow());
                var diff = after.DiffFrom(before);

                ApplyNow("reload (" + label + ")");

                started.Stop();
                ReloadCount++;
                LastReloadSummary =
                    $"TEXTURES: reloaded {diff.Counts()}, applied {AppliedRules.Count} rule(s), " +
                    $"{FailedCount} failed in {started.ElapsedMilliseconds} ms ({label})" +
                    (diff.IsEmpty ? string.Empty : "; " + diff.Summarize()) +
                    (rescanned ? string.Empty : "; the pack rescan failed: " + PackManager.LastScanError);

                ConsoleEvents.Emit(
                    TraceSys,
                    "reload",
                    ConsoleTags.Texture,
                    "reloaded " + diff.Counts(),
                    "applied,failed,ms,trigger",
                    ("trigger", label),
                    ("files", diff.Total),
                    ("changed", diff.Changed.Count),
                    ("added", diff.Added.Count),
                    ("removed", diff.Removed.Count),
                    ("applied", AppliedRules.Count),
                    ("failed", FailedCount),
                    ("rescanned", rescanned),
                    ("ms", started.ElapsedMilliseconds),
                    ("watch", TextureReload.Mode));

                return AppliedRules.Count;
            }
            catch (Exception ex)
            {
                started.Stop();
                LastReloadSummary =
                    $"TEXTURES: reload failed ({label}): {ex.GetType().Name}: {ex.Message}";
                ConsoleEvents.EmitAt(
                    TraceSys,
                    "reload",
                    ConsoleTags.Texture,
                    ConsoleLevel.Error,
                    "reload failed (" + label + ")",
                    "error,ms",
                    ("trigger", label),
                    ("error", ex.GetType().Name + ": " + ex.Message),
                    ("ms", started.ElapsedMilliseconds));

                return -1;
            }
            finally
            {
                _reloading = false;
            }
        }

        /// <summary>
        /// The apply pass. Public entry points and <see cref="TextureHost.Pump"/> all land here.
        /// </summary>
        /// <param name="trigger">What asked for this pass, for the log line.</param>
        internal static void ApplyNow(string trigger)
        {
            try
            {
                if (!Enabled)
                {
                    RestoreAll(quiet: true);
                    LastSummary = $"TEXTURES: disabled ({ConfigSection}.{ConfigKeyEnabled}=false)";
                    HasApplied = true;
                    ConsoleEvents.Setup(
                        "textures", $"disabled ({ConfigSection}.{ConfigKeyEnabled}=false)");
                    return;
                }

                var rules = BuildRuleSet(out var textureFiles, out var propertyRules);
                lock (Gate)
                {
                    _rules = rules;
                    _propertyRules = propertyRules;
                }

                foreach (var problem in rules.Problems)
                {
                    // Keyed by pack as well as path: two packs can ship the same bad relative path.
                    WarnOnce("problem:" + problem.PackId + ":" + problem.Path, "TEXTURES: " + problem);
                }

                foreach (var problem in propertyRules.Problems)
                {
                    WarnOnce("problem:" + problem.PackId + ":" + problem.Path, "TEXTURES: " + problem);
                }

                foreach (var loser in rules.Overridden)
                {
                    CwyLsModLoader.Log.LogDebug($"TEXTURES: {loser.PackId}/{loser.RelativePath} overridden by a later pack");
                }

                var reverted = AuditBeforeRestore(trigger);
                RestoreAll(quiet: true);

                var applied = new List<AppliedTexture>(rules.Count);
                var failed = 0;
                MaterialLookup? passMaterials = null;

                if (rules.Count > 0)
                {
                    var lookup = RendererLookup.Scan();
                    var materials = MaterialLookup.Scan(lookup);
                    passMaterials = materials;
                    var report = new LoadReport();

                    if (Diagnose)
                    {
                        DumpDiagnostics(rules, lookup, materials);
                    }

                    foreach (var rule in rules.Rules)
                    {
                        var result = ApplyRule(rule, lookup, texture: null, report, materials);
                        if (result is null)
                        {
                            failed++;
                            continue;
                        }

                        applied.Add(result);
                        if (LogEachApply)
                        {
                            // One line per PASS on the console (below); a rule is file detail.
                            CwyLsModLoader.Log.LogDebug("TEXTURE " + result);
                        }
                    }

                    if (report.Count > 0)
                    {
                        CwyLsModLoader.Log.LogDebug("TEXTURES: " + report.Summarize());
                    }
                }

                // Scenery: material property rules, on the same restored state.
                var propertiesApplied = 0;
                if (propertyRules.Count > 0 && SceneryFeature.Enabled)
                {
                    var materials = passMaterials ?? MaterialLookup.Scan(RendererLookup.Scan());
                    propertiesApplied = MaterialPropertyRules.Apply(propertyRules, materials, WarnOnce, out var propertiesFailed);
                    failed += propertiesFailed;
                }

                lock (Gate)
                {
                    _applied = applied.Count == 0 ? NoneApplied : applied;
                }

                FailedCount = failed;
                HasApplied = true;
                // LastSummary belongs to the SMOKE REPORT; the console line is built
                // from the payload, so the two can no longer drift into each other.
                LastSummary = $"TEXTURES: applied {applied.Count} rule(s), {failed} failed" +
                              (propertyRules.Count > 0 ? $", {propertiesApplied} property rule(s)" : string.Empty);

                var total = applied.Count + propertiesApplied;
                ConsoleEvents.Emit(
                    TraceSys,
                    "applied",
                    ConsoleTags.Texture,
                    total == 1 ? "1 rule applied" : total + " rules applied",
                    propertyRules.Count > 0 ? "failed,trigger,mode,files,props" : "failed,trigger,mode,files",
                    ("applied", applied.Count),
                    ("failed", failed),
                    ("trigger", trigger),
                    ("mode", WriteMode.ToString()),
                    ("files", textureFiles),
                    ("props", propertiesApplied),
                    ("rules", rules.Summarize()),
                    ("propertyRules", propertyRules.Summarize()),
                    ("reverted", reverted));
            }
            catch (Exception ex)
            {
                // A broken pack, a destroyed renderer, a shader that changed under us: none of them
                // may take the frame down. The state stays whatever the last good pass left.
                FailedCount++;
                LastSummary = $"TEXTURES: apply failed ({trigger}): {ex.GetType().Name}: {ex.Message}";
                CwyLsModLoader.Log.LogError(LastSummary);
            }
        }

        /// <summary>
        /// Dumps one <c>TEXDIAG</c> block per distinct renderer the rules target, before anything is
        /// written, so the log shows the state the game left rather than the state we made.
        /// </summary>
        private static void DumpDiagnostics(TextureRuleSet rules, RendererLookup lookup, MaterialLookup materials)
        {
            foreach (var name in rules.MaterialNames)
            {
                try
                {
                    var text = RendererDiagnostics.DescribeMaterial(name, materials.Find(name), lookup);

                    lock (Gate)
                    {
                        Diagnostics[TextureRule.MaterialPrefix + name] = text;
                    }

                    // A TEXDIAG block is four physical lines of which only the first is
                    // stamped - the one class of line a line-oriented viewer cannot attribute.
                    // It goes to the debug channel; DiagnosticFor() still serves it to the smoke
                    // report and the console command.
                    CwyLsModLoader.Log.LogDebug(text);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"{RendererDiagnostics.Prefix} '@{name}' could not be taken: {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var name in rules.RendererNames)
            {
                try
                {
                    var chosen = lookup.Find(name, out _);
                    var index = FirstMaterialIndexFor(rules, name);
                    var text = RendererDiagnostics.Describe(name, chosen, lookup.All(name), lookup, index);

                    lock (Gate)
                    {
                        Diagnostics[name] = text;
                    }

                    // A TEXDIAG block is four physical lines of which only the first is
                    // stamped - the one class of line a line-oriented viewer cannot attribute.
                    // It goes to the debug channel; DiagnosticFor() still serves it to the smoke
                    // report and the console command.
                    CwyLsModLoader.Log.LogDebug(text);
                }
                catch (Exception ex)
                {
                    // The diagnostic exists to explain failures; it must never become one.
                    CwyLsModLoader.Log.LogWarning(
                        $"{RendererDiagnostics.Prefix} '{name}' could not be taken: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static int FirstMaterialIndexFor(TextureRuleSet rules, string name)
        {
            var forRenderer = rules.ForRenderer(name);
            return forRenderer.Count == 0 ? TextureRule.DefaultMaterialIndex : forRenderer[0].MaterialIndex;
        }

        /// <summary>
        /// Compares every slot we wrote against its live value, before restoring. A mismatch means
        /// something else - a costume swap, a lightmap switcher - put its own texture back, which is
        /// exactly what a delayed re-pass exists to catch.
        /// </summary>
        /// <param name="trigger">The pass's trigger, for the log line.</param>
        /// <returns>How many slots had been reverted.</returns>
        private static int AuditBeforeRestore(string trigger)
        {
            List<TextureBackup> snapshot;
            lock (Gate)
            {
                if (Backups.Count == 0)
                {
                    return 0;
                }

                snapshot = new List<TextureBackup>(Backups);
            }

            var reverted = 0;

            foreach (var backup in snapshot)
            {
                try
                {
                    var live = backup.ReadLive(out var alive);

                    if (!alive)
                    {
                        reverted++;
                        CwyLsModLoader.Log.LogWarning(
                            $"TEXTURES: re-applying ({trigger}): {backup} no longer exists - the material or " +
                            "renderer was replaced since the last pass");
                        continue;
                    }

                    if (backup.Written == null || live == backup.Written)
                    {
                        continue;
                    }

                    reverted++;
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: re-applying ({trigger}): {backup} previous value was " +
                        $"{RendererDiagnostics.Describe(live)}, we wrote {RendererDiagnostics.Describe(backup.Written)}");
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: could not audit {backup}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return reverted;
        }

        /// <summary>Applies one rule. Returns null when nothing could be written; warns once per rule.</summary>
        /// <param name="rule">The rule.</param>
        /// <param name="lookup">The renderer index for this pass.</param>
        /// <param name="texture">A texture supplied by the caller, or null to load the rule's file.</param>
        /// <param name="report">Load report for the file, or null for the session report.</param>
        private static AppliedTexture? ApplyRule(
            TextureRule rule,
            RendererLookup lookup,
            Texture2D? texture,
            LoadReport? report = null,
            MaterialLookup? materials = null)
        {
            if (rule.Target == TextureRuleTarget.Material)
            {
                return ApplyMaterialRule(rule, lookup, materials ?? MaterialLookup.Scan(lookup), texture, report);
            }

            var renderer = lookup.Find(rule.RendererName, out var ambiguity);
            if (ambiguity != null)
            {
                WarnOnce("ambiguous:" + rule.RendererName, "TEXTURES: " + ambiguity);
            }

            if (renderer == null)
            {
                WarnOnce(
                    "renderer:" + rule.Key,
                    $"TEXTURES: no renderer named '{rule.RendererName}' in the scene ({rule}); " +
                    $"scanned {lookup.Scanned} renderer(s) over {lookup.NameCount} name(s)");
                return null;
            }

            var loaded = texture;
            if (loaded == null)
            {
                var pack = FindPack(rule.PackId);
                if (pack is null)
                {
                    WarnOnce("pack:" + rule.Key, $"TEXTURES: pack '{rule.PackId}' is no longer loaded ({rule})");
                    return null;
                }

                // TextureLoader records and logs its own failures; null here means it already said why.
                loaded = TextureLoader.Load(pack.FullPath(rule.RelativePath), TextureLoadOptions.Default, report);
                if (loaded == null)
                {
                    return null;
                }
            }

            var written = new List<string>(4);
            var mode = WriteMode;

            // ORDER IS LOAD-BEARING. Reading renderer.materials does not just return copies: it
            // instantiates them and rebinds the renderer, so sharedMaterials afterwards returns the
            // instance, not the asset. Writing "instance first, then shared" therefore writes the
            // instance twice and never touches the asset - which is exactly how the first build
            // patched mt_Body's instance while the worn costume's Body1 kept the original asset
            // (id 23774) and nothing changed on screen. Shared always goes first, on the asset.
            var sharedMaterial = At(SafeMaterials(renderer!, instances: false), rule.MaterialIndex);
            var before = sharedMaterial == null ? 0 : sharedMaterial!.GetInstanceID();

            if (mode != TextureWriteMode.Instance)
            {
                // The shared asset, in memory only - no game file is touched. This is what reaches a
                // costume prefab renderer we cannot name; see the note on TextureWriteMode.
                WriteMaterial(renderer!, sharedMaterial, TextureTarget.Shared, rule, loaded!, written);
            }

            if (mode != TextureWriteMode.Shared)
            {
                // Only now, and only when asked: this call instantiates and rebinds the renderer.
                WriteMaterial(
                    renderer!, At(SafeMaterials(renderer!, instances: true), rule.MaterialIndex),
                    TextureTarget.Instance, rule, loaded!, written);
            }

            WriteBlock(renderer!, rule, loaded!, written);

            var after = At(SafeMaterials(renderer!, instances: false), rule.MaterialIndex);
            var afterId = after == null ? 0 : after!.GetInstanceID();

            if (written.Count == 0)
            {
                WarnOnce(
                    "nowrite:" + rule.Key,
                    $"TEXTURES: nothing written for {rule}: no material at index {rule.MaterialIndex} " +
                    $"declares {rule.PropertyName} (mode={mode}). " + RendererDiagnostics.Describe(sharedMaterial));
                return null;
            }

            var ids = "mat=" + before + (afterId == before ? string.Empty : "->" + afterId + " REBOUND");

            return new AppliedTexture(
                rule,
                RendererLookup.PathOf(renderer),
                loaded!,
                RendererDiagnostics.Brief(renderer, lookup.All(rule.RendererName)) + " " + ids + " -> " +
                string.Join("+", written.ToArray()));
        }

        /// <summary>
        /// Applies a rule that names a material asset rather than a renderer: writes every loaded
        /// material with that name - the asset and any instance copied from it - so the texture
        /// reaches renderers no rule could name, and is already in place when the game instantiates
        /// a costume prefab that uses it.
        /// </summary>
        /// <param name="rule">The material rule.</param>
        /// <param name="lookup">The pass's renderer index, for reporting who uses the material.</param>
        /// <param name="materials">The pass's material index.</param>
        /// <param name="texture">A texture supplied by the caller, or null to load the rule's file.</param>
        /// <param name="report">Load report for the file, or null for the session report.</param>
        private static AppliedTexture? ApplyMaterialRule(
            TextureRule rule,
            RendererLookup lookup,
            MaterialLookup materials,
            Texture2D? texture,
            LoadReport? report)
        {
            var matches = materials.Find(rule.MaterialName);
            if (matches.Count == 0)
            {
                WarnOnce(
                    "material:" + rule.Key,
                    $"TEXTURES: no loaded material named '{rule.MaterialName}' ({rule}); " +
                    $"scanned {materials.Scanned} material(s) over {materials.NameCount} name(s)");
                return null;
            }

            var loaded = texture;
            if (loaded == null)
            {
                var pack = FindPack(rule.PackId);
                if (pack is null)
                {
                    WarnOnce("pack:" + rule.Key, $"TEXTURES: pack '{rule.PackId}' is no longer loaded ({rule})");
                    return null;
                }

                loaded = TextureLoader.Load(pack.FullPath(rule.RelativePath), TextureLoadOptions.Default, report);
                if (loaded == null)
                {
                    return null;
                }
            }

            var written = new List<string>(matches.Count);
            var ids = new List<string>(matches.Count);
            var activeUsers = 0;

            foreach (var material in matches)
            {
                if (material == null)
                {
                    continue;
                }

                var slots = new List<string>(3);

                // The renderer is only carried for the log line; a material rule is keyed on the
                // material's own instance id, so the same asset reached from ten renderers is
                // backed up once and restored once.
                foreach (var property in PropertiesFor(rule.PropertyName, material!))
                {
                    Remember(
                        TextureTarget.Shared,
                        FirstUserOf(lookup, material!),
                        material,
                        TextureRule.DefaultMaterialIndex,
                        property,
                        material!.GetTexture(property),
                        loaded);

                    material.SetTexture(property, loaded!);
                    slots.Add(property);
                }

                if (slots.Count == 0)
                {
                    continue;
                }

                written.Add(material!.name + "{" + string.Join("+", slots.ToArray()) + "}");
                ids.Add(material.GetInstanceID().ToString());
                activeUsers += lookup.ActiveUsersOf(material).Count;
            }

            if (written.Count == 0)
            {
                WarnOnce(
                    "nowrite:" + rule.Key,
                    $"TEXTURES: nothing written for {rule}: none of the {matches.Count} material(s) " +
                    $"named '{rule.MaterialName}' declares {rule.PropertyName}");
                return null;
            }

            return new AppliedTexture(
                rule,
                $"{matches.Count} material(s) named '{rule.MaterialName}', {activeUsers} active renderer(s)",
                loaded!,
                $"[material] mat={string.Join(",", ids.ToArray())} -> {string.Join(" ", written.ToArray())}");
        }

        /// <summary>A renderer currently using this material, for the backup's log line; may be null.</summary>
        private static Renderer? FirstUserOf(RendererLookup lookup, Material material)
        {
            var users = lookup.ActiveUsersOf(material);
            return users.Count > 0 ? users[0] : lookup.AnyUserOf(material);
        }

        /// <summary>
        /// Writes the rule's property, and its SRP mirrors when they exist, onto one material.
        /// </summary>
        /// <param name="renderer">The renderer that owns the material, for the backup record.</param>
        /// <param name="material">The material, or null when the index does not exist.</param>
        /// <param name="target">Which kind of material this is.</param>
        /// <param name="rule">The rule being applied.</param>
        /// <param name="texture">The texture to write.</param>
        /// <param name="written">Collects <c>target:property</c> for the log line.</param>
        private static void WriteMaterial(
            Renderer? renderer,
            Material? material,
            TextureTarget target,
            TextureRule rule,
            Texture2D texture,
            List<string> written)
        {
            if (material == null)
            {
                return;
            }

            foreach (var property in PropertiesFor(rule.PropertyName, material!))
            {
                Remember(target, renderer, material, rule.MaterialIndex, property, material!.GetTexture(property), texture);
                material.SetTexture(property, texture);
                written.Add(Label(target) + ":" + property);
            }
        }

        /// <summary>
        /// Re-sets a <c>MaterialPropertyBlock</c> that already carries the property. A block
        /// overrides the material for every property it holds, so a correct material write is
        /// invisible while a block keeps the old texture.
        /// </summary>
        private static void WriteBlock(Renderer renderer, TextureRule rule, Texture2D texture, List<string> written)
        {
            if (!ClearPropertyBlockTextures || !renderer.HasPropertyBlock())
            {
                return;
            }

            var block = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(block);

            var changed = false;
            foreach (var property in BlockProperties(rule.PropertyName))
            {
                var existing = block.GetTexture(property);
                if (existing == null)
                {
                    // Only a property the block already overrides: adding one would make the block
                    // authoritative for a slot the material was happily driving.
                    continue;
                }

                Remember(TextureTarget.PropertyBlock, renderer, null, rule.MaterialIndex, property, existing, texture);
                block.SetTexture(property, texture);
                written.Add("block:" + property);
                changed = true;
            }

            if (changed)
            {
                renderer.SetPropertyBlock(block);
            }
        }

        /// <summary>The properties to write on a material: the rule's, plus any mirror it declares.</summary>
        private static IEnumerable<string> PropertiesFor(string property, Material material)
        {
            if (material.HasProperty(property))
            {
                yield return property;
            }

            if (!AlsoSetSrpMirrors)
            {
                yield break;
            }

            foreach (var mirror in TextureSlotAliases.MirrorsOf(property))
            {
                if (material.HasProperty(mirror))
                {
                    yield return mirror;
                }
            }
        }

        /// <summary>The properties to consider on a property block. A block has no shader to ask.</summary>
        private static IEnumerable<string> BlockProperties(string property)
        {
            yield return property;

            if (!AlsoSetSrpMirrors)
            {
                yield break;
            }

            foreach (var mirror in TextureSlotAliases.MirrorsOf(property))
            {
                yield return mirror;
            }
        }

        private static Material[] SafeMaterials(Renderer renderer, bool instances)
        {
            try
            {
                return (instances ? renderer.materials : renderer.sharedMaterials) ?? new Material[0];
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not read {(instances ? "instance" : "shared")} materials of " +
                    $"{RendererLookup.PathOf(renderer)}: {ex.GetType().Name}: {ex.Message}");
                return new Material[0];
            }
        }

        private static Material? At(Material[] materials, int index) =>
            index >= 0 && index < materials.Length ? materials[index] : null;

        private static string Label(TextureTarget target) =>
            target == TextureTarget.Instance ? "instance" : "shared";

        /// <summary>
        /// Arms an apply pass when <see cref="SceneName"/> loads. The game also loads a Transition
        /// scene around it, so this can fire more than once per visit; re-arming is harmless
        /// because a pass restores before it applies.
        /// </summary>
        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == SceneName)
            {
                TextureHost.Request("sceneLoaded");
            }
        }

        /// <summary>
        /// Takes the scan's resolution and arms an apply pass, so a rescan takes effect without a
        /// restart. Raised on whatever thread scanned, which is why it only arms.
        /// <para>
        /// The pass is skipped until the first scene-driven pass has run - but the resolution is
        /// still stored, so the first pass uses it. Before <see cref="SceneName"/> exists there are
        /// no renderers to find, and a pass then would warn "no renderer named Body" - which, being
        /// a warn-once, would swallow the real warning if it ever mattered.
        /// </para>
        /// </summary>
        /// <param name="resolution">What that scan loaded and refused.</param>
        private static void OnPacksResolved(PackResolution resolution)
        {
            SetResolution(resolution);

            // A pack that was added or removed moves the set of folders to watch.
            TextureReload.Arm(PacksNow());

            // _reloading: ReloadAll rescans the packs itself and applies straight afterwards, so
            // the pass it would arm here is a second, redundant one a frame later.
            if (HasApplied && !_reloading)
            {
                TextureHost.Request("packsResolved");
            }
        }

        /// <summary>Stores a resolution and drops the folder list built from the previous one.</summary>
        /// <param name="resolution">The resolution to apply from now on; null is treated as empty.</param>
        private static void SetResolution(PackResolution? resolution)
        {
            lock (Gate)
            {
                _resolution = resolution ?? PackResolution.Empty;
                _packs = null;
            }
        }

        /// <summary>
        /// The current resolution's packs as folders on disk, in load order. Never null.
        /// </summary>
        /// <remarks>
        /// Built from the resolution a scan handed us rather than read back from
        /// <c>PackManager.Loaded</c>, so a pass and the rules it is applying always come from the
        /// same scan, and cached until the next scan replaces it.
        /// </remarks>
        internal static IReadOnlyList<LoadedPack> PacksNow()
        {
            lock (Gate)
            {
                return _packs ??= PacksOf(_resolution);
            }
        }

        private static IReadOnlyList<LoadedPack> PacksOf(PackResolution resolution)
        {
            if (resolution.Loaded.Count == 0)
            {
                return NoPacks;
            }

            var packs = new List<LoadedPack>(resolution.Loaded.Count);
            foreach (var pack in resolution.Loaded)
            {
                // A directory source knows its real folder; any other source (a test's, a future
                // archive's) can only be named under CWYLS Mods, which is where the scan looked.
                var root = pack.Source is DirectoryPackSource directory
                    ? directory.RootPath
                    : Path.Combine(ModLoaderPaths.PacksRoot, pack.Source.FolderName);

                packs.Add(new LoadedPack(pack.Manifest, root, pack.Index));
            }

            return packs;
        }

        /// <summary>The pack a rule came from, or null when the last scan did not load it.</summary>
        /// <param name="packId">A pack id, matched the way <c>PackManager.Find</c> matches.</param>
        private static LoadedPack? FindPack(string packId)
        {
            foreach (var pack in PacksNow())
            {
                if (string.Equals(pack.Id, packId, StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }

            return null;
        }

        /// <summary>Reads every loaded pack's rules, in load order, and merges them.</summary>
        /// <param name="textureFiles">How many PNGs the packs hold under <c>textures/</c>.</param>
        /// <param name="propertyRules">Scenery: the merged material property rules of the same files.</param>
        private static TextureRuleSet BuildRuleSet(out int textureFiles, out MaterialPropertyRuleSet propertyRules)
        {
            textureFiles = 0;
            var sets = new List<TextureRuleSet?>();
            var propertySets = new List<MaterialPropertyRuleSet?>();

            foreach (var pack in PacksNow())
            {
                textureFiles += pack.Index.Textures.Count;
                var json = ReadRulesJson(pack);
                sets.Add(TextureRuleParser.Build(pack.Id, pack.Index.Files, json));
                propertySets.Add(MaterialPropertyRuleParser.FromRulesJson(pack.Id, json));
            }

            propertyRules = MaterialPropertyRuleSet.Merge(propertySets);
            return TextureRuleSet.Merge(sets);
        }

        /// <summary>The text of a pack's <c>textures/_rules.json</c>, or null when it has none.</summary>
        private static string? ReadRulesJson(LoadedPack pack)
        {
            try
            {
                var path = pack.FullPath(TextureRuleParser.RulesPath);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex)
            {
                // An unreadable rules file costs that pack its advanced mapping, not its textures.
                CwyLsModLoader.Log.LogWarning(
                    $"TEXTURES: could not read {pack.Id}/{TextureRuleParser.RulesPath}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static int RestoreAll(bool quiet)
        {
            // Scenery: material property slots first (they are independent of the texture slots).
            var propertySlots = MaterialPropertyRules.BackedUpCount;
            var restoredProperties = MaterialPropertyRules.RestoreAll();

            List<TextureBackup> backups;
            lock (Gate)
            {
                if (Backups.Count == 0)
                {
                    _applied = NoneApplied;
                    if (!quiet && propertySlots > 0)
                    {
                        ConsoleEvents.Emit(
                            TraceSys,
                            "restored",
                            ConsoleTags.Texture,
                            "restored " + restoredProperties + " of " + propertySlots + " slot(s)",
                            null,
                            ("restored", restoredProperties),
                            ("slots", propertySlots));
                    }

                    return restoredProperties;
                }

                backups = new List<TextureBackup>(Backups);
                Backups.Clear();
                BackedUp.Clear();
                _applied = NoneApplied;
            }

            var restored = restoredProperties;

            // Newest first, so a slot written twice ends on the value it had before the first write.
            for (var i = backups.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (backups[i].Restore())
                    {
                        restored++;
                    }
                }
                catch (Exception ex)
                {
                    // One dead material must not strand the rest of the restore.
                    CwyLsModLoader.Log.LogWarning(
                        $"TEXTURES: could not restore {backups[i]}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (!quiet)
            {
                ConsoleEvents.Emit(
                    TraceSys,
                    "restored",
                    ConsoleTags.Texture,
                    "restored " + restored + " of " + (backups.Count + propertySlots) + " slot(s)",
                    null,
                    ("restored", restored),
                    ("slots", backups.Count + propertySlots));
            }

            return restored;
        }

        /// <summary>Stores what a slot held, the first time we write it and never again.</summary>
        private static void Remember(
            TextureTarget target,
            Renderer? renderer,
            Material? material,
            int materialIndex,
            string property,
            Texture? original,
            Texture? written)
        {
            var backup = new TextureBackup(target, renderer, material, materialIndex, property, original, written);

            lock (Gate)
            {
                if (BackedUp.Add(backup.Key))
                {
                    Backups.Add(backup);
                }
            }
        }

        private static void WarnOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!Warned.Add(key))
                {
                    return;
                }
            }

            CwyLsModLoader.Log.LogWarning(message);
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".textures.cfg"), saveOnInit: true);
    }
}
