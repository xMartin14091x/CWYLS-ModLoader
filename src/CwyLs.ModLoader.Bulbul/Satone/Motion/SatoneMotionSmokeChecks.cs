using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The Phase-2 Satone checks: proof, in the running game, that a name reaches the Animator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three of the four checks move Satone on screen, so they are registered
    /// <see cref="SmokeCheck.Loud"/>: an automatic pass reports them as skipped and only
    /// an explicit trigger - the smoke hotkey or <c>BepInEx/smoke.run</c> - actually runs them.
    /// <see cref="TableId"/> only reads data, so it stays quiet and runs on every pass.
    /// </para>
    /// <para>
    /// Each of them also refuses to interrupt her: when
    /// <c>HeroineService.IsPossibleTalk()</c> is false - she is asleep, mid-scenario, leaving the
    /// chair - the check passes with a "skipped" detail rather than shoving a motion into a state
    /// machine that did not ask for one.
    /// </para>
    /// <para>
    /// The checks run on the main thread inside <c>SmokeRunner</c>'s coroutine, so the game call
    /// happens inline and its immediate effect (the integer the game stored) can be asserted right
    /// away. Whether the Animator actually transitioned is answered a frame or two later by
    /// <see cref="SatoneMotionHost.LastObservation"/>, which each check reports from the previous
    /// pass.
    /// </para>
    /// </remarks>
    internal static class SatoneMotionSmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "P2";

        internal const string TableId = "p2.satone.table";
        internal const string FaceId = "p2.satone.face";
        internal const string MotionId = "p2.satone.motion";
        internal const string LookId = "p2.satone.look";
        internal const string RestoreId = "p2.satone.restore";

        /// <summary>
        /// How long a loud check's change is held before it is handed back to the game.
        /// <para>
        /// Long enough for <see cref="SatoneMotionHost.LastObservation"/> to confirm the change -
        /// the Animator reaches the requested state in a few frames - and short enough for the
        /// acceptance bar: facial, motion and look return to the pre-run values within 1 s.
        /// A one-shot wild clip is therefore cut off part-way; that is the deliberate trade.
        /// </para>
        /// </summary>
        internal const float RestoreAfterSeconds = 0.5f;

        /// <summary>
        /// How long after the restore <see cref="RestoreId"/> reads the result back. The game's own
        /// expression reset waits <c>MyDefine.InitFacialDelaySecondsAfterTalk</c> (0.77 s), so the
        /// verdict has to be taken well after that.
        /// </summary>
        internal const float RestoreVerdictSeconds = 2f;

        /// <summary>Floor the table has to clear to be the real generated one, not a stub.</summary>
        internal const int MinFacials = 20;

        /// <summary>Floor for motions; the v1.17.2 table has 181.</summary>
        internal const int MinMotions = 100;

        /// <summary>The expression the face check sets.</summary>
        internal const string FaceName = "smile";

        /// <summary>
        /// Short wild idles the motion check rotates through, so three explicit smoke triggers
        /// play three different motions. All three are
        /// one-shot clips Satone plays by herself while idling, so none of them looks wrong.
        /// </summary>
        internal static readonly string[] MotionRotation =
        {
            "wild001_stretch_shoulder",
            "wild001_good",
            "wild001_breath"
        };

        /// <summary>Look weight and duration the look check asks for.</summary>
        internal const float LookScale = 1f;

        internal const float LookSeconds = 0.4f;

        private static readonly Dictionary<string, RestoreScope> Armed =
            new Dictionary<string, RestoreScope>(System.StringComparer.Ordinal);

        private static int _motionIndex;
        private static RestoreVerdict? _restoreVerdict;

        /// <summary>Registers every Satone check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            // The table check only reads data, so it is quiet and runs on every pass.
            SmokeRegistry.Register(new SmokeCheck(TableId, Phase, Table));

            // The other three move Satone on screen, so they are loud: an automatic pass skips
            // them and only the hotkey or BepInEx/smoke.run runs them.
            // Each loud check also hands its undo to the registry, which runs it in its own
            // finally around the body. ScheduleRestore is idempotent per pass - the check's own
            // finally has normally already scheduled and disarmed it by then - so the registry
            // hook is the net for a body that never reached its finally at all.
            SmokeRegistry.Register(
                new SmokeCheck(FaceId, Phase, Face), loud: true, restore: () => ScheduleRestore(FaceId));
            SmokeRegistry.Register(
                new SmokeCheck(MotionId, Phase, Motion), loud: true, restore: () => ScheduleRestore(MotionId));
            SmokeRegistry.Register(
                new SmokeCheck(LookId, Phase, Look), loud: true, restore: () => ScheduleRestore(LookId));
            SmokeRegistry.Register(
                new SmokeCheck(RestoreId, Phase, Restore), loud: true, restore: () => ScheduleRestore(RestoreId));
        }

        /// <summary>The generated table is embedded, parses, and is the real one.</summary>
        private static SmokeCheckResult Table()
        {
            if (!SatoneTable.Loaded)
            {
                return SmokeCheckResult.Fail(TableId, SatoneTable.LoadError ?? "the table did not load");
            }

            var facials = SatoneTable.Facials.Count;
            var motions = SatoneTable.Motions.Count;
            if (facials < MinFacials || motions < MinMotions)
            {
                return SmokeCheckResult.Fail(
                    TableId,
                    $"{facials} facials / {motions} motions is below the floor "
                    + $"({MinFacials}/{MinMotions}); is satone-table.json stale?");
            }

            // A name the API documents has to actually resolve, or the table is the wrong shape.
            if (!SatoneTable.TryFacial(FaceName, out var face) || face is null
                || !SatoneTable.TryMotion(MotionRotation[0], out var motion) || motion is null)
            {
                return SmokeCheckResult.Fail(
                    TableId, $"'{FaceName}' or '{MotionRotation[0]}' is missing from the table");
            }

            return SmokeCheckResult.Pass(
                TableId,
                $"{facials} facials, {motions} motions, {SatoneTable.States.Count} states "
                + $"(schema {SatoneTable.Catalog.Schema}); {face} and {motion} resolve");
        }

        /// <summary>A named expression reaches the Animator's <c>Facial</c> parameter.</summary>
        private static SmokeCheckResult Face()
        {
            if (Skip(FaceId, out var skipped, out var heroine))
            {
                return skipped!;
            }

            if (!SatoneTable.TryFacial(FaceName, out var facial) || facial is null)
            {
                return SmokeCheckResult.Fail(FaceId, $"'{FaceName}' is not in the table");
            }

            var before = SatoneSeam.ReadInteger(heroine, SatoneSeam.FacialParameter);
            var accepted = false;
            try
            {
                accepted = Satone.Face(FaceName);
                if (!accepted)
                {
                    return SmokeCheckResult.Fail(FaceId, $"Satone.Face(\"{FaceName}\") was refused; see the SATONE log lines");
                }

                Arm(FaceId, RestoreScope.Face);

                var after = SatoneSeam.ReadInteger(heroine, SatoneSeam.FacialParameter);
                if (after is null)
                {
                    // The read-back is a diagnostic, not the feature: say so instead of failing.
                    return SmokeCheckResult.Pass(
                        FaceId,
                        $"Satone.Face(\"{FaceName}\") -> ChangeHeroineFacialAnimation({facial.Id}) accepted; "
                        + "no Animator read-back to confirm it (" + SatoneSeam.AnimatorSeamDetail + "); "
                        + Scheduled());
                }

                if (after.Value != facial.Id)
                {
                    return SmokeCheckResult.Fail(
                        FaceId,
                        $"Facial is {after.Value} after asking for {facial.Name} ({facial.Id}); "
                        + $"it was {Show(before)} before");
                }

                return SmokeCheckResult.Pass(
                    FaceId,
                    $"Facial {Show(before)} -> {after.Value} = {facial.Name} "
                    + $"[{facial.Meshes}] via clip {facial.Clip}; {Scheduled()}. {Previous()}");
            }
            finally
            {
                // The game has no automatic facial reset - the smile state has no outgoing
                // transition at all - so whatever happens above, she is handed back here.
                ScheduleRestore(FaceId);
            }
        }

        /// <summary>A named motion reaches the game's own animation state.</summary>
        private static SmokeCheckResult Motion()
        {
            if (Skip(MotionId, out var skipped, out var heroine))
            {
                return skipped!;
            }

            var index = _motionIndex++ % MotionRotation.Length;
            var name = MotionRotation[index];
            if (!SatoneTable.TryMotion(name, out var motion) || motion is null)
            {
                return SmokeCheckResult.Fail(MotionId, $"'{name}' is not in the table");
            }

            var before = (int)heroine.GetCurrentAnimationType();
            var accepted = false;
            try
            {
                accepted = Satone.Motion(name);
                if (!accepted)
                {
                    return SmokeCheckResult.Fail(MotionId, $"Satone.Motion(\"{name}\") was refused; see the SATONE log lines");
                }

                Arm(MotionId, RestoreScope.Motion);

                var after = (int)heroine.GetCurrentAnimationType();
                if (after != motion.Id)
                {
                    return SmokeCheckResult.Fail(
                        MotionId,
                        $"the game still reports motion {before} after asking for {motion.Name} ({motion.Id}); "
                        + "ChangeAnimation did not take");
                }

                var parameter = SatoneSeam.ReadInteger(heroine, SatoneSeam.MotionParameter);
                var readBack = parameter is null
                    ? "no Animator read-back"
                    : SatoneSeam.MotionParameter + "=" + parameter.Value.ToString(CultureInfo.InvariantCulture);

                return SmokeCheckResult.Pass(
                    MotionId,
                    $"[{index + 1}/{MotionRotation.Length}] motion {before} -> {after} = {motion.Name} "
                    + $"(state {motion.State}, clip {motion.Clip}, {readBack}); "
                    + $"the animator is watched for {SatoneMotionFeature.ConfirmSeconds:0.#} s; "
                    + $"{Scheduled()}. {Previous()}");
            }
            finally
            {
                // The AI would heal this itself after a random delay serialized in the prefab;
                // re-dispatching her state is the deterministic way.
                ScheduleRestore(MotionId);
            }
        }

        /// <summary>The look seam is reachable and accepts a request.</summary>
        private static SmokeCheckResult Look()
        {
            if (Skip(LookId, out var skipped, out var heroine))
            {
                return skipped!;
            }

            var before = SatoneSeam.ReadFloat(heroine, SatoneSeam.LookParameter);
            var accepted = false;
            try
            {
                accepted = Satone.LookAt(LookScale, LookSeconds);
                if (!accepted)
                {
                    return SmokeCheckResult.Fail(LookId, "Satone.LookAt was refused; see the SATONE log lines");
                }

                Arm(LookId, RestoreScope.Gaze);

                // LookScale is tweened by DOTween over LookSeconds, so it has not moved yet. What
                // this proves now is that the reflected seam bound and the call went through; the
                // value is confirmed asynchronously and reported by the next pass.
                return SmokeCheckResult.Pass(
                    LookId,
                    $"LookAt({LookScale:0.##}, {LookSeconds:0.##}s) accepted; {SatoneSeam.LookSeamDetail}; "
                    + $"LookScale was {Show(before)}; {Scheduled()}. {Previous()}");
            }
            finally
            {
                // Nothing in the game resets LookScale on its own.
                ScheduleRestore(LookId);
            }
        }

        /// <summary>
        /// End to end: change her expression, hand her back, and read the result. The verdict is
        /// necessarily asynchronous - the game's own reset waits
        /// <c>MyDefine.InitFacialDelaySecondsAfterTalk</c> - so this pass arms the probe and the
        /// next pass reports what it saw.
        /// </summary>
        private static SmokeCheckResult Restore()
        {
            var previous = _restoreVerdict;
            _restoreVerdict = null;

            if (Skip(RestoreId, out var skipped, out _))
            {
                if (previous is null)
                {
                    return skipped!;
                }

                // A pass that cannot arm a new probe must still report the last verdict honestly:
                // "she is stuck smiling" is exactly what this check exists to catch.
                return previous.Ok
                    ? SmokeCheckResult.Pass(RestoreId, skipped!.Detail + " " + previous.Describe())
                    : SmokeCheckResult.Fail(RestoreId, skipped!.Detail + " " + previous.Describe());
            }

            var before = Satone.Capture();
            var accepted = false;
            try
            {
                accepted = Satone.Face(FaceName);
                if (!accepted)
                {
                    return SmokeCheckResult.Fail(
                        RestoreId, $"Satone.Face(\"{FaceName}\") was refused; see the SATONE log lines");
                }

                Arm(RestoreId, RestoreScope.All);

                var armed = $"armed: {before}, face {FaceName}, restore in "
                            + $"{RestoreAfterSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s, "
                            + $"verdict {RestoreVerdictSeconds.ToString("0.##", CultureInfo.InvariantCulture)} s later";

                if (previous is null)
                {
                    return SmokeCheckResult.NotYetObserved(
                        RestoreId, "no verdict from an earlier pass yet; " + armed);
                }

                return previous.Ok
                    ? SmokeCheckResult.Pass(RestoreId, previous.Describe() + "; " + armed)
                    : SmokeCheckResult.Fail(RestoreId, previous.Describe() + "; " + armed);
            }
            finally
            {
                if (accepted)
                {
                    ScheduleRestore(RestoreId);
                    SatoneMotionHost.PostAfter(
                        RestoreAfterSeconds + RestoreVerdictSeconds,
                        RestoreId + " verdict",
                        () => RecordVerdict(before));
                }
            }
        }

        /// <summary>Reads back what the restore did; runs on the main thread from the host.</summary>
        private static void RecordVerdict(SatoneSnapshot before)
        {
            var now = Satone.Capture();
            var restore = SatoneMotionFeature.LastRestore;

            if (!now.Valid)
            {
                _restoreVerdict = new RestoreVerdict(
                    false, "the room scope went away before the verdict could be taken");
                return;
            }

            // The game's normal is Facial 0 (HeroineFacialController.InitFacial, :73-77); the
            // snapshot's own value is accepted too, because that is what "back where she was"
            // means when she was not neutral to begin with.
            var facial = now.FacialId;
            var facialOk = !facial.HasValue
                           || facial.Value == 0
                           || (before.HasFacial && facial.Value == before.FacialId!.Value);

            // This check only moves the expression, so her motion must be exactly where it was -
            // unless the restore re-dispatched her state, which puts her on that state's base loop.
            var motionOk = !now.HasMotion
                           || !before.HasMotion
                           || now.MotionId!.Value == before.MotionId!.Value
                           || (restore != null && restore.MotionRedispatched);

            var ok = facialOk && motionOk;
            var detail =
                "before [" + before + "] -> now [" + now + "]"
                + "; restore " + (restore is null ? "never ran" : restore.Describe());

            if (!facialOk)
            {
                detail += "; the expression never went back"
                          + (facial.HasValue ? string.Empty : " (no Animator read-back)");
            }

            if (!motionOk)
            {
                detail += "; the body motion is not on her state's loop";
            }

            _restoreVerdict = new RestoreVerdict(ok, detail);
        }

        /// <summary>
        /// Notes that a check has just changed something, so exactly one restore is owed for it.
        /// </summary>
        /// <param name="id">The check.</param>
        /// <param name="scope">Which channels it changed.</param>
        private static void Arm(string id, RestoreScope scope) => Armed[id] = scope;

        /// <summary>
        /// Hands the channels a loud check touched back to the game, a moment later so the
        /// observation watcher still sees the change first.
        /// </summary>
        /// <param name="id">The check, as armed by <see cref="Arm"/>.</param>
        /// <remarks>
        /// Idempotent per pass: disarming first means the check's own <c>finally</c> and the
        /// registry's <see cref="SmokeCheck.Restore"/> hook - which both call this - schedule one
        /// restore between them, and a check that never changed anything schedules none.
        /// </remarks>
        private static void ScheduleRestore(string id)
        {
            if (!Armed.TryGetValue(id, out var scope))
            {
                return;
            }

            Armed.Remove(id);
            SatoneMotionHost.PostAfter(
                RestoreAfterSeconds,
                id + " restore",
                () => Satone.RestoreDefaults(scope));
        }

        /// <summary>The phrase every loud check's detail carries, so the report proves the restore ran.</summary>
        private static string Scheduled() =>
            "restore scheduled in "
            + RestoreAfterSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";

        /// <summary>One pass's asynchronous verdict, read by the next pass.</summary>
        private sealed class RestoreVerdict
        {
            internal RestoreVerdict(bool ok, string detail)
            {
                Ok = ok;
                Detail = detail;
            }

            internal bool Ok { get; }

            internal string Detail { get; }

            internal string Describe() => (Ok ? "restored: " : "NOT restored: ") + Detail;
        }

        /// <summary>
        /// Common guard: the feature is up, the room is loaded, and Satone can be interrupted.
        /// </summary>
        /// <param name="id">Check id for the result.</param>
        /// <param name="result">The result to return when this returns true.</param>
        /// <param name="heroine">The live service; non-null exactly when this returns false.</param>
        /// <returns>True when the caller should return <paramref name="result"/> as-is.</returns>
        private static bool Skip(
            string id,
            out SmokeCheckResult? result,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out global::Bulbul.HeroineService? heroine)
        {
            heroine = null;
            result = null;

            if (!SatoneMotionFeature.Initialized)
            {
                result = SmokeCheckResult.Fail(id, "SatoneMotionFeature.Initialize was never called");
                return true;
            }

            if (!SatoneMotionFeature.Enabled)
            {
                result = SmokeCheckResult.Pass(
                    id,
                    "skipped: "
                    + SatoneMotionFeature.ConfigSection + "." + SatoneMotionFeature.ConfigKeyEnabled + "=false");
                return true;
            }

            if (!SatoneSeam.TryHeroine(out heroine) || heroine is null)
            {
                result = SmokeCheckResult.Pass(
                    id,
                    "skipped: HeroineService is not resolvable "
                    + (SatoneSeam.IsRoomLoaded ? "although RoomScene is loaded" : "(RoomScene is not loaded)"));
                return true;
            }

            if (!heroine.IsPossibleTalk())
            {
                var state = Satone.State;
                result = SmokeCheckResult.Pass(
                    id,
                    $"skipped: Satone cannot be interrupted right now (state {state}, "
                    + $"motion {Satone.CurrentMotion})");
                heroine = null;
                return true;
            }

            return false;
        }

        /// <summary>What the previous pass's request turned into, for the detail line.</summary>
        private static string Previous()
        {
            var observation = SatoneMotionHost.LastObservation;
            return observation is null ? "no earlier request observed" : "previous: " + observation;
        }

        private static string Show(int? value) =>
            value is null ? "?" : value.Value.ToString(CultureInfo.InvariantCulture);

        private static string Show(float? value) =>
            value is null ? "?" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
