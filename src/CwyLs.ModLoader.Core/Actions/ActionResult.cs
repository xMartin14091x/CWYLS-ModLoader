using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>How an action call ended. Values are explicit and never renumbered (the API reports them).</summary>
    public enum ActionStatus
    {
        /// <summary>It ran and did what it says.</summary>
        Ok = 0,

        /// <summary>It ran and failed (the game refused, a handler threw).</summary>
        Failed = 1,

        /// <summary>The arguments do not fit the action's parameters.</summary>
        InvalidArgs = 2,

        /// <summary>No action has this name.</summary>
        NotFound = 3,

        /// <summary>The gate refused it (tier, caller or a config switch); nothing ran.</summary>
        Denied = 4,

        /// <summary>The player said no in the in-game confirmation, or the confirmation could not be shown; nothing ran.</summary>
        Declined = 5,

        /// <summary>The main thread did not run it in time; it will not run late.</summary>
        TimedOut = 6,

        /// <summary>The action system (or the action's backend) is not installed.</summary>
        NotAvailable = 7,
    }

    /// <summary>Which way an action reached the game (semantic call first, UI click as fallback).</summary>
    public enum ActionPath
    {
        /// <summary>Nothing reached the game (refused, not found, not available).</summary>
        None = 0,

        /// <summary>Through the game's own service or presenter method.</summary>
        Semantic = 1,

        /// <summary>By clicking the game's own UI control.</summary>
        UiClick = 2,
    }

    /// <summary>The outcome of one action call: status, path, a message for people and optional data. Immutable.</summary>
    public sealed class ActionResult
    {
        private static readonly IReadOnlyDictionary<string, object?> NoData = new Dictionary<string, object?>();

        private ActionResult(ActionStatus status, ActionPath path, string message, IReadOnlyDictionary<string, object?>? data)
        {
            Status = status;
            Path = path;
            Message = message ?? string.Empty;
            Data = data ?? NoData;
        }

        /// <summary>How it ended.</summary>
        public ActionStatus Status { get; }

        /// <summary>Which way it reached the game.</summary>
        public ActionPath Path { get; }

        /// <summary>One line for people (<c>playing "Rain"</c>, <c>writes are off</c>).</summary>
        public string Message { get; }

        /// <summary>Structured results for callers (the API returns them as JSON). Never null.</summary>
        public IReadOnlyDictionary<string, object?> Data { get; }

        /// <summary>True only for <see cref="ActionStatus.Ok"/>.</summary>
        public bool IsOk => Status == ActionStatus.Ok;

        /// <summary>It ran and worked.</summary>
        /// <param name="message">What happened.</param>
        /// <param name="path">Which way (normally <see cref="ActionPath.Semantic"/>).</param>
        /// <param name="data">Structured results, or null.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is <see cref="ActionPath.None"/>.</exception>
        public static ActionResult Ok(string message, ActionPath path = ActionPath.Semantic, IReadOnlyDictionary<string, object?>? data = null)
        {
            if (path == ActionPath.None)
            {
                throw new ArgumentException("a successful action reached the game by some path", nameof(path));
            }

            return new ActionResult(ActionStatus.Ok, path, message, data);
        }

        /// <summary>It ran and failed.</summary>
        /// <param name="message">Why.</param>
        /// <param name="path">How far it got; <see cref="ActionPath.None"/> when it never reached the game.</param>
        public static ActionResult Failed(string message, ActionPath path = ActionPath.None) =>
            new ActionResult(ActionStatus.Failed, path, Require(message), null);

        /// <summary>The arguments do not fit.</summary>
        /// <param name="message">Which argument and why.</param>
        public static ActionResult InvalidArgs(string message) =>
            new ActionResult(ActionStatus.InvalidArgs, ActionPath.None, Require(message), null);

        /// <summary>No action has this name.</summary>
        /// <param name="name">The name asked for.</param>
        public static ActionResult NotFound(string name) =>
            new ActionResult(ActionStatus.NotFound, ActionPath.None, "no action named \"" + name + "\"", null);

        /// <summary>The gate refused it.</summary>
        /// <param name="reason">Which rule, naming the config switch if there is one.</param>
        public static ActionResult Denied(string reason) =>
            new ActionResult(ActionStatus.Denied, ActionPath.None, Require(reason), null);

        /// <summary>The confirmation said no (or could not ask).</summary>
        /// <param name="reason">What the confirmation reported.</param>
        public static ActionResult Declined(string reason) =>
            new ActionResult(ActionStatus.Declined, ActionPath.None, Require(reason), null);

        /// <summary>The main thread did not run it in time.</summary>
        /// <param name="reason">The limit and what was waiting.</param>
        public static ActionResult TimedOut(string reason) =>
            new ActionResult(ActionStatus.TimedOut, ActionPath.None, Require(reason), null);

        /// <summary>The action system or its backend is not installed.</summary>
        /// <param name="reason">What is missing.</param>
        public static ActionResult NotAvailable(string reason) =>
            new ActionResult(ActionStatus.NotAvailable, ActionPath.None, Require(reason), null);

        /// <summary><c>Ok via Semantic: playing "Rain"</c>.</summary>
        public override string ToString() =>
            Status + (Path == ActionPath.None ? string.Empty : " via " + Path) + (Message.Length == 0 ? string.Empty : ": " + Message);

        private static string Require(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("a result other than Ok needs a message", nameof(message));
            }

            return message;
        }
    }
}
