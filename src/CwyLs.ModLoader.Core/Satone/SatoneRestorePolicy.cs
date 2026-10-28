using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// Decides whether Satone's current AI state may be re-dispatched to put her body motion back
    /// on its base loop - the idiom the game itself uses after a click reaction
    /// (<c>HeroineAI.FinishHeroineClickReaction</c>, whose Work branch re-dispatches the current
    /// action state).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why an allow-list and not just a skip-list.</b> <c>StateMachineMonobehavior.Dispatch</c>
    /// re-enters the state it is already in - <c>Exit</c> then <c>Enter</c>
    /// - so re-dispatching runs the state's <c>OnEnter</c> again. That is exactly what we want for
    /// a work or break state, whose <c>OnEnter</c> sets a looping base motion (e.g.
    /// <c>HeroineStateWorkPC</c>, <c>HeroineStateBreakMovie</c>) and whose <c>OnUpdate</c> already
    /// pushes foreign motions back to it (<c>HeroineStateWorkPC</c>). It is wrong for every other
    /// state: the wild idles and the desk-to-desk transitions replay a one-shot clip
    /// (e.g. <c>HeroineStateWildGuts</c>, <c>HeroineStateFromPcToBook</c>), and the directed
    /// states restart a whole sequence.
    /// </para>
    /// <para>
    /// The allow-list is exactly <c>HeroineAI.GetCurrentStateActionType</c>'s <c>Work</c> and
    /// <c>Break</c> members minus the three <c>LeaveChair*</c> states, which
    /// that method also counts as <c>Break</c> but whose <c>OnEnter</c> starts her walking away
    /// (<c>HeroineStateLeaveChairGoSofa</c>, <c>HeroineStateLeaveChairGoFar</c>).
    /// </para>
    /// <para>
    /// Names are compared loosely, so both spellings of the same state work: the table's
    /// <c>work_pc</c> and the game's own enum member <c>WorkPC</c>.
    /// </para>
    /// </remarks>
    public static class SatoneRestorePolicy
    {
        /// <summary>
        /// The AI states whose <c>OnEnter</c> only re-arms a self-healing base loop, so
        /// re-dispatching them is a restore rather than a new performance.
        /// </summary>
        public static readonly IReadOnlyList<string> Redispatchable = new[]
        {
            "work_pc",
            "work_book",
            "work_report",
            "break_movie",
            "break_read_book",
            "break_listen_music",
            "break_tea_time",
            "break_sleep",
            "break_forward"
        };

        /// <summary>
        /// The states that must never be re-dispatched, each with the reason a restore reports.
        /// A state that is in neither list is refused too, with the generic reason.
        /// </summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> Directed = new[]
        {
            new KeyValuePair<string, string>(
                "none",
                "the AI has not dispatched a state yet"),
            new KeyValuePair<string, string>(
                "game_start_direction",
                "re-entering restarts the opening direction (HeroineStateGameStartDirection)"),
            new KeyValuePair<string, string>(
                "want_talk",
                "re-entering re-fires OnStartWantTalk and re-shows the bubble (HeroineWantTalk)"),
            new KeyValuePair<string, string>(
                "main_story",
                "a story owns her; the AI is not even ticked while it plays (RoomGameManager)"),
            new KeyValuePair<string, string>(
                "click_heroine",
                "the game leaves this state through FinishHeroineClickReaction (HeroineAI)"),
            new KeyValuePair<string, string>(
                "leave_chair_preparing",
                "re-entering restarts her leaving the chair (HeroineAI counts it as Break)"),
            new KeyValuePair<string, string>(
                "leave_chair_go_sofa",
                "re-entering restarts the walk to the sofa (HeroineStateLeaveChairGoSofa)"),
            new KeyValuePair<string, string>(
                "leave_chair_go_far",
                "re-entering restarts the walk to the kitchen (HeroineStateLeaveChairGoFar)"),
            new KeyValuePair<string, string>(
                "game_end_talk",
                "re-entering restarts the end-of-session talk (HeroineGameEndTalk)"),
            new KeyValuePair<string, string>(
                "event_new_year_countdown",
                "re-entering restarts the countdown event (HeroineStateEventNewYearCountdown)")
        };

        /// <summary>The reason given for a state that is in neither list.</summary>
        public const string NotSelfHealingReason =
            "re-entering it replays a one-shot clip instead of restoring a loop";

        /// <summary>The reason given when the state name is empty or was never resolved.</summary>
        public const string UnknownStateReason =
            "her AI state could not be named, so re-dispatching it is not safe";

        /// <summary>
        /// Whether the body motion may be restored by re-dispatching <paramref name="stateName"/>.
        /// </summary>
        /// <param name="stateName">
        /// The state, in either spelling: the table's <c>work_pc</c> or the game's <c>WorkPC</c>.
        /// Null, empty or an <c>unknown_*</c> placeholder answers false.
        /// </param>
        /// <param name="reason">
        /// Why not, when this returns false; <see cref="string.Empty"/> when it returns true.
        /// Never null - it is printed in the restore line.
        /// </param>
        /// <returns>True when re-dispatching the state restores rather than performs.</returns>
        public static bool ShouldRedispatch(string? stateName, out string reason)
        {
            var key = Normalize(stateName);
            if (key.Length == 0 || key.StartsWith("unknown", StringComparison.Ordinal))
            {
                reason = UnknownStateReason;
                return false;
            }

            foreach (var name in Redispatchable)
            {
                if (string.Equals(key, Normalize(name), StringComparison.Ordinal))
                {
                    reason = string.Empty;
                    return true;
                }
            }

            foreach (var pair in Directed)
            {
                if (string.Equals(key, Normalize(pair.Key), StringComparison.Ordinal))
                {
                    reason = pair.Value;
                    return false;
                }
            }

            reason = NotSelfHealingReason;
            return false;
        }

        /// <summary>
        /// True when <paramref name="stateName"/> is one of the named directed sequences (as
        /// opposed to merely not being re-dispatchable).
        /// </summary>
        /// <param name="stateName">The state, in either spelling.</param>
        public static bool IsDirected(string? stateName)
        {
            var key = Normalize(stateName);
            if (key.Length == 0)
            {
                return false;
            }

            foreach (var pair in Directed)
            {
                if (string.Equals(key, Normalize(pair.Key), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The comparison form of a state name: lower case with separators removed, so
        /// <c>work_pc</c>, <c>WorkPC</c> and <c>Work PC</c> are one key. Never null.
        /// </summary>
        /// <param name="stateName">A state name, or null.</param>
        public static string Normalize(string? stateName)
        {
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return string.Empty;
            }

            var buffer = new char[stateName!.Length];
            var length = 0;
            foreach (var c in stateName)
            {
                if (c == '_' || c == '-' || c == ' ')
                {
                    continue;
                }

                buffer[length++] = char.ToLowerInvariant(c);
            }

            return new string(buffer, 0, length);
        }
    }
}
