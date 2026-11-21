using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.Satone.Motion;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// "A dub is never cut off" (DS-03 Must 7): the game must not move on from a line, close its text
    /// box or end its scene while a dub is still playing, in any language and on every line kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every voiced line of the game is a row of <c>ScenarioReader</c> (story, click reactions through
    /// <c>FacilityClickHeroine.OneWord</c>, self-talk, pomodoro and timer lines, the game-start greeting,
    /// pack lines, <c>Satone.Say</c> and Pack Maker's Play). The reader waits for the voice before it
    /// moves on - except its <c>VoiceText</c> and <c>PomodoroVoiceText</c> rows, which give up after 30 s
    /// - and then calls <c>AutoNext(waitSeconds)</c>, after which the next voiced row stops the voice and
    /// the end of the scene closes the box. Three seams cover it:
    /// </para>
    /// <list type="number">
    /// <item><b><c>AutoNext</c> prefix.</b> When the clip that played is not the game's own
    /// (<see cref="MakerDubTiming.IsReplacement"/>, from <c>VoicePlayback</c>, which records the clip the
    /// lookup really returned - Pack Maker's live swaps and its Play pin included) and the voice still
    /// plays, the wait is raised to the clip's end + 0.3 s (<see cref="MakerDubTiming.HoldSeconds"/>). It
    /// runs next to <c>SubtitleHold</c>'s prefix on the same method: both only ever raise the wait, so the
    /// longer of the two wins. <c>SubtitleHold</c> covers pack languages and <c>Satone.Say</c> reading
    /// time; this covers a dub in every language, on wordless rows too.</item>
    /// <item><b>The game-start greeting</b> (<c>HeroineStateGameStartDirection</c>, local function
    /// <c>EndSpeak</c>) ends the story after at most 20 s. A transpiler routes that 20 through
    /// <see cref="GreetingCap"/>, which multiplies it by <see cref="MakerDubTiming.MaxDubRatio"/> while a dub
    /// can play.</item>
    /// <item><b>The timer-end talk</b> (<c>TimerCoreService.StartTalk</c>, local function
    /// <c>ForceFinishStoryForLeave</c>) ends the story after 15 s; the same for that 15 through
    /// <see cref="LeaveTalkCap"/>.</item>
    /// </list>
    /// <para>
    /// Both transpilers patch only when the exact instruction pattern is found once (checked against
    /// game 1.18.0); otherwise they change nothing and <see cref="Detail"/> says so. Off with
    /// <c>[Maker] DubHold = false</c>. A click, a skip, leaving the room or quitting still ends a line
    /// at once: those are the player's, not a timer's.
    /// </para>
    /// </remarks>
    internal static class MakerDubHold
    {
        /// <summary>Config section.</summary>
        internal const string ConfigSection = "Maker";

        /// <summary>Config key of the switch.</summary>
        internal const string ConfigKeyEnabled = "DubHold";

        private const string TimerCoreTypeName = "Bulbul.TimerCoreService";
        private const string GreetingTypeName = "HeroineStateGameStartDirection";
        private const string LeaveMarker = "g__ForceFinishStoryForLeave|";
        private const string GreetingMarker = "g__EndSpeak|";
        private const double LeaveCapSeconds = 15d;
        private const int GreetingCapSeconds = 20;
        private const int MaxLoggedErrors = 3;

        private static ConfigEntry<bool>? _enabled;
        private static bool _initialized;
        private static int _errors;
        private static string _leave = "not attempted";
        private static string _greeting = "not attempted";
        private static string _autoNext = "not attempted";

        /// <summary>What the transpiler that just ran decided (Harmony runs it inside <c>Patch</c>).</summary>
        private static string _pendingResult = string.Empty;

        /// <summary>True when the hold is on (<c>[Maker] DubHold</c>, default true).</summary>
        internal static bool Enabled => _enabled?.Value ?? true;

        /// <summary>How many line waits were raised for a dub this session.</summary>
        internal static long Holds { get; private set; }

        /// <summary>The last raise, for the console; empty before one.</summary>
        internal static string LastHold { get; private set; } = string.Empty;

        /// <summary>What is patched, in one line.</summary>
        internal static string Detail => "autoNext " + _autoNext + "; greeting " + _greeting + "; timer talk " + _leave;

        /// <summary>Binds the switch and applies the three patches. Idempotent.</summary>
        /// <param name="config">The adapter's config file, or null for the default (on).</param>
        internal static void Initialize(ConfigFile? config)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _enabled = config?.Bind(
                ConfigSection,
                ConfigKeyEnabled,
                true,
                "Never cut a dub off: while a clip that is not the game's own still plays, the game waits for it "
                + "(+0.3 s) before the next line or the end of the scene, and the greeting / timer-talk time limits "
                + "are raised while dubs can play. Off keeps the game's own timing.");

            if (!Enabled)
            {
                _autoNext = _greeting = _leave = "off (" + ConfigSection + "." + ConfigKeyEnabled + "=false)";
                return;
            }

            var harmony = new Harmony(BulbulPlugin.PluginGuid + ".maker");
            _autoNext = ApplyAutoNext(harmony);

            // By name, so a game update that renames either class costs that one limit, not the editor.
            _leave = ApplyCap(harmony, TimerCoreTypeName, LeaveMarker, nameof(LeaveTranspiler));
            _greeting = ApplyCap(harmony, GreetingTypeName, GreetingMarker, nameof(GreetingTranspiler));
        }

        /// <summary>The greeting's limit, scaled while dubs can play. Called from the game's transpiled code.</summary>
        /// <param name="seconds">The game's limit (20).</param>
        public static int GreetingCap(int seconds) => SafeCap(seconds, "greeting");

        /// <summary>The timer-end talk's limit, scaled while dubs can play. Called from the game's transpiled code.</summary>
        /// <param name="seconds">The game's limit (15).</param>
        public static double LeaveTalkCap(double seconds)
        {
            try
            {
                var capped = MakerDubTiming.ScaledCap(seconds, Enabled && DubsInForce());
                Trace("timerTalkCap", seconds, capped);
                return capped;
            }
            catch (Exception ex)
            {
                Error("timer talk cap", ex);
                return seconds;
            }
        }

        /// <summary>
        /// True while a dub can play: a pack ships a voice set for the language the game shows now (read
        /// from the game's setting, so it is right before the voice switch has noticed), or Pack Maker
        /// swapped a take in this session.
        /// </summary>
        private static bool DubsInForce() =>
            VoiceFeature.Enabled && LanguageVoices.Enabled
            && (LanguageVoices.HasSet(LanguageFeature.CurrentCode) || LanguageVoices.LiveSwapCount > 0);

        private static int SafeCap(int seconds, string what)
        {
            try
            {
                var capped = MakerDubTiming.ScaledCap(seconds, Enabled && DubsInForce());
                Trace(what + "Cap", seconds, capped);
                return capped;
            }
            catch (Exception ex)
            {
                Error(what + " cap", ex);
                return seconds;
            }
        }

        private static void Trace(string name, double game, double used)
        {
            if (Math.Abs(game - used) > 0.001d)
            {
                ConsoleEvents.File(MakerEditor.TraceSys, name, ("gameSeconds", game), ("usedSeconds", used), ("lang", LanguageVoices.ActiveCode));
            }
        }

        private static string ApplyAutoNext(Harmony harmony)
        {
            try
            {
                var autoNext = AccessTools.Method(typeof(Game.ScenarioReader), "AutoNext", new[] { typeof(float) });
                if (autoNext is null)
                {
                    CwyLsModLoader.Log.LogError(MakerEditor.LogPrefix + " dub hold: PATCH TARGET MISSING: Bulbul.ScenarioReader.AutoNext(float)");
                    return "missing";
                }

                harmony.Patch(autoNext, prefix: new HarmonyMethod(typeof(MakerDubHold), nameof(AutoNextPrefix)));
                return "prefixed";
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(MakerEditor.LogPrefix + " dub hold: could not patch AutoNext: " + ex.GetType().Name + ": " + ex.Message);
                return "failed";
            }
        }

        /// <summary>
        /// Harmony prefix on <c>ScenarioReader.AutoNext(float waitSeconds)</c>: raises the wait while a
        /// replacement clip still plays. Runs in the async method's stub, before the argument is copied
        /// into its state machine (as <c>SubtitleHold</c>'s does).
        /// </summary>
        private static void AutoNextPrefix(ref float waitSeconds)
        {
            if (!Enabled)
            {
                return;
            }

            try
            {
                if (!VoicePlayback.TryLast(out var name, out var length, out var ago, out var choice)
                    || !MakerDubTiming.IsReplacement(choice))
                {
                    return;
                }

                var playing = SatoneSeam.TryHeroine(out var heroine) && heroine != null && !heroine.IsFinishedVoice;
                var hold = MakerDubTiming.HoldSeconds(ago, length, playing);
                var raised = MakerDubTiming.Raise(waitSeconds, hold);
                if (raised <= waitSeconds)
                {
                    return;
                }

                LastHold = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} ({1}, {2:0.0} s, playing {3}): wait {4:0.00} s -> {5:0.00} s",
                    name,
                    choice,
                    length,
                    playing,
                    waitSeconds,
                    raised);
                ConsoleEvents.File(
                    MakerEditor.TraceSys,
                    "dubHold",
                    ("voice", name),
                    ("choice", choice),
                    ("clipSeconds", Math.Round(length, 2)),
                    ("sinceStart", Math.Round(ago, 2)),
                    ("playing", playing),
                    ("gameWait", Math.Round(waitSeconds, 2)),
                    ("wait", Math.Round(raised, 2)));
                waitSeconds = (float)raised;
                Holds++;
            }
            catch (Exception ex)
            {
                // Never break the reader: log and leave the game's wait.
                Error("hold", ex);
            }
        }

        /// <summary>Finds the compiler-made state machine of a local async function and transpiles its MoveNext.</summary>
        private static string ApplyCap(Harmony harmony, string ownerName, string marker, string transpiler)
        {
            try
            {
                var owner = AccessTools.TypeByName(ownerName);
                if (owner is null)
                {
                    CwyLsModLoader.Log.LogWarning(MakerEditor.LogPrefix + " dub hold: PATCH TARGET MISSING: " + ownerName + "; its time limit is left as the game has it");
                    return "missing";
                }

                var found = new List<Type>();
                Collect(owner, marker, found);
                if (found.Count != 1)
                {
                    CwyLsModLoader.Log.LogWarning(
                        MakerEditor.LogPrefix + " dub hold: " + owner.Name + " has " + found.Count + " state machine(s) named *" + marker
                        + "*; its time limit is left as the game has it");
                    return "not found";
                }

                var moveNext = AccessTools.Method(found[0], "MoveNext");
                if (moveNext is null)
                {
                    return "no MoveNext";
                }

                _pendingResult = "pattern not found";
                harmony.Patch(moveNext, transpiler: new HarmonyMethod(typeof(MakerDubHold), transpiler));
                if (_pendingResult != "patched")
                {
                    CwyLsModLoader.Log.LogWarning(
                        MakerEditor.LogPrefix + " dub hold: " + found[0].Name + ": " + _pendingResult + "; its time limit is left as the game has it");
                }

                return _pendingResult;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    MakerEditor.LogPrefix + " dub hold: could not patch " + ownerName + "'s time limit: " + ex.GetType().Name + ": " + ex.Message);
                return "failed";
            }
        }

        private static void Collect(Type type, string marker, List<Type> found)
        {
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (nested.Name.IndexOf(marker, StringComparison.Ordinal) >= 0 && typeof(IAsyncStateMachine).IsAssignableFrom(nested))
                {
                    found.Add(nested);
                }

                Collect(nested, marker, found);
            }
        }

        /// <summary>
        /// <c>ldc.r8 15; call TimeSpan.FromSeconds(double)</c> becomes
        /// <c>ldc.r8 15; call LeaveTalkCap(double); call TimeSpan.FromSeconds(double)</c>.
        /// </summary>
        private static IEnumerable<CodeInstruction> LeaveTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var fromSeconds = AccessTools.Method(typeof(TimeSpan), nameof(TimeSpan.FromSeconds), new[] { typeof(double) });
            var at = -1;
            var matches = 0;
            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R8 && list[i].operand is double value && Math.Abs(value - LeaveCapSeconds) < 1e-9
                    && list[i + 1].Calls(fromSeconds))
                {
                    matches++;
                    at = i;
                }
            }

            if (matches != 1)
            {
                _pendingResult = "found " + matches + " ldc.r8 15 -> TimeSpan.FromSeconds (expected 1)";
                return list;
            }

            list.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MakerDubHold), nameof(LeaveTalkCap))));
            _pendingResult = "patched";
            return list;
        }

        /// <summary>
        /// <c>ldc.i4.s 20 … call UniTask.WaitForSeconds(int, …)</c> becomes
        /// <c>ldc.i4.s 20; call GreetingCap(int) … call UniTask.WaitForSeconds(int, …)</c>.
        /// </summary>
        private static IEnumerable<CodeInstruction> GreetingTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = new List<CodeInstruction>(instructions);
            var at = -1;
            var matches = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].LoadsConstant(GreetingCapSeconds))
                {
                    continue;
                }

                // The int overload's first argument; the other arguments are loaded in between.
                for (var j = i + 1; j < list.Count && j <= i + 8; j++)
                {
                    if (list[j].operand is MethodInfo method && method.Name == "WaitForSeconds"
                        && method.DeclaringType?.Name == "UniTask")
                    {
                        var parameters = method.GetParameters();
                        if (parameters.Length > 0 && parameters[0].ParameterType == typeof(int))
                        {
                            matches++;
                            at = i;
                        }

                        break;
                    }
                }
            }

            if (matches != 1)
            {
                _pendingResult = "found " + matches + " 20 -> UniTask.WaitForSeconds(int, ...) (expected 1)";
                return list;
            }

            list.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MakerDubHold), nameof(GreetingCap))));
            _pendingResult = "patched";
            return list;
        }

        private static void Error(string what, Exception ex)
        {
            _errors++;
            var message = MakerEditor.LogPrefix + " dub hold " + what + " failed (" + _errors + "): " + ex.GetType().Name + ": " + ex.Message;
            if (_errors <= MaxLoggedErrors)
            {
                CwyLsModLoader.Log.LogWarning(message);
            }
            else
            {
                CwyLsModLoader.Log.LogDebug(message);
            }
        }
    }
}
