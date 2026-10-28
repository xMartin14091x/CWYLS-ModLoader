using System.Globalization;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The Phase-2 <c>Satone.Say</c> checks: proof, in the running game, that a brand-new line
    /// reaches the game's own scenario reader - carrying a translation for the active pack
    /// language when there is one - and that a second line while the game is busy is queued rather
    /// than forced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both checks are <see cref="SmokeCheck.Loud"/>: they make Satone speak, so an
    /// automatic pass reports them as skipped and only an explicit trigger - the smoke hotkey or
    /// <c>BepInEx/smoke.run</c> - runs them. Both also refuse to interrupt her: when
    /// <c>HeroineService.IsPossibleTalk()</c> is false they pass with a "skipped" detail.
    /// </para>
    /// <para>
    /// A check cannot see its own verdict. The clip starts a frame later and runs for seconds, so
    /// <see cref="ScenarioId"/> asserts what is true immediately - the request was accepted and
    /// the rows are in the game's master data - and reports the <em>previous</em> pass's
    /// <see cref="SatoneSayFeature.LastObservation"/>, which is where "the voice started; the text
    /// seam asked for [code] and got the line's translation" appears. Same pattern as the Satone
    /// motion checks.
    /// </para>
    /// <para>
    /// Language-neutral: the translation is attached for whatever pack language the game is showing
    /// (<see cref="LanguageFeature.Active"/>), as <see cref="PackTranslation"/> of the sample line,
    /// so the text box proves it came from the line's own table rather than a fallback column. In
    /// one of the game's own languages no translation is attached and the box shows the row's text.
    /// </para>
    /// </remarks>
    internal static class SatoneSaySmokeChecks
    {
        /// <summary>Phase label these checks are reported under.</summary>
        internal const string Phase = "P2";

        /// <summary>Id of the scenario-path check.</summary>
        internal const string ScenarioId = "p2.say.scenario";

        /// <summary>Id of the busy-policy check.</summary>
        internal const string FallbackId = "p2.say.fallback";

        /// <summary>The line the scenario check speaks - the same sentence the example pack ships.</summary>
        internal const string SampleText = "Hello from the Mod Loader.";

        /// <summary>
        /// The clip the example pack registers (<c>audio/voice/Voice_CWY_Test.wav</c>). The check
        /// borrows the <em>clip</em>, never the name: passing it as
        /// <see cref="SayOptions.Clip"/> makes the line register it under its own generated
        /// <c>CWY_Say_&lt;n&gt;</c> alias (<see cref="SayIdGenerator.VoiceName"/>).
        /// <para>
        /// Testing found the old behaviour - reusing the name on every Say line - collided twice over:
        /// once with <c>p1.voice.play</c>, which plays the same clip in the same loud pass and made
        /// one Say line look like two spoken lines; and once with the example pack's own
        /// <c>cwy_example_01_001</c> row, which claims the same <c>Voice</c> column and so won the
        /// reverse voice index. A per-line name removes both.
        /// </para>
        /// </summary>
        internal const string SampleVoice = "Voice_CWY_Test";

        /// <summary>The expression the sample line is performed with.</summary>
        internal const string SampleFacial = "smile";

        /// <summary>The body motion the sample line is performed with.</summary>
        internal const string SampleMotion = "wild001_good";

        /// <summary>The text of the queue-policy line, which is never actually spoken.</summary>
        internal const string QueueProbeText = "(queue probe)";

        private static SayHandle? _lastScenario;
        private static SayHandle? _lastProbe;

        /// <summary>Registers both checks, loud. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(
                new SmokeCheck(ScenarioId, Phase, Scenario), loud: true, restore: RestoreScenario);
            SmokeRegistry.Register(
                new SmokeCheck(FallbackId, Phase, Fallback), loud: true, restore: RestoreProbe);
        }

        /// <summary>
        /// The registry's undo for <see cref="ScenarioId"/>. A line that is going to speak is
        /// <em>not</em> cancelled - speaking is the whole point of the check, and
        /// <c>SatoneSayFeature</c> hands Satone back itself when the scenario ends. This only
        /// covers the case where the line will never speak, and therefore will never restore.
        /// </summary>
        private static void RestoreScenario()
        {
            var handle = _lastScenario;
            _lastScenario = null;

            if (handle is null)
            {
                return;
            }

            if (!handle.IsCompleted || handle.Outcome == SayOutcome.Spoken)
            {
                // Still queued, speaking, or already spoken: its own end restores her.
                CwyLsModLoader.Log.LogDebug(
                    "SMOKE " + ScenarioId + " left the restore to the line itself (" + handle.Outcome + ")");
                return;
            }

            // Rejected, cancelled or timed out after the performance had been applied.
            Satone.RestoreDefaults(RestoreScope.All);
        }

        /// <summary>
        /// The registry's undo for <see cref="FallbackId"/>: take the probe line back out of
        /// the queue. The check already does this inline; this is what makes it exception-safe.
        /// </summary>
        private static void RestoreProbe()
        {
            var handle = _lastProbe;
            _lastProbe = null;

            if (handle is null || handle.IsCompleted)
            {
                return;
            }

            if (handle.Cancel())
            {
                CwyLsModLoader.Log.LogDebug("SMOKE " + FallbackId + " cancelled the probe line in its restore");
            }
        }

        /// <summary>
        /// The translation the scenario check attaches for a pack language: the sample line tagged
        /// with the code, e.g. <c>[th] Hello from the Mod Loader.</c>. Only the line's own
        /// text table can supply it, so seeing it in the box proves that path.
        /// </summary>
        /// <param name="code">The pack language's code.</param>
        internal static string PackTranslation(string code) => "[" + code + "] " + SampleText;

        /// <summary>
        /// A brand-new line goes to the game's scenario reader, with a translation for the active
        /// pack language when the game is showing one.
        /// </summary>
        private static SmokeCheckResult Scenario()
        {
            if (Skip(ScenarioId, out var skipped))
            {
                return skipped!;
            }

            // Read before the new request replaces it: this is the previous pass's verdict.
            var previous = Describe(SatoneSayFeature.LastObservation);

            // Borrow the clip, not the name: Satone.Say then generates CWY_Say_<n> for this line
            // and registers the clip under it, so nothing else in the pass claims the same name.
            var hasClip = VoiceFeature.TryGet(SampleVoice, out var clip);
            var options = new SayOptions
            {
                Clip = hasClip ? clip : null,
                Facial = SampleFacial,
                Motion = SampleMotion,
            };

            // Read once: the player could switch language between here and the assertion below.
            var packCode = LanguageFeature.Active?.Code;
            if (!string.IsNullOrEmpty(packCode))
            {
                options.Texts[packCode!] = PackTranslation(packCode!);
            }

            var handle = Satone.Say(SampleText, options);
            _lastScenario = handle;
            var line = handle.Line;

            if (handle.Outcome == SayOutcome.Rejected)
            {
                return SmokeCheckResult.Fail(ScenarioId, "Satone.Say was refused: " + handle.Detail);
            }

            if (!SatoneSayRows.Install(line, out var problem))
            {
                return SmokeCheckResult.Fail(
                    ScenarioId, "the line was accepted but its rows could not be written: " + problem);
            }

            if (!SatoneSayRows.Contains(line))
            {
                return SmokeCheckResult.Fail(
                    ScenarioId,
                    "the rows for " + line.GroupId + " are not in MasterDataLoader after writing them; "
                    + SatoneSayRows.LastProblem);
            }

            string language;
            if (string.IsNullOrEmpty(packCode))
            {
                language = "no pack language active ('" + LanguageFeature.CurrentCode
                    + "'), so the box shows the row's own text";
            }
            else if (!line.TryText(packCode, out var translation))
            {
                return SmokeCheckResult.Fail(
                    ScenarioId, "the line carries no [" + packCode + "] translation although one was attached");
            }
            else
            {
                language = packCode + "=\"" + translation + "\"";
            }

            if (line.FacialId == SayLine.NoMotion || line.MotionId == SayLine.NoMotion)
            {
                return SmokeCheckResult.Fail(
                    ScenarioId,
                    "'" + SampleFacial + "' or '" + SampleMotion + "' did not resolve in the P2-3 table "
                    + "(facial " + line.FacialId + ", motion " + line.MotionId + ")");
            }

            var voice = hasClip
                ? "voice " + line.VoiceName + " (the example clip " + SampleVoice + " under this line's own name)"
                : "no voice: " + SampleVoice + " is not in our registry, so the line is text only";

            return SmokeCheckResult.Pass(
                ScenarioId,
                "queued " + line + " with " + language + "; " + voice
                + "; rows in MasterDataLoader ("
                + SatoneSayRows.LiveCount + " live line(s), "
                + (SatoneSayFeature.IsSpeaking ? "speaking now" : SatoneSayFeature.WaitingCount + " waiting")
                + "). Previous: " + previous);
        }

        /// <summary>
        /// The busy policy: a line asked for while the game is busy waits its turn instead of
        /// interrupting, and can be taken back out of the queue.
        /// </summary>
        /// <remarks>
        /// The probe line is cancelled rather than spoken, so this check never puts a second
        /// sentence on screen; what it proves is the admission and the cancellation, plus the
        /// configured fallback policy and the session's counters.
        /// </remarks>
        private static SmokeCheckResult Fallback()
        {
            if (Skip(FallbackId, out var skipped))
            {
                return skipped!;
            }

            var busy = SatoneSayFeature.BlockedReason;
            var waitingBefore = SatoneSayFeature.WaitingCount;

            var handle = Satone.Say(QueueProbeText);
            _lastProbe = handle;

            if (handle.Outcome == SayOutcome.Rejected)
            {
                return SmokeCheckResult.Fail(FallbackId, "the probe line was refused: " + handle.Detail);
            }

            if (SatoneSayFeature.WaitingCount != waitingBefore + 1)
            {
                return SmokeCheckResult.Fail(
                    FallbackId,
                    "the probe line was accepted but the queue went from " + waitingBefore + " to "
                    + SatoneSayFeature.WaitingCount + " waiting");
            }

            var cancelled = handle.Cancel();
            _lastProbe = null;
            var policy = SatoneSayFeature.FallbackWhenBusy
                ? "falls back after " + SatoneSayFeature.MaxWaitSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"
                : "waits indefinitely (" + SatoneSayFeature.ConfigSection + "."
                    + SatoneSayFeature.ConfigKeyFallbackWhenBusy + "=false)";

            var state = string.IsNullOrEmpty(busy)
                ? "the game was free"
                : "the game was busy: " + busy;

            if (!cancelled)
            {
                // It started before the check could take it back - harmless (it is a one-word
                // line) and still proof that the queue moves.
                return SmokeCheckResult.Pass(
                    FallbackId,
                    "the probe line started before it could be cancelled, so the queue is live; "
                    + state + "; policy: " + policy + "; " + Counters());
            }

            return SmokeCheckResult.Pass(
                FallbackId,
                "queued behind the game and cancelled again without speaking; " + state
                + "; policy: " + policy + "; " + Counters());
        }

        private static string Counters() =>
            SatoneSayFeature.SpokenCount + " spoken, " + SatoneSayFeature.FallbackCount
            + " fell back, " + SatoneSayFeature.RejectedCount + " refused, "
            + SatoneSayRows.LiveCount + " line(s) with rows";

        /// <summary>
        /// Common guard: the feature is up, the room is loaded, and Satone can be interrupted.
        /// </summary>
        /// <param name="id">Check id for the result.</param>
        /// <param name="result">The result to return when this returns true.</param>
        /// <returns>True when the caller should return <paramref name="result"/> as-is.</returns>
        private static bool Skip(string id, out SmokeCheckResult? result)
        {
            result = null;

            if (!SatoneSayFeature.Initialized)
            {
                result = SmokeCheckResult.Fail(id, "SatoneSayFeature.Initialize was never called");
                return true;
            }

            if (!SatoneSayFeature.Enabled)
            {
                result = SmokeCheckResult.Pass(
                    id,
                    "skipped: " + SatoneSayFeature.ConfigSection + "."
                    + SatoneSayFeature.ConfigKeyEnabled + "=false");
                return true;
            }

            if (!SatoneSeam.TryHeroine(out var heroine) || heroine is null)
            {
                result = SmokeCheckResult.Pass(
                    id,
                    "skipped: HeroineService is not resolvable "
                    + (SatoneSeam.IsRoomLoaded ? "although RoomScene is loaded" : "(RoomScene is not loaded)"));
                return true;
            }

            if (!heroine.IsPossibleTalk())
            {
                result = SmokeCheckResult.Pass(
                    id,
                    "skipped: Satone cannot be interrupted right now (" + SatoneSayFeature.BlockedReason + ")");
                return true;
            }

            if (!SatoneSayRows.IsReady(out var reason))
            {
                result = SmokeCheckResult.Pass(id, "skipped: " + reason);
                return true;
            }

            return false;
        }

        private static string Describe(SayObservation? observation) =>
            observation is null ? "no earlier line observed" : observation.ToString();
    }
}
