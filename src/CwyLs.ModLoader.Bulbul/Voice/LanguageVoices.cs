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
    /// <b>First line (DS-12).</b> The first voiced line of a scene speaks a fraction of a second after
    /// <c>StartReady</c>, too soon for the queue while the background preload is still running, so the
    /// prefetch readies that one clip at once (<see cref="LanguageVoiceFirstLine"/>): a WAV is decoded
    /// there and then on the main thread (a few milliseconds, traced as <c>first line ready now</c>);
    /// an Ogg, which only the engine decodes and only asynchronously, has its request started at once
    /// and finished before any other. The main thread never waits for a request.
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
        private static int _firstLineErrors;
        private static string _exclusiveDoneFor = string.Empty;

        /// <summary>The last scene's first line whose clip went to the engine, until its request ends (for the trace).</summary>
        private static FirstLine? _firstLine;

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

            // Pack Maker's live swaps and its Play pin answer first, exactly as in ResolveForPlay.
            if (_probeCode is null && TryAnswerLive(name!, countHit: false, out var live, out var liveClip))
            {
                clip = live.HasClip ? liveClip : null;
                return clip != null;
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
            ReconcileLive();
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

                // Pack Maker (DS-03): a dub saved this session, or the clip its Play button pinned,
                // answers before the table. Skipped while probing, so the smoke checks keep proving
                // the table itself.
                if (TryAnswerLive(name, countHit: true, out var live, out clip))
                {
                    return live;
                }
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
        /// about to play: the first voiced line's clip at once (<see cref="ReadyFirstLine"/>), the rest
        /// at the front of the queue. Called by the <c>ScenarioReader.StartReady</c> postfix.
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

            ReadyFirstLine(code, names, groupId);

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
            }

            Kick();
        }

        /// <summary>
        /// DS-12: gets the clip of a scene's first voiced line ready now, so that line does not lose the
        /// race to the queue and play the game's clip. A WAV is decoded here, on the main thread; an Ogg
        /// has its engine request started here and finished before any other (the engine has no
        /// synchronous path, and the main thread never waits for one). One clip at most per scene
        /// start. A failure leaves the line to today's fallback - the game's clip, that one time.
        /// </summary>
        /// <param name="code">The set in force.</param>
        /// <param name="names">The scenario's voice names, in reading order.</param>
        /// <param name="groupId">The scenario group, for the trace.</param>
        private static void ReadyFirstLine(string code, IReadOnlyList<string> names, string? groupId)
        {
            // The postfix runs on the main thread; anything else must not touch Unity objects.
            if (_probeCode != null || (MainThread.IsArmed && !MainThread.IsMainThread))
            {
                return;
            }

            var name = LanguageVoiceFirstLine.FirstVoiced(names);
            if (name is null)
            {
                return;
            }

            try
            {
                var lookup = Table.Lookup(code, name);
                if (lookup.Outcome != LanguageVoiceOutcome.NotLoaded || lookup.Step is null)
                {
                    // Loaded, failed, or not the set's line: nothing to hurry (the common case once preloaded).
                    return;
                }

                var step = lookup.Step;
                string? path = null;
                var bytes = -1L;
                if (step.Loader == VoiceLoaderKind.WavSync && TryLocate(step, out var located, out var size, out _))
                {
                    path = located;
                    bytes = size;
                }

                var registered = VoiceFeature.TryGet(name, out _) ? VoiceFeature.EntryOf(name) : null;
                var outranked = LanguageVoiceFirstLine.Outranked(registered, TryLive(code, name, out _) || IsPinned(name, out _));
                var state = Table.StateOf(code, name);

                switch (LanguageVoiceFirstLine.Decide(lookup, state, outranked, bytes))
                {
                    case LanguageVoiceFirstLineAction.LoadNow:
                        LoadFirstLineNow(code, step, path!, bytes, state == LanguageVoiceState.Loading, groupId);
                        break;

                    case LanguageVoiceFirstLineAction.StartNow:
                        var taken = Table.TakeNow(code, name);
                        if (taken != null)
                        {
                            StartFirstLine(code, taken, groupId, "engine");
                        }

                        break;

                    case LanguageVoiceFirstLineAction.FinishFirst:
                        FinishFirstLineFirst(code, step, groupId);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Never let this stop a scene: the line simply loads with the rest of the scene.
                _firstLineErrors++;
                var message = "VOICE the first line of '" + groupId + "' (" + code + "/" + name + ") could not be readied ("
                    + _firstLineErrors + "): " + ex.GetType().Name + ": " + ex.Message + "; it loads with the rest of the scene";
                if (_firstLineErrors <= MaxLoggedFailures)
                {
                    CwyLsModLoader.Log.LogWarning(message);
                }
                else
                {
                    CwyLsModLoader.Log.LogDebug(message);
                }
            }
        }

        /// <summary>Decodes a first line's WAV now and holds it; hands it to the engine if the managed decoder refuses it.</summary>
        /// <param name="code">The set in force.</param>
        /// <param name="step">The set's file for the line.</param>
        /// <param name="path">The file's absolute path.</param>
        /// <param name="bytes">The file's size.</param>
        /// <param name="inFlight">True when the background already requested it from the engine.</param>
        /// <param name="groupId">The scenario group, for the trace.</param>
        private static void LoadFirstLineNow(string code, VoiceLoadStep step, string path, long bytes, bool inFlight, string? groupId)
        {
            var watch = Stopwatch.StartNew();
            if (!inFlight && Table.TakeNow(code, step.Name) is null)
            {
                return;
            }

            var clip = LanguageVoiceLoader.LoadWavNow(path, step.Name, out var error);
            if (clip == null)
            {
                // The managed decoder reads 16-bit PCM only; the engine may read more. It decides, as before.
                if (inFlight)
                {
                    FinishFirstLineFirst(code, step, groupId);
                }
                else
                {
                    StartFirstLine(code, step, groupId, "wav refused: " + error);
                }

                return;
            }

            if (!Keep(code, step, clip!, bytes))
            {
                // Keep logged why; the line plays the game's clip this once, as it did before.
                return;
            }

            if (inFlight)
            {
                // The background's request for the same file is no longer needed.
                var index = IndexOfInFlight(code, step);
                if (index >= 0)
                {
                    LanguageVoiceLoader.Abandon(InFlightLoads[index].Request);
                    InFlightLoads.RemoveAt(index);
                }
            }

            var ms = watch.Elapsed.TotalMilliseconds;
            ConsoleEvents.Emit(
                VoiceFeature.TraceSys,
                "langFirstLine",
                ConsoleTags.Voice,
                "first line ready now: " + step.Name + " in " + ms.ToString("0.0", CultureInfo.InvariantCulture) + " ms",
                "lang,group,seconds",
                ("name", step.Name),
                ("lang", code),
                ("group", groupId),
                ("how", "now"),
                ("ms", Math.Round(ms, 2)),
                ("bytes", bytes),
                ("seconds", Math.Round(clip!.length, 2)),
                ("wasInFlight", inFlight),
                ("pack", step.PackId));
        }

        /// <summary>Starts the engine's request for a first line's clip (taken from the table) at the front of the in-flight list.</summary>
        /// <param name="code">The set in force.</param>
        /// <param name="step">The step, already marked loading.</param>
        /// <param name="groupId">The scenario group, for the trace.</param>
        /// <param name="why">Why the engine, for the trace.</param>
        private static void StartFirstLine(string code, VoiceLoadStep step, string? groupId, string why)
        {
            var before = InFlightLoads.Count;
            Start(code, step, Clock.Elapsed.TotalSeconds, urgent: true);

            // Tracked only while a request is in flight: a file that failed at once was logged by Failed().
            _firstLine = InFlightLoads.Count > before ? new FirstLine(code, step.Name, groupId, Clock.Elapsed.TotalSeconds) : null;
            ConsoleEvents.File(
                VoiceFeature.TraceSys,
                "langFirstLineStart",
                ("name", step.Name),
                ("lang", code),
                ("group", groupId),
                ("loader", step.Loader.ToString()),
                ("why", why),
                ("started", _firstLine != null));
        }

        /// <summary>
        /// Moves a first line's request that is already in flight to the front, so the next frame
        /// finishes it before any other; finishes it right here when the engine is already done.
        /// </summary>
        private static void FinishFirstLineFirst(string code, VoiceLoadStep step, string? groupId)
        {
            var index = IndexOfInFlight(code, step);
            if (index < 0)
            {
                return;
            }

            var load = InFlightLoads[index];
            InFlightLoads.RemoveAt(index);
            _firstLine = new FirstLine(code, step.Name, groupId, Clock.Elapsed.TotalSeconds);
            ConsoleEvents.File(
                VoiceFeature.TraceSys,
                "langFirstLineFirst",
                ("name", step.Name),
                ("lang", code),
                ("group", groupId),
                ("inFlightMs", Math.Round((Clock.Elapsed.TotalSeconds - load.StartedAt) * 1000d, 1)));

            if (LanguageVoiceLoader.IsDone(load.Request))
            {
                Finish(load, timedOut: false);
                return;
            }

            InFlightLoads.Insert(0, load);
        }

        /// <summary>The index of the in-flight request for a step of a language, or -1.</summary>
        private static int IndexOfInFlight(string code, VoiceLoadStep step)
        {
            for (var i = 0; i < InFlightLoads.Count; i++)
            {
                var load = InFlightLoads[i];
                if (string.Equals(load.Code, code, StringComparison.Ordinal) && SameLiveFile(load.Step, step)
                    && string.Equals(load.Step.Name, step.Name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Traces how a first line's engine request ended (the line may already have asked for it).</summary>
        private static void NoteFirstLineDone(string code, string name, bool kept)
        {
            var first = _firstLine;
            if (first is null || !first.Is(code, name))
            {
                return;
            }

            _firstLine = null;
            if (!kept)
            {
                // Failed or timed out: Failed() already logged why, and the line keeps the game's clip.
                return;
            }

            var ms = (Clock.Elapsed.TotalSeconds - first.ReadiedAt) * 1000d;
            ConsoleEvents.Emit(
                VoiceFeature.TraceSys,
                "langFirstLine",
                ConsoleTags.Voice,
                "first line ready: " + name + " in " + ms.ToString("0", CultureInfo.InvariantCulture) + " ms (engine)"
                    + (first.FellBack ? "; too late, the line had already played another clip" : string.Empty),
                "lang,group,late",
                ("name", name),
                ("lang", code),
                ("group", first.GroupId),
                ("how", "engine"),
                ("ms", Math.Round(ms, 1)),
                ("late", first.FellBack));
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

        /// <summary>Starts the engine's request for one file.</summary>
        /// <param name="code">The language the step belongs to.</param>
        /// <param name="step">A step taken from the table (state loading).</param>
        /// <param name="now">The clock, for the timeout.</param>
        /// <param name="urgent">
        /// True for a scene's first line (DS-12): the request goes to the front of the in-flight list, so
        /// it is polled and finished first. It is started even when the list is full - one extra at most.
        /// </param>
        private static void Start(string code, VoiceLoadStep step, double now, bool urgent = false)
        {
            if (TryHandOver(code, step))
            {
                return;
            }

            if (!TryLocate(step, out var path, out var bytes, out var reason))
            {
                Failed(code, step, reason);
                return;
            }

            var audioType = step.Loader == VoiceLoaderKind.WavSync ? AudioType.WAV : AudioType.OGGVORBIS;
            var request = LanguageVoiceLoader.Begin(path, audioType, out var error);
            if (request is null)
            {
                Failed(code, step, error);
                return;
            }

            var load = new InFlight(code, step, request, bytes, now);
            if (urgent)
            {
                InFlightLoads.Insert(0, load);
            }
            else
            {
                InFlightLoads.Add(load);
            }
        }

        /// <summary>The absolute path and size of a step's file.</summary>
        /// <param name="step">The step.</param>
        /// <param name="path">The file's absolute path.</param>
        /// <param name="bytes">Its size.</param>
        /// <param name="reason">Why it cannot be loaded, or empty.</param>
        /// <returns>True when the file exists inside a loaded pack.</returns>
        private static bool TryLocate(VoiceLoadStep step, out string path, out long bytes, out string reason)
        {
            path = string.Empty;
            bytes = 0L;

            string? full = null;
            try
            {
                if (_packs.TryGetValue(step.PackId, out var pack))
                {
                    full = pack.FullPath(step.RelativePath);
                }
            }
            catch (ArgumentException ex)
            {
                reason = "not a path inside the pack: " + ex.Message;
                return false;
            }

            if (full is null)
            {
                reason = "the pack is not loaded any more";
                return false;
            }

            try
            {
                var info = new FileInfo(full);
                if (!info.Exists)
                {
                    reason = "file not found";
                    return false;
                }

                path = full;
                bytes = info.Length;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "unreadable: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static void Finish(InFlight load, bool timedOut)
        {
            var kept = false;
            try
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

                kept = Keep(load.Code, load.Step, clip!, load.Bytes);
            }
            finally
            {
                NoteFirstLineDone(load.Code, load.Step.Name, kept);
            }
        }

        /// <summary>
        /// Holds a freshly loaded clip in the table and counts it to the preload in progress, or frees it
        /// (never handed to the game) and records why.
        /// </summary>
        /// <param name="code">The language the step belongs to.</param>
        /// <param name="step">The step that was loading.</param>
        /// <param name="clip">The new clip.</param>
        /// <param name="bytes">The file's size.</param>
        /// <returns>True when the table holds the clip.</returns>
        private static bool Keep(string code, VoiceLoadStep step, AudioClip clip, long bytes)
        {
            try
            {
                if (clip.length <= 0f)
                {
                    UnityEngine.Object.Destroy(clip);
                    Failed(code, step, "the clip is empty");
                    return false;
                }

                clip.name = step.Name;
                TryLoadAudioData(clip);

                if (!Table.Complete(code, step, clip, out var reason))
                {
                    // Never handed to the game (the slot changed under a rescan), so it is safe to free.
                    UnityEngine.Object.Destroy(clip);
                    CwyLsModLoader.Log.LogDebug("VOICE " + code + "/" + step.Name + " was not kept: " + reason);
                    return false;
                }

                var run = _run;
                if (run != null && string.Equals(run.Code, code, StringComparison.Ordinal))
                {
                    run.Loaded++;
                    run.Bytes += bytes;
                    run.Lengths.Add(step.Name, clip.length, GameLengthOf(step.Name));
                }

                return true;
            }
            catch (Exception ex)
            {
                Failed(code, step, ex.GetType().Name + ": " + ex.Message);
                return false;
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
            if (_firstLine != null && _firstLine.Is(lookup.Code, lookup.Name))
            {
                _firstLine.FellBack = true;
            }

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

        // ---- Pack Maker live hooks (DS-03) ------------------------------------------------------
        //
        // A dub Pack Maker saves is swapped into the set of its language at once: the clip lookup asks
        // the swap before the table (ResolveForPlay, TryPeek), so the next time the game plays that
        // line in that language it plays the new take. A swap of "nothing" hides the set's file for
        // that name (a removed dub). The Play button pins one name to one clip (or to the game's own
        // clip) for the length of one line, whatever the language in force. Main thread only; the
        // lock covers TryPeek callers on other threads.

        /// <summary>How long a maker clip that was replaced waits before it is destroyed (it may still be playing).</summary>
        private const double ReleaseAfterSeconds = 90d;

        private static readonly object LiveGate = new object();
        private static readonly Dictionary<string, LiveSwap> LiveSwaps = new Dictionary<string, LiveSwap>(StringComparer.Ordinal);
        private static readonly HashSet<AudioClip> MakerOwned = new HashSet<AudioClip>();
        private static LivePin? _pin;
        private static int _pinTokens;

        /// <summary>How many Pack Maker swaps are in force (dubs saved or removed this session).</summary>
        internal static int LiveSwapCount
        {
            get { lock (LiveGate) { return LiveSwaps.Count; } }
        }

        /// <summary>
        /// Pack Maker: from now on the set of <paramref name="code"/> answers <paramref name="name"/> with
        /// <paramref name="clip"/>, whatever the plan says, until it is swapped again, dropped, or a pack
        /// rescan finds the plan names a different file. Main thread only.
        /// </summary>
        /// <param name="code">The language code (any accepted spelling).</param>
        /// <param name="name">The voice name.</param>
        /// <param name="clip">The loaded clip.</param>
        /// <param name="packId">The pack the file belongs to.</param>
        /// <param name="relativePath">The file's pack-relative path.</param>
        /// <param name="createdByMaker">True when the editor built the clip (it may destroy it once replaced).</param>
        internal static void SwapLive(string code, string name, AudioClip clip, string packId, string relativePath, bool createdByMaker)
        {
            var canonical = SubtitleLanguages.Normalize(code);
            clip.name = name;
            var swap = new LiveSwap(canonical, name, clip, new VoiceLoadStep(packId, relativePath, name, VoiceLoaderKind.WavSync));
            ReplaceLive(swap, createdByMaker ? clip : null);
            ConsoleEvents.File(
                VoiceFeature.TraceSys,
                "makerSwap",
                ("lang", canonical),
                ("name", name),
                ("pack", packId),
                ("path", relativePath),
                ("seconds", Math.Round(clip.length, 2)));
        }

        /// <summary>
        /// Pack Maker: the set of <paramref name="code"/> no longer covers <paramref name="name"/> (its dub
        /// was removed and nothing else in the packs replaces it), so the game's clip plays.
        /// </summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        internal static void HideLive(string code, string name)
        {
            var canonical = SubtitleLanguages.Normalize(code);
            ReplaceLive(new LiveSwap(canonical, name, null, null), null);
            ConsoleEvents.File(VoiceFeature.TraceSys, "makerHide", ("lang", canonical), ("name", name));
        }

        /// <summary>Pack Maker: forgets a swap, so the plan's own clip answers again.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        internal static void DropLive(string code, string name)
        {
            LiveSwap? old;
            var key = LiveKey(SubtitleLanguages.Normalize(code), name);
            lock (LiveGate)
            {
                LiveSwaps.TryGetValue(key, out old);
                LiveSwaps.Remove(key);
            }

            Release(old?.Clip);
        }

        /// <summary>Pack Maker: the swap for a name, if any. <paramref name="clip"/> is null for a hidden one.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        /// <param name="clip">The swapped clip, or null.</param>
        /// <returns>True when a swap (or a hide) is in force.</returns>
        internal static bool TryLive(string code, string name, out AudioClip? clip)
        {
            lock (LiveGate)
            {
                if (LiveSwaps.TryGetValue(LiveKey(SubtitleLanguages.Normalize(code), name), out var swap))
                {
                    clip = IsPlayable(swap.Clip) ? swap.Clip : null;
                    return true;
                }
            }

            clip = null;
            return false;
        }

        /// <summary>What the plan's table holds for a name in a language, without side effects.</summary>
        /// <param name="code">The language code.</param>
        /// <param name="name">The voice name.</param>
        internal static LanguageVoiceLookup PeekTable(string code, string name) => Table.Lookup(code, name);

        /// <summary>
        /// Pack Maker's Play: until <see cref="Unpin"/> or <paramref name="seconds"/> pass, a lookup of
        /// <paramref name="name"/> in any language answers <paramref name="clip"/> (a dub), or, when it is
        /// null, nothing (so the game's own clip plays). One pin at a time; a new one replaces the old.
        /// </summary>
        /// <param name="name">The voice name.</param>
        /// <param name="clip">The dub to play, or null for the game's own clip.</param>
        /// <param name="packId">The dub's pack, for the trace; empty for the original.</param>
        /// <param name="code">The dub's language, for the trace.</param>
        /// <param name="seconds">The longest the pin lasts.</param>
        /// <returns>A token for <see cref="Unpin"/>.</returns>
        internal static int Pin(string name, AudioClip? clip, string packId, string code, double seconds)
        {
            lock (LiveGate)
            {
                var token = ++_pinTokens;
                var step = clip is null ? null : new VoiceLoadStep(packId.Length == 0 ? "maker" : packId, "play", name, VoiceLoaderKind.WavSync);
                _pin = new LivePin(token, name, clip, step, SubtitleLanguages.Normalize(code), Clock.Elapsed.TotalSeconds + seconds);
                return token;
            }
        }

        /// <summary>Ends a pin, if it is still the one in force.</summary>
        /// <param name="token">What <see cref="Pin"/> returned.</param>
        internal static void Unpin(int token)
        {
            lock (LiveGate)
            {
                if (_pin != null && _pin.Token == token)
                {
                    _pin = null;
                }
            }
        }

        /// <summary>True while a Play pin is in force for this name (the dub-hold reads it).</summary>
        /// <param name="name">The voice name.</param>
        /// <param name="isDub">True when the pin plays a dub rather than the game's clip.</param>
        internal static bool IsPinned(string? name, out bool isDub)
        {
            lock (LiveGate)
            {
                var pin = _pin;
                if (pin != null && Clock.Elapsed.TotalSeconds < pin.ExpiresAt && string.Equals(pin.Name, name, StringComparison.Ordinal))
                {
                    isDub = pin.Clip != null;
                    return true;
                }
            }

            isDub = false;
            return false;
        }

        /// <summary>The live answer for a lookup: the Play pin, then a swap of the language in force.</summary>
        private static bool TryAnswerLive(string name, bool countHit, out LanguageVoiceLookup lookup, out AudioClip? clip)
        {
            clip = null;
            lookup = default;

            lock (LiveGate)
            {
                var pin = _pin;
                if (pin != null && Clock.Elapsed.TotalSeconds >= pin.ExpiresAt)
                {
                    _pin = pin = null;
                }

                if (pin != null && string.Equals(pin.Name, name, StringComparison.Ordinal))
                {
                    if (pin.Clip is null)
                    {
                        // "Original": no language clip, so the lookup falls through to the game's own.
                        lookup = new LanguageVoiceLookup(LanguageVoiceOutcome.NoSet, string.Empty, name, null, null, null);
                        return true;
                    }

                    if (IsPlayable(pin.Clip))
                    {
                        clip = pin.Clip;
                        lookup = new LanguageVoiceLookup(LanguageVoiceOutcome.Loaded, pin.Code, name, pin.Step, pin.Clip, null);
                        return true;
                    }
                }

                if (LiveSwaps.Count == 0)
                {
                    return false;
                }

                var code = SubtitleLanguages.Normalize(CurrentLanguageCode());
                if (code.Length == 0 || !LiveSwaps.TryGetValue(LiveKey(code, name), out var swap))
                {
                    return false;
                }

                if (swap.Clip is null)
                {
                    lookup = new LanguageVoiceLookup(LanguageVoiceOutcome.NotInSet, code, name, null, null, null);
                    return true;
                }

                if (!IsPlayable(swap.Clip))
                {
                    return false;
                }

                clip = swap.Clip;
                lookup = new LanguageVoiceLookup(LanguageVoiceOutcome.Loaded, code, name, swap.Step, swap.Clip, null);
            }

            if (countHit)
            {
                Hits++;
            }

            return true;
        }

        /// <summary>
        /// The background preload reached a name Pack Maker swapped: hand the swap's clip to the table
        /// when it is the very file the plan names (a rescan saw the saved WAV), or skip the file
        /// quietly when the swap replaced or hid it (it may have been moved aside).
        /// </summary>
        private static bool TryHandOver(string code, VoiceLoadStep step)
        {
            LiveSwap? swap;
            var key = LiveKey(SubtitleLanguages.Normalize(code), step.Name);
            lock (LiveGate)
            {
                if (!LiveSwaps.TryGetValue(key, out swap))
                {
                    return false;
                }

                if (swap.Clip != null && swap.Step != null && SameLiveFile(swap.Step, step) && IsPlayable(swap.Clip))
                {
                    LiveSwaps.Remove(key);
                    MakerOwned.Remove(swap.Clip);
                }
                else
                {
                    swap = null;
                }
            }

            if (swap != null && Table.Complete(code, step, swap.Clip, out _))
            {
                ConsoleEvents.File(VoiceFeature.TraceSys, "makerHandOver", ("lang", code), ("name", step.Name), ("path", step.RelativePath));
                return true;
            }

            Table.Fail(code, step, "replaced live by Pack Maker");
            ConsoleEvents.File(VoiceFeature.TraceSys, "makerSkip", ("lang", code), ("name", step.Name), ("path", step.RelativePath));
            return true;
        }

        /// <summary>
        /// After a pack rescan the plan is read from disk again, which is the truth: a swap survives only
        /// while it is the loaded form of the very file the plan now names; every other swap and every
        /// hide is dropped.
        /// </summary>
        private static void ReconcileLive()
        {
            var dropped = new List<LiveSwap>();
            lock (LiveGate)
            {
                foreach (var swap in new List<LiveSwap>(LiveSwaps.Values))
                {
                    var planned = Table.Lookup(swap.Code, swap.Name).Step;
                    if (swap.Clip != null && swap.Step != null && planned != null && SameLiveFile(swap.Step, planned))
                    {
                        continue;
                    }

                    LiveSwaps.Remove(LiveKey(swap.Code, swap.Name));
                    dropped.Add(swap);
                }
            }

            foreach (var swap in dropped)
            {
                Release(swap.Clip);
            }

            if (dropped.Count > 0)
            {
                ConsoleEvents.File(VoiceFeature.TraceSys, "makerReconciled", ("dropped", dropped.Count), ("kept", LiveSwapCount));
            }
        }

        private static void ReplaceLive(LiveSwap swap, AudioClip? owned)
        {
            LiveSwap? old;
            lock (LiveGate)
            {
                var key = LiveKey(swap.Code, swap.Name);
                LiveSwaps.TryGetValue(key, out old);
                LiveSwaps[key] = swap;
                if (owned != null)
                {
                    MakerOwned.Add(owned);
                }
            }

            if (old?.Clip != null && !ReferenceEquals(old.Clip, swap.Clip))
            {
                Release(old.Clip);
            }
        }

        /// <summary>
        /// Destroys a clip the editor built once nothing refers to it any more, after a delay (it may be
        /// playing). Clips the table or the asset cache own are never destroyed here.
        /// </summary>
        private static void Release(AudioClip? clip)
        {
            if (clip is null)
            {
                return;
            }

            lock (LiveGate)
            {
                if (!MakerOwned.Contains(clip))
                {
                    return;
                }
            }

            MainThread.PostAfter(ReleaseAfterSeconds, "voice.lang.makerRelease", () =>
            {
                lock (LiveGate)
                {
                    if (!MakerOwned.Contains(clip) || (_pin != null && ReferenceEquals(_pin.Clip, clip)))
                    {
                        return;
                    }

                    foreach (var swap in LiveSwaps.Values)
                    {
                        if (ReferenceEquals(swap.Clip, clip))
                        {
                            return;
                        }
                    }

                    MakerOwned.Remove(clip);
                }

                if (clip != null)
                {
                    UnityEngine.Object.Destroy(clip);
                }
            });
        }

        private static string LiveKey(string code, string name) => code + "\u001F" + name;

        private static bool SameLiveFile(VoiceLoadStep a, VoiceLoadStep b) =>
            string.Equals(a.PackId, b.PackId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);

        private sealed class LiveSwap
        {
            internal LiveSwap(string code, string name, AudioClip? clip, VoiceLoadStep? step)
            {
                Code = code;
                Name = name;
                Clip = clip;
                Step = step;
            }

            internal string Code { get; }

            internal string Name { get; }

            /// <summary>The clip, or null for "the set does not cover this name".</summary>
            internal AudioClip? Clip { get; }

            internal VoiceLoadStep? Step { get; }
        }

        private sealed class LivePin
        {
            internal LivePin(int token, string name, AudioClip? clip, VoiceLoadStep? step, string code, double expiresAt)
            {
                Token = token;
                Name = name;
                Clip = clip;
                Step = step;
                Code = code;
                ExpiresAt = expiresAt;
            }

            internal int Token { get; }

            internal string Name { get; }

            internal AudioClip? Clip { get; }

            internal VoiceLoadStep? Step { get; }

            internal string Code { get; }

            internal double ExpiresAt { get; }
        }

        /// <summary>A scene's first line whose clip went to the engine (DS-12), until its request ends.</summary>
        private sealed class FirstLine
        {
            internal FirstLine(string code, string name, string? groupId, double readiedAt)
            {
                Code = code;
                Name = name;
                GroupId = groupId;
                ReadiedAt = readiedAt;
            }

            internal string Code { get; }

            internal string Name { get; }

            internal string? GroupId { get; }

            /// <summary>When the scene was readied (<c>StartReady</c>), on <see cref="Clock"/>.</summary>
            internal double ReadiedAt { get; }

            /// <summary>True once the line asked for its clip before it was ready (it played another clip).</summary>
            internal bool FellBack { get; set; }

            internal bool Is(string code, string name) =>
                string.Equals(Code, code, StringComparison.Ordinal) && string.Equals(Name, name, StringComparison.Ordinal);
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
