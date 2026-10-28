using System;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>How a <c>UiClick.Invoke</c> or <c>UiClick.Probe</c> ended.</summary>
    public enum UiClickStatus
    {
        /// <summary>The control was clicked.</summary>
        Clicked = 0,

        /// <summary>A probe found a clickable, allowed control and did not click it.</summary>
        Probed = 1,

        /// <summary>The deny list refused it (shop, account, cloud sync, SNS/browser); nothing was clicked.</summary>
        Denied = 2,

        /// <summary>No active object has that path (or the path is not a full one).</summary>
        NotFound = 3,

        /// <summary>The object and its ancestors take no click.</summary>
        NoHandler = 4,

        /// <summary>The control is disabled or blocked, as it would be for the player.</summary>
        NotInteractable = 5,

        /// <summary>The click could not be made (not on the main thread, an engine error).</summary>
        Failed = 6,
    }

    /// <summary>The outcome of one UI click or probe. Immutable.</summary>
    public sealed class UiClickResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="status">How it ended.</param>
        /// <param name="requestedPath">The path asked for.</param>
        /// <param name="resolvedPath">The control's real hierarchy path (the click receiver), or empty.</param>
        /// <param name="detail">Why, for people; required unless clicked or probed.</param>
        /// <exception cref="ArgumentException"><paramref name="detail"/> is blank for a status that needs one.</exception>
        public UiClickResult(UiClickStatus status, string? requestedPath, string? resolvedPath, string? detail)
        {
            if (status != UiClickStatus.Clicked && status != UiClickStatus.Probed && string.IsNullOrWhiteSpace(detail))
            {
                throw new ArgumentException("a refused or failed click needs a reason", nameof(detail));
            }

            Status = status;
            RequestedPath = requestedPath ?? string.Empty;
            ResolvedPath = resolvedPath ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        /// <summary>How it ended.</summary>
        public UiClickStatus Status { get; }

        /// <summary>The path asked for.</summary>
        public string RequestedPath { get; }

        /// <summary>The click receiver's real hierarchy path, or empty when nothing was found.</summary>
        public string ResolvedPath { get; }

        /// <summary>Why it was refused or failed; may be empty for a click.</summary>
        public string Detail { get; }

        /// <summary>True when the control was clicked.</summary>
        public bool IsClicked => Status == UiClickStatus.Clicked;

        /// <summary>
        /// The result as an action outcome: Clicked → Ok with path <see cref="ActionPath.UiClick"/>;
        /// Denied → Denied; a probe (nothing clicked) and everything else → Failed.
        /// </summary>
        /// <param name="what">What the click did, for the Ok message (e.g. <c>pressed play</c>).</param>
        public ActionResult ToActionResult(string what)
        {
            switch (Status)
            {
                case UiClickStatus.Clicked:
                    return ActionResult.Ok(string.IsNullOrWhiteSpace(what) ? "clicked " + ResolvedPath : what, ActionPath.UiClick);
                case UiClickStatus.Denied:
                    return ActionResult.Denied("the UI click is refused: " + Detail);
                case UiClickStatus.Probed:
                    return ActionResult.Failed("a probe does not click (" + ResolvedPath + ")");
                default:
                    return ActionResult.Failed("the UI click did not happen (" + Status + "): " + Detail);
            }
        }

        /// <summary><c>Clicked Root/A/B</c>, <c>Denied Root/SNS/X: sns/browser (...)</c>.</summary>
        public override string ToString() =>
            Status + " " + (ResolvedPath.Length > 0 ? ResolvedPath : RequestedPath) + (Detail.Length == 0 ? string.Empty : ": " + Detail);
    }
}
