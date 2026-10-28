using System;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>How the console should look (<c>Console.Style</c>).</summary>
    public enum ConsoleStyle
    {
        /// <summary>Like one of the game's own windows, when the running game provides one; IMGUI otherwise.</summary>
        Game,

        /// <summary>Always the plain IMGUI overlay.</summary>
        Imgui
    }

    /// <summary>Which drawing of the console is on screen.</summary>
    public enum ConsoleSurfaceKind
    {
        /// <summary>The IMGUI overlay. Needs nothing from the game, so it always works.</summary>
        Imgui,

        /// <summary>A window built from the game's own UI assets.</summary>
        GameStyle
    }

    /// <summary>
    /// Chooses between the game-style console and the IMGUI fallback, and decides which of those
    /// choices are worth a log line. Pure; one instance lives for the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule: if the game's UI assets cannot be resolved (a game update renamed
    /// them, or the room is not loaded yet), fall back to IMGUI and say so <em>once</em>; the
    /// console must never become unusable. "Once" is per state change, not per attempt: the first
    /// fallback is logged, repeated failures are not, a later recovery is logged once, and a
    /// fallback after that recovery is logged again. A surface that threw while running is
    /// broken for the rest of the session and never tried again.
    /// </para>
    /// </remarks>
    public sealed class ConsoleSurfacePolicy
    {
        private enum Outcome
        {
            Unknown,
            Shown,
            FellBack
        }

        private readonly object _gate = new object();

        private Outcome _last = Outcome.Unknown;
        private bool _configNoted;
        private string _brokenReason = string.Empty;

        /// <summary>True once a game-style surface failed at runtime; it is not tried again this session.</summary>
        public bool Broken
        {
            get { lock (_gate) { return _brokenReason.Length > 0; } }
        }

        /// <summary>Why the game-style surface is broken, or empty.</summary>
        public string BrokenReason
        {
            get { lock (_gate) { return _brokenReason; } }
        }

        /// <summary>The surface to try first.</summary>
        /// <param name="style">The configured style.</param>
        /// <param name="gameStyleRegistered">True when an adapter registered a game-style surface.</param>
        public ConsoleSurfaceKind Preferred(ConsoleStyle style, bool gameStyleRegistered)
        {
            lock (_gate)
            {
                return style == ConsoleStyle.Game && gameStyleRegistered && _brokenReason.Length == 0
                    ? ConsoleSurfaceKind.GameStyle
                    : ConsoleSurfaceKind.Imgui;
            }
        }

        /// <summary>
        /// Records one attempt to show the game-style surface. Returns the line to log, or null
        /// when this outcome was already reported.
        /// </summary>
        /// <param name="shown">True when the surface is on screen.</param>
        /// <param name="detail">
        /// On success, what was built (e.g. which optional assets were missing); on failure, why
        /// it could not be shown. Null is allowed.
        /// </param>
        public string? RecordAttempt(bool shown, string? detail)
        {
            lock (_gate)
            {
                var outcome = shown ? Outcome.Shown : Outcome.FellBack;
                if (outcome == _last)
                {
                    return null;
                }

                var previous = _last;
                _last = outcome;

                var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : ": " + detail!.Trim();
                if (shown)
                {
                    return (previous == Outcome.FellBack ? "game-style console available again" : "game-style console")
                        + suffix;
                }

                return "game-style console unavailable, using the IMGUI console" + suffix;
            }
        }

        /// <summary>
        /// Records that the game-style surface threw while running. It is not tried again this
        /// session. Returns the line to log, or null when a failure was already recorded.
        /// </summary>
        /// <param name="reason">What went wrong, e.g. the exception type and message.</param>
        public string? RecordFailure(string? reason)
        {
            lock (_gate)
            {
                if (_brokenReason.Length > 0)
                {
                    return null;
                }

                _brokenReason = string.IsNullOrWhiteSpace(reason) ? "unknown failure" : reason!.Trim();
                _last = Outcome.FellBack;
                return "game-style console failed and is off for this session, using the IMGUI console: "
                    + _brokenReason;
            }
        }

        /// <summary>
        /// The one line saying the IMGUI console was chosen by configuration, or null when it was
        /// already said.
        /// </summary>
        /// <param name="style">The configured style.</param>
        public string? RecordConfiguredStyle(ConsoleStyle style)
        {
            lock (_gate)
            {
                if (style != ConsoleStyle.Imgui || _configNoted)
                {
                    return null;
                }

                _configNoted = true;
                return "IMGUI console (Console.Style=Imgui)";
            }
        }
    }
}
