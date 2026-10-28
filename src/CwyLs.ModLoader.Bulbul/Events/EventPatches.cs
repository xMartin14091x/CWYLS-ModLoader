using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using UnityEngine;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// The static half of the bus: every Harmony patch that turns something the game does into an
    /// event. Static on purpose - in this game the BepInEx plugin component is destroyed about
    /// 0.2 s after startup, and these have to outlive it.
    /// </summary>
    /// <remarks>
    /// Harmony parameters are named positionally (<c>__0</c>, <c>__1</c>) rather than by the game's
    /// parameter names, so a build that ships without parameter metadata, or an update that renames
    /// an argument, cannot silently stop a patch from binding.
    /// </remarks>
    internal static class EventPatches
    {
        /// <summary>Reflection name of the voice manager, which lives in AudioManager.dll.</summary>
        internal const string VoiceManagerType = "KanKikuchi.AudioManager.VoiceManager";

        /// <summary>Simple name of the third-party assembly that declares the voice manager.</summary>
        internal const string VoiceManagerAssembly = "AudioManager";

        /// <summary>Minimum gap between two <see cref="SaveSection"/> events for the same section.</summary>
        internal const long SaveThrottleMs = 250L;

        /// <summary>
        /// The four <c>ScenarioReader</c> commands that read a master-data row and then call
        /// <c>HeroineService.PlayVoice</c>. All four
        /// are private <c>async UniTask</c> methods taking the row; a prefix on the outer stub sees
        /// the argument before the state machine starts.
        /// </summary>
        internal static readonly string[] ScenarioRowCommands =
        {
            "CommandText",
            "CommandVoiceText",
            "CommandPomodoroVoiceText",
            "CommandTextLoopPlayText"
        };

        /// <summary>Trace <c>sys</c> of the file-only line written for a suppressed scenario end.</summary>
        internal const string ScenarioTraceSystem = "scenario";

        /// <summary>Trace event name of the file-only line written for a suppressed scenario end.</summary>
        internal const string EndSuppressedEvent = "endSuppressed";

        /// <summary>How many game frames the suppressed-end diagnostic names as the caller of <c>EndStory</c>.</summary>
        private const int CallerFrames = 2;

        private static readonly EventThrottle SaveThrottle = new EventThrottle(SaveThrottleMs);

        /// <summary>One <see cref="ScenarioEnded"/> per <see cref="ScenarioStarted"/>; see <see cref="ScenarioEndTracker"/>.</summary>
        private static readonly ScenarioEndTracker ScenarioEnds = new ScenarioEndTracker();

        /// <summary>
        /// True once the <c>StartReady</c> postfix is in place. Without it the tracker would never see
        /// a start and would suppress every end, so ends are then published unfiltered, as they were before the tracker existed.
        /// </summary>
        private static bool _scenarioStartsTracked;
        private static readonly List<string> AppliedPatches = new List<string>();
        private static readonly List<string> FailedPatches = new List<string>();
        private static IDisposable? _saveSubscription;

        /// <summary>Descriptions of the patches that were applied, for the startup summary.</summary>
        internal static IReadOnlyList<string> Applied => AppliedPatches;

        /// <summary>Descriptions of the patches that could not be applied; every one was logged.</summary>
        internal static IReadOnlyList<string> Failed => FailedPatches;

        /// <summary>
        /// How many <c>SaveDataManager.Save*</c> methods feed <see cref="SaveSection"/> through the
        /// shared <see cref="SaveDataHook"/>.
        /// </summary>
        internal static int SaveMethodCount => SaveDataHook.SaveMethodCount;

        /// <summary>
        /// Applies every patch. Each one is applied independently, so a seam a game update removed
        /// costs only its own event.
        /// </summary>
        /// <param name="harmony">The plugin's Harmony instance.</param>
        /// <exception cref="ArgumentNullException"><paramref name="harmony"/> is null.</exception>
        internal static void Apply(Harmony harmony)
        {
            if (harmony is null)
            {
                throw new ArgumentNullException(nameof(harmony));
            }

            // Satone's state machine.
            Patch(
                harmony,
                AccessTools.Method(typeof(HeroineAI), "ChangeState", new[] { typeof(HeroineAI.ActionStateType) }),
                "HeroineAI.ChangeState",
                prefix: nameof(ChangeStatePrefix),
                postfix: nameof(ChangeStatePostfix));

            // The voice path, top to bottom. The two PlayVoice patches only record intent; the
            // clip really starts in VoiceManager.Play, which is where the line is published.
            Patch(
                harmony,
                AccessTools.Method(
                    typeof(Game.HeroineService), "PlayVoice",
                    new[] { typeof(string), typeof(bool), typeof(bool) }),
                "HeroineService.PlayVoice",
                postfix: nameof(PlayVoiceRequestPostfix));

            Patch(
                harmony,
                AccessTools.Method(
                    typeof(HeroineVoiceController), "PlayVoice",
                    new[] { typeof(string), typeof(bool), typeof(bool) }),
                "HeroineVoiceController.PlayVoice",
                postfix: nameof(PlayVoiceRequestPostfix));

            PatchVoiceManager(harmony);

            // Scenario boundaries, which also supply every line's context.
            _scenarioStartsTracked = Patch(
                harmony,
                AccessTools.Method(
                    typeof(Game.ScenarioReader), "StartReady",
                    new[]
                    {
                        typeof(Game.MasterData.ScenarioType),
                        typeof(string),
                        typeof(Game.ScenarioReader.ScenarioReadMode),
                        typeof(bool),
                        typeof(bool),
                        typeof(bool)
                    }),
                "ScenarioReader.StartReady",
                postfix: nameof(StartReadyPostfix));

            Patch(
                harmony,
                AccessTools.Method(typeof(Game.ScenarioReader), "EndStory"),
                "ScenarioReader.EndStory",
                postfix: nameof(EndStoryPostfix));

            PatchScenarioRowCommands(harmony);

            // Window views. ChangeWindowView/ChangeWeatherAndTime switch one object on and the
            // rest of their family off; the event reports the one that came on.
            PatchWindow(harmony, "ChangeWindowView", nameof(WindowOnPostfix));
            PatchWindow(harmony, "ChangeWeatherAndTime", nameof(WindowOnPostfix));
            PatchWindow(harmony, "ActivateWindow", nameof(WindowOnPostfix));
            PatchWindow(harmony, "DeactivateWindow", nameof(WindowOffPostfix));

            // The fallback route to the two services whose observables the bus subscribes to. The
            // primary route is a VContainer resolve; whichever arrives first wins, and the bus logs
            // which one it was.
            Patch(
                harmony,
                AccessTools.Method(typeof(Game.PomodoroService), "Setup"),
                "PomodoroService.Setup",
                postfix: nameof(PomodoroSetupPostfix));

            Patch(
                harmony,
                AccessTools.Method(typeof(global::MusicService), "Setup"),
                "MusicService.Setup",
                postfix: nameof(MusicSetupPostfix));

            PatchSaveMethods(harmony);
        }

        /// <summary>Harmony prefix: remembers the state Satone is actually in before the dispatch.</summary>
        /// <param name="__instance">The HeroineAI being changed.</param>
        /// <param name="__state">Carries the previous state to the postfix.</param>
        internal static void ChangeStatePrefix(HeroineAI __instance, out int __state)
        {
            // Read here rather than trusting GetBeforeState() afterwards: the state machine only
            // updates BeforeStateKey when a transition matches (MyUtil.StateMachine), so a
            // rejected transition would otherwise report a stale "previous".
            __state = (int)__instance.GetCurrentState();
        }

        /// <summary>Harmony postfix: publishes <see cref="SatoneStateChanged"/>.</summary>
        /// <param name="__0">The requested next state.</param>
        /// <param name="__state">The state captured by the prefix.</param>
        internal static void ChangeStatePostfix(HeroineAI.ActionStateType __0, int __state)
        {
            var next = (int)__0;
            CWYEvents.Publish(
                new SatoneStateChanged(
                    ((HeroineAI.ActionStateType)__state).ToString(), __0.ToString(), __state, next));
        }

        /// <summary>
        /// Harmony postfix on both <c>PlayVoice</c> layers: records the caller's intent for the
        /// clip that is about to start.
        /// </summary>
        /// <param name="__0">Voice clip name.</param>
        /// <param name="__1">Whether the mouth-talk motion was requested.</param>
        /// <param name="__2">Whether story priority was requested.</param>
        internal static void PlayVoiceRequestPostfix(string __0, bool __1, bool __2) =>
            SatoneSpeech.NoteRequest(__0, __1, __2);

        /// <summary>
        /// Harmony postfix on <c>VoiceManager.Play</c>: the one point a voice clip actually starts.
        /// </summary>
        /// <param name="__instance">
        /// The voice manager. Typed <c>object</c> because AudioManager.dll is not referenced at
        /// compile time; it is only used to read the private clip cache.
        /// </param>
        /// <param name="__0">The clip name.</param>
        internal static void VoicePlayPostfix(object __instance, string __0) =>
            SatoneSpeech.BeginLine(__instance, __0, Time.unscaledTime);

        /// <summary>
        /// Harmony prefix on each of the four <see cref="ScenarioRowCommands"/>: records the row
        /// the reader is about to speak, so the clip it starts is attributed to that row rather
        /// than to whatever row happens to share its voice name.
        /// </summary>
        /// <param name="__0">The <c>NovelData</c> row being read.</param>
        internal static void ScenarioRowPrefix(Master.NovelData __0)
        {
            try
            {
                // A null row is possible only if the game changes shape; treat it as "no hint"
                // rather than letting an NRE escape into the reader's own call stack.
                SatoneSpeech.NoteScenarioRow(__0?.LocalizationID, __0?.ScenarioGroupID, Time.frameCount);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT could not record the scenario row: {ex.GetType().Name}: {ex.Message}. "
                    + "The line will be attributed by voice name instead.");
            }
        }

        /// <summary>Harmony postfix: publishes <see cref="ScenarioStarted"/> and opens the context.</summary>
        /// <param name="__0">Scenario type.</param>
        /// <param name="__1">Scenario group id.</param>
        /// <param name="__2">Read mode.</param>
        internal static void StartReadyPostfix(
            Game.MasterData.ScenarioType __0, string __1, Game.ScenarioReader.ScenarioReadMode __2)
        {
            var groupId = __1 ?? string.Empty;
            ScenarioEnds.Start(groupId);
            SatoneSpeech.EnterScenario(__0.ToString(), groupId);
            CWYEvents.Publish(new ScenarioStarted(__0.ToString(), groupId, __2.ToString()));
        }

        /// <summary>
        /// Harmony postfix: publishes <see cref="ScenarioEnded"/> and closes the context - once per
        /// started scenario. A repeated or stray <c>EndStory</c> is written to the trace file as
        /// <c>scenario/endSuppressed</c> and goes no further.
        /// </summary>
        /// <param name="__instance">The reader whose scenario ended.</param>
        internal static void EndStoryPostfix(Game.ScenarioReader __instance)
        {
            // Release() does not clear the playing group id, so it is
            // still readable here and is the most reliable answer to "which scenario ended?". The
            // same fact is why a second EndStory() reports the last scenario again: the game calls
            // it twice at game start (HeroineStateGameStartDirection, one frame after the
            // reader ended the greeting itself) and unconditionally from the exit button
            // (UIManagerForPC). See ScenarioEndTracker.
            var groupId = __instance.PlayingScenarioGroupID;

            if (_scenarioStartsTracked)
            {
                var decision = ScenarioEnds.End(groupId);
                if (!decision.Publish)
                {
                    // Not SatoneSpeech.ExitScenario() either: the context may already belong to a
                    // scenario that started since, and this end is not its end.
                    NoteSuppressedEnd(__instance, decision);
                    return;
                }
            }

            SatoneSpeech.ExitScenario();
            CWYEvents.Publish(new ScenarioEnded(string.IsNullOrEmpty(groupId) ? null : groupId));
        }

        /// <summary>
        /// Forgets which scenarios are open. Called when the room scene unloads: its reader is a
        /// room-scoped singleton (registered by RoomRegister), so nothing it started can end afterwards.
        /// </summary>
        internal static void ForgetScenarios() => ScenarioEnds.Forget();

        /// <summary>
        /// Files the evidence for a suppressed end - which id, why, and which game code called
        /// <c>EndStory</c> - in the trace file only. The console keeps its one end per scenario.
        /// </summary>
        private static void NoteSuppressedEnd(Game.ScenarioReader reader, ScenarioEndDecision decision)
        {
            try
            {
                ConsoleEvents.File(
                    ScenarioTraceSystem,
                    EndSuppressedEvent,
                    ("groupId", decision.GroupId),
                    ("reason", decision.Reason),
                    ("scenarioType", reader.PlayingScenarioType.ToString()),
                    ("caller", EndStoryCaller()),
                    ("suppressed", ScenarioEnds.Suppressed));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not record a suppressed ScenarioEnded ({decision.GroupId}, {decision.Reason}): "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// The game methods that called <c>EndStory</c>, innermost first, e.g.
        /// <c>Bulbul.UIManagerForPC.OnClickExitButton</c>. Walks the stack only on the suppressed
        /// path, which the game reaches a handful of times per session.
        /// </summary>
        /// <returns>Up to <see cref="CallerFrames"/> frames joined by <c> &lt; </c>, or null.</returns>
        private static string? EndStoryCaller()
        {
            try
            {
                var frames = new StackTrace(1, false).GetFrames();
                if (frames is null)
                {
                    return null;
                }

                var sb = new StringBuilder();
                var taken = 0;
                foreach (var frame in frames)
                {
                    var method = frame?.GetMethod();
                    var type = method?.DeclaringType;

                    // Skip Harmony's replacement of EndStory itself (a dynamic method, often with
                    // no declaring type) and this class.
                    if (method is null
                        || type is null
                        || type == typeof(EventPatches)
                        || method.Name.IndexOf("EndStory", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }

                    if (sb.Length > 0)
                    {
                        sb.Append(" < ");
                    }

                    sb.Append(type.FullName).Append('.').Append(method.Name);
                    if (++taken >= CallerFrames)
                    {
                        break;
                    }
                }

                return sb.Length == 0 ? null : sb.ToString();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not name the caller of a repeated EndStory: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Harmony postfix: hands the pomodoro service to the bridge.</summary>
        /// <param name="__instance">The service the game just set up.</param>
        internal static void PomodoroSetupPostfix(Game.PomodoroService __instance) =>
            EventBridge.CaptureFromPatch(__instance);

        /// <summary>Harmony postfix: hands the music service to the bridge.</summary>
        /// <param name="__instance">The service the game just set up.</param>
        internal static void MusicSetupPostfix(global::MusicService __instance) =>
            EventBridge.CaptureFromPatch(__instance);

        /// <summary>Harmony postfix: a window view was switched on.</summary>
        /// <param name="__0">The window view type.</param>
        internal static void WindowOnPostfix(Game.WindowViewType __0) =>
            CWYEvents.Publish(new WindowChanged(__0.ToString(), true));

        /// <summary>Harmony postfix: a window view was switched off.</summary>
        /// <param name="__0">The window view type.</param>
        internal static void WindowOffPostfix(Game.WindowViewType __0) =>
            CWYEvents.Publish(new WindowChanged(__0.ToString(), false));

        /// <summary>
        /// The event bus's listener on the shared <see cref="SaveDataHook"/>. Publishes the section
        /// a <c>Save*</c> method wrote, throttled because the game saves in bursts. <c>Delete*</c>
        /// calls are the data bus's business and are ignored here.
        /// </summary>
        /// <param name="call">The hooked call.</param>
        internal static void OnSaved(SaveCall call)
        {
            try
            {
                if (!SaveMethodRules.IsSave(call.MethodName))
                {
                    return;
                }

                var section = SaveMethodRules.EventSection(call.MethodName);
                if (SaveThrottle.TryPass(section))
                {
                    CWYEvents.Publish(new SaveSection(section));
                }
            }
            catch (Exception ex)
            {
                // Never throw into the game's save path.
                CwyLsModLoader.Log.LogError(
                    $"EVENT save listener failed for {call.MethodName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void PatchWindow(Harmony harmony, string methodName, string postfix) =>
            Patch(
                harmony,
                AccessTools.Method(
                    typeof(Game.WindowViewService), methodName, new[] { typeof(Game.WindowViewType) }),
                "WindowViewService." + methodName,
                postfix: postfix);

        private static void PatchScenarioRowCommands(Harmony harmony)
        {
            foreach (var name in ScenarioRowCommands)
            {
                Patch(
                    harmony,
                    AccessTools.Method(
                        typeof(Game.ScenarioReader), name, new[] { typeof(Master.NovelData) }),
                    "ScenarioReader." + name,
                    prefix: nameof(ScenarioRowPrefix));
            }
        }

        private static void PatchVoiceManager(Harmony harmony)
        {
            var type = FindVoiceManager();
            if (type is null)
            {
                Fail("VoiceManager.Play", "type '" + VoiceManagerType + "' not found");
                return;
            }

            var play = AccessTools.Method(
                type,
                "Play",
                new[]
                {
                    typeof(string), typeof(float), typeof(float), typeof(float),
                    typeof(bool), typeof(Action), typeof(string), typeof(Transform)
                });

            Patch(harmony, play, "VoiceManager.Play", postfix: nameof(VoicePlayPostfix));
        }

        /// <summary>
        /// Finds the voice manager, loading AudioManager.dll if nothing has touched it yet. Mono
        /// loads a game assembly lazily, and at plugin Awake this one is often still unloaded - a
        /// domain scan alone would then report the seam as gone when it is merely asleep.
        /// </summary>
        private static Type? FindVoiceManager()
        {
            var type = AccessTools.TypeByName(VoiceManagerType);
            if (type != null)
            {
                return type;
            }

            try
            {
                return Assembly.Load(new AssemblyName(VoiceManagerAssembly)).GetType(VoiceManagerType, false);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not load {VoiceManagerAssembly}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Installs the shared save hook (one postfix per save/delete method) and listens to it.
        /// The data bus listens to the same hook, so a save is observed once for both buses.
        /// </summary>
        private static void PatchSaveMethods(Harmony harmony)
        {
            try
            {
                SaveDataHook.Install(harmony);
            }
            catch (Exception ex)
            {
                Fail("SaveDataManager.Save*", ex.GetType().Name + ": " + ex.Message);
                return;
            }

            foreach (var name in SaveDataHook.Failures)
            {
                // The hook logged each one already; a failed Save* method is also a failed event
                // site, which is what AllSeamsOk and p1.events.ready report.
                if (SaveMethodRules.IsSave(name))
                {
                    FailedPatches.Add("SaveDataManager." + name);
                }
            }

            _saveSubscription ??= SaveDataHook.Subscribe(OnSaved);
            AppliedPatches.Add("SaveDataManager.Save* x" + SaveDataHook.SaveMethodCount);
        }

        /// <summary>Applies one patch; a missing target or a Harmony failure is logged, never thrown.</summary>
        /// <returns>True when the patch was applied.</returns>
        private static bool Patch(
            Harmony harmony, MethodBase? target, string description, string? prefix = null, string? postfix = null)
        {
            if (target is null)
            {
                Fail(description, "method not found");
                return false;
            }

            try
            {
                harmony.Patch(
                    target,
                    prefix: prefix is null ? null : new HarmonyMethod(typeof(EventPatches), prefix),
                    postfix: postfix is null ? null : new HarmonyMethod(typeof(EventPatches), postfix));
                AppliedPatches.Add(description);
                return true;
            }
            catch (Exception ex)
            {
                Fail(description, ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static void Fail(string description, string detail)
        {
            FailedPatches.Add(description);
            CwyLsModLoader.Log.LogError(
                "EVENT could not patch " + description + ": " + detail
                + " - the events it feeds will not be published");
        }
    }
}
