using System;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// The outcome of one smoke check. Immutable: a check builds one with
    /// <see cref="Pass"/>/<see cref="Fail"/> and the runner re-stamps id and timing
    /// (<see cref="WithId"/>, <see cref="WithElapsed"/>) before it reaches the report.
    /// </summary>
    public sealed class SmokeCheckResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="id">Check id, as registered. Required.</param>
        /// <param name="ok">Whether the check passed.</param>
        /// <param name="detail">Human-readable evidence; <c>null</c> becomes an empty string.</param>
        /// <param name="elapsedMs">Wall-clock duration of the check, in milliseconds.</param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsedMs"/> is negative.</exception>
        public SmokeCheckResult(string id, bool ok, string? detail = null, long elapsedMs = 0)
            : this(id, ok, detail, elapsedMs, informational: false)
        {
        }

        /// <summary>Creates a result, saying whether a failure is only "not exercised yet".</summary>
        /// <param name="id">Check id, as registered. Required.</param>
        /// <param name="ok">Whether the check passed.</param>
        /// <param name="detail">Human-readable evidence; <c>null</c> becomes an empty string.</param>
        /// <param name="elapsedMs">Wall-clock duration of the check, in milliseconds.</param>
        /// <param name="informational">
        /// True when a false <paramref name="ok"/> means "nothing has exercised this yet" rather
        /// than "this is broken" (see <see cref="Informational"/>).
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsedMs"/> is negative.</exception>
        public SmokeCheckResult(string id, bool ok, string? detail, long elapsedMs, bool informational)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("a check id is required", nameof(id));
            }

            if (elapsedMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedMs), elapsedMs, "elapsed time cannot be negative");
            }

            Id = id;
            Ok = ok;
            Detail = detail ?? string.Empty;
            ElapsedMs = elapsedMs;
            Informational = informational;
        }

        /// <summary>Check id, matching the <see cref="SmokeCheck.Id"/> it came from.</summary>
        public string Id { get; }

        /// <summary>True when the check passed.</summary>
        public bool Ok { get; }

        /// <summary>Evidence for the verdict: what was found, or why it failed. Never null.</summary>
        public string Detail { get; }

        /// <summary>Wall-clock duration of the check, in milliseconds.</summary>
        public long ElapsedMs { get; }

        /// <summary>
        /// True when a failure is only "the game has not done this yet this session".
        /// <para>
        /// Half the early checks report what has been <em>observed</em> - Satone changing state, a
        /// pomodoro running, a line with text. Three seconds into a launch none of them can have
        /// happened, and the runner logging them at Error painted a healthy session red. The
        /// verdict is still not ok, because the check has not proved anything; the level it is
        /// announced at is Warning.
        /// </para>
        /// </summary>
        public bool Informational { get; }

        /// <summary>A passing result.</summary>
        public static SmokeCheckResult Pass(string id, string? detail = null) =>
            new SmokeCheckResult(id, true, detail);

        /// <summary>A failing result. <paramref name="detail"/> is what the log line will say.</summary>
        public static SmokeCheckResult Fail(string id, string detail) =>
            new SmokeCheckResult(id, false, detail);

        /// <summary>
        /// A check that has not been exercised yet: not ok, but <see cref="Informational"/>, so it
        /// is announced at Warning rather than Error. Use it for every "not yet observed" /
        /// "not exercised" verdict, and keep <see cref="Fail"/> for a seam that is actually broken.
        /// </summary>
        /// <param name="id">Check id, as registered.</param>
        /// <param name="detail">What has not happened yet, and what would make it happen.</param>
        public static SmokeCheckResult NotYetObserved(string id, string detail) =>
            new SmokeCheckResult(id, false, detail, 0, informational: true);

        /// <summary>
        /// A check that was not run. Reported as ok, because a check nobody asked to run has not
        /// failed; <paramref name="reason"/> says so in the report, e.g.
        /// <see cref="SmokeTriggers.LoudSkipDetail"/>.
        /// </summary>
        public static SmokeCheckResult Skipped(string id, string reason) =>
            new SmokeCheckResult(id, true, reason);

        /// <summary>This result reported under <paramref name="id"/>.</summary>
        public SmokeCheckResult WithId(string id) =>
            string.Equals(Id, id, StringComparison.Ordinal)
                ? this
                : new SmokeCheckResult(id, Ok, Detail, ElapsedMs, Informational);

        /// <summary>This result timed at <paramref name="elapsedMs"/>.</summary>
        public SmokeCheckResult WithElapsed(long elapsedMs) =>
            elapsedMs == ElapsedMs
                ? this
                : new SmokeCheckResult(Id, Ok, Detail, elapsedMs, Informational);
    }
}
