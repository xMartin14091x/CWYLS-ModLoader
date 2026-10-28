using System;
using System.Globalization;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// Named expressions, body motions and look control for Satone. The public API is
    /// <see cref="Satone"/>; this class owns the wiring, the config, the table and the logging.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every call takes a name or an id, resolves it through the generated
    /// <see cref="SatoneTable"/>, marshals onto the main thread through
    /// <see cref="SatoneMotionHost"/>, calls the game, logs one line at Info and publishes one
    /// <see cref="TraceBus"/> event. An unknown name is a warning with suggestions and a
    /// <c>false</c> return - never an exception, because a mod asking for a motion that a game
    /// update removed should degrade, not crash.
    /// </para>
    /// <para>
    /// Static, with no MonoBehaviour of its own: this game destroys BepInEx plugin components
    /// about 0.2 s after boot.
    /// </para>
    /// </remarks>
    public static class SatoneMotionFeature
    {
        /// <summary><c>sys</c> tag every event from this feature carries on the trace bus.</summary>
        public const string TraceSystem = "satone";

        /// <summary>Config section this feature binds under.</summary>
        public const string ConfigSection = "Satone";

        /// <summary>Config key: master switch.</summary>
        public const string ConfigKeyEnabled = "Enabled";

        /// <summary>Config key: log one Info line per call.</summary>
        public const string ConfigKeyLogEachCall = "LogEachCall";

        /// <summary>Config key: how long a request is watched for its effect.</summary>
        public const string ConfigKeyConfirmSeconds = "ConfirmSeconds";

        /// <summary>Default seconds a request is watched before it is called a timeout.</summary>
        public const float DefaultConfirmSeconds = 2f;

        /// <summary>How close the Animator's <c>LookScale</c> must get to count as confirmed.</summary>
        public const float LookTolerance = 0.05f;

        /// <summary>The <c>name</c> every restore event carries on the trace bus.</summary>
        public const string RestoreTraceName = "restore";

        private static readonly object Gate = new object();

        private static ConfigEntry<bool>? _enabled;
        private static ConfigEntry<bool>? _logEachCall;
        private static ConfigEntry<float>? _confirmSeconds;
        private static bool _initialized;
        private static SatoneSnapshot _lastSnapshot = SatoneSnapshot.Unknown;
        private static RestoreScope _touched;
        private static RestoreResult? _lastRestore;

        /// <summary>True once <see cref="Initialize"/> has run.</summary>
        public static bool Initialized
        {
            get { lock (Gate) { return _initialized; } }
        }

        /// <summary>Master switch; false makes every call a no-op that returns false.</summary>
        public static bool Enabled => _enabled?.Value ?? true;

        /// <summary>Whether each accepted call writes an Info line.</summary>
        public static bool LogEachCall => _logEachCall?.Value ?? true;

        /// <summary>Seconds a request is watched for its effect.</summary>
        public static float ConfirmSeconds => _confirmSeconds?.Value ?? DefaultConfirmSeconds;

        /// <summary>
        /// What Satone looked like immediately before the most recent <c>Face</c>/<c>Motion</c>/
        /// <c>LookAt</c> call. <see cref="SatoneSnapshot.Unknown"/> until one has been made.
        /// </summary>
        public static SatoneSnapshot LastSnapshot
        {
            get { lock (Gate) { return _lastSnapshot; } }
        }

        /// <summary>
        /// Which channels our own calls have changed since the last restore. Cleared by
        /// <see cref="RestoreDefaults"/>.
        /// </summary>
        public static RestoreScope TouchedScopes
        {
            get { lock (Gate) { return _touched; } }
        }

        /// <summary>The most recent restore, or null before the first one.</summary>
        public static RestoreResult? LastRestore
        {
            get { lock (Gate) { return _lastRestore; } }
        }

        /// <summary>How long the expression reset waits - the game's own number, see the seam.</summary>
        public static float FacialResetDelaySeconds => SatoneSeam.FacialResetDelaySeconds;

        /// <summary>
        /// Binds the config, arms the main-thread host, verifies the seams and registers the smoke
        /// checks. Safe to call twice; the second call is ignored.
        /// </summary>
        /// <param name="config">The plugin's config file, or null to accept every default.</param>
        public static void Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_initialized)
                {
                    CwyLsModLoader.Log.LogDebug("SATONE already initialised; ignoring the second call");
                    return;
                }

                _initialized = true;
                BindConfig(config);
            }

            ConsoleEvents.Setup("satone", SatoneTable.Summary());
            if (!SatoneTable.Loaded)
            {
                CwyLsModLoader.Log.LogError(
                    "SATONE the name table could not be read, so every name lookup will fail: "
                    + SatoneTable.LoadError);
            }

            SatoneMotionSmokeChecks.RegisterAll();

            PatchTargetRegistry.Register(BulbulPlugin.PluginName + " (satone)", SatoneMotionTargets.All);
            PatchTargetVerifier.VerifyAll(SatoneMotionTargets.All, new ReflectionMemberResolver());

            if (!Enabled)
            {
                ConsoleEvents.Setup(
                    "satone",
                    $"disabled ({ConfigSection}.{ConfigKeyEnabled}=false); the API answers false");
                return;
            }

            SatoneMotionHost.Arm();
        }

        // ---- operations -------------------------------------------------------------------

        /// <summary>Changes Satone's expression. See <see cref="Satone.Face"/>.</summary>
        /// <param name="nameOrId">Expression name, alias or id.</param>
        internal static bool Face(string? nameOrId)
        {
            if (!Ready("face"))
            {
                return false;
            }

            if (!SatoneTable.TryFacial(nameOrId, out var facial) || facial is null)
            {
                Unknown("face", nameOrId, SatoneTable.Suggest(nameOrId));
                return false;
            }

            return SatoneMotionHost.Post("face " + facial.Name, () =>
            {
                if (!Heroine("face", out var heroine) || heroine is null)
                {
                    return;
                }

                Remember(RestoreScope.Face, touched: true);
                heroine.ChangeHeroineFacialAnimation(facial.Id);
                Accepted("face", facial.Name, facial.Id, ("meshes", facial.Meshes.ToString()), ("clip", facial.Clip));
                WatchFacial(heroine, facial);
            });
        }

        /// <summary>Returns the expression to neutral. See <see cref="Satone.FaceReset"/>.</summary>
        internal static bool FaceReset()
        {
            if (!Ready("face"))
            {
                return false;
            }

            return SatoneMotionHost.Post("face reset", () =>
            {
                if (!Heroine("face", out var heroine) || heroine is null)
                {
                    return;
                }

                // HeroineService.InitHeroineFacial resets the facial to 0,
                // so the effect to watch for is facial 0 - "none" in the table.
                Remember(RestoreScope.Face, touched: false);
                heroine.InitHeroineFacial();
                var neutral = SatoneTable.TryFacial("0", out var zero) && zero is not null ? zero : null;
                Accepted("face", neutral?.Name ?? "0", 0, ("reset", true));

                if (neutral is not null)
                {
                    WatchFacial(heroine, neutral);
                }
            });
        }

        /// <summary>Plays a body motion. See <see cref="Satone.Motion"/>.</summary>
        /// <param name="nameOrId">Motion name, alias or id.</param>
        /// <param name="trigger">Use the Animator trigger rather than the integer parameter.</param>
        internal static bool Motion(string? nameOrId, bool trigger)
        {
            if (!Ready("motion"))
            {
                return false;
            }

            if (!SatoneTable.TryMotion(nameOrId, out var motion) || motion is null)
            {
                Unknown("motion", nameOrId, SatoneTable.Suggest(nameOrId));
                return false;
            }

            var useTrigger = trigger;
            if (useTrigger && !motion.Trigger)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"SATONE motion {motion.Name} (id {motion.Id}) has no Animator trigger parameter; "
                    + "falling back to MotionType. Motions with trigger=true are the ones the table "
                    + "marks trigger:true.");
                useTrigger = false;
            }

            return SatoneMotionHost.Post("motion " + motion.Name, () =>
            {
                if (!Heroine("motion", out var heroine) || heroine is null)
                {
                    return;
                }

                Remember(RestoreScope.Motion, touched: true);
                var type = (Game.HeroineService.AnimationType)motion.Id;
                if (useTrigger)
                {
                    // Sets the Animator trigger by name and records the type.
                    heroine.ChangeHeroineAnimationForTrigger(type);
                }
                else
                {
                    // Records before/next and calls
                    // ChangeHeroineAnimationForInteger, which is also where the game fires the
                    // handful of ids that need a trigger as well as the integer.
                    heroine.ChangeAnimation(type);
                }

                Accepted(
                    "motion",
                    motion.Name,
                    motion.Id,
                    ("category", motion.Category),
                    ("state", motion.State),
                    ("clip", motion.Clip),
                    ("trigger", useTrigger));

                WatchMotion(motion);
            });
        }

        /// <summary>Turns Satone's gaze towards the player. See <see cref="Satone.LookAt"/>.</summary>
        /// <param name="scale">Look weight, 0 = away, 1 = at the player.</param>
        /// <param name="seconds">Tween duration.</param>
        internal static bool LookAt(float scale, float seconds)
        {
            if (!Ready("look"))
            {
                return false;
            }

            return SatoneMotionHost.Post("look " + scale.ToString("0.##", CultureInfo.InvariantCulture), () =>
            {
                if (!Heroine("look", out var heroine) || heroine is null)
                {
                    return;
                }

                Remember(RestoreScope.Gaze, touched: true);
                if (!SatoneSeam.TryChangeLookScale(heroine, scale, seconds, out var error))
                {
                    CwyLsModLoader.Log.LogWarning("SATONE look failed: " + error);
                    return;
                }

                Accepted("look", scale.ToString("0.##", CultureInfo.InvariantCulture), -1, ("seconds", seconds));
                WatchLook(scale, seconds);
            });
        }

        /// <summary>Returns the gaze to its idle behaviour. See <see cref="Satone.LookReset"/>.</summary>
        internal static bool LookReset()
        {
            if (!Ready("look"))
            {
                return false;
            }

            return SatoneMotionHost.Post("look reset", () =>
            {
                if (!Heroine("look", out var heroine) || heroine is null)
                {
                    return;
                }

                // HeroineService.LookInitSlowly starts a 1.5 s look ease,
                // which waits half a second before it starts tweening.
                Remember(RestoreScope.Gaze, touched: false);
                heroine.LookInitSlowly();
                Accepted("look", "reset", -1, ("seconds", 1.5f));
                WatchLook(0f, 2.5f);
            });
        }

        // ---- restore ----------------------------------------------------------------------

        /// <summary>Hands Satone back to the game. See <see cref="Satone.RestoreDefaults"/>.</summary>
        /// <param name="scope">Which channels to restore.</param>
        internal static RestoreResult RestoreDefaults(RestoreScope scope)
        {
            if (!Ready("restore"))
            {
                return Finish(RestoreResult.Refused(
                    scope,
                    Initialized
                        ? ConfigSection + "." + ConfigKeyEnabled + "=false"
                        : "SatoneMotionFeature.Initialize was never called"));
            }

            if (scope == RestoreScope.None)
            {
                return Finish(RestoreResult.Refused(scope, "no channel was asked for"));
            }

            if (!SatoneMotionHost.OnMainThread)
            {
                SatoneMotionHost.Post("restore " + scope.Describe(), () => RunRestore(scope));
                var queued = RestoreResult.QueuedFor(scope);
                lock (Gate)
                {
                    _lastRestore = queued;
                }

                return queued;
            }

            return RunRestore(scope);
        }

        /// <summary>
        /// A scoped change: the caller gets a handle that restores <paramref name="scope"/> when it
        /// is disposed. See <see cref="Satone.Scoped"/>.
        /// </summary>
        /// <param name="scope">Which channels the handle restores.</param>
        internal static SatoneRevert Scoped(RestoreScope scope)
        {
            if (scope == RestoreScope.None)
            {
                return SatoneRevert.None();
            }

            return new SatoneRevert(scope, Capture(), (channels, _) => RestoreDefaults(channels));
        }

        /// <summary>
        /// What Satone looks like right now. Live on the main thread; off it, the host's periodic
        /// cache, which knows the motion and the AI state but not the two Animator read-backs.
        /// </summary>
        internal static SatoneSnapshot Capture()
        {
            if (!SatoneMotionHost.OnMainThread)
            {
                SatoneMotionHost.WantCache();
                var cache = SatoneMotionHost.Cache;
                return new SatoneSnapshot(
                    cache.Valid, null, cache.Valid ? cache.MotionId : (int?)null, null,
                    cache.StateId, cache.State, SatoneMotionHost.Now);
            }

            if (!SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                return SatoneSnapshot.Unknown.At(SatoneMotionHost.Now);
            }

            var stateId = (int)heroine.GetCurrentAIState();
            return new SatoneSnapshot(
                true,
                SatoneSeam.ReadInteger(heroine, SatoneSeam.FacialParameter),
                (int)heroine.GetCurrentAnimationType(),
                SatoneSeam.ReadFloat(heroine, SatoneSeam.LookParameter),
                stateId,
                StateName(stateId),
                SatoneMotionHost.Now);
        }

        /// <summary>
        /// The restore itself. Main thread only.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the game's own "put her back" triple, in the order
        /// <c>RoomGameManager.OnEndPlayedReaction</c> runs it:
        /// <c>LookInitSlowly()</c>, <c>InitHeroineFacialAfterDelay(0.77f)</c>, and the motion
        /// through a re-dispatch of the current AI state rather than through a direct
        /// <c>ChangeAnimation</c> - because the state's <c>OnEnter</c> is the only thing that knows
        /// which base loop belongs to it (e.g. 200 for <c>HeroineStateWorkPC</c>, 600 for
        /// <c>HeroineStateBreakMovie</c>).
        /// </para>
        /// <para>
        /// Nothing here starts an observation: the restore is meant to land <em>after</em> the
        /// change it undoes has been watched, and <see cref="SatoneMotionHost.Watch"/> keeps only
        /// the newest observation, so arming one here would erase the evidence for the call being
        /// undone.
        /// </para>
        /// </remarks>
        private static RestoreResult RunRestore(RestoreScope scope)
        {
            if (!Heroine("restore", out var heroine) || heroine is null)
            {
                return Finish(RestoreResult.Refused(scope, "HeroineService is not resolvable"));
            }

            if (ForeignScenario(out var playing))
            {
                return Finish(RestoreResult.Refused(
                    scope, "the game is playing " + playing + ", which owns her pose"));
            }

            var gazeReset = false;
            if (scope.Has(RestoreScope.Gaze))
            {
                // HeroineService.LookInitSlowly: half a second of delay, then a 1.5 s ease-out
                // (OutQuad) of both the IK weight and LookScale. It no-ops while a previous ease
                // is still running (HeroineLookDirectionService), which is
                // harmless - the gaze is already on its way back.
                heroine.LookInitSlowly();
                gazeReset = true;
            }

            var faceScheduled = false;
            var faceDelay = 0f;
            if (scope.Has(RestoreScope.Face))
            {
                faceDelay = SatoneSeam.FacialResetDelaySeconds;
                heroine.InitHeroineFacialAfterDelay(faceDelay);
                faceScheduled = true;
            }

            var stateName = string.Empty;
            var skipReason = string.Empty;
            var redispatched = false;
            if (scope.Has(RestoreScope.Motion))
            {
                var state = heroine.GetCurrentAIState();
                stateName = StateName((int)state);

                if (!SatoneRestorePolicy.ShouldRedispatch(stateName, out skipReason))
                {
                    // Left alone on purpose; the reason travels out in the result and the log line.
                }
                else if (SatoneSeam.TryIsActionChangeable(heroine, out var changeable, out var detail)
                         && !changeable)
                {
                    // The game guards its own re-dispatch the same way (in HeroineAI).
                    skipReason = "the AI is between actions right now (" + detail + ")";
                }
                else
                {
                    // HeroineService.DebugChangeState -> HeroineAI.DebugChangeState -> ChangeState
                    // -> a dispatch on the AI's action state machine, which is the identical call
                    // HeroineAI.FinishHeroineClickReaction makes.
                    heroine.DebugChangeState(state);
                    redispatched = true;
                }
            }

            var result = new RestoreResult(
                scope, faceScheduled, faceDelay, redispatched, stateName, skipReason, gazeReset, null, false);

            lock (Gate)
            {
                _touched &= ~result.Applied;
            }

            return Finish(result);
        }

        /// <summary>
        /// True when the game's own scenario reader is mid-scenario and the scenario is not one of
        /// ours - restoring then would wipe a facial the reader just set.
        /// </summary>
        private static bool ForeignScenario(out string playing)
        {
            playing = string.Empty;

            if (!GameScopes.TryRoom<Game.ScenarioReader>(out var reader) || reader is null)
            {
                return false;
            }

            try
            {
                // ScenarioReader.IsPlayingScenario. EndStory clears the flag before our
                // ScenarioEnded event is published, so a Say line's own restore is never blocked by this.
                if (!reader.IsPlayingScenario())
                {
                    return false;
                }

                var group = reader.PlayingScenarioGroupID;
                if (SayIdGenerator.IsSayGroup(group))
                {
                    return false;
                }

                playing = string.IsNullOrEmpty(group)
                    ? reader.PlayingScenarioType.ToString()
                    : reader.PlayingScenarioType + " " + group;
                return true;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    "SATONE restore could not ask ScenarioReader whether it is busy, so it went "
                    + "ahead: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Records a pre-call snapshot and what the call is about to change.</summary>
        private static void Remember(RestoreScope channel, bool touched)
        {
            var before = Capture();
            lock (Gate)
            {
                _lastSnapshot = before;
                if (touched)
                {
                    _touched |= channel;
                }
                else
                {
                    _touched &= ~channel;
                }
            }
        }

        /// <summary>The one console line every restore owns, plus its trace event.</summary>
        private static RestoreResult Finish(RestoreResult result)
        {
            lock (Gate)
            {
                _lastRestore = result;
            }

            // One publish, one console line. The direct "SATONE restore ..." twin is gone;
            // RestoreResult.Describe() is still the sentence a person reads, it is just the record's
            // primary column now.
            ConsoleEvents.Emit(
                TraceSystem,
                RestoreTraceName,
                ConsoleTags.Restore,
                result.Describe(),
                "scope,state:motionState,gaze,detail",
                ("phase", result.Queued ? "queued" : result.Problem.Length > 0 ? "refused" : "done"),
                ("scope", result.Requested.Describe()),
                ("applied", result.Applied.Describe()),
                ("faceDelaySec", result.FaceScheduled ? (object?)result.FaceDelaySeconds : null),
                ("motionState", result.MotionRedispatched ? result.MotionState : null),
                ("motionSkipped", result.MotionSkipReason.Length == 0 ? null : result.MotionState + ": " + result.MotionSkipReason),
                ("gaze", result.GazeReset),
                ("detail", result.Problem));

            return result;
        }

        /// <summary>The table's name for an <c>ActionStateType</c> value.</summary>
        private static string StateName(int stateId) =>
            SatoneTable.TryState(stateId, out var state) && state is not null
                ? state.Name
                : Satone.UnknownName + "_" + stateId.ToString(CultureInfo.InvariantCulture);

        // ---- state ------------------------------------------------------------------------

        /// <summary>A live snapshot of Satone's state; main thread only.</summary>
        internal static SatoneStateCache Snapshot()
        {
            if (!SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                return SatoneStateCache.Unknown;
            }

            var stateId = (int)heroine.GetCurrentAIState();
            var motionId = (int)heroine.GetCurrentAnimationType();

            return new SatoneStateCache(
                true,
                SatoneTable.TryState(stateId, out var state) && state is not null
                    ? state.Name
                    : Satone.UnknownName + "_" + stateId.ToString(CultureInfo.InvariantCulture),
                stateId,
                SatoneTable.TryMotion(motionId.ToString(CultureInfo.InvariantCulture), out var motion) && motion is not null
                    ? motion.Name
                    : Satone.UnknownName + "_" + motionId.ToString(CultureInfo.InvariantCulture),
                motionId,
                heroine.IsEndAnimation(),
                heroine.IsPossibleTalk());
        }

        /// <summary>Refreshes the off-thread cache. Called by the host; main thread only.</summary>
        internal static void RefreshCache() => SatoneMotionHost.PublishCache(Snapshot());

        /// <summary>One frame of observation. Called by the host; main thread only.</summary>
        internal static void Tick()
        {
            var pending = SatoneMotionHost.LastObservation;
            if (pending is null || pending.Outcome != SatoneObservationOutcome.Pending)
            {
                return;
            }

            if (!SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                SatoneMotionHost.Advance((o) => false, (o, ok) => "the room scope went away");
                return;
            }

            switch (pending.Kind)
            {
                case "motion":
                    SatoneMotionHost.Advance(
                        o => heroine.IsCurrentAnimation(o.Expected),
                        (o, ok) => ok
                            ? "animator state '" + o.Expected + "' is current"
                            : "animator state '" + o.Expected + "' never became current; it is playing '"
                              + CurrentStateGuess(heroine) + "'");
                    break;

                case "face":
                    var facial = SatoneSeam.ReadInteger(heroine, SatoneSeam.FacialParameter);
                    SatoneMotionHost.Advance(
                        o => facial.HasValue && facial.Value.ToString(CultureInfo.InvariantCulture) == o.Expected,
                        (o, ok) => facial.HasValue
                            ? SatoneSeam.FacialParameter + "=" + facial.Value.ToString(CultureInfo.InvariantCulture)
                            : "no Animator read-back: " + SatoneSeam.AnimatorSeamDetail);
                    break;

                case "look":
                    var look = SatoneSeam.ReadFloat(heroine, SatoneSeam.LookParameter);
                    SatoneMotionHost.Advance(
                        o => look.HasValue
                             && float.TryParse(o.Expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var want)
                             && Mathf.Abs(look.Value - want) <= LookTolerance,
                        (o, ok) => look.HasValue
                            ? SatoneSeam.LookParameter + "=" + look.Value.ToString("0.###", CultureInfo.InvariantCulture)
                              + (ok ? string.Empty : "; the game routes the look through PlayToLook during a click scenario")
                            : "no Animator read-back: " + SatoneSeam.AnimatorSeamDetail);
                    break;

                default:
                    SatoneMotionHost.Advance(
                        (o) => false, (o, ok) => "no rule for observation kind '" + o.Kind + "'");
                    break;
            }
        }

        // ---- helpers ----------------------------------------------------------------------

        /// <summary>
        /// Best guess at the Animator state Satone is actually in, for a timeout message. There is
        /// no "give me the current state name" API - <c>AnimatorStateInfo</c> only exposes a name
        /// hash - so this reports the motion id the game last stored instead.
        /// </summary>
        private static string CurrentStateGuess(Game.HeroineService heroine)
        {
            var id = (int)heroine.GetCurrentAnimationType();
            return SatoneTable.TryMotion(id.ToString(CultureInfo.InvariantCulture), out var motion) && motion is not null
                ? (motion.State ?? motion.Name)
                : "motion " + id.ToString(CultureInfo.InvariantCulture);
        }

        private static void WatchMotion(SatoneMotion motion)
        {
            if (string.IsNullOrEmpty(motion.State))
            {
                // No provable state for this id: the game's data does not tie it to one, so there is
                // nothing to compare the Animator against. Say so rather than report a timeout.
                var blind = new SatoneObservation(
                    "motion", motion.Name, string.Empty, Time.realtimeSinceStartup, ConfirmSeconds);
                blind.Settle(
                    SatoneObservationOutcome.Unobservable,
                    0,
                    "the table has no Animator state for this id, so the effect cannot be observed");
                SatoneMotionHost.Watch(blind);
                return;
            }

            SatoneMotionHost.Watch(new SatoneObservation(
                "motion", motion.Name, motion.State!, Time.realtimeSinceStartup, ConfirmSeconds));
        }

        private static void WatchFacial(Game.HeroineService heroine, SatoneFacial facial)
        {
            if (SatoneSeam.ReadInteger(heroine, SatoneSeam.FacialParameter) is null)
            {
                var blind = new SatoneObservation(
                    "face", facial.Name, string.Empty, Time.realtimeSinceStartup, ConfirmSeconds);
                blind.Settle(
                    SatoneObservationOutcome.Unobservable, 0, SatoneSeam.AnimatorSeamDetail);
                SatoneMotionHost.Watch(blind);
                return;
            }

            SatoneMotionHost.Watch(new SatoneObservation(
                "face",
                facial.Name,
                facial.Id.ToString(CultureInfo.InvariantCulture),
                Time.realtimeSinceStartup,
                ConfirmSeconds));
        }

        private static void WatchLook(float target, float seconds)
        {
            SatoneMotionHost.Watch(new SatoneObservation(
                "look",
                target.ToString("0.##", CultureInfo.InvariantCulture),
                target.ToString("0.###", CultureInfo.InvariantCulture),
                Time.realtimeSinceStartup,
                Math.Max(ConfirmSeconds, seconds + ConfirmSeconds),
                warnOnTimeout: false));
        }

        private static bool Ready(string kind)
        {
            if (!Initialized)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"SATONE {kind} was called before SatoneMotionFeature.Initialize; ignored");
                return false;
            }

            if (!Enabled)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"SATONE {kind} ignored: {ConfigSection}.{ConfigKeyEnabled}=false");
                return false;
            }

            return true;
        }

        private static bool Heroine(string kind, out Game.HeroineService? heroine)
        {
            if (SatoneSeam.TryHeroine(out heroine) && heroine != null)
            {
                return true;
            }

            CwyLsModLoader.Log.LogWarning(
                $"SATONE {kind} ignored: HeroineService is not resolvable "
                + (SatoneSeam.IsRoomLoaded ? "although RoomScene is loaded" : "because RoomScene is not loaded"));
            return false;
        }

        private static void Unknown(string kind, string? nameOrId, System.Collections.Generic.IReadOnlyList<string> suggestions)
        {
            var hint = suggestions.Count == 0
                ? string.Empty
                : " Did you mean: " + string.Join(", ", System.Linq.Enumerable.ToArray(suggestions)) + "?";

            CwyLsModLoader.Log.LogWarning(
                $"SATONE unknown {kind} '{nameOrId}'.{hint}"
                + (SatoneTable.Loaded ? string.Empty : " (the name table failed to load: " + SatoneTable.LoadError + ")"));

            // The warning above is the console line (the sink tags it WARN); the bus copy is the
            // trace file's record of the same refusal.
            ConsoleEvents.File(
                TraceSystem, kind,
                ("phase", "unknown"),
                ("what", nameOrId),
                ("suggestions", string.Join(",", System.Linq.Enumerable.ToArray(suggestions))));
        }

        /// <summary>
        /// The one line an accepted call owns: published once, with its own tag, primary and
        /// field order - the direct "SATONE face smile (id 10)" twin this used to log as well is
        /// gone, and <c>Satone.LogEachCall=false</c> now means "file only" rather than "one line
        /// instead of two".
        /// </summary>
        /// <param name="kind">Bus event name and console tag source: face, motion or look.</param>
        /// <param name="name">The table's name for what was asked for.</param>
        /// <param name="id">The numeric index behind the name, or -1.</param>
        /// <param name="extra">Anything else worth writing to the trace file.</param>
        private static void Accepted(string kind, string name, int id, params (string Key, object? Value)[] extra)
        {
            var fields = new (string Key, object? Value)[extra.Length + 4];
            fields[0] = (ConsolePayload.MirrorKey, LogEachCall && _quietDepth == 0);
            fields[1] = ("phase", "called");
            fields[2] = ("name", name);
            fields[3] = ("id", id);
            Array.Copy(extra, 0, fields, 4, extra.Length);

            ConsoleEvents.Emit(TraceSystem, kind, TagFor(kind), name, "id", fields);
        }

        /// <summary>
        /// Depth of the "this call already has a console line" scope. Non-zero while
        /// <c>Satone.Say</c> applies the expression, motion and gaze it already named in its own
        /// SAY line: printing FACE, MOTION and LOOK as well would be the same information three
        /// more times, which is exactly the duplication this avoids. The trace file still gets
        /// every call.
        /// </summary>
        [ThreadStatic]
        private static int _quietDepth;

        /// <summary>
        /// Opens a scope in which accepted calls are trace-file only, because the caller has
        /// already written the console line that describes them. Always pair with
        /// <see cref="EndOwnedCalls"/> in a <c>finally</c>. Main thread only, like every call it
        /// wraps.
        /// </summary>
        internal static void BeginOwnedCalls() => _quietDepth++;

        /// <summary>Closes the scope opened by <see cref="BeginOwnedCalls"/>.</summary>
        internal static void EndOwnedCalls()
        {
            if (_quietDepth > 0)
            {
                _quietDepth--;
            }
        }

        /// <summary>The console tag for one call kind.</summary>
        /// <param name="kind">face, motion or look.</param>
        private static string TagFor(string kind)
        {
            switch (kind)
            {
                case "face": return ConsoleTags.Face;
                case "motion": return ConsoleTags.Motion;
                case "look": return ConsoleTags.Look;
                case RestoreTraceName: return ConsoleTags.Restore;
                default: return kind.ToUpperInvariant();
            }
        }

        private static void BindConfig(ConfigFile? config)
        {
            if (config is null)
            {
                return;
            }

            _enabled = config.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Master switch for the Satone expression/motion/look API (P2-3). "
                + "False makes every Satone.* call a no-op that returns false.");

            _logEachCall = config.Bind(
                ConfigSection,
                ConfigKeyLogEachCall,
                true,
                "Write one Info line per accepted call, e.g. 'SATONE face smile (id 10)'. "
                + "Trace-bus events are published either way.");

            _confirmSeconds = config.Bind(
                ConfigSection,
                ConfigKeyConfirmSeconds,
                DefaultConfirmSeconds,
                "How long, in seconds, a request is watched for its effect before it is reported "
                + "as a timeout. The Animator needs a frame or two; 0 turns confirmation into an "
                + "immediate timeout and is only useful for debugging.");
        }
    }
}
