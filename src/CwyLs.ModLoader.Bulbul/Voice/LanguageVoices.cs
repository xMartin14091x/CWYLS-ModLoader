using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Core.Voice;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace CwyLs.ModLoader.Bulbul
{
    /// <summary>
    /// Language voice sets. A pack folder <c>audio/voice/&lt;lang&gt;/</c> replaces the
    /// game's voice clips <em>only while that language is selected</em>; every other language keeps
    /// the game's own clips (and any global <c>audio/voice/*.ogg</c> override).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Selection.</b> The clip lookup prefix (<see cref="VoicePatches"/>) asks
    /// <see cref="ResolveForPlay"/> on every voice line; precedence is runtime registration, then the
    /// set of the language in force, then a global pack clip, then the game
    /// (<see cref="LanguageVoiceRules"/>). The language is read from the game's own setting at each
    /// lookup, so a switch in the language selector takes effect on the next line with no restart,
    /// and a line already playing is never interrupted (nothing is stopped or destroyed).
    /// </para>
    /// <para>
    /// <b>Loading.</b> Clips stay Vorbis in memory (<see cref="LanguageVoiceLoader"/>), never decoded
    /// to PCM at startup. The set of the language in force is preloaded in the background once the
    /// room is ready (or when the language is switched to), a few files per frame on the shared
    /// main-thread dispatcher; the lines of a scenario that is about to play are loaded first
    /// (<see cref="Prefetch"/>, from a <c>ScenarioReader.StartReady</c> postfix). A line whose clip is
    /// not loaded yet plays the game's clip that one time - never silence - and its clip jumps to the
    /// front of the queue.
    /// </para>
    /// <para>
    /// <b>Timing.</b> The game waits for the clip that plays (story text rows wait for text and voice;
    /// the mouth runs while the clip plays), so a translated take drives the advance and the mouth by
    /// itself. The subtitle hold also keeps the text up for the playing clip plus 0.3 s
    /// (<see cref="VoicePlayback"/>, <c>ReadingTime.RemainingFor</c>).
    /// </para>
    /// <para>Main thread only, static, initialised from <see cref="VoiceFeature.Initialize"/>.</para>
    /// </remarks>
    public static class LanguageVoices
    {
        /// <summary>Config key of the switch, in the <c>[Voice]</c> section.</summary>
        public const string ConfigKey = "LanguageVoices";

        /// <summary>The <c>src</c> of the startup line.</summary>
        public const string SetupFeature = "voice.lang";

        /// <summary>Files started per frame, at most.</summary>
        internal const int MaxStartsPerFrame = 3;

        /// <summary>Requests in flight, at most.</summary>
        internal const int MaxInFlight = 6;

        /// <summary>Main-thread milliseconds per frame spent finishing loads, after the first one.</summary>
        internal const double CompletionBudgetMs = 2d;

        /// <summary>A request that has not finished after this long is dropped as failed.</summary>
        internal const double RequestTimeoutSeconds = 20d;

        /// <summary>Load failures logged as WARN lines before the rest are only counted.</summary>
        internal const int MaxLoggedFailures = 5;

        private const string TickerOwner = "voice";
        private const string TickerLabel = "voice.lang.preload";
        private const double BytesPerMB = 1024d * 1024d;

        private static readonly List<InFlight> InFlightLoads = new List<InFlight>();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        // One table and one switch for the session: a rescan rebuilds the table in place, so the
        // switch keeps the set in force and reports a change only when there really is one.
        private static readonly LanguageVoiceTable Table = new LanguageVoiceTable(LanguageVoicePlan.Empty);
        private static readonly LanguageVoiceSwitch Switch = new LanguageVoiceSwitch(code => Table.Has(code));

        private static Dictionary<string, LoadedPack> _packs = new Dictionary<string, LoadedPack>(StringComparer.OrdinalIgnoreCase);
        private static IDisposable? _ticker;
        private static PreloadRun? _run;
        private static string? _probeCode;
        private static bool _initialized;
        private static bool _roomReady;
        private static bool _codesChecked;
        private static int _loggedFailures;
        private static int _silentLines;
        private static string _exclusiveDoneFor = string.Empty;

        /// <summary>True when language voice sets are switched on and their loader is available.</summary>
        public static bool Enabled { get; private set; }

        /// <summary>Why language voice sets are off, or empty while they are on.</summary>
        public static string DisabledReason { get; private set; } = "not initialised";

        /// <summary>The language codes a pack ships a set for, ordinally sorted.</summary>
        public static IReadOnlyList<string> Codes => Table.Codes;

        /// <summary>The code of the set in force (the game's language has a set), or empty.</summary>
        public static string ActiveCode => Switch.Active;

        /// <summary>The plan's one-line summary, e.g. <c>th: 12 clip(s) from my.lang; 0 skipped</c>.</summary>
        public static string PlanSummary => Table.Plan.Summarize();

        /// <summary>The last finished preload's summary line, or empty.</summary>
        public static string LastSummary { get; private set; } = string.Empty;

        /// <summary>Lines answered by a language set this session.</summary>
        public static long Hits { get; private set; }

        /// <summary>Lines whose set clip was not ready (still loading, or failed) and played another clip.</summary>
        public static long Fallbacks { get; private set; }

        /// <summary>Lines of a language with a set that the set does not cover.</summary>
        public static long Missing { get; private set; }

        /// <summary>The last fallback, for the smoke report, e.g. <c>th/Voice_X: NotLoaded</c>; empty before one.</summary>
        public static string LastFallback { get; private set; } = string.Empty;

        /// <summary>How many clips of one language are in each state.</summary>
        /// <param name="code">A language code.</param>
        public static LanguageVoiceCounts CountsFor(string? code) => Table.CountsFor(code);

        /// <summary>True when a pack ships a set for this language code (any accepted spelling).</summary>
        /// <param name="code">A language code.</param>
        public static bool HasSet(string? code) => Table.Has(code);

        /// <summary>Up to <paramref name="max"/> loaded clips of one language, in plan order, for the smoke checks.</summary>
        /// <param name="code">A language code.</param>
        /// <param name="max">The most to return.</param>
        internal static IReadOnlyList<KeyValuePair<string, object>> LoadedSample(string code, int max) =>
            Table.LoadedClips(code, max);

        /// <summary>
        /// The clip a language set would play for a name right now, without side effects (no load is
        /// started, nothing is traced). Null when the set of the language in force does not have a
        /// loaded clip for it.
        /// </summary>
        /// <param name="name">The voice name.</param>
        /// <param name="clip">The loaded clip, or null.</param>
        /// <returns>True when a loaded clip would answer.</returns>
        public static bool TryPeek(string? name, out AudioClip? clip)
        {
            clip = null;
            if (!Enabled || string.IsNullOrEmpty(name))
            {
                return false;
            }

            var lookup = Table.Lookup(CurrentLanguageCode(), name);
            var found = lookup.HasClip ? lookup.Clip as AudioClip : null;
            if (!IsPlayable(found))
            {
                return false;
            }

            clip = found;
            return true;
        }

        /// <summary>
        /// True when a voice name would play audio right now: a mod's or pack's registered clip, the
        /// loaded clip of the language set in force, or the game's own clip. False means the line
        /// would be silent this time - a line that only exists in a language set (no game clip) whose
        /// clip has not loaded yet. A selection gate for pack-exclusive lines can ask this before
        /// choosing such a line. Free of side effects.
        /// </summary>
        /// <param name="name">The voice name; case-sensitive.</param>
        public static bool CanPlay(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (VoiceFeature.TryGetEffective(name, out _))
            {
                return true;
            }

            var instance = VoiceSeam.TryGetInstance(out _);
            return VoiceSeam.TryGetGameClip(instance, name) is AudioClip game && game != null;
        }

        /// <summary>One line describing the sets and the language in force, for a console command.</summary>
        public static string Describe()
        {
            if (!Enabled)
            {
                return "language voices off: " + DisabledReason;
            }

            var parts = new List<string>();
            foreach (var code in Table.Codes)
            {
                parts.Add(Table.CountsFor(code).ToString());
            }

            return (parts.Count == 0 ? LanguageVoicePlan.NothingSummary : string.Join("; ", parts))
                + "; in force: " + (Switch.Active.Length == 0 ? "none" : Switch.Active)
                + string.Format(CultureInfo.InvariantCulture, "; hits {0}, fallbacks {1}, missing {2}", Hits, Fallbacks, Missing);
        }

        /// <summary>
        /// Builds the sets from the scanned packs, applies the prefetch patch and subscribes to the
        /// room and clock events. Called once from <see cref="VoiceFeature.Initialize"/>, after the
        /// lookup prefix is in place; a second call is ignored.
        /// </summary>
        /// <param name="harmony">The voice feature's Harmony instance.</param>
        /// <param name="enabled">The <c>[Voice] LanguageVoices</c> setting.</param>
        internal static void Initialize(Harmony harmony, bool enabled)
        {
            if (_initialized)
            {
                CwyLsModLoader.Log.LogDebug("VOICE language voices already initialised; ignoring the second call");
                return;
            }

            _initialized = true;

            if (!enabled)
            {
                DisabledReason = VoiceFeature.ConfigSection + "." + ConfigKey + "=false";
                ConsoleEvents.Setup(
                    SetupFeature,
                    "disabled (" + DisabledReason + "); every language plays the game's clips");
                return;
            }

            if (!LanguageVoiceLoader.TryResolve(out var loaderError))
            {
                DisabledReason = "loader unavailable: " + loaderError;
                CwyLsModLoader.Log.LogError("VOICE language voices are off: " + loaderError);
                ConsoleEvents.Setup(SetupFeature, "unavailable; every language plays the game's clips", ("reason", loaderError));
                return;
            }

            Build(PackManager.LastResolution);
            Enabled = true;
            DisabledReason = string.Empty;

            if (!LanguageVoicePatches.Apply(harmony))
            {
                // Not fatal: without the prefetch the first line of a scene may fall back once.
                CwyLsModLoader.Log.LogWarning("VOICE language prefetch is off: " + LanguageVoicePatches.Detail);
            }

            Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => OnRoomReady()));
            Subscriptions.Add(CWYEvents.Subscribe<GameClockTick>(_ => Observe("tick")));
            Subscriptions.Add(CWYEvents.Subscribe<SceneChanged>(OnSceneChanged));
            Application.quitting += Shutdown;

            var plan = Table.Plan;
            foreach (var skip in plan.Skipped)
            {
                CwyLsModLoader.Log.LogDebug("VOICE language set skipped " + skip);
            }

            ConsoleEvents.Setup(
                SetupFeature,
                plan.Sets.Count == 0 ? LanguageVoicePlan.NothingSummary : DescribeSets(plan),
                ("sets", plan.Sets.Count),
                ("clips", plan.Count),
                ("skipped", plan.Skipped.Count),
                ("load", "compressed, after room ready"),
                ("prefetch", LanguageVoicePatches.Applied));
        }

        /// <summary>
        /// Re-plans after a pack rescan: unchanged files keep their loaded clips, the rest load again.
        /// </summary>
        /// <param name="resolution">The new resolution.</param>
        internal static void OnPacksResolved(PackResolution resolution)
        {
            if (!Enabled)
            {
                return;
            }

            var rebuild = Build(resolution);
            ConsoleEvents.Emit(
                VoiceFeature.TraceSys,
                "langRebuilt",
                ConsoleTags.Voice,
                "language voice sets rebuilt: " + Table.Plan.Summarize(),
                "kept,added,dropped",
                ("kept", rebuild.Kept),
                ("added", rebuild.Added),
                ("dropped", rebuild.Dropped));

            _codesChecked = false;
            _exclusiveDoneFor = string.Empty;
            if (Switch.Refresh(out var previous))
            {
                ReportSwitch(previous, "rescan");
            }
            else
            {
                PrioritizeExclusive(Switch.Active);
            }

            Kick();
        }

        /// <summary>
        /// The language set's answer for a line the game is about to play. Called by the lookup prefix
        /// only when no runtime registration outranks the set. Side effects: records a language
        /// switch, traces a fallback, and moves a clip that is not loaded yet to the front of the queue.
        /// </summary>
        /// <param name="name">The voice name.</param>
        /// <param name="clip">The set's loaded clip, or null.</param>
        /// <returns>What the set says.</returns>
        internal static LanguageVoiceLookup ResolveForPlay(string name, out AudioClip? clip)
        {
            clip = null;
            if (!Enabled)
            {
                return new LanguageVoiceLookup(LanguageVoiceOutcome.NoSet, string.Empty, name, null, null, null);
            }

            var probing = _probeCode != null || VoicePlayback.IsSuppressed;
            if (!probing)
            {
                Observe("lookup");
            }

            var lookup = Table.Lookup(CurrentLanguageCode(), name);
            switch (lookup.Outcome)
            {
                case LanguageVoiceOutcome.Loaded:
                    var found = lookup.Clip as AudioClip;
                    if (IsPlayable(found))
                    {
                        clip = found;
                        if (!probing)
                        {
                            Hits++;
                        }

                        return lookup;
                    }

                    // Destroyed under us (Unity-null), its data failed, or it is still loading in the
                    // background: the game's player decides a clip has ended when its source is not
                    // playing a frame later (KanKikuchi's AudioPlayer), so handing it over could make a
                    // silent line. Play the next clip in line this time and get this one ready again.
                    var state = found == null ? "destroyed" : found!.loadState.ToString();
                    if (!probing)
                    {
                        if (found == null || found!.loadState == AudioDataLoadState.Failed)
                        {
                            Table.Reset(lookup.Code, name);
                            Table.Prioritize(lookup.Code, name);
                            Kick();
                        }

                        TraceFallback(lookup, "notReady:" + state);
                    }

                    return new LanguageVoiceLookup(LanguageVoiceOutcome.NotLoaded, lookup.Code, name, lookup.Step, null, state);

                case LanguageVoiceOutcome.NotLoaded:
                    if (!probing)
                    {
                        Table.Prioritize(lookup.Code, name);
                        Kick();
                        TraceFallback(lookup, "notLoaded:" + Table.StateOf(lookup.Code, name));
                        WarnIfNothingElse(lookup);
                    }

                    return lookup;

                case LanguageVoiceOutcome.Failed:
                    if (!probing)
                    {
                        TraceFallback(lookup, "failed");
                    }

                    return lookup;

                case LanguageVoiceOutcome.NotInSet:
                    if (!probing)
                    {
                        Missing++;
                        ConsoleEvents.File(
                            VoiceFeature.TraceSys,
                            "langMissing",
                            ("name", name),
                            ("lang", lookup.Code));
                    }

                    return lookup;

                default:
                    return lookup;
            }
        }

        /// <summary>
        /// Loads the given lines of the language in force first - the lines of a scenario that is
        /// about to play. Called by the <c>ScenarioReader.StartReady</c> postfix.
        /// </summary>
        /// <param name="names">The scenario's voice names, in reading order.</param>
        /// <param name="groupId">The scenario group, for the trace.</param>
        internal static void Prefetch(IReadOnlyList<string> names, string? groupId)
        {
            if (!Enabled || names.Count == 0)
            {
                return;
            }

            Observe("prefetch");
            var code = Switch.Active;
            if (code.Length == 0)
            {
                return;
            }

            var moved = Table.Prioritize(code, names);
            if (moved > 0)
            {
                ConsoleEvents.File(
                    VoiceFeature.TraceSys,
                    "langPrefetch",
                    ("group", groupId),
                    ("lang", code),
                    ("lines", names.Count),
                    ("queued", moved));
                Kick();
            }
        }

        /// <summary>
        /// Runs <paramref name="body"/> as if <paramref name="code"/> were the game's language - for the
        /// smoke checks, which prove both branches through the real lookup without switching the game's
        /// language. Nothing is traced, loaded or recorded meanwhile. Main thread only.
        /// </summary>
        /// <typeparam name="T">The body's result.</typeparam>
        /// <param name="code">The language to pretend; empty means "a language with no set".</param>
        /// <param name="body">The work.</param>
        internal static T Probe<T>(string code, Func<T> body)
        {
            var previous = _probeCode;
            _probeCode = code ?? string.Empty;
            using (VoicePlayback.Suppress())
            {
                try
                {
                    return body();
                }
                finally
                {
                    _probeCode = previous;
                }
            }
        }

        /// <summary>The language the lookup uses: the probe's, or the game's.</summary>
        internal static string CurrentLanguageCode() => _probeCode ?? LanguageFeature.CurrentCode;

        /// <summary>Records the game's language and reports a change of the set in force.</summary>
        /// <param name="via">What noticed it, for the log.</param>
        internal static void Observe(string via)
        {
            if (!Enabled || _probeCode != null)
            {
                return;
            }

            if (Switch.Observe(LanguageFeature.CurrentCode, out var previous))
            {
                ReportSwitch(previous, via);
                Kick();
            }
        }

        private static LanguageVoiceRebuild Build(PackResolution? resolution)
        {
            var packs = new Dictionary<string, LoadedPack>(StringComparer.OrdinalIgnoreCase);
            var indexes = new List<KeyValuePair<string, PackContentIndex>>();
            foreach (var pack in VoiceFeature.PacksOf(resolution))
            {
                packs[pack.Id] = pack;
                indexes.Add(new KeyValuePair<string, PackContentIndex>(pack.Id, pack.Index));
            }

            var plan = LanguageVoicePlan.Build(indexes);
            _packs = packs;
            return Table.Rebuild(plan);
        }

        private static void OnRoomReady()
        {
            _roomReady = true;
            CheckCodes();
            Observe("roomReady");
            PrioritizeExclusive(Switch.Active);
            StartRunIfNeeded("roomReady");
            Kick();
        }

        private static void OnSceneChanged(SceneChanged change)
        {
            // Leaving the room pauses the background preload until the next RoomReady; on-demand
            // loads still run. Nothing already loaded is dropped.
            if (!change.Loaded && string.Equals(change.Scene, EventHost.SceneName, StringComparison.Ordinal))
            {
                _roomReady = false;
            }
        }

        /// <summary>Warns once per set whose folder is not a language this session can display.</summary>
        private static void CheckCodes()
        {
            if (_codesChecked)
            {
                return;
            }

            _codesChecked = true;
            foreach (var code in Table.Codes)
            {
                if (!LanguageFeature.Registry.TryFromCode(code, out _))
                {
                    CwyLsModLoader.Log.LogWarning(
                        "VOICE audio/voice/" + code + "/ is not a language this session can select "
                        + "(no pack declares it and the game has no such language); its clips never play");
                }
            }
        }

        private static void ReportSwitch(string previous, string via)
        {
            var next = Switch.Active;

            // A preload of the set we just left stops being accounted: it is resumed (and re-timed)
            // if that language comes back.
            if (_run != null && !string.Equals(_run.Code, next, StringComparison.Ordinal))
            {
                _run = null;
            }

            if (next.Length == 0)
            {
                ConsoleEvents.Emit(
                    VoiceFeature.TraceSys,
                    "langSwitch",
                    ConsoleTags.Voice,
                    previous + " voices off; the game's clips play",
                    "lang:language,via",
                    ("language", string.IsNullOrEmpty(Switch.LastObserved) ? "unknown" : Switch.LastObserved),
                    ("via", via),
                    ("previous", previous),
                    ("kept", Table.CountsFor(previous).Loaded));
                return;
            }

            PrioritizeExclusive(next);
            var counts = Table.CountsFor(next);
            ConsoleEvents.Emit(
                VoiceFeature.TraceSys,
                "langSwitch",
                ConsoleTags.Voice,
                next + " voices on: " + counts.Total.ToString(CultureInfo.InvariantCulture) + " clip(s)",
                "loaded,via,previous",
                ("lang", next),
                ("clips", counts.Total),
                ("loaded", counts.Loaded),
                ("failed", counts.Failed),
                ("via", via),
                ("previous", previous.Length == 0 ? "none" : previous));

            StartRunIfNeeded(via);
        }

        /// <summary>Starts the accounting of a background preload of the set in force.</summary>
        private static void StartRunIfNeeded(string via)
        {
            var code = Switch.Active;
            if (!_roomReady || code.Length == 0 || (_run != null && string.Equals(_run.Code, code, StringComparison.Ordinal)))
            {
                return;
            }

            var counts = Table.CountsFor(code);
            if (counts.Settled)
            {
                return;
            }

            _run = new PreloadRun(code, via, Clock.Elapsed.TotalSeconds, AllocatedBytes());
        }

        private static void Kick()
        {
            if (_ticker != null)
            {
                return;
            }

            if (InFlightLoads.Count == 0 && !Table.HasWaiting(Switch.Active, _roomReady))
            {
                return;
            }

            try
            {
                _ticker = MainThread.AddTicker(TickerLabel, Pump, TickerOwner);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("VOICE language preload could not start: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>One frame of loading: finish what is done, start what fits, stop when idle.</summary>
        private static void Pump()
        {
            var watch = Stopwatch.StartNew();
            var now = Clock.Elapsed.TotalSeconds;
            var finished = 0;

            for (var i = 0; i < InFlightLoads.Count;)
            {
                var load = InFlightLoads[i];
                var timedOut = now - load.StartedAt > RequestTimeoutSeconds;
                if (!timedOut && !LanguageVoiceLoader.IsDone(load.Request))
                {
                    i++;
                    continue;
                }

                if (finished > 0 && watch.Elapsed.TotalMilliseconds > CompletionBudgetMs)
                {
                    break;
                }

                InFlightLoads.RemoveAt(i);
                finished++;
                Finish(load, timedOut);
            }

            var code = Switch.Active;
            if (code.Length > 0)
            {
                var room = Math.Min(MaxStartsPerFrame, MaxInFlight - InFlightLoads.Count);
                foreach (var step in Table.TakeNext(code, room, includeBackground: _roomReady))
                {
                    Start(code, step, now);
                }
            }

            var elapsedMs = watch.Elapsed.TotalMilliseconds;
            if (_run != null)
            {
                _run.Frames++;
                _run.MainThreadMs += elapsedMs;
                _run.MaxFrameMs = Math.Max(_run.MaxFrameMs, elapsedMs);
            }

            if (InFlightLoads.Count == 0 && !Table.HasWaiting(code, _roomReady))
            {
                FinishRunIfSettled();
                StopTicker();
            }
        }

        private static void Start(string code, VoiceLoadStep step, double now)
        {
            string? path = null;
            try
            {
                if (_packs.TryGetValue(step.PackId, out var pack))
                {
                    path = pack.FullPath(step.RelativePath);
                }
            }
            catch (ArgumentException ex)
            {
                Failed(code, step, "not a path inside the pack: " + ex.Message);
                return;
            }

            if (path is null)
            {
                Failed(code, step, "the pack is not loaded any more");
                return;
            }

            long bytes;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    Failed(code, step, "file not found");
                    return;
                }

                bytes = info.Length;
            }
            catch (Exception ex)
            {
                Failed(code, step, "unreadable: " + ex.GetType().Name + ": " + ex.Message);
                return;
            }

            var audioType = step.Loader == VoiceLoaderKind.WavSync ? AudioType.WAV : AudioType.OGGVORBIS;
            var request = LanguageVoiceLoader.Begin(path, audioType, out var error);
            if (request is null)
            {
                Failed(code, step, error);
                return;
            }

            InFlightLoads.Add(new InFlight(code, step, request, bytes, now));
        }

        private static void Finish(InFlight load, bool timedOut)
        {
            if (timedOut)
            {
                LanguageVoiceLoader.Abandon(load.Request);
                Failed(load.Code, load.Step, "no answer after " + RequestTimeoutSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
                return;
            }

            var clip = LanguageVoiceLoader.End(load.Request, out var error);
            if (clip == null)
            {
                Failed(load.Code, load.Step, error);
                return;
            }

            try
            {
                if (clip!.length <= 0f)
                {
                    UnityEngine.Object.Destroy(clip);
                    Failed(load.Code, load.Step, "the clip is empty");
                    return;
                }

                clip.name = load.Step.Name;
                TryLoadAudioData(clip);

                if (!Table.Complete(load.Code, load.Step, clip, out var reason))
                {
                    // Never handed to the game (the slot changed under a rescan), so it is safe to free.
                    UnityEngine.Object.Destroy(clip);
                    CwyLsModLoader.Log.LogDebug("VOICE " + load.Code + "/" + load.Step.Name + " was not kept: " + reason);
                    return;
                }

                var run = _run;
                if (run != null && string.Equals(run.Code, load.Code, StringComparison.Ordinal))
                {
                    run.Loaded++;
                    run.Bytes += load.Bytes;
                    run.Lengths.Add(load.Step.Name, clip.length, GameLengthOf(load.Step.Name));
                }
            }
            catch (Exception ex)
            {
                Failed(load.Code, load.Step, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Failed(string code, VoiceLoadStep step, string? reason)
        {
            Table.Fail(code, step, reason);

            var run = _run;
            if (run != null && string.Equals(run.Code, code, StringComparison.Ordinal))
            {
                run.Failed++;
            }

            var message = "VOICE " + code + "/" + step.Name + " (" + step.PackId + "/" + step.RelativePath + ") did not load: "
                + reason + "; the game's clip plays instead";
            if (_loggedFailures < MaxLoggedFailures)
            {
                _loggedFailures++;
                CwyLsModLoader.Log.LogWarning(
                    message + (_loggedFailures == MaxLoggedFailures ? " - further failures go to the debug log" : string.Empty));
            }
            else
            {
                CwyLsModLoader.Log.LogDebug(message);
            }
        }

        private static void FinishRunIfSettled()
        {
            var run = _run;
            if (run is null)
            {
                return;
            }

            var counts = Table.CountsFor(run.Code);
            if (!counts.Settled)
            {
                return;
            }

            _run = null;
            var seconds = Clock.Elapsed.TotalSeconds - run.StartedAt;
            var allocDeltaMB = (AllocatedBytes() - run.AllocatedAtStart) / BytesPerMB;
            var compressedMB = run.Bytes / BytesPerMB;

            LastSummary = string.Format(
                CultureInfo.InvariantCulture,
                "{0} voices preloaded: {1}/{2} loaded, {3} failed in {4:0.0} s; {5:0.0} MB compressed in memory",
                run.Code,
                counts.Loaded,
                counts.Total,
                counts.Failed,
                seconds,
                compressedMB);

            var overruns = new List<string>();
            foreach (var overrun in run.Lengths.TopOverruns)
            {
                overruns.Add(overrun.Key + " +" + overrun.Value.ToString("0.00", CultureInfo.InvariantCulture) + "s");
            }

            ConsoleEvents.Emit(
                VoiceFeature.TraceSys,
                "langPreloaded",
                ConsoleTags.Voice,
                LastSummary,
                "mainMs,maxFrameMs,longer,shorter,maxOver",
                ("lang", run.Code),
                ("loaded", counts.Loaded),
                ("failed", counts.Failed),
                ("seconds", Math.Round(seconds, 2)),
                ("compressedMB", Math.Round(compressedMB, 2)),
                ("allocDeltaMB", Math.Round(allocDeltaMB, 1)),
                ("mainMs", Math.Round(run.MainThreadMs, 1)),
                ("maxFrameMs", Math.Round(run.MaxFrameMs, 2)),
                ("frames", run.Frames),
                ("trigger", run.Trigger),
                ("longer", run.Lengths.Longer),
                ("shorter", run.Lengths.Shorter),
                ("maxOver", run.Lengths.MaxOverrunSeconds > 0d
                    ? "+" + run.Lengths.MaxOverrunSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s " + run.Lengths.MaxOverrunName
                    : "none"),
                ("lengths", run.Lengths.Summarize()),
                ("topOverruns", overruns.ToArray()));

            foreach (var failure in Table.Failures(run.Code, MaxLoggedFailures))
            {
                CwyLsModLoader.Log.LogDebug("VOICE " + run.Code + "/" + failure.Key + " failed: " + failure.Value);
            }
        }

        /// <summary>
        /// A set clip that is not loaded yet normally falls back to the game's clip. A line that only
        /// exists in the set has none, so it is silent this one time; say so (rate-limited) rather
        /// than let it pass as a normal fallback.
        /// </summary>
        private static void WarnIfNothingElse(LanguageVoiceLookup lookup)
        {
            if (VoiceFeature.TryGet(lookup.Name, out _))
            {
                return;
            }

            var instance = VoiceSeam.TryGetInstance(out _);
            if (VoiceSeam.TryGetGameClip(instance, lookup.Name) is AudioClip game && game != null)
            {
                return;
            }

            _silentLines++;
            var message = "VOICE " + lookup.Code + "/" + lookup.Name + " was asked for before its clip loaded and the game "
                + "has no clip of that name, so this line has no voice this time (" + _silentLines + ")";
            if (_silentLines <= MaxLoggedFailures)
            {
                CwyLsModLoader.Log.LogWarning(message);
            }
            else
            {
                CwyLsModLoader.Log.LogDebug(message);
            }
        }

        /// <summary>
        /// Moves the set's lines that have no game clip at all (pack-exclusive lines) to the front of
        /// the queue: for those, a line asked for before its clip loads has nothing to fall back to.
        /// </summary>
        /// <param name="code">The set in force.</param>
        private static void PrioritizeExclusive(string code)
        {
            if (code.Length == 0
                || string.Equals(_exclusiveDoneFor, code, StringComparison.Ordinal)
                || !Table.Plan.TryGet(code, out var set) || set is null)
            {
                return;
            }

            var cache = VoiceSeam.TryGetGameClipCache(VoiceSeam.TryGetInstance(out _));
            if (cache is null)
            {
                // The voice manager is not up yet; RoomReady tries again.
                return;
            }

            _exclusiveDoneFor = code;

            var exclusive = new List<string>();
            foreach (var step in set.Steps)
            {
                if (!cache.Contains(step.Name) && !VoiceFeature.TryGet(step.Name, out _))
                {
                    exclusive.Add(step.Name);
                }
            }

            var moved = Table.Prioritize(code, exclusive);
            if (moved > 0)
            {
                ConsoleEvents.File(
                    VoiceFeature.TraceSys,
                    "langExclusiveFirst",
                    ("lang", code),
                    ("exclusive", exclusive.Count),
                    ("queued", moved));
            }
        }

        private static void TraceFallback(LanguageVoiceLookup lookup, string reason)
        {
            Fallbacks++;
            LastFallback = lookup.Code + "/" + lookup.Name + ": " + reason;
            ConsoleEvents.File(
                VoiceFeature.TraceSys,
                "langFallback",
                ("name", lookup.Name),
                ("lang", lookup.Code),
                ("reason", reason),
                ("failure", lookup.Failure),
                ("plays", "the next clip in line (pack or game) for this one play"));
        }

        private static double GameLengthOf(string name)
        {
            try
            {
                var instance = VoiceSeam.TryGetInstance(out _);
                return VoiceSeam.TryGetGameClip(instance, name) is AudioClip game && game != null ? game.length : 0d;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE could not read the game's length of '" + name + "': " + ex.GetType().Name + ": " + ex.Message);
                return 0d;
            }
        }

        /// <summary>
        /// True when the game's player can start the clip this frame: loaded, or unloaded (its
        /// <c>Play</c> calls <c>LoadAudioData</c> first, synchronously, exactly as for the game's own
        /// clips, which ship with preloading off). A clip still loading in the background, or whose
        /// data failed, is not.
        /// </summary>
        /// <param name="clip">The clip; a destroyed one is not playable.</param>
        private static bool IsPlayable(AudioClip? clip)
        {
            if (clip == null)
            {
                return false;
            }

            var state = clip!.loadState;
            return state == AudioDataLoadState.Loaded || state == AudioDataLoadState.Unloaded;
        }

        /// <summary>Gets a fresh clip's data ready at load time rather than at its first line.</summary>
        /// <param name="clip">The clip.</param>
        private static void TryLoadAudioData(AudioClip clip)
        {
            try
            {
                if (clip.loadState == AudioDataLoadState.Unloaded)
                {
                    clip.LoadAudioData();
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE LoadAudioData failed for '" + clip.name + "': " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static long AllocatedBytes()
        {
            try
            {
                return Profiler.GetTotalAllocatedMemoryLong();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE allocated memory is not readable: " + ex.GetType().Name + ": " + ex.Message);
                return 0L;
            }
        }

        private static string DescribeSets(LanguageVoicePlan plan)
        {
            var parts = new List<string>(plan.Sets.Count);
            foreach (var set in plan.Sets)
            {
                parts.Add(set.Code + " " + set.Count.ToString(CultureInfo.InvariantCulture) + " clip(s) from " + string.Join("+", set.PackIds));
            }

            return string.Join(", ", parts);
        }

        private static void StopTicker()
        {
            var ticker = _ticker;
            _ticker = null;
            try
            {
                ticker?.Dispose();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("VOICE language preload ticker could not be removed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Shutdown()
        {
            StopTicker();
            foreach (var load in InFlightLoads)
            {
                LanguageVoiceLoader.Abandon(load.Request);
            }

            InFlightLoads.Clear();

            foreach (var subscription in Subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("VOICE a language voice subscription could not be disposed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Subscriptions.Clear();
        }

        private sealed class InFlight
        {
            internal InFlight(string code, VoiceLoadStep step, LanguageVoiceLoader.Request request, long bytes, double startedAt)
            {
                Code = code;
                Step = step;
                Request = request;
                Bytes = bytes;
                StartedAt = startedAt;
            }

            internal string Code { get; }

            internal VoiceLoadStep Step { get; }

            internal LanguageVoiceLoader.Request Request { get; }

            internal long Bytes { get; }

            internal double StartedAt { get; }
        }

        /// <summary>The accounting of one background preload of one set.</summary>
        private sealed class PreloadRun
        {
            internal PreloadRun(string code, string trigger, double startedAt, long allocatedAtStart)
            {
                Code = code;
                Trigger = trigger;
                StartedAt = startedAt;
                AllocatedAtStart = allocatedAtStart;
            }

            internal string Code { get; }

            internal string Trigger { get; }

            internal double StartedAt { get; }

            internal long AllocatedAtStart { get; }

            internal int Loaded { get; set; }

            internal int Failed { get; set; }

            internal long Bytes { get; set; }

            internal int Frames { get; set; }

            internal double MainThreadMs { get; set; }

            internal double MaxFrameMs { get; set; }

            internal LanguageVoiceLengths Lengths { get; } = new LanguageVoiceLengths();
        }
    }
}
