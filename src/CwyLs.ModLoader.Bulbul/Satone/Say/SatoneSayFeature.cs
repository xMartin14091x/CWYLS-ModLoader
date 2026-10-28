using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Bulbul.Events;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// <c>Satone.Say</c> - one call that makes Satone speak a line the game never shipped,
    /// through the game's own scenario pipeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The line is turned into the master-data rows the game's own writers would have authored
    /// (<see cref="SatoneSayRows"/>) and handed to <c>ScenarioReader</c>. From there everything is
    /// the game's: the clip, her mouth animation, the story text box, the typing speed, the wait
    /// for the voice to finish and the close of the scenario. Nothing here animates anything by
    /// hand except the expression, body motion and gaze, which the reader's <c>VoiceText</c> path
    /// does not apply - those go through the Satone motion API, which calls exactly the
    /// <c>HeroineService</c> methods the reader's own <c>ChangeMotion</c> command calls.
    /// </para>
    /// <para>
    /// <b>Busy policy.</b> A line is queued, not forced. Once a frame the queue is asked whether
    /// the game could start a scenario - nothing else playing, Satone interruptible, rows
    /// writable. If not, the line waits; after <see cref="MaxWaitSeconds"/> (and with
    /// <see cref="FallbackWhenBusy"/>) it is spoken the simple way instead:
    /// <c>VoiceFeature.PlayAsSatone</c> moves her mouth, but there is no story text box and no
    /// scenario motion. Nothing else draws the text (the old overlay panel was retired), so
    /// on that path it appears only in the console's <c>SAY</c> line.
    /// </para>
    /// <para>
    /// Static, with no MonoBehaviour of its own: the game destroys BepInEx plugin components about
    /// 0.2 s after boot. The frame tick comes from <see cref="SatoneSayHost"/>.
    /// </para>
    /// </remarks>
    public static class SatoneSayFeature
    {
        /// <summary>Config section this feature binds under.</summary>
        public const string ConfigSection = "Say";

        /// <summary>Config key: master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key: the read mode a Say scenario is started with.</summary>
        public const string ConfigKeyDefaultReadMode = "DefaultReadMode";

        /// <summary>Config key: how many lines may wait.</summary>
        public const string ConfigKeyMaxQueue = "MaxQueue";

        /// <summary>Config key: whether a line that waited too long is spoken the simple way.</summary>
        public const string ConfigKeyFallbackWhenBusy = "FallbackWhenBusy";

        /// <summary>Config key: how long a line waits for the scenario path.</summary>
        public const string ConfigKeyMaxWaitSeconds = "MaxWaitSeconds";

        /// <summary>Config key: one Info line per spoken line.</summary>
        public const string ConfigKeyLogEachLine = "LogEachLine";

        /// <summary><c>sys</c> tag every event from this feature carries on the trace bus.</summary>
        public const string TraceSystem = "satone";

        /// <summary>The event name this feature's trace events carry.</summary>
        public const string TraceName = "say";

        /// <summary>The <c>source</c> a clip supplied to <c>Satone.Say</c> is registered under.</summary>
        public const string VoiceSource = "say";

        /// <summary>
        /// How long after a line's performance the restore fires anyway, when the game never
        /// reports the line ending. The scenario path normally restores at
        /// <c>ScenarioEnded</c> and the fallback path at <c>LineEnded</c>; this is only the net
        /// under both, so that a lost event can never leave her smiling forever.
        /// </summary>
        public const float RestoreBackstopSeconds = 8f;

        private static readonly object Gate = new object();
        private static readonly SayIdGenerator Ids = new SayIdGenerator();
        private static readonly Dictionary<int, SayHandle> Handles = new Dictionary<int, SayHandle>();
        private static readonly Dictionary<int, AudioClip> Clips = new Dictionary<int, AudioClip>();
        private static readonly Dictionary<string, SayLine> ByLocalizationId =
            new Dictionary<string, SayLine>(StringComparer.Ordinal);

        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<Game.ScenarioReader.ScenarioReadMode>? _readMode;
        private static ConfigEntry<int>? _maxQueue;
        private static ConfigEntry<bool>? _fallbackWhenBusy;
        private static ConfigEntry<float>? _maxWaitSeconds;
        private static ConfigEntry<bool>? _logEachLine;
        private static ConfigFile? _ownConfig;
        private static SayLine? _awaitingRestore;
        private static SayQueue? _queue;
        private static SatoneSaySubtitles? _subtitles;
        private static bool _initialized;
        private static string _blockedReason = "nothing has been asked for yet";

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool Initialized
        {
            get { lock (Gate) { return _initialized; } }
        }

        /// <summary>Master switch; false makes every call a rejected handle.</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>The read mode a Say scenario is started with; <c>Auto</c> advances itself.</summary>
        public static Game.ScenarioReader.ScenarioReadMode ReadMode =>
            _readMode?.Value ?? Game.ScenarioReader.ScenarioReadMode.Auto;

        /// <summary>How many lines may wait.</summary>
        public static int MaxQueue => _maxQueue?.Value ?? SayQueue.DefaultCapacity;

        /// <summary>Whether a line that waited too long is spoken without the scenario.</summary>
        public static bool FallbackWhenBusy => _fallbackWhenBusy?.Value ?? true;

        /// <summary>How long a line waits for the scenario path, in seconds.</summary>
        public static float MaxWaitSeconds =>
            _maxWaitSeconds?.Value ?? (float)SayQueue.DefaultMaxWaitSeconds;

        /// <summary>Whether every accepted line writes an Info line.</summary>
        public static bool LogEachLine => _logEachLine?.Value ?? true;

        /// <summary>True while a Say line is being spoken.</summary>
        public static bool IsSpeaking => _queue?.IsSpeaking ?? false;

        /// <summary>How many lines are waiting their turn.</summary>
        public static int WaitingCount => _queue?.WaitingCount ?? 0;

        /// <summary>Lines the game's scenario reader played to the end.</summary>
        public static long SpokenCount { get; private set; }

        /// <summary>Lines spoken the simple way because the game stayed busy.</summary>
        public static long FallbackCount { get; private set; }

        /// <summary>Lines refused: bad request, feature off, or a full queue.</summary>
        public static long RejectedCount { get; private set; }

        /// <summary>
        /// True once this feature's per-language texts are registered as a text table
        /// (<c>SubtitleSources</c>), so while a pack language is active the game's text seam can
        /// serve the caller's translation for a Say localization id.
        /// </summary>
        public static bool SubtitleSourceRegistered => _subtitles != null;

        /// <summary>The most recently accepted line, or null.</summary>
        public static SayLine? LastLine { get; private set; }

        /// <summary>The most recently completed handle, or null.</summary>
        public static SayHandle? LastCompleted { get; private set; }

        /// <summary>Why the queue could not start a line last time it was asked. Never null.</summary>
        public static string BlockedReason
        {
            get { lock (Gate) { return _blockedReason; } }
        }

        /// <summary>
        /// What the event bus saw of the last Say line that actually reached the game's voice
        /// path, and what the text seam asked for while a pack language was active. Null before
        /// the first one.
        /// </summary>
        public static SayObservation? LastObservation { get; private set; }

        /// <summary>
        /// Binds the config, registers the subtitle source and the smoke checks, subscribes to the
        /// event bus and arms the main-thread host. Safe to call twice; the second call is ignored.
        /// </summary>
        /// <param name="config">The plugin's config file, or null to bind in this feature's own .cfg.</param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("SATONE say already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
                _queue = new SayQueue(MaxQueue, MaxWaitSeconds, FallbackWhenBusy);
            }

            SatoneSaySmokeChecks.RegisterAll();

            if (!Enabled)
            {
                ConsoleEvents.Setup(
                    "say",
                    "disabled (" + ConfigSection + "." + ConfigKeyEnabled
                    + "=false); Satone.Say returns a rejected handle");
                return;
            }

            _subtitles = new SatoneSaySubtitles();
            SubtitleSources.Register(_subtitles, "Satone.Say");

            Subscribe();
            SatoneSayHost.Arm();

            ConsoleEvents.Setup(
                "say",
                "ready",
                ("queue", MaxQueue),
                ("readMode", ReadMode),
                ("fallback", FallbackWhenBusy ? "after " + Seconds(MaxWaitSeconds) + "s" : "off"),
                ("rows", SatoneSayRows.MaxLines));
        }

        /// <summary>
        /// Queues a line for Satone to say. See <see cref="Satone.Say(string, SayOptions)"/>.
        /// </summary>
        /// <param name="text">What she says.</param>
        /// <param name="options">Clip, translations, expression, motion and gaze; null for text only.</param>
        /// <returns>The handle. Never null; a refused line comes back already completed.</returns>
        public static SayHandle Say(string? text, SayOptions? options)
        {
            var settings = options ?? new SayOptions();
            return Say(settings.ToRequest(text ?? string.Empty), settings.Clip);
        }

        /// <summary>
        /// Queues a line for Satone to say.
        /// </summary>
        /// <param name="request">The line and how it is performed. Required.</param>
        /// <param name="clip">
        /// A clip to register under the line's voice name first, for example a freshly synthesised
        /// one. Null uses <see cref="SayRequest.VoiceName"/> as an existing name.
        /// </param>
        /// <returns>The handle. Never null.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public static SayHandle Say(SayRequest request, AudioClip? clip = null)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!Initialized)
            {
                return Refuse(request, "SatoneSayFeature.Initialize was never called");
            }

            if (!Enabled)
            {
                return Refuse(request, ConfigSection + "." + ConfigKeyEnabled + "=false");
            }

            if (!request.Validate(out var problem))
            {
                return Refuse(request, problem);
            }

            var id = Ids.Next();
            var voiceName = ResolveVoiceName(request, clip, id);
            var facialId = ResolveFacial(request.Facial);
            var motionId = ResolveMotion(request.Motion);
            var line = new SayLine(id, request, voiceName, facialId, motionId, SatoneSayHost.Now);
            var handle = new SayHandle(line, CancelWaiting);

            lock (Gate)
            {
                Handles[id] = handle;
                ByLocalizationId[line.LocalizationId] = line;
                if (clip != null)
                {
                    Clips[id] = clip;
                }
            }

            if (_queue is null || !_queue.TryEnqueue(line))
            {
                Forget(line);
                RejectedCount++;
                handle.Complete(
                    SayOutcome.Rejected,
                    "the queue is full (" + ConfigSection + "." + ConfigKeyMaxQueue + "=" + MaxQueue + ")");
                CwyLsModLoader.Log.LogWarning("SATONE say refused " + line + ": " + handle.Detail);
                Emit(line, "rejected", handle.Detail);
                return handle;
            }

            LastLine = line;

            // Write the rows now when we already are on the main thread and the loader is ready.
            // Nothing depends on it - Perform installs them again just before the scenario starts,
            // and Install is idempotent - but it means the line is inspectable (and provable by a
            // smoke check) from the moment it is accepted.
            if (SatoneSayHost.OnMainThread && SatoneSayRows.IsReady(out _))
            {
                SatoneSayRows.Install(line, out _);
            }

            // Queuing is trace-file business. The console gets one SAY line when the line
            // actually starts (Announce) and a SPEECH line when Satone speaks it.
            Emit(line, "queued", WaitingCount + " waiting");

            return handle;
        }

        /// <summary>
        /// The caller's translation of one of this feature's own lines. Called by
        /// <see cref="SatoneSaySubtitles"/> on behalf of the game's text seam, which asks only while
        /// a pack language is active.
        /// </summary>
        /// <param name="localizationId">The row id the seam is asking about.</param>
        /// <param name="lang">A pack language code.</param>
        /// <param name="text">The line, or empty. Never null.</param>
        /// <returns>True when this feature owns that id and has that language.</returns>
        internal static bool TryText(string localizationId, string lang, out string text)
        {
            text = string.Empty;

            SayLine? line;
            lock (Gate)
            {
                if (!ByLocalizationId.TryGetValue(localizationId, out line) || line is null)
                {
                    return false;
                }
            }

            var found = line.TryText(lang, out text);
            LastObservation?.NoteSubtitleAsk(localizationId, lang, found);
            return found;
        }

        /// <summary>
        /// One frame of queue work. Called by <see cref="SatoneSayHost.Pump"/> on the main thread.
        /// </summary>
        /// <param name="nowSeconds">The host's monotonic clock.</param>
        internal static void Tick(double nowSeconds)
        {
            var queue = _queue;
            if (queue is null || !Enabled)
            {
                return;
            }

            var step = queue.Next(nowSeconds, CanStartNow());
            if (!step.HasWork)
            {
                return;
            }

            var line = step.Line!;
            switch (step.Kind)
            {
                case SayStepKind.Start:
                    if (!Perform(line, out var problem))
                    {
                        // The scenario path failed after the queue had already made this the
                        // speaking line, so release it and speak the simple way instead.
                        queue.Complete(line.GroupId, out _);
                        CwyLsModLoader.Log.LogWarning(
                            "SATONE say could not start the scenario for " + line + ": " + problem
                            + "; speaking it the simple way");
                        Fallback(line, problem);
                    }

                    break;

                case SayStepKind.Fallback:
                    Fallback(line, BlockedReason);
                    break;

                case SayStepKind.Abandon:
                    Complete(
                        line,
                        SayOutcome.TimedOut,
                        "the game never reported " + line.GroupId + " ending within "
                        + Seconds((float)queue.ActiveTimeoutSeconds) + " s");
                    RestoreAfterLine(line, "the line was abandoned");
                    break;
            }
        }

        /// <summary>Forgets every queued line, for example when the room scene has gone.</summary>
        internal static void Reset(string reason)
        {
            var queue = _queue;
            if (queue is null)
            {
                return;
            }

            foreach (var line in queue.Clear())
            {
                Complete(line, SayOutcome.Cancelled, reason);
            }

            // Whichever line was mid-performance loses its end event with the queue, so its
            // restore has to happen here instead.
            RestoreAfterLine(null, reason);
        }

        // ---- the two paths ----------------------------------------------------------------

        /// <summary>
        /// The real path: rows into the game's master data, then <c>ScenarioReader</c> plays them.
        /// </summary>
        private static bool Perform(SayLine line, out string problem)
        {
            if (!SatoneSayRows.Install(line, out problem))
            {
                return false;
            }

            PrepareVoice(line);

            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null)
            {
                problem = "ScenarioReader is not resolvable from the room scope";
                return false;
            }

            try
            {
                // Exactly the pair the game itself uses for a MotionScenario group
                // (FacilityVoiceTextScenario): StartReady loads the group's rows and
                // StartVoiceTextScenario opens the text box and reads row 0. isUseMask:false keeps
                // the story letterbox off, which is what a short talk looks like.
                reader.StartReady(
                    SatoneSayRows.Scenario,
                    line.GroupId,
                    ReadMode,
                    isTalkLog: false,
                    isUseMask: false);

                ApplyPerformance(line);

                reader.StartVoiceTextScenario();
            }
            catch (Exception ex)
            {
                problem = ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            LastObservation = new SayObservation(line, SayObservation.ViaScenario, SatoneSayHost.Now);
            Announce(line, SayObservation.ViaScenario);
            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// The safety net: the clip through <c>HeroineService.PlayVoice</c>. Her mouth moves; the
        /// story text box and the scenario timing do not happen, and nothing else draws the text,
        /// so the handle's detail says where the line went.
        /// </summary>
        private static void Fallback(SayLine line, string why)
        {
            PrepareVoice(line);

            // Best effort: with the rows in place the voice postfix attributes the clip to this
            // line's row, so the SPEECH line carries its localization id and its own text.
            SatoneSayRows.Install(line, out _);

            var spoke = line.HasVoice && VoiceFeature.PlayAsSatone(line.VoiceName, line.Request.MoveMouth);
            ApplyPerformance(line);

            LastObservation = new SayObservation(line, SayObservation.ViaFallback, SatoneSayHost.Now);
            FallbackCount++;
            Announce(line, SayObservation.ViaFallback);
            Complete(
                line,
                SayOutcome.FellBack,
                why + "; " + (spoke
                    ? "voice only, no text box"
                    : "no voice and no text box: the line is only in this log"));
        }

        /// <summary>
        /// Expression, body motion and gaze. The reader's <c>VoiceText</c> path never reads those
        /// three columns - only <c>Text</c> and <c>ChangeMotion</c>
        /// rows do - so they are applied here, through the Satone motion API, which calls the same
        /// <c>HeroineService.ChangeHeroineAnimationForInteger</c> /
        /// <c>ChangeHeroineFacialAnimation</c> / <c>ChangeLookScaleAnimation</c> the reader would.
        /// </summary>
        private static void ApplyPerformance(SayLine line)
        {
            if (!SatoneMotionFeature.Initialized || !SatoneMotionFeature.Enabled)
            {
                if (line.MotionId != SayLine.NoMotion || line.FacialId != SayLine.NoMotion || line.LookScale.HasValue)
                {
                    CwyLsModLoader.Log.LogDebug(
                        "SATONE say " + line.GroupId + " asked for an expression or motion, but the "
                        + SatoneMotionFeature.ConfigSection + " feature is off; only the voice and text happen");
                }

                return;
            }

            // The SAY line already printed facial= and motion=, so these three calls are
            // trace-file only. An expression a mod asks for directly still gets its own FACE line.
            SatoneMotionFeature.BeginOwnedCalls();
            try
            {
                if (line.MotionId != SayLine.NoMotion)
                {
                    Satone.Motion(line.MotionId);
                }

                if (line.FacialId != SayLine.NoMotion)
                {
                    Satone.Face(line.FacialId);
                }

                if (line.LookScale.HasValue)
                {
                    Satone.LookAt(line.LookScale.Value, line.Request.LookSeconds);
                }
            }
            finally
            {
                SatoneMotionFeature.EndOwnedCalls();
            }

            if (line.MotionId == SayLine.NoMotion
                && line.FacialId == SayLine.NoMotion
                && !line.LookScale.HasValue)
            {
                // A text-only line changed nothing, so there is nothing to hand back.
                return;
            }

            lock (Gate)
            {
                _awaitingRestore = line;
            }

            SatoneMotionHost.PostAfter(
                RestoreBackstopSeconds,
                "say " + line.GroupId + " restore backstop",
                () => RestoreAfterLine(line, "the game never reported the line ending"));
        }

        /// <summary>
        /// Hands Satone back to the game after one of our lines - the expression to neutral after
        /// the game's own 0.77 s, the gaze eased out, the body motion back on her AI state's loop.
        /// </summary>
        /// <param name="line">
        /// The line that is ending, or null for "whichever line is waiting". A line that is not the
        /// one waiting is ignored, so an older backstop cannot undo a newer line's performance.
        /// </param>
        /// <param name="why">What ended, for the debug line when the restore is skipped.</param>
        /// <remarks>
        /// <c>ScenarioReader.EndStory</c> restores nothing on Satone;
        /// the game's own restore lives in <c>EndTidyingForStory</c>, which only the story
        /// controller reaches. A Say line uses <c>StartReady</c> + <c>StartVoiceTextScenario</c>
        /// directly, so this is the only thing that puts her back.
        /// </remarks>
        private static void RestoreAfterLine(SayLine? line, string why)
        {
            SayLine? awaiting;
            lock (Gate)
            {
                awaiting = _awaitingRestore;
                if (awaiting is null)
                {
                    return;
                }

                if (line != null && awaiting.Id != line.Id)
                {
                    return;
                }

                _awaitingRestore = null;
            }

            if (IsSpeaking)
            {
                // A queued line started before this one's restore landed; it owns her pose now and
                // will restore her when it ends.
                CwyLsModLoader.Log.LogDebug(
                    "SATONE say skipped the restore for " + awaiting.GroupId + " (" + why
                    + "): another line is already speaking");
                return;
            }

            // Writes its own single "SATONE restore face=... motion=... gaze=..." line.
            Satone.RestoreDefaults(RestoreScope.All);
        }

        /// <summary>Registers the caller's clip, once, under the line's voice name.</summary>
        private static void PrepareVoice(SayLine line)
        {
            AudioClip? clip;
            lock (Gate)
            {
                Clips.TryGetValue(line.Id, out clip);
                Clips.Remove(line.Id);
            }

            if (clip != null)
            {
                VoiceFeature.Register(line.VoiceName, clip, VoiceSource);
                return;
            }

            if (line.HasVoice && !VoiceFeature.TryGet(line.VoiceName, out _))
            {
                // Not ours: it is either one of the game's own 1381 clips or a name nobody has.
                // The game answers that question itself - an unknown name simply plays nothing -
                // so this is a note, not a refusal.
                CwyLsModLoader.Log.LogDebug(
                    "SATONE say '" + line.VoiceName + "' is not in our voice registry; assuming it is "
                    + "one of the game's own clips");
            }
        }

        // ---- gating and completion --------------------------------------------------------

        /// <summary>
        /// Whether the game could start a Say scenario this frame. Sets <see cref="BlockedReason"/>
        /// either way, because that string is the whole diagnosis when a line does not speak.
        /// </summary>
        private static bool CanStartNow()
        {
            string reason;

            if (!GameScopes.IsRoomLoaded)
            {
                reason = "RoomScene is not loaded";
            }
            else if (!SatoneSayRows.IsReady(out var rowsReason))
            {
                reason = rowsReason;
            }
            else if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null)
            {
                reason = "ScenarioReader is not resolvable from the room scope";
            }
            else if (reader.IsPlayingScenario())
            {
                reason = "the game is already playing "
                    + (string.IsNullOrEmpty(reader.PlayingScenarioGroupID)
                        ? reader.PlayingScenarioType.ToString()
                        : reader.PlayingScenarioType + " " + reader.PlayingScenarioGroupID);
            }
            else if (!SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                reason = "HeroineService is not resolvable from the room scope";
            }
            else if (!heroine.IsPossibleTalk())
            {
                // Same question Satone.CanInteract asks, read straight from the seam so this works
                // even when the Satone motion feature is switched off.
                reason = "Satone cannot be interrupted right now";
            }
            else
            {
                lock (Gate)
                {
                    _blockedReason = string.Empty;
                }

                return true;
            }

            lock (Gate)
            {
                _blockedReason = reason;
            }

            return false;
        }

        private static bool CancelWaiting(SayLine line)
        {
            var queue = _queue;
            if (queue is null || !queue.Cancel(line.Id))
            {
                return false;
            }

            Forget(line);
            return true;
        }

        private static void Complete(SayLine line, SayOutcome outcome, string detail)
        {
            SayHandle? handle;
            lock (Gate)
            {
                Handles.TryGetValue(line.Id, out handle);
                Handles.Remove(line.Id);
                Clips.Remove(line.Id);
            }

            if (handle is null || !handle.Complete(outcome, detail))
            {
                return;
            }

            LastCompleted = handle;

            if (outcome == SayOutcome.Spoken)
            {
                SpokenCount++;
            }
            else if (outcome == SayOutcome.Rejected)
            {
                RejectedCount++;
            }

            if (outcome == SayOutcome.Spoken)
            {
                // The SPEECH line already said she said it, with the text and the duration.
                Emit(line, "spoken", detail);
            }
            else
            {
                // A line that did not speak is the one outcome worth interrupting for.
                ConsoleEvents.EmitAt(
                    TraceSystem,
                    TraceName,
                    ConsoleTags.Say,
                    ConsoleLevel.Warn,
                    "#" + line.Id.ToString(CultureInfo.InvariantCulture) + " " + outcome.ToString().ToLowerInvariant()
                        + " \"" + line.ShortText() + "\"",
                    "detail,voice",
                    ("phase", outcome.ToString().ToLowerInvariant()),
                    ("id", line.Id),
                    ("group", line.GroupId),
                    ("localizationId", line.LocalizationId),
                    ("text", line.Text),
                    ("voice", line.HasVoice ? line.VoiceName : null),
                    ("waiting", WaitingCount),
                    ("detail", detail));
            }
        }

        private static void Forget(SayLine line)
        {
            lock (Gate)
            {
                ByLocalizationId.Remove(line.LocalizationId);
                Clips.Remove(line.Id);
            }
        }

        private static SayHandle Refuse(SayRequest request, string reason)
        {
            // The line is built anyway so the caller gets the same shape of answer either way; it
            // is never queued and never reaches the game.
            var line = new SayLine(Ids.Next(), request, request.VoiceName, SayLine.NoMotion, SayLine.NoMotion, SatoneSayHost.Now);
            RejectedCount++;
            CwyLsModLoader.Log.LogWarning("SATONE say refused \"" + line.ShortText() + "\": " + reason);
            return SayHandle.Rejected(line, reason);
        }

        // ---- event bus --------------------------------------------------------------------

        private static void Subscribe()
        {
            Subscriptions.Add(CWYEvents.Subscribe<ScenarioEnded>(OnScenarioEnded));
            Subscriptions.Add(CWYEvents.Subscribe<LineSpoken>(OnLineSpoken));
            Subscriptions.Add(CWYEvents.Subscribe<LineEnded>(OnLineEnded));
            Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => OnRoomReady()));
        }

        /// <summary>
        /// A room scene just became ready, so anything queued from the previous one - and every
        /// row this feature thinks it installed - belongs to a world that no longer exists.
        /// </summary>
        private static void OnRoomReady()
        {
            Reset("the room scene was (re)loaded");
            SatoneSayRows.Forget();

            // Any restore still on the motion host's timer was scheduled against the room that
            // just went away; letting it fire would only find a dead HeroineService. This is the
            // one place in this assembly that already reacts to a room reload.
            SatoneMotionHost.CancelDelayed("the room scene was (re)loaded");
        }

        private static void OnScenarioEnded(ScenarioEnded ended)
        {
            var queue = _queue;
            if (queue is null || ended is null)
            {
                return;
            }

            if (queue.Complete(ended.GroupId, out var line) && line != null)
            {
                Complete(line, SayOutcome.Spoken, "the game ended " + ended.GroupId);
                RestoreAfterLine(line, "the scenario ended");
            }
        }

        private static void OnLineSpoken(LineSpoken spoken)
        {
            if (spoken is null || LastObservation is null)
            {
                return;
            }

            LastObservation.NoteSpoken(spoken, SatoneSayHost.Now);
        }

        private static void OnLineEnded(LineEnded ended)
        {
            if (ended is null)
            {
                return;
            }

            LastObservation?.NoteEnded(ended, SatoneSayHost.Now);

            // The fallback path has no scenario to end, so the clip finishing is its only signal.
            // Matched by voice name so a game line ending cannot trigger our restore.
            SayLine? awaiting;
            lock (Gate)
            {
                awaiting = _awaitingRestore;
            }

            if (awaiting != null
                && awaiting.HasVoice
                && string.Equals(awaiting.VoiceName, ended.VoiceName, StringComparison.Ordinal))
            {
                RestoreAfterLine(awaiting, "the clip finished");
            }
        }

        // ---- plumbing ---------------------------------------------------------------------

        private static string ResolveVoiceName(SayRequest request, AudioClip? clip, int id)
        {
            if (request.HasVoiceName)
            {
                return request.VoiceName!;
            }

            return clip != null ? SayIdGenerator.VoiceName(id) : string.Empty;
        }

        private static int ResolveFacial(string? nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return SayLine.NoMotion;
            }

            if (SatoneTable.TryFacial(nameOrId, out var facial) && facial != null)
            {
                return facial.Id;
            }

            CwyLsModLoader.Log.LogWarning(
                "SATONE say does not know the expression '" + nameOrId + "'; the line keeps her current face. "
                + "Closest: " + string.Join(", ", Copy(SatoneTable.Suggest(nameOrId))));
            return SayLine.NoMotion;
        }

        private static int ResolveMotion(string? nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return SayLine.NoMotion;
            }

            if (SatoneTable.TryMotion(nameOrId, out var motion) && motion != null)
            {
                return motion.Id;
            }

            CwyLsModLoader.Log.LogWarning(
                "SATONE say does not know the motion '" + nameOrId + "'; the line keeps her current motion. "
                + "Closest: " + string.Join(", ", Copy(SatoneTable.Suggest(nameOrId))));
            return SayLine.NoMotion;
        }

        /// <summary>
        /// The one console line a started line owns: <c>SAY  #7 "…"  via= voice= facial= motion=</c>.
        /// Published once, with its own tag and field order.
        /// </summary>
        /// <param name="line">The line that is about to be spoken.</param>
        /// <param name="via">How it reaches the game: <c>scenario</c> or <c>fallback</c>.</param>
        private static void Announce(SayLine line, string via)
        {
            ConsoleEvents.Emit(
                TraceSystem,
                TraceName,
                ConsoleTags.Say,
                "#" + line.Id.ToString(CultureInfo.InvariantCulture) + " \"" + line.ShortText() + "\"",
                "via:phase,voice,facial:facialName,motion:motionName",
                ("phase", via),
                ("id", line.Id),
                ("group", line.GroupId),
                ("localizationId", line.LocalizationId),
                ("text", line.Text),
                ("voice", line.HasVoice ? line.VoiceName : null),
                ("facial", line.FacialId),
                ("facialName", Name(SatoneTable.TryFacial(line.FacialId.ToString(CultureInfo.InvariantCulture), out var facial) ? facial?.Name : null, line.FacialId)),
                ("motion", line.MotionId),
                ("motionName", Name(SatoneTable.TryMotion(line.MotionId.ToString(CultureInfo.InvariantCulture), out var motion) ? motion?.Name : null, line.MotionId)),
                ("look", line.LookScale),
                ("languages", Copy(Languages(line))),
                ("waiting", WaitingCount));
        }

        /// <summary>The trace-file record of a phase the console does not show.</summary>
        /// <param name="line">The line.</param>
        /// <param name="phase">queued, spoken, cancelled…</param>
        /// <param name="detail">Anything worth keeping about why.</param>
        private static void Emit(SayLine line, string phase, string detail)
        {
            ConsoleEvents.File(
                TraceSystem,
                TraceName,
                ("phase", phase),
                ("id", line.Id),
                ("group", line.GroupId),
                ("localizationId", line.LocalizationId),
                ("text", line.Text),
                ("voice", line.HasVoice ? line.VoiceName : null),
                ("facial", line.FacialId),
                ("motion", line.MotionId),
                ("look", line.LookScale),
                ("languages", Copy(Languages(line))),
                ("waiting", WaitingCount),
                ("detail", detail));
        }

        private static IReadOnlyList<string> Languages(SayLine line)
        {
            var codes = new List<string>(line.Texts.Count);
            foreach (var pair in line.Texts)
            {
                codes.Add(pair.Key);
            }

            codes.Sort(StringComparer.Ordinal);
            return codes;
        }

        private static string Name(string? name, int id) =>
            id == SayLine.NoMotion ? "-" : (name ?? id.ToString(CultureInfo.InvariantCulture));

        private static string Seconds(float value) => value.ToString("0.#", CultureInfo.InvariantCulture);

        private static string[] Copy(IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return copy;
        }

        private static void BindConfig(ConfigFile? config)
        {
            var file = config ?? OwnConfig();

            _enabled = file.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Let mods make Satone speak brand-new lines through the game's own scenario reader. "
                + "Off makes every Satone.Say call return a refused handle and touches nothing.");

            _readMode = file.Bind(
                ConfigSection,
                ConfigKeyDefaultReadMode,
                Game.ScenarioReader.ScenarioReadMode.Auto,
                "How a Say line advances. Auto closes the line itself when the voice and the text "
                + "have finished; Click waits for the player to click, which a mod-driven line "
                + "almost never wants.");

            _maxQueue = file.Bind(
                ConfigSection,
                ConfigKeyMaxQueue,
                SayQueue.DefaultCapacity,
                "How many lines may wait for their turn. One line speaks at a time, so this is how "
                + "far ahead a chat mod may run before its requests start being refused.");

            _fallbackWhenBusy = file.Bind(
                ConfigSection,
                ConfigKeyFallbackWhenBusy,
                true,
                "When the game stays busy for longer than " + ConfigKeyMaxWaitSeconds + ", speak the "
                + "line anyway with the voice only - her mouth moves, but there is no story text box "
                + "(the text is only in the console) and no scenario motion. Off keeps it waiting.");

            _maxWaitSeconds = file.Bind(
                ConfigSection,
                ConfigKeyMaxWaitSeconds,
                (float)SayQueue.DefaultMaxWaitSeconds,
                "Seconds a line waits for the game's scenario path before " + ConfigKeyFallbackWhenBusy
                + " applies.");

            _logEachLine = file.Bind(
                ConfigSection,
                ConfigKeyLogEachLine,
                true,
                "Write one Info line per queued and per finished line. Off keeps only the refusals "
                + "and the failures.");
        }

        private static ConfigFile OwnConfig() =>
            _ownConfig ??= new ConfigFile(
                Path.Combine(BepInEx.Paths.ConfigPath, BulbulPlugin.PluginGuid + ".say.cfg"),
                saveOnInit: true);
    }
}
