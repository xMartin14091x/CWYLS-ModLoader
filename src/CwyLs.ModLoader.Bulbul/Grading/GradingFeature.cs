using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Grading;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Textures;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Grading
{
    /// <summary>
    /// Scenery, part two: a Lightroom-style photo-grading layer over the whole room camera.
    /// <para>
    /// A preset (<c>grading/&lt;name&gt;.json</c> in a pack, or the player's own in
    /// <c>BepInEx/data/grading/</c>) is parsed and mapped in Core (<see cref="GradingPresetParser"/>,
    /// <see cref="GradingMapper"/>), then rendered through one global URP volume of our own
    /// (<see cref="GradingVolume"/>) at a priority above the game's time-of-day volumes. Only URP
    /// components the game's profiles do not use are overridden (they hold Beautify + LiftGammaGain
    /// only), so the preset sits on top of the day/sunset/night look and follows it; a preset can carry
    /// <c>day</c>/<c>sunset</c>/<c>night</c>/<c>cloudy</c> blocks for a look of its own per time of day.
    /// UI canvases are Screen Space Overlay (<c>Paremt/PCPlatform/Canvas</c>, render mode 0), drawn
    /// after the camera's post-processing, so they are never graded.
    /// </para>
    /// <para>
    /// Console: <c>grade list|apply &lt;name&gt;|off|set &lt;control&gt; &lt;value&gt;|save &lt;name&gt;|ab|export|controls|status|reload</c>.
    /// The applied preset survives a restart (<c>[Grading] Preset</c>); the file is watched and
    /// re-applied when it changes. <c>[Grading] Enabled=false</c>, <c>grade off</c> and quitting
    /// remove the volume and everything it made. Main thread only.
    /// </para>
    /// </summary>
    public static class GradingFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Grading";

        /// <summary>Config key of the switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key of the remembered preset.</summary>
        public const string ConfigKeyPreset = "Preset";

        /// <summary>Config key of the volume priority.</summary>
        public const string ConfigKeyPriority = "Priority";

        /// <summary>Config key of the hot-reload poll.</summary>
        public const string ConfigKeyWatchPollSeconds = "WatchPollSeconds";

        /// <summary>The <c>sys</c> tag of grading's bus events.</summary>
        public const string TraceSys = "grading";

        /// <summary>
        /// The console tag grading lines use: <c>TEXTURE</c> (the "how the room looks" family) until a
        /// dedicated tag exists in <c>ConsoleTags</c>.
        /// </summary>
        public const string ConsoleTag = ConsoleTags.Texture;

        /// <summary>Name of the export file in the user folder (not listed as a preset).</summary>
        public const string ExportFileName = "_export.json";

        /// <summary>Name shown for an unsaved preset built with <c>grade set</c>.</summary>
        public const string CustomName = "custom";

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<string>? _preset;
        private static ConfigEntry<float>? _priority;
        private static ConfigEntry<float>? _pollSeconds;

        private static bool _initialised;
        private static GradingUrp? _urp;
        private static string? _unavailable;
        private static GradingVolume? _volume;
        private static GradingPreset? _working;
        private static string _activeName = string.Empty;
        private static string? _activePath;
        private static string? _activeFolder;
        private static GradingTimeOfDay _timeOfDay = GradingTimeOfDay.Unknown;
        private static bool _showOriginal;
        private static GradingPlan? _lastPlan;
        private static string _lastSummary = string.Empty;
        private static TextureFileSnapshot _watched = TextureFileSnapshot.Empty;
        private static float _nextPollAt;

        /// <summary>True while <c>[Grading] Enabled</c> is on (default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>True when URP's volume machinery was found and grading can run.</summary>
        public static bool IsAvailable => _urp != null;

        /// <summary>Why grading cannot run, or null.</summary>
        public static string? UnavailableReason => _unavailable;

        /// <summary>The game's current look, as last seen.</summary>
        public static GradingTimeOfDay TimeOfDay => _timeOfDay;

        /// <summary>The applied preset's name (<c>pack:stem</c>, <c>user:stem</c>, <c>custom</c>), or null when off.</summary>
        public static string? ActivePreset => _working is null ? null : _activeName;

        /// <summary>The preset being edited and rendered, or null when off. Immutable; changes go through <see cref="Set"/>.</summary>
        public static GradingPreset? Working => _working;

        /// <summary>True while <c>grade ab</c> shows the game's own look.</summary>
        public static bool ShowingOriginal => _showOriginal;

        /// <summary>What the last apply rendered, or null.</summary>
        public static GradingPlan? LastPlan => _lastPlan;

        /// <summary>The last apply line, for the smoke report.</summary>
        public static string LastSummary => _lastSummary;

        /// <summary>The volume's description (for the smoke report); <c>no volume</c> when off.</summary>
        public static string VolumeState => _volume?.Describe() ?? "no volume";

        /// <summary>The player's own presets folder: <c>BepInEx/data/grading/</c>.</summary>
        public static string UserFolder => Path.Combine(ModLoaderPaths.DataRoot, "grading");

        /// <summary>
        /// One line: <c>on, warm-film (night), ColorAdjustments+SplitToning, graded</c>, or why not.
        /// </summary>
        public static string Status
        {
            get
            {
                if (!IsAvailable)
                {
                    return "unavailable: " + (_unavailable ?? "not initialised");
                }

                if (!Enabled)
                {
                    return "off (" + ConfigSection + "." + ConfigKeyEnabled + "=false)";
                }

                if (_working is null)
                {
                    return "on, no preset applied";
                }

                return "on, " + _activeName + " (" + Describe(_timeOfDay) + "), " + (_lastPlan?.Describe() ?? "none") +
                       ", " + (_showOriginal ? "showing the ORIGINAL" : "graded");
            }
        }

        /// <summary>
        /// Binds <c>[Grading]</c>, resolves URP, registers the command, smoke checks, patch targets
        /// and mod facet, and subscribes to room/window events. Call once from <c>BulbulPlugin.Awake</c>.
        /// </summary>
        /// <param name="config">The plugin's config file.</param>
        public static void Initialize(ConfigFile config)
        {
            lock (Gate)
            {
                if (_initialised)
                {
                    return;
                }

                _initialised = true;
            }

            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            _enabled = config.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Photo grading over the room camera (grading/<name>.json presets, console 'grade'). Off removes our " +
                "volume and shows the game's own look.");
            _preset = config.Bind(
                ConfigSection,
                ConfigKeyPreset,
                string.Empty,
                "The preset applied at start (set by 'grade apply'/'grade save', cleared by 'grade off'): a name, " +
                "<pack id>:<name> or user:<name>. Empty = none.");
            _priority = config.Bind(
                ConfigSection,
                ConfigKeyPriority,
                100f,
                new ConfigDescription(
                    "Priority of our volume; above the game's time-of-day volumes (0) and its start-scene volume (1).",
                    new AcceptableValueRange<float>(2f, 10000f)));
            _pollSeconds = config.Bind(
                ConfigSection,
                ConfigKeyWatchPollSeconds,
                1f,
                "Seconds between checks of the applied preset file (and its LUT) for edits; 0 turns hot reload off.");

            _enabled.SettingChanged += (_, __) => OnEnabledChanged();

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (grading)", GradingPatchTargets.All);
            PatchTargetVerifier.VerifyAll(GradingPatchTargets.All, new ReflectionMemberResolver());

            _urp = GradingUrp.Bind(out _unavailable);
            if (_urp is null)
            {
                ConsoleEvents.EmitAt(
                    TraceSys, "unavailable", ConsoleTag, ConsoleLevel.Warn,
                    "grading off: URP volume machinery not found", "reason", ("reason", _unavailable));
            }
            else if (_urp.BeautifyProblem != null)
            {
                ConsoleEvents.EmitAt(
                    TraceSys, "beautify", ConsoleTag, ConsoleLevel.Warn,
                    "grading: the bloom control is off", "reason", ("reason", _urp.BeautifyProblem));
            }

            GradingCommands.Register();
            GradingSmokeChecks.RegisterAll();
            ModFacetBackends.Use<ICwyModGrading>("grading", new GradingFacetBackend());

            try
            {
                Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => OnRoomReady()));
                Subscriptions.Add(CWYEvents.Subscribe<WindowChanged>(OnWindowChanged));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("GRADING: room/window events unavailable; apply with 'grade apply': " +
                                          ex.GetType().Name + ": " + ex.Message);
            }

            Subscriptions.Add(MainThread.AddTicker("grading.watch", Poll));
            Application.quitting += Teardown;

            ConsoleEvents.Setup(
                "grading",
                IsAvailable ? "armed" : "unavailable",
                ("enabled", Enabled),
                ("preset", string.IsNullOrEmpty(_preset.Value) ? "none" : _preset.Value),
                ("bloom", _urp?.BeautifyType != null),
                ("command", "grade"));
        }

        /// <summary>Every preset the game can apply: the player's own, then each loaded pack's <c>grading/*.json</c>.</summary>
        public static GradingCatalog Catalog()
        {
            var entries = new List<GradingCatalogEntry>();
            AddFolder(entries, GradingCatalogEntry.UserSource, UserFolder, 0);

            var packs = PackManager.Loaded;
            for (var i = 0; i < packs.Count; i++)
            {
                AddFolder(entries, packs[i].Id, Path.Combine(packs[i].RootPath, "grading"), i);
            }

            return new GradingCatalog(entries);
        }

        /// <summary>Applies a preset by name (<c>warm-film</c>, <c>cwy.demo.scenery:warm-film</c>, <c>user:mine</c>) and remembers it.</summary>
        /// <param name="name">The name.</param>
        public static GradingResult Apply(string? name)
        {
            var refusal = Refusal();
            if (refusal != null)
            {
                return refusal;
            }

            var entry = Catalog().Find(name, out var shadowed);
            if (entry is null)
            {
                return GradingResult.Failure("no preset named '" + name + "'; 'grade list' shows them");
            }

            var parsed = ReadFile(entry.Path, out var readProblem);
            if (parsed is null)
            {
                return Fail("apply", entry.QualifiedName + ": " + readProblem);
            }

            if (!parsed.Ok)
            {
                return Fail("apply", entry.QualifiedName + ": " + FirstError(parsed));
            }

            _working = parsed.Preset!;
            _activeName = entry.QualifiedName;
            _activePath = entry.Path;
            _activeFolder = Path.GetDirectoryName(entry.Path);
            _showOriginal = false;
            Remember(entry.QualifiedName);
            ResetWatch();

            var note = Problems(parsed) + (shadowed.Count == 0 ? string.Empty : "; shadows " + shadowed[0].QualifiedName);
            return Render("apply", "grade apply " + _activeName, note);
        }

        /// <summary>
        /// Applies a preset given as text (for mods): parsed like a file, not remembered across restarts.
        /// </summary>
        /// <param name="name">Name to show, e.g. <c>mod.id:look</c>.</param>
        /// <param name="json">The preset document.</param>
        /// <param name="folder">Folder a <c>lut.file</c> is relative to; null when it has none.</param>
        public static GradingResult ApplyJson(string name, string json, string? folder)
        {
            var refusal = Refusal();
            if (refusal != null)
            {
                return refusal;
            }

            var parsed = GradingPresetParser.Parse(json);
            if (!parsed.Ok)
            {
                return Fail("apply", name + ": " + FirstError(parsed));
            }

            _working = parsed.Preset!;
            _activeName = string.IsNullOrWhiteSpace(name) ? CustomName : name.Trim();
            _activePath = null;
            _activeFolder = folder;
            _showOriginal = false;
            ResetWatch();
            return Render("apply", "grade apply " + _activeName, Problems(parsed));
        }

        /// <summary>Removes the grading: the volume, its profile, curves and LUT. Forgets the remembered preset.</summary>
        public static GradingResult Off()
        {
            var was = _working is null ? null : _activeName;
            TearDownVolume();
            _working = null;
            _activePath = null;
            _activeFolder = null;
            _lastPlan = null;
            _showOriginal = false;
            Remember(string.Empty);
            ResetWatch();

            var line = was is null ? "grade off (nothing was applied)" : "grade off (" + was + " removed)";
            Emit("off", line, null);
            return GradingResult.Success(line);
        }

        /// <summary>Sets one control on the applied preset (or starts a <c>custom</c> one), clamped to its range.</summary>
        /// <param name="control">Id or alias, e.g. <c>exposure</c>, <c>basic.exposure</c>, <c>blues.lum</c>.</param>
        /// <param name="value">The value.</param>
        public static GradingResult Set(string? control, double value)
        {
            var refusal = Refusal();
            if (refusal != null)
            {
                return refusal;
            }

            if (!GradingControls.TryFind(control, out var found))
            {
                return GradingResult.Failure("no control '" + control + "'; 'grade controls' lists them");
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return GradingResult.Failure("a control value must be a finite number");
            }

            var clamped = found!.Clamp(value, out var changed);
            if (_working is null)
            {
                _working = GradingPreset.Neutral.WithName(CustomName);
                _activeName = CustomName;
                _activePath = null;
                _activeFolder = null;
                ResetWatch();
            }

            _working = _working.WithValue(found.Id, clamped);
            var note = changed
                ? (found.IsAngle ? "wrapped from " : "clamped from ") + value.ToString("0.###", CultureInfo.InvariantCulture)
                : null;
            return Render("set", "grade set " + found.Id + "=" + found.FormatValue(clamped), note);
        }

        /// <summary>Saves the applied preset as the player's own <c>BepInEx/data/grading/&lt;name&gt;.json</c> and switches to it.</summary>
        /// <param name="name">File name without extension.</param>
        public static GradingResult Save(string? name)
        {
            var problem = GradingFormat.NameProblem(name);
            if (problem != null)
            {
                return GradingResult.Failure(problem);
            }

            if (_working is null)
            {
                return GradingResult.Failure("nothing to save: 'grade apply <name>' or 'grade set <control> <value>' first");
            }

            try
            {
                Directory.CreateDirectory(UserFolder);
                var preset = _working.Name.Length == 0 || _working.Name == CustomName ? _working.WithName(name!) : _working;
                var path = Path.Combine(UserFolder, name + GradingFormat.Extension);
                CopyLut(preset);
                File.WriteAllText(path, GradingPresetWriter.Write(preset));

                _working = preset;
                _activeName = GradingCatalogEntry.UserSource + ":" + name;
                _activePath = path;
                _activeFolder = UserFolder;
                Remember(_activeName);
                ResetWatch();

                var line = "grade save " + _activeName;
                Emit("save", line, path);
                return GradingResult.Success(line + " -> " + path);
            }
            catch (Exception ex)
            {
                return Fail("save", "could not save '" + name + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Flips between the game's own look and the graded one, instantly (the volume's weight).</summary>
        public static GradingResult ToggleOriginal() => ShowOriginal(!_showOriginal);

        /// <summary>Shows the game's own look (true) or the graded one (false).</summary>
        /// <param name="original">True for the game's own look.</param>
        public static GradingResult ShowOriginal(bool original)
        {
            var refusal = Refusal();
            if (refusal != null)
            {
                return refusal;
            }

            if (_working is null || _volume is null || !_volume.Alive)
            {
                return GradingResult.Failure("nothing applied to compare; 'grade apply <name>' first");
            }

            _showOriginal = original;
            _volume.Weight = original ? 0f : 1f;

            var line = original ? "grade ab ORIGINAL (the game's own look)" : "grade ab GRADED (" + _activeName + ")";
            Emit("ab", line, null);
            return GradingResult.Success(line);
        }

        /// <summary>The applied preset as JSON (what <c>grade save</c> would write).</summary>
        public static string ExportJson() => GradingPresetWriter.Write(_working ?? GradingPreset.Neutral);

        /// <summary>Writes the applied preset to <c>BepInEx/data/grading/_export.json</c> and returns the text.</summary>
        public static GradingResult Export()
        {
            var json = ExportJson();
            try
            {
                Directory.CreateDirectory(UserFolder);
                var path = Path.Combine(UserFolder, ExportFileName);
                File.WriteAllText(path, json);
                Emit("export", "grade export " + (_working is null ? "(nothing applied: neutral)" : _activeName), path);
                return GradingResult.Success("wrote " + path + "\n" + json.TrimEnd());
            }
            catch (Exception ex)
            {
                return Fail("export", "could not write " + ExportFileName + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Re-reads the applied preset's file now (what the watcher does on its own).</summary>
        public static GradingResult Reload()
        {
            if (_activePath is null)
            {
                return GradingResult.Failure(_working is null ? "nothing applied" : _activeName + " has no file to reload");
            }

            return ReloadFrom("grade reload " + _activeName);
        }

        /// <summary>Applies the remembered or current preset again (room ready, time of day, enabled).</summary>
        /// <param name="trigger">For the log line.</param>
        internal static void Reapply(string trigger)
        {
            if (!Enabled || !IsAvailable)
            {
                return;
            }

            if (_working != null)
            {
                Render(trigger, "grade " + _activeName + " (" + trigger + ")", null);
                return;
            }

            var remembered = _preset?.Value;
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                var result = Apply(remembered);
                if (!result.Ok)
                {
                    WarnOnce("remembered:" + remembered, "GRADING: the remembered preset '" + remembered + "' was not applied: " + result.Message);
                }
            }
        }

        /// <summary>The effective plan's components and the volume, for the console's status line.</summary>
        internal static string Describe(GradingTimeOfDay timeOfDay) =>
            timeOfDay == GradingTimeOfDay.Unknown ? "time of day unknown" : GradingTimesOfDay.BlockName(timeOfDay);

        private static GradingResult Render(string trigger, string line, string? note)
        {
            if (_working is null || _urp is null)
            {
                return GradingResult.Failure("nothing to render");
            }

            try
            {
                var effective = _working.Resolve(_timeOfDay).ApplyBlend();
                var plan = GradingMapper.Map(effective);

                Texture? lut = null;
                if (plan.NeedsLut)
                {
                    lut = GradingLutTexture.Get(plan, _activeFolder, _urp.LutSize(), out var lutProblem);
                    if (lutProblem != null)
                    {
                        WarnOnce("lut:" + _activeName + ":" + lutProblem, "GRADING: " + _activeName + ": " + lutProblem);
                    }
                }

                if (_volume != null && !_volume.Alive)
                {
                    // Something destroyed our object (it is DontDestroyOnLoad, so this should not happen): rebuild.
                    CwyLsModLoader.Log.LogWarning("GRADING: the CWY.Grading volume was destroyed outside the feature; recreating it");
                    _volume = null;
                }

                _volume ??= GradingVolume.Create(_urp, _priority?.Value ?? 100f);

                float? gameBloom = null;
                if (plan.BloomFactor.HasValue && _urp.BeautifyType != null && _urp.TryReadGameBloom(_volume.Volume, out var bloom))
                {
                    gameBloom = bloom;
                }

                _volume.Apply(plan, lut, gameBloom);
                _volume.Weight = _showOriginal ? 0f : 1f;
                _lastPlan = plan;

                var block = _working.Blocks.ContainsKey(_timeOfDay) ? " +" + GradingTimesOfDay.BlockName(_timeOfDay) + " block" : string.Empty;
                _lastSummary = line + ": " + plan.Describe() + block;

                ConsoleEvents.Emit(
                    TraceSys,
                    trigger,
                    ConsoleTag,
                    line,
                    note is null ? "look,timeOfDay" : "look,timeOfDay,note",
                    ("look", plan.Describe() + block),
                    ("timeOfDay", Describe(_timeOfDay)),
                    ("note", note),
                    ("blend", _working.Resolve(_timeOfDay).BlendWeight),
                    ("original", _showOriginal),
                    ("volume", _volume.Describe()));

                return GradingResult.Success(_lastSummary + (note is null ? string.Empty : " (" + note + ")"));
            }
            catch (Exception ex)
            {
                return Fail(trigger, line + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static GradingResult? Refusal()
        {
            if (!IsAvailable)
            {
                return GradingResult.Failure("grading is unavailable: " + (_unavailable ?? "not initialised"));
            }

            if (!Enabled)
            {
                return GradingResult.Failure("grading is off (" + ConfigSection + "." + ConfigKeyEnabled + "=false)");
            }

            return MainThread.IsMainThread ? null : GradingResult.Failure("grading must be driven from the Unity main thread");
        }

        private static void OnRoomReady()
        {
            _timeOfDay = GradingTimeOfDayProbe.Current();
            Reapply("room ready");
        }

        private static void OnWindowChanged(WindowChanged change)
        {
            if (!change.Active)
            {
                return;
            }

            var timeOfDay = GradingTimesOfDay.FromName(change.Window);
            if (timeOfDay == GradingTimeOfDay.Unknown || timeOfDay == _timeOfDay)
            {
                return;
            }

            _timeOfDay = timeOfDay;

            // A block for the new look, or a bloom factor on the game's bloom (which the look changed),
            // needs a new profile; anything else follows the game's look by itself.
            if (_working != null && Enabled && IsAvailable &&
                (_working.Blocks.Count > 0 || (_lastPlan?.BloomFactor.HasValue ?? false)))
            {
                Render("timeOfDay", "grade " + _activeName + " follows " + GradingTimesOfDay.BlockName(timeOfDay), null);
            }
        }

        private static void OnEnabledChanged()
        {
            if (Enabled)
            {
                Reapply("enabled");
                return;
            }

            TearDownVolume();
            Emit("disabled", "grade disabled (" + ConfigSection + "." + ConfigKeyEnabled + "=false): the game's own look", null);
        }

        private static void Poll()
        {
            var seconds = _pollSeconds?.Value ?? 1f;
            if (seconds <= 0 || _activePath is null || Time.realtimeSinceStartup < _nextPollAt)
            {
                return;
            }

            _nextPollAt = Time.realtimeSinceStartup + Math.Max(0.25f, seconds);
            var now = Snapshot();
            if (now.DiffFrom(_watched).IsEmpty)
            {
                return;
            }

            _watched = now;
            if (Enabled && IsAvailable)
            {
                ReloadFrom("grade reloaded " + _activeName + " (file changed)");
            }
        }

        private static GradingResult ReloadFrom(string line)
        {
            var parsed = ReadFile(_activePath!, out var problem);
            if (parsed is null || !parsed.Ok)
            {
                var reason = parsed is null ? problem : FirstError(parsed);
                return Fail("reload", _activeName + " not reloaded, keeping the current look: " + reason);
            }

            _working = parsed.Preset!;
            ResetWatch();
            return Render("reload", line, Problems(parsed));
        }

        private static void ResetWatch()
        {
            _watched = Snapshot();
            _nextPollAt = 0f;
        }

        private static TextureFileSnapshot Snapshot()
        {
            var files = new List<KeyValuePair<string, long>>(2);
            if (_activePath != null)
            {
                files.Add(new KeyValuePair<string, long>(_activePath, StampOf(_activePath)));
                if (_working?.LutFile != null && _activeFolder != null)
                {
                    var lut = Path.Combine(_activeFolder, _working.LutFile.Replace('/', Path.DirectorySeparatorChar));
                    files.Add(new KeyValuePair<string, long>(lut, StampOf(lut)));
                }
            }

            return TextureFileSnapshot.Of(files);
        }

        private static long StampOf(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : TextureFileSnapshot.MissingStamp;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: could not stat " + path + ": " + ex.GetType().Name + ": " + ex.Message);
                return TextureFileSnapshot.MissingStamp;
            }
        }

        private static GradingParseResult? ReadFile(string path, out string? problem)
        {
            problem = null;
            try
            {
                return GradingPresetParser.Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                problem = "could not read " + path + ": " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static void CopyLut(GradingPreset preset)
        {
            // A preset saved from a pack keeps working: its LUT file is copied next to the user preset.
            if (preset.LutFile is null || _activeFolder is null ||
                string.Equals(Path.GetFullPath(_activeFolder), Path.GetFullPath(UserFolder), StringComparison.Ordinal))
            {
                return;
            }

            var relative = preset.LutFile.Replace('/', Path.DirectorySeparatorChar);
            var source = Path.Combine(_activeFolder, relative);
            var target = Path.Combine(UserFolder, relative);
            if (!File.Exists(source))
            {
                WarnOnce("lutcopy:" + source, "GRADING: the preset's LUT file " + source + " does not exist; saved without copying it");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        private static void AddFolder(List<GradingCatalogEntry> entries, string source, string folder, int order)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                foreach (var file in Directory.GetFiles(folder, "*" + GradingFormat.Extension))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (stem.Length == 0 || stem[0] == '_' || stem[0] == '.')
                    {
                        continue;
                    }

                    entries.Add(new GradingCatalogEntry(source, stem, file, order));
                }
            }
            catch (Exception ex)
            {
                WarnOnce("folder:" + folder, "GRADING: could not list " + folder + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Remember(string name)
        {
            if (_preset != null && _preset.Value != name)
            {
                _preset.Value = name;
            }
        }

        private static string? Problems(GradingParseResult parsed)
        {
            if (parsed.Problems.Count == 0)
            {
                return null;
            }

            foreach (var problem in parsed.Problems)
            {
                CwyLsModLoader.Log.LogDebug("GRADING: " + _activeName + ": " + problem);
            }

            return parsed.ErrorCount + " error(s), " + parsed.WarningCount + " warning(s); first: " + parsed.Problems[0];
        }

        private static string FirstError(GradingParseResult parsed)
        {
            foreach (var problem in parsed.Problems)
            {
                if (problem.IsError)
                {
                    return problem.ToString();
                }
            }

            return parsed.Problems.Count > 0 ? parsed.Problems[0].ToString() : "unreadable";
        }

        private static void TearDownVolume()
        {
            try
            {
                _volume?.Destroy();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("GRADING: could not remove the volume: " + ex.GetType().Name + ": " + ex.Message);
            }

            _volume = null;
            GradingLutTexture.Release();
        }

        private static void Teardown()
        {
            TearDownVolume();
            foreach (var subscription in Subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("GRADING: unsubscribe failed at quit: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Subscriptions.Clear();
        }

        private static void Emit(string name, string line, string? path) =>
            ConsoleEvents.Emit(
                TraceSys, name, ConsoleTag, line, path is null ? null : "path", ("path", path), ("volume", VolumeState));

        private static GradingResult Fail(string name, string message)
        {
            ConsoleEvents.EmitAt(TraceSys, name, ConsoleTag, ConsoleLevel.Warn, "grade " + message, null);
            return GradingResult.Failure(message);
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
    }
}
