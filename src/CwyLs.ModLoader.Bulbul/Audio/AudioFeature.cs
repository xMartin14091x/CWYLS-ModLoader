using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Loaders;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Packs;
using HarmonyLib;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// Replaces the clips of the game's sound rows (ambient loops, ambient one-shots, system
    /// and timer sounds, story music) from packs and mods, adds pack music to the music list, and
    /// offers a live A/B switch between the game's clip and the replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rows.</b> A pack file <c>audio/&lt;table&gt;/&lt;Name&gt;.ogg|wav</c> replaces the row of that name
    /// (<see cref="AudioReplacementPlan"/>). The swap writes the row's <c>AudioClip</c> field in
    /// <c>MasterDataLoader</c>'s lists, which every game service reads live on each play (e.g.
    /// <c>RoomAmbientSoundService</c>, <c>SystemSeService</c>), and gives our clip the game clip's
    /// <em>name</em>, because the KanKikuchi managers (<c>AudioManager</c>, <c>AmbientSEManager</c>)
    /// find, fade and stop players by name. Every swap also moves the players holding
    /// the old clip (<see cref="AudioPlayers"/>), so a loop never ends up unstoppable.
    /// </para>
    /// <para>
    /// <b>When.</b> Applied after every master-data injection (<c>MasterDataFeature.Injected</c>,
    /// a master-data edit may rebuild the lists), when the pack clips finish decoding, at
    /// <c>RoomReady</c> (the fallback when the master-data feature is off), and by the console. Restored
    /// at <c>Application.quitting</c> and by <c>audio off</c>.
    /// </para>
    /// <para>
    /// <b>Paid content.</b> Never replaced: see <see cref="AudioPaidContent"/> (story music that plays
    /// a soundtrack track; pack tracks re-listing a game track). One WARN names each refused row.
    /// </para>
    /// <para>
    /// Static and initialised from the adapter plugin's Awake (the game destroys plugin components
    /// shortly after boot). Everything runs on the main thread: Awake, the clip callbacks
    /// (<c>AudioLoader</c>), the injection event, the console and the Harmony patches.
    /// </para>
    /// </remarks>
    public static class AudioFeature
    {
        /// <summary>Config section.</summary>
        public const string ConfigSection = "Audio";

        /// <summary>Master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Row replacement switch.</summary>
        public const string ConfigKeyReplaceClips = "ReplaceClips";

        /// <summary>Pack music switch.</summary>
        public const string ConfigKeyPackMusic = "PackMusic";

        /// <summary>Per-row log switch.</summary>
        public const string ConfigKeyLogEachClip = "LogEachClip";

        /// <summary>The trace <c>sys</c> family: <c>audio/preload</c>, <c>applied</c>, <c>refused</c>, <c>ab</c>, <c>music</c>, <c>saveguard</c>, <c>restore</c>.</summary>
        public const string TraceSys = "audio";

        private static readonly object Gate = new object();
        private static readonly AudioSwapBook<AudioClip> Book = new AudioSwapBook<AudioClip>();
        private static readonly Dictionary<string, AudioClip> PackClips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WarnedKeys = new HashSet<string>(StringComparer.Ordinal);

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _replaceClips;
        private static ConfigEntry<bool>? _packMusic;
        private static ConfigEntry<bool>? _logEachClip;
        private static ConfigFile? _ownConfig;
        private static Harmony? _harmony;
        private static bool _initialized;
        private static bool _active = true;
        private static bool _quitting;
        private static bool _rowSeams;
        private static bool _musicSeams;
        private static bool _soundtrackSeam;
        private static bool _playSeams;
        private static string _missing = string.Empty;
        private static string _fingerprint = string.Empty;
        private static int _pending;
        private static int _loadGeneration;
        private static IReadOnlyList<AudioCandidate> _candidates = Array.Empty<AudioCandidate>();
        private static List<PackMusicInput> _musicInputs = new List<PackMusicInput>();
        private static List<RowBinding> _rows = new List<RowBinding>();
        private static AudioReplacementPlan _plan = AudioReplacementPlan.Empty;
        private static SoundtrackCatalog _soundtrack = SoundtrackCatalog.Empty;
        private static HashSet<string> _paidKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static IDisposable? _roomReady;

        /// <summary>True when the feature is switched on (<c>Audio.Enabled</c>, default true).</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>True when pack rows are replaced (<c>Audio.ReplaceClips</c>, default true).</summary>
        public static bool ReplaceClipsEnabled => Enabled && (_replaceClips?.Value ?? true) && _rowSeams;

        /// <summary>True when pack music is listed (<c>Audio.PackMusic</c>, default true) and its seams are in place.</summary>
        public static bool PackMusicEnabled => Enabled && (_packMusic?.Value ?? true) && _musicSeams;

        /// <summary>True when every replacement is logged (<c>Audio.LogEachClip</c>, default true).</summary>
        public static bool LogEachClip => _logEachClip?.Value ?? true;

        /// <summary>False after <c>audio off</c>: every row plays the game's clip until <c>audio on</c>.</summary>
        public static bool IsActive => _active;

        /// <summary>The missing seams, or empty when all resolved.</summary>
        public static string MissingSeams => _missing;

        /// <summary>The last preload/apply summary, or empty.</summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>The current row plan (after the last apply).</summary>
        public static AudioReplacementPlan Plan => _plan;

        /// <summary>True once the game's sound tables have been read.</summary>
        public static bool RowsKnown => _rows.Count > 0;

        /// <summary>How many pack clips are decoded.</summary>
        public static int LoadedClipCount => PackClips.Count;

        /// <summary>How many pack files are still decoding.</summary>
        public static int PendingCount => _pending;

        /// <summary>The game's music list as last read.</summary>
        public static SoundtrackCatalog Soundtrack => _soundtrack;

        /// <summary>
        /// Binds <c>[Audio]</c>, verifies the seams, patches the music load and the save guard,
        /// registers the console command, smoke checks and the mod facet, and starts decoding the
        /// packs' audio. Called once from the adapter plugin's Awake; later calls do nothing.
        /// </summary>
        /// <param name="config">The plugin's config file; null binds in the feature's own file.</param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            AudioSmokeChecks.RegisterAll();
            AudioCommands.Register();

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (audio)", AudioTargets.All);
            var report = PatchTargetVerifier.VerifyAll(AudioTargets.All, new ReflectionMemberResolver());
            ReadSeams(report);

            if (!Enabled)
            {
                ConsoleEvents.Setup("audio", "disabled (" + ConfigSection + "." + ConfigKeyEnabled + "=false); the game's own sounds are untouched");
                return;
            }

            ModFacetBackends.Use<ICwyModAudio>("audio", AudioFacetBackend.Instance);

            _harmony = new Harmony(BulbulPlugin.PluginGuid + ".audio");
            if (_musicSeams && (_packMusic?.Value ?? true))
            {
                if (!PackMusic.Apply(_harmony))
                {
                    _musicSeams = false;
                    CwyLsModLoader.Log.LogWarning("AUDIO pack music is off: " + PackMusic.Detail);
                }
                else
                {
                    PackMusic.LoadState();
                }
            }

            MasterDataFeature.Injected += OnInjected;
            PackManager.PacksResolved += OnPacksResolved;
            Application.quitting += OnQuitting;
            try
            {
                _roomReady = CWYEvents.Subscribe<RoomReady>(_ => Apply("roomReady"));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO could not subscribe to RoomReady (rows apply on injection and decode only): " + ex.GetType().Name + ": " + ex.Message);
            }

            ConsoleEvents.Setup(
                "audio",
                "sound replacement and pack music ready; console 'audio'",
                ("rows", ReplaceClipsEnabled),
                ("music", PackMusicEnabled),
                ("ab", AudioPlayers.Available ? "live" : "pending"),
                ("missing", _missing.Length == 0 ? null : _missing));

            Preload("startup", force: true);
        }

        /// <summary>
        /// Re-reads the packs' audio: changed files are decoded again, new ones loaded, rows and the
        /// music list updated once they finish. A removed pack track stays listed until the next launch
        /// (the game has no way to remove a non-local track).
        /// </summary>
        /// <param name="trigger">What asked, for the log.</param>
        public static void Reload(string trigger = "reload")
        {
            if (!Enabled)
            {
                return;
            }

            Preload(string.IsNullOrWhiteSpace(trigger) ? "reload" : trigger, force: true);
        }

        /// <summary>
        /// Brings every row to the clip it should hold (replacement, original, or untouched) and moves
        /// the players with it. Main thread.
        /// </summary>
        /// <param name="trigger">What asked, for the log.</param>
        /// <returns>How many rows changed.</returns>
        public static int Apply(string trigger)
        {
            if (!Enabled || _quitting)
            {
                return 0;
            }

            try
            {
                var loader = MasterDataFeature.ResolveLoader();
                if (!AudioRows.IsLoaded(loader))
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO (" + trigger + "): the game's sound tables are not loaded yet");
                    return 0;
                }

                _soundtrack = AudioRows.Soundtrack(loader);
                _rows = AudioRows.Read(loader, ObserveAndDescribe);

                var descriptors = new List<AudioRowDescriptor>(_rows.Count);
                foreach (var row in _rows)
                {
                    descriptors.Add(row.Descriptor);
                }

                _plan = ReplaceClipsEnabled ? AudioReplacementPlan.Build(_candidates, descriptors, _soundtrack) : AudioReplacementPlan.Empty;
                _paidKeys = PaidKeys(descriptors);
                Book.SetPackClaims(PackClaims());
                ReportSkips();

                var changed = 0;
                foreach (var row in _rows)
                {
                    if (Swap(row, trigger, logLine: LogEachClip) != AudioSwitchMode.Unchanged)
                    {
                        changed++;
                    }
                }

                RefreshMusicPlan(trigger);
                PackMusic.AddLate(trigger);

                if (changed > 0)
                {
                    LastSummary = _plan.Summarize() + "; " + changed + " row(s) changed (" + trigger + ")";
                    ConsoleEvents.Emit(
                        TraceSys, "applied", ConsoleTags.Audio, LastSummary, "trigger,changed,replaced,refused",
                        ("trigger", trigger), ("changed", changed), ("replaced", _plan.Assignments.Count),
                        ("refused", _plan.RefusedCount), ("active", _active));
                }

                return changed;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("AUDIO (" + trigger + ") apply failed: " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        /// <summary>
        /// The A/B switch: the game's clip or the replacement for the rows a spec names; a playing loop
        /// moves to the other clip at the same position. One <c>AUDIO</c> line per row. Main thread.
        /// </summary>
        /// <param name="spec">A row (<c>LightRain</c>, <c>ambient/LightRain</c>), a table, or <c>all</c>.</param>
        /// <param name="variant">The clip to play; null toggles (all rows together).</param>
        public static AudioSwitchResult Select(string spec, AudioVariant? variant)
        {
            if (!Enabled)
            {
                return AudioSwitchResult.Refused(ConfigSection + "." + ConfigKeyEnabled + "=false");
            }

            Apply("ab");
            var infos = Rows();
            if (infos.Count == 0)
            {
                return AudioSwitchResult.Refused("the game's sound tables are not loaded yet");
            }

            var resolved = AudioRowSpec.Resolve(spec, infos, replacedOnly: true);
            if (!resolved.Ok)
            {
                return AudioSwitchResult.Refused(resolved.Problem);
            }

            var target = variant ?? ToggleTarget(resolved.Rows);
            if (target == AudioVariant.Pack && !_active)
            {
                return AudioSwitchResult.Refused("audio is off ('audio on' first)");
            }

            var outcomes = new List<AudioSwitchOutcome>();
            foreach (var info in resolved.Rows)
            {
                var row = Find(info.Key);
                if (row is null)
                {
                    outcomes.Add(new AudioSwitchOutcome(info.Key, Book.VariantOf(info.Key), AudioSwitchMode.Failed, 0, -1, "the row is gone (master data reloaded); try again"));
                    continue;
                }

                var before = Book.VariantOf(info.Key);
                Book.Select(info.Key, target);
                var outcome = SwitchRow(row, target, before);
                outcomes.Add(outcome);
            }

            return new AudioSwitchResult(outcomes, null);
        }

        /// <summary>Every row back to the game's clip (players moved), until <see cref="SetActive"/>(true). Main thread.</summary>
        /// <param name="active">False = <c>audio off</c>; true = <c>audio on</c>.</param>
        /// <returns>How many rows changed.</returns>
        public static int SetActive(bool active)
        {
            _active = active;
            var changed = Apply(active ? "on" : "off");
            ConsoleEvents.Emit(
                TraceSys, active ? "on" : "off", ConsoleTags.Audio,
                (active ? "on: replacements back" : "off: every row plays the game's clip") + " (" + changed + " row(s) changed)",
                "changed", ("changed", changed), ("active", active));
            return changed;
        }

        /// <summary>Every game sound row with its replacement, selection and players. Main thread.</summary>
        public static IReadOnlyList<AudioRowInfo> Rows()
        {
            var infos = new List<AudioRowInfo>(_rows.Count);
            foreach (var row in _rows)
            {
                var original = Book.OriginalOf(row.Key);
                var replacement = Book.ReplacementOf(row.Key, out var source);
                if (_paidKeys.Contains(row.Key))
                {
                    replacement = null;
                    source = string.Empty;
                }

                var playing = AudioPlayers.Describe(row.Table, original ?? row.Clip, replacement, out var count, out var volume);
                infos.Add(new AudioRowInfo(
                    row.Descriptor,
                    source,
                    replacement != null ? SafeLength(replacement) : 0,
                    Book.VariantOf(row.Key),
                    RefusalOf(row.Key),
                    playing,
                    count,
                    volume));
            }

            return infos;
        }

        /// <summary>The pack music tracks with their state.</summary>
        public static IReadOnlyList<MusicTrackInfo> MusicTracks() => PackMusic.Infos();

        /// <summary>One line: what is installed and active.</summary>
        public static string Status()
        {
            var sb = new StringBuilder("audio: ");
            if (!Enabled)
            {
                return sb.Append("disabled (" + ConfigSection + "." + ConfigKeyEnabled + "=false)").ToString();
            }

            sb.Append(_active ? "on" : "OFF ('audio on' restores the replacements)");
            sb.Append("; ").Append(_plan.Assignments.Count).Append(" row(s) replaced, ").Append(_plan.RefusedCount).Append(" refused (paid)");
            sb.Append("; ").Append(PackClips.Count).Append(" pack clip(s) decoded");
            if (_pending > 0)
            {
                sb.Append(", ").Append(_pending).Append(" decoding");
            }

            var music = PackMusic.Infos();
            var listed = 0;
            foreach (var track in music)
            {
                if (track.Listed)
                {
                    listed++;
                }
            }

            sb.Append("; music ").Append(listed).Append('/').Append(music.Count).Append(" pack track(s) listed");
            sb.Append("; save guard ").Append(PackMusic.Patched ? "on (" + PackMusic.GuardRuns + " save(s), " + PackMusic.StrippedSaves + " stripped)" : "off");
            sb.Append("; A/B ").Append(AudioPlayers.Available ? "live" : "unavailable (" + AudioPlayers.Error + ")");
            if (!RowsKnown)
            {
                sb.Append("; the game's sound tables are not loaded yet");
            }

            if (_missing.Length > 0)
            {
                sb.Append("; missing seams: ").Append(_missing);
            }

            return sb.ToString();
        }

        /// <summary>Plays a one-shot row once through the game's own service (the row's current clip). Main thread.</summary>
        /// <param name="spec">The row.</param>
        /// <returns>What happened, for the console.</returns>
        public static string Play(string spec)
        {
            Apply("play");
            var resolved = AudioRowSpec.Resolve(spec, Rows(), replacedOnly: false);
            if (!resolved.Ok)
            {
                return resolved.Problem;
            }

            if (resolved.Rows.Count != 1)
            {
                return "'" + spec + "' names " + resolved.Rows.Count + " rows; name one";
            }

            var info = resolved.Rows[0];
            if (!_playSeams)
            {
                return "audio play is unavailable: a sound service seam is missing (" + _missing + ")";
            }

            try
            {
                switch (info.Table)
                {
                    case AudioTable.SystemSe:
                        if (!GameScopes.TryRoom<Game.SystemSeService>(out var system) || system is null)
                        {
                            return "the system sound service is not up yet (it lives in the room)";
                        }

                        system.Play(new Game.SystemSeParam { SeSound = Parse<Game.SystemSeType>(info.Name), IsAllowsDuplicate = true });
                        break;
                    case AudioTable.AmbientSe:
                        if (!GameScopes.TryRoom<Game.AmbientSeService>(out var ambientSe) || ambientSe is null)
                        {
                            return "the ambient sound service is not up yet (it lives in the room)";
                        }

                        ambientSe.Play(new Game.AmbientSeParam(Parse<Game.AmbientSeType>(info.Name), true));
                        break;
                    case AudioTable.TimerSe:
                        if (!GameScopes.TryRoom<Game.TimerSeService>(out var timer) || timer is null)
                        {
                            return "the timer sound service is not up yet (it lives in the room)";
                        }

                        timer.PlayPreview(Parse<Game.TimerSeType>(info.Name), 1f);
                        break;
                    default:
                        return info.Key + " is a loop: switch it on in the game (the sound panel), then use 'audio ab " + info.Name + "' to compare";
                }
            }
            catch (Exception ex)
            {
                return "could not play " + info.Key + ": " + ex.GetType().Name + ": " + ex.Message;
            }

            ConsoleEvents.Emit(
                TraceSys, "play", ConsoleTags.Audio, "play " + info.Key, "using",
                ("row", info.Key), ("using", info.HasReplacement ? (info.Variant == AudioVariant.Original ? "original" : "pack") : "original"));
            return string.Empty;
        }

        /// <summary>Adds a mod's replacement for a row (the facet's backend). Main thread.</summary>
        /// <param name="owner">Who.</param>
        /// <param name="spec">The row.</param>
        /// <param name="clip">The clip.</param>
        internal static AudioClaimResult Claim(string owner, string spec, AudioClip clip)
        {
            if (!Enabled)
            {
                return AudioClaimResult.Refused(spec, ConfigSection + "." + ConfigKeyEnabled + "=false");
            }

            if (!_active)
            {
                return AudioClaimResult.Refused(spec, "audio is off");
            }

            var key = ResolveKey(spec, out var problem);
            if (key is null)
            {
                return AudioClaimResult.Refused(spec, problem);
            }

            if (_paidKeys.Contains(key) || (key.StartsWith(AudioTables.Id(AudioTable.DirectionMusic) + "/", StringComparison.OrdinalIgnoreCase) && !RowsKnown))
            {
                return AudioClaimResult.Refused(key, RefusalOf(key).Length > 0 ? RefusalOf(key)
                    : "story music cannot be checked against the soundtrack before the game's data is loaded; try again after RoomReady");
            }

            var handle = Book.AddRuntime(key, owner, clip);
            Apply("claim:" + owner);
            return AudioClaimResult.Accepted(key, new ActionHandle(() =>
            {
                handle.Dispose();
                Apply("unclaim:" + owner);
            }));
        }

        /// <summary>Re-plans the pack music against the game's music list, when it is known.</summary>
        /// <param name="trigger">What asked.</param>
        internal static void RefreshMusicPlan(string trigger)
        {
            if (!PackMusicEnabled)
            {
                PackMusic.SetPlan(PackMusicPlan.Empty);
                return;
            }

            var loader = MasterDataFeature.ResolveLoader();
            var soundtrack = AudioRows.Soundtrack(loader);
            if (!soundtrack.IsKnown)
            {
                // Without the game's list a re-listed soundtrack track could not be recognised: list nothing.
                PackMusic.SetPlan(PackMusicPlan.Empty);
                if (_musicInputs.Count > 0)
                {
                    WarnOnce("music:nocatalog", "AUDIO pack music waits for the game's music list (" + trigger + "); nothing is listed before it can be checked for paid tracks");
                }

                return;
            }

            _soundtrack = soundtrack;
            var plan = PackMusicPlan.Build(_musicInputs, soundtrack);
            foreach (var skip in plan.Skipped)
            {
                if (skip.IsWarning)
                {
                    WarnOnce("music:" + skip, (skip.IsPaid ? "AUDIO ! music refused: " : "AUDIO music skipped: ") + skip);
                }
            }

            PackMusic.SetPlan(plan);
        }

        /// <summary>The pack clip of a pack file, or null while it is not decoded.</summary>
        /// <param name="source"><c>pack/path</c>.</param>
        internal static AudioClip? PackClipOf(string source) =>
            PackClips.TryGetValue(source, out var clip) && clip != null ? clip : null;

        /// <summary>The row binding for a key, or null.</summary>
        /// <param name="key">The row key.</param>
        internal static RowBinding? Find(string key)
        {
            foreach (var row in _rows)
            {
                if (string.Equals(row.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return row;
                }
            }

            return null;
        }

        /// <summary>The game clip of a row as the book remembers it, or null.</summary>
        /// <param name="key">The row key.</param>
        internal static AudioClip? OriginalOf(string key) => Book.OriginalOf(key);

        /// <summary>The replacement of a row, or null.</summary>
        /// <param name="key">The row key.</param>
        internal static AudioClip? ReplacementOf(string key) => _paidKeys.Contains(key) ? null : Book.ReplacementOf(key, out _);

        /// <summary>The paid-content refusal recorded for a row, or empty.</summary>
        /// <param name="key">The row key.</param>
        internal static string RefusalOf(string key)
        {
            foreach (var skip in _plan.Skipped)
            {
                if (skip.Kind == AudioSkipKind.Paid && skip.Row != null && string.Equals(skip.Row.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return skip.Reason;
                }
            }

            if (_paidKeys.Contains(key) && Book.ReplacementOf(key, out _) != null)
            {
                var row = Find(key);
                return row != null ? AudioPaidContent.CheckRowReplacement(row.Descriptor, _soundtrack) ?? string.Empty : string.Empty;
            }

            return string.Empty;
        }

        /// <summary>True when a row is paid content (never replaced).</summary>
        /// <param name="key">The row key.</param>
        internal static bool IsPaid(string key) => _paidKeys.Contains(key);

        private static AudioClip? ObserveAndDescribe(string key, AudioClip? current)
        {
            Book.Observe(key, current);
            return Book.OriginalOf(key) ?? current;
        }

        private static AudioSwitchMode Swap(RowBinding row, string trigger, bool logLine)
        {
            var current = row.Clip;
            var desired = _paidKeys.Contains(row.Key)
                ? (Book.IsOurs(current) ? Book.OriginalOf(row.Key) : null)
                : Book.Desired(row.Key, current, _active);

            if (desired == null || ReferenceEquals(desired, current))
            {
                return AudioSwitchMode.Unchanged;
            }

            var toOurs = Book.IsOurs(desired);
            if (toOurs && AudioTables.IsLoop(row.Table) && !AudioPlayers.Available)
            {
                WarnOnce("loop:" + row.Key, "AUDIO " + row.Key + " is not replaced: the audio players are not available (" + AudioPlayers.Error + "), and a loop swapped under a playing player could not be stopped");
                return AudioSwitchMode.Unchanged;
            }

            var original = Book.OriginalOf(row.Key);
            if (toOurs && original != null)
            {
                CopyName(desired, original);
            }

            row.SetClip(desired);
            var moved = current != null ? AudioPlayers.Move(row.Table, current, desired) : new AudioPlayers.MoveResult();
            var mode = moved.Moved > 0 ? AudioSwitchMode.Moved : moved.Restarted > 0 ? AudioSwitchMode.Restarted : AudioSwitchMode.RowOnly;

            if (logLine)
            {
                Book.ReplacementOf(row.Key, out var source);
                ConsoleEvents.Emit(
                    TraceSys,
                    toOurs ? "applied" : "restore",
                    ConsoleTags.Audio,
                    (toOurs ? "~ " : "- ") + row.Key + (toOurs ? " <- " + source : " (game clip)"),
                    "len,orig,players,via",
                    ("row", row.Key),
                    ("source", toOurs ? source : "game"),
                    ("len", AudioListFormatter.Seconds(SafeLength(desired))),
                    ("orig", AudioListFormatter.Seconds(row.Descriptor.OriginalSeconds)),
                    ("players", moved.Total),
                    ("via", trigger));
            }

            return mode;
        }

        private static AudioSwitchOutcome SwitchRow(RowBinding row, AudioVariant target, AudioVariant before)
        {
            var current = row.Clip;
            var desired = Book.Desired(row.Key, current, _active);
            if (desired == null || ReferenceEquals(desired, current))
            {
                EmitAb(row.Key, target, AudioSwitchMode.Unchanged, 0, -1, before == target ? "already" : "row already holds it");
                return new AudioSwitchOutcome(row.Key, target, AudioSwitchMode.Unchanged, 0, -1, "already " + Word(target));
            }

            if (AudioTables.IsLoop(row.Table) && !AudioPlayers.Available)
            {
                Book.Select(row.Key, before);
                var why = "the audio players are not available (" + AudioPlayers.Error + "); loops are not switched";
                EmitAb(row.Key, before, AudioSwitchMode.Failed, 0, -1, why);
                return new AudioSwitchOutcome(row.Key, before, AudioSwitchMode.Failed, 0, -1, why);
            }

            var original = Book.OriginalOf(row.Key);
            if (Book.IsOurs(desired) && original != null)
            {
                CopyName(desired, original);
            }

            row.SetClip(desired);
            var moved = current != null ? AudioPlayers.Move(row.Table, current, desired) : new AudioPlayers.MoveResult();
            var mode = moved.Moved > 0 ? AudioSwitchMode.Moved : moved.Restarted > 0 ? AudioSwitchMode.Restarted : AudioSwitchMode.RowOnly;
            var detail = mode == AudioSwitchMode.RowOnly
                ? (AudioTables.IsLoop(row.Table) ? "not playing; switch it on in the game to hear it" : "plays the next time the game plays it ('audio play " + row.Descriptor.Name + "')")
                : mode == AudioSwitchMode.Moved ? "same position" : "restarted";
            EmitAb(row.Key, target, mode, moved.Total, moved.Position, detail);
            return new AudioSwitchOutcome(row.Key, target, mode, moved.Total, moved.Position, detail);
        }

        private static void EmitAb(string key, AudioVariant variant, AudioSwitchMode mode, int players, double position, string detail)
        {
            ConsoleEvents.Emit(
                TraceSys,
                "ab",
                ConsoleTags.Audio,
                "ab " + key + " -> " + Word(variant),
                "mode,players,pos,note",
                ("row", key),
                ("variant", Word(variant)),
                ("mode", mode.ToString().ToLowerInvariant()),
                ("players", players),
                ("pos", position >= 0 ? position.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s" : null),
                ("note", detail));
        }

        private static string Word(AudioVariant variant) => variant == AudioVariant.Original ? "original" : "pack";

        private static AudioVariant ToggleTarget(IReadOnlyList<AudioRowInfo> rows)
        {
            // One direction for the whole set, so 'audio ab all' flips everything together.
            foreach (var row in rows)
            {
                if (row.Variant == AudioVariant.Pack)
                {
                    return AudioVariant.Original;
                }
            }

            return AudioVariant.Pack;
        }

        private static void CopyName(AudioClip clip, AudioClip original)
        {
            try
            {
                var name = original.name;
                if (!string.Equals(clip.name, name, StringComparison.Ordinal))
                {
                    clip.name = name;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO could not give a replacement the game clip's name (the game may not find its player by name): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static HashSet<string> PaidKeys(IReadOnlyList<AudioRowDescriptor> rows)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (AudioPaidContent.CheckRowReplacement(row, _soundtrack) != null)
                {
                    keys.Add(row.Key);
                }
            }

            return keys;
        }

        private static List<KeyValuePair<string, AudioPackClaim<AudioClip>>> PackClaims()
        {
            var claims = new List<KeyValuePair<string, AudioPackClaim<AudioClip>>>();
            foreach (var assignment in _plan.Assignments)
            {
                var clip = PackClipOf(assignment.Candidate.Source);
                if (clip != null)
                {
                    claims.Add(new KeyValuePair<string, AudioPackClaim<AudioClip>>(
                        assignment.Row.Key, new AudioPackClaim<AudioClip>(assignment.Candidate.Source, clip)));
                }
            }

            return claims;
        }

        private static void ReportSkips()
        {
            foreach (var skip in _plan.Skipped)
            {
                switch (skip.Kind)
                {
                    case AudioSkipKind.Paid:
                        if (WarnedKeys.Add("paid:" + skip.Candidate.Source))
                        {
                            ConsoleEvents.EmitAt(
                                TraceSys, "refused", ConsoleTags.Audio, ConsoleLevel.Warn,
                                "! " + (skip.Row?.Key ?? skip.Candidate.Stem) + " refused (paid content)",
                                "pack,reason",
                                ("row", skip.Row?.Key), ("pack", skip.Candidate.Source), ("reason", skip.Reason));
                        }

                        break;
                    case AudioSkipKind.UnknownRow:
                        WarnOnce("unknown:" + skip.Candidate.Source, "AUDIO " + skip);
                        break;
                    default:
                        CwyLsModLoader.Log.LogDebug("AUDIO " + skip);
                        break;
                }
            }
        }

        private static string? ResolveKey(string spec, out string problem)
        {
            problem = string.Empty;
            var text = (spec ?? string.Empty).Trim();
            var slash = text.IndexOf('/');
            if (slash > 0)
            {
                if (!AudioTables.TryParse(text.Substring(0, slash), out var table) || slash == text.Length - 1)
                {
                    problem = "'" + text + "' is not a row key (table/name, e.g. ambient/LightRain)";
                    return null;
                }

                var key = AudioRowDescriptor.KeyOf(table, text.Substring(slash + 1));
                if (RowsKnown && Find(key) is null)
                {
                    problem = "no row " + key + "; try 'audio list'";
                    return null;
                }

                return Find(key)?.Key ?? key;
            }

            if (!RowsKnown)
            {
                problem = "before the game's data is loaded a row must be named as table/name (e.g. ambient/LightRain)";
                return null;
            }

            var resolved = AudioRowSpec.Resolve(text, Rows(), replacedOnly: false);
            if (!resolved.Ok)
            {
                problem = resolved.Problem;
                return null;
            }

            if (resolved.Rows.Count != 1)
            {
                problem = "'" + text + "' names " + resolved.Rows.Count + " rows; name one";
                return null;
            }

            return resolved.Rows[0].Key;
        }

        private static void ReadSeams(PatchTargetReport report)
        {
            var missing = new List<string>();
            _rowSeams = true;
            _musicSeams = true;
            _soundtrackSeam = true;
            _playSeams = true;
            foreach (var result in report.MissingResults)
            {
                var id = result.Target.Id;
                missing.Add(id);
                if (id.StartsWith("audio.masterdata.", StringComparison.Ordinal) && id != "audio.masterdata.music"
                    || id.StartsWith("audio.row.", StringComparison.Ordinal))
                {
                    _rowSeams = false;
                }
                else if (id == "audio.masterdata.music")
                {
                    _soundtrackSeam = false;
                }
                else if (id.StartsWith("audio.music.", StringComparison.Ordinal) || id.StartsWith("audio.save.", StringComparison.Ordinal)
                    || id == "audio.gameaudioinfo.createnormal")
                {
                    _musicSeams = false;
                }
                else if (id.StartsWith("audio.service.", StringComparison.Ordinal))
                {
                    _playSeams = false;
                }
            }

            // Without the game's music list nothing can be checked for the soundtrack: no pack music,
            // and story music is refused anyway (AudioPaidContent).
            if (!_soundtrackSeam)
            {
                _musicSeams = false;
            }

            _missing = string.Join(",", missing.ToArray());
            if (!_rowSeams)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO row replacement is off: a master-data seam is missing (" + _missing + ")");
            }

            if (!_musicSeams)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO pack music is off: a music or save seam is missing (" + _missing + ")");
            }
        }

        private static void Preload(string trigger, bool force)
        {
            if (!Enabled)
            {
                return;
            }

            var packs = new List<LoadedPack>(PackManager.Loaded);
            var fingerprint = Fingerprint(packs);
            if (!force && string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
            {
                CwyLsModLoader.Log.LogDebug("AUDIO packs unchanged (" + trigger + "); clips kept");
                return;
            }

            _fingerprint = fingerprint;
            var generation = ++_loadGeneration;

            var indexes = new List<KeyValuePair<string, PackContentIndex>>(packs.Count);
            var musicInputs = new List<PackMusicInput>();
            foreach (var pack in packs)
            {
                indexes.Add(new KeyValuePair<string, PackContentIndex>(pack.Id, pack.Index));
                string? json = null;
                string? readError = null;
                var list = pack.Index.MusicTracks;
                if (list != null)
                {
                    try
                    {
                        json = File.ReadAllText(pack.FullPath(list));
                    }
                    catch (Exception ex)
                    {
                        readError = ex.GetType().Name + ": " + ex.Message;
                    }
                }

                musicInputs.Add(new PackMusicInput(pack.Id, pack.Index, json, readError));
            }

            _candidates = (_replaceClips?.Value ?? true) ? AudioCandidate.FromPacks(indexes) : Array.Empty<AudioCandidate>();
            _musicInputs = (_packMusic?.Value ?? true) && _musicSeams ? musicInputs : new List<PackMusicInput>();

            // Every file that could be needed; the plans decide later which are used.
            var loads = new List<KeyValuePair<string, string>>();
            foreach (var candidate in _candidates)
            {
                var path = PathOf(packs, candidate.PackId, candidate.RelativePath);
                if (path != null)
                {
                    loads.Add(new KeyValuePair<string, string>(candidate.Source, path));
                }
            }

            var musicPlan = PackMusicPlan.Build(_musicInputs, null);
            foreach (var track in musicPlan.Tracks)
            {
                var path = PathOf(packs, track.PackId, track.RelativePath);
                if (path != null)
                {
                    loads.Add(new KeyValuePair<string, string>("music:" + track.Uuid, path));
                }
            }

            // Decoded into a fresh table and swapped in when the run finishes, so a reload never
            // leaves the rows without their clips (an audible flip to the game's sound) meanwhile.
            var incoming = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            _pending = loads.Count;
            ConsoleEvents.File(TraceSys, "preload", ("trigger", trigger), ("rowFiles", _candidates.Count), ("musicFiles", musicPlan.Tracks.Count));
            if (loads.Count == 0)
            {
                Finish(trigger, generation, 0, incoming);
                return;
            }

            var report = new LoadReport();
            var failed = 0;
            foreach (var load in loads)
            {
                var key = load.Key;
                AudioLoader.LoadAsync(
                    load.Value,
                    clip =>
                    {
                        if (generation != _loadGeneration)
                        {
                            return;
                        }

                        if (key.StartsWith("music:", StringComparison.Ordinal))
                        {
                            PackMusic.OnClip(key.Substring(6), clip, "the clip did not decode; see the loader warning");
                        }
                        else if (clip != null)
                        {
                            incoming[key] = clip;
                        }

                        if (clip == null)
                        {
                            failed++;
                            CwyLsModLoader.Log.LogWarning("AUDIO " + (key.StartsWith("music:", StringComparison.Ordinal) ? load.Value : key) + " did not decode; " + report.Summarize());
                        }

                        if (--_pending <= 0)
                        {
                            _pending = 0;
                            Finish(trigger, generation, failed, incoming);
                        }
                    },
                    report);
            }
        }

        private static void Finish(string trigger, int generation, int failed, Dictionary<string, AudioClip> incoming)
        {
            if (generation != _loadGeneration)
            {
                return;
            }

            PackClips.Clear();
            foreach (var pair in incoming)
            {
                PackClips[pair.Key] = pair.Value;
            }

            var musicFiles = PackMusicPlan.Build(_musicInputs, null).Tracks.Count;
            LastSummary = "AUDIO: " + PackClips.Count + " row clip(s) and " + musicFiles + " music file(s) from packs; " + failed + " failed";
            ConsoleEvents.Emit(
                TraceSys, "preload", ConsoleTags.Setup, LastSummary, "src,trigger,rows,music,failed",
                ("src", "audio"), ("trigger", trigger), ("rows", PackClips.Count), ("music", musicFiles), ("failed", failed));

            Apply("decoded:" + trigger);
        }

        private static string Fingerprint(IReadOnlyList<LoadedPack> packs)
        {
            var sb = new StringBuilder();
            foreach (var pack in packs)
            {
                sb.Append(pack.Id).Append('|');
                foreach (var file in pack.Index.Files)
                {
                    if (!file.Path.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || file.Kind == PackContentKind.Voice)
                    {
                        continue;
                    }

                    sb.Append(file.Path);
                    try
                    {
                        var info = new FileInfo(pack.FullPath(file));
                        if (info.Exists)
                        {
                            sb.Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                        }
                    }
                    catch (Exception ex)
                    {
                        sb.Append(":?");
                        CwyLsModLoader.Log.LogDebug("AUDIO could not stat " + file.Path + ": " + ex.GetType().Name + ": " + ex.Message);
                    }

                    sb.Append(';');
                }
            }

            return sb.ToString();
        }

        private static string? PathOf(IReadOnlyList<LoadedPack> packs, string packId, string relativePath)
        {
            foreach (var pack in packs)
            {
                if (string.Equals(pack.Id, packId, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        return pack.FullPath(relativePath);
                    }
                    catch (ArgumentException ex)
                    {
                        CwyLsModLoader.Log.LogError("AUDIO " + packId + "/" + relativePath + " is not a path inside the pack: " + ex.Message);
                        return null;
                    }
                }
            }

            return null;
        }

        private static void OnInjected(string trigger) => Apply("injected:" + trigger);

        private static void OnPacksResolved(PackResolution resolution) => Preload("packsResolved", force: false);

        private static void OnQuitting()
        {
            // Rows only: the players are being torn down with the scene. The master data lives in
            // memory, so this is hygiene - nothing of ours is ever written to the game's files.
            var restored = 0;
            try
            {
                foreach (var row in _rows)
                {
                    var current = row.Clip;
                    var original = Book.OriginalOf(row.Key);
                    if (current != null && original != null && Book.IsOurs(current))
                    {
                        row.SetClip(original);
                        restored++;
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO restore on quit failed: " + ex.GetType().Name + ": " + ex.Message);
            }

            _quitting = true;
            ConsoleEvents.File(TraceSys, "restore", ("reason", "quit"), ("rows", restored));
        }

        private static T Parse<T>(string name)
            where T : struct =>
            (T)Enum.Parse(typeof(T), name);

        private static double SafeLength(AudioClip clip)
        {
            try
            {
                return clip.length;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read a clip length: " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (WarnedKeys.Add(key))
            {
                CwyLsModLoader.Log.LogWarning(message);
            }
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();
            _enabled = file.Bind(ConfigSection, ConfigKeyEnabled, true,
                "Replace game sounds and add music from content packs and mods. Off leaves every game sound untouched.");
            _replaceClips = file.Bind(ConfigSection, ConfigKeyReplaceClips, true,
                "Replace ambient loops, ambient one-shots, system and timer sounds and story music from the packs' audio/ambient, "
                + "audio/ambient_se, audio/se/system, audio/se/timer and audio/direction_music folders.");
            _packMusic = file.Bind(ConfigSection, ConfigKeyPackMusic, true,
                "Add the packs' audio/music tracks to the game's music list. Their ids never reach the game's save (Steam Cloud stays vanilla).");
            _logEachClip = file.Bind(ConfigSection, ConfigKeyLogEachClip, true,
                "Write one AUDIO line per replaced row. Off keeps the summary lines and the A/B lines.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".audio.cfg"), saveOnInit: true);

        /// <summary>A disposable that runs an action once.</summary>
        private sealed class ActionHandle : IDisposable
        {
            private Action? _action;

            internal ActionHandle(Action action) => _action = action;

            public void Dispose()
            {
                var action = System.Threading.Interlocked.Exchange(ref _action, null);
                action?.Invoke();
            }
        }
    }
}
