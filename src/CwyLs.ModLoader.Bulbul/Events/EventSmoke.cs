using System.Globalization;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// The Phase-1 smoke checks for the event bus: did the bus come up, and has each family of
    /// events actually been seen this session.
    /// </summary>
    /// <remarks>
    /// The counters are the bus's own publish counts, which run for the whole session, so a second
    /// smoke run after exercising a feature turns its check green without restarting the game. A
    /// check that needs the player to do something says so in its failure detail, and names the
    /// ways a run can be triggered (<see cref="TriggerHint"/>) rather than assuming a keyboard
    /// where F9 is free.
    /// </remarks>
    internal static class EventSmoke
    {
        private const string Phase = "P1";

        /// <summary>
        /// How a failure detail tells the reader to run the checks again: the hotkey, its Mac
        /// alternate, and the trigger file a test script can touch without a keyboard at all.
        /// </summary>
        private const string TriggerHint =
            "trigger a smoke run (F9 / Ctrl+Shift+S or the BepInEx/smoke.run file)";

        /// <summary>Registers every event check. Idempotent - the registry replaces by id.</summary>
        internal static void Register()
        {
            SmokeRegistry.Register(new SmokeCheck("p1.events.ready", Phase, Ready));
            SmokeRegistry.Register(new SmokeCheck("p1.events.scene", Phase, Scene));
            SmokeRegistry.Register(new SmokeCheck("p1.events.room_ready", Phase, Room));
            SmokeRegistry.Register(new SmokeCheck("p1.events.satone_state", Phase, SatoneState));
            SmokeRegistry.Register(new SmokeCheck("p1.events.music", Phase, Music));
            SmokeRegistry.Register(new SmokeCheck("p1.events.speech_detail", Phase, SpeechDetail));
            SmokeRegistry.Register(new SmokeCheck("p1.events.attribution", Phase, Attribution));
            SmokeRegistry.Register(new SmokeCheck("p1.events.pomodoro_wired", Phase, PomodoroWired));
            SmokeRegistry.Register(new SmokeCheck("p1.events.pomodoro_seen", Phase, PomodoroSeen));
        }

        private static SmokeCheckResult Ready()
        {
            const string id = "p1.events.ready";

            if (!CWYEvents.IsReady)
            {
                return SmokeCheckResult.Fail(id, "the bus did not finish starting; see the EVENT lines above");
            }

            var targets = EventBus.Targets;
            if (targets is null)
            {
                return SmokeCheckResult.Fail(id, "patch targets were never checked");
            }

            if (!targets.AllFound)
            {
                return SmokeCheckResult.Fail(
                    id, targets.MissingCount + " patch target(s) missing: " + string.Join("; ", Lines(targets)));
            }

            if (EventPatches.Failed.Count > 0)
            {
                return SmokeCheckResult.Fail(
                    id, EventPatches.Failed.Count + " patch site(s) failed: " + Join(EventPatches.Failed));
            }

            return SmokeCheckResult.Pass(
                id,
                $"{targets.OkCount} targets ok, {EventPatches.Applied.Count} patch sites, "
                + $"{EventPatches.SaveMethodCount} save methods, {CWYEvents.TotalPublished} events published");
        }

        private static SmokeCheckResult Scene()
        {
            const string id = "p1.events.scene";
            var seen = CWYEvents.CountOf<SceneChanged>();

            return EventHost.RoomSceneSeen
                ? SmokeCheckResult.Pass(id, $"{EventHost.SceneName} seen; {seen} scene event(s)")
                : SmokeCheckResult.Fail(id, $"no SceneChanged for {EventHost.SceneName} yet ({seen} scene event(s))");
        }

        private static SmokeCheckResult Room()
        {
            const string id = "p1.events.room_ready";
            var count = CWYEvents.CountOf<RoomReady>();

            return count > 0L
                ? SmokeCheckResult.Pass(id, count + " RoomReady event(s)")
                : SmokeCheckResult.Fail(
                    id,
                    EventHost.RoomSceneSeen
                        ? "RoomScene loaded but its services are still not resolvable"
                        : "RoomScene has not loaded yet");
        }

        private static SmokeCheckResult SatoneState()
        {
            const string id = "p1.events.satone_state";
            var count = CWYEvents.CountOf<SatoneStateChanged>();

            return count > 0L
                ? SmokeCheckResult.Pass(id, count + " state change(s)")
                : SmokeCheckResult.NotYetObserved(id, "not yet observed; " + TriggerHint + " later");
        }

        private static SmokeCheckResult Music()
        {
            const string id = "p1.events.music";
            var count = CWYEvents.CountOf<MusicChanged>();

            return count > 0L
                ? SmokeCheckResult.Pass(id, count + " track change(s) via " + EventBridge.MusicPath)
                : SmokeCheckResult.Fail(
                    id,
                    EventBridge.MusicAttached
                        ? "music not started"
                        : "music not started, and MusicService is not attached yet");
        }

        private static SmokeCheckResult SpeechDetail()
        {
            const string id = "p1.events.speech_detail";
            var lines = CWYEvents.CountOf<LineSpoken>();

            if (CWYEvents.DetailedLineCount > 0L)
            {
                return SmokeCheckResult.Pass(
                    id,
                    $"{CWYEvents.DetailedLineCount}/{lines} line(s) fully described "
                    + $"(language {SatoneSpeech.Language}, {SatoneSpeech.IndexedVoiceCount} voices indexed)"
                    + Last());
            }

            var why = lines == 0L
                ? "Satone has not spoken yet"
                : "no spoken line carried text, Japanese text and a recognised source";

            return SmokeCheckResult.Fail(
                id,
                $"{why} ({SatoneSpeech.IndexedVoiceCount} voices indexed, language source: "
                + SatoneSpeech.LanguageSource + ")" + Last());
        }

        /// <summary>
        /// A line the scenario reader spoke is attributed to the reader's own row, and no line
        /// was published twice.
        /// </summary>
        /// <remarks>
        /// Scripted lines - the tutorial, a pomodoro announcement, a click reaction, a
        /// <c>Satone.Say</c> - must come back as <see cref="LineAttribution.Row"/>. Lines attributed
        /// by voice name are fine and expected (a mod's clip, a TTS line, an idle voice), so the
        /// check only fails when a scenario has run and <em>not one</em> of its lines was named by
        /// its row, or when a duplicate was suppressed.
        /// </remarks>
        private static SmokeCheckResult Attribution()
        {
            const string id = "p1.events.attribution";

            var rows = SatoneSpeech.RowAttributedLines;
            var index = SatoneSpeech.VoiceIndexAttributedLines;
            var detail =
                $"{rows} line(s) named by the reader's own row, {index} by the voice index, "
                + $"{SatoneSpeech.CollidingVoiceNames} clip name(s) claimed by more than one row"
                + Last();

            if (SatoneSpeech.SuppressedRepeats > 0L)
            {
                return SmokeCheckResult.Fail(
                    id,
                    $"{SatoneSpeech.SuppressedRepeats} duplicate LineSpoken publish(es) were dropped - "
                    + "one line was published twice; " + detail);
            }

            if (rows > 0L)
            {
                return SmokeCheckResult.Pass(id, detail);
            }

            if (CWYEvents.CountOf<ScenarioStarted>() == 0L)
            {
                return SmokeCheckResult.NotYetObserved(
                    id, "no scenario has played yet; " + TriggerHint + " after she speaks. " + detail);
            }

            return SmokeCheckResult.Fail(
                id,
                "a scenario played but no line was attributed to the row it was reading - the "
                + "ScenarioReader.Command* prefixes are not capturing rows. " + detail);
        }

        private static SmokeCheckResult PomodoroWired()
        {
            const string id = "p1.events.pomodoro_wired";

            return EventBridge.PomodoroAttached
                ? SmokeCheckResult.Pass(id, "PomodoroService observables attached via " + EventBridge.PomodoroPath)
                : SmokeCheckResult.Fail(
                    id,
                    "PomodoroService observables not attached after "
                    + EventBridge.Attempts.ToString(CultureInfo.InvariantCulture) + " attempt(s)");
        }

        private static SmokeCheckResult PomodoroSeen()
        {
            const string id = "p1.events.pomodoro_seen";
            var events = CWYEvents.CountOf<PomodoroEvent>();
            var ticks = CWYEvents.CountOf<PomodoroTick>();

            return events > 0L
                ? SmokeCheckResult.Pass(id, events + " pomodoro event(s), " + ticks + " tick(s)")
                : SmokeCheckResult.NotYetObserved(id, "not exercised: start a pomodoro, then " + TriggerHint);
        }

        /// <summary>The last spoken line, appended to a detail line; empty when there is none.</summary>
        private static string Last()
        {
            var line = CWYEvents.LastLine;
            return line is null ? string.Empty : "; last: " + line.Describe();
        }

        private static string[] Lines(CwyLs.ModLoader.Core.PatchTargetReport report)
        {
            var missing = report.MissingLines();
            var copy = new string[missing.Count];
            for (var i = 0; i < missing.Count; i++)
            {
                copy[i] = missing[i];
            }

            return copy;
        }

        private static string Join(System.Collections.Generic.IReadOnlyList<string> values)
        {
            var copy = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return string.Join(", ", copy);
        }
    }
}
