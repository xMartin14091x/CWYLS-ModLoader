using System;
using System.Diagnostics;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// One registered smoke check: an id, the phase that owns it, and the work to run.
    /// Pure logic - the check body is supplied by whoever registers it, so this type stays
    /// free of Unity and BepInEx and can be unit-tested off-engine.
    /// </summary>
    public sealed class SmokeCheck
    {
        /// <summary>Creates a check.</summary>
        /// <param name="id">Stable id, e.g. <c>p0.paths</c>. Required; also the report key.</param>
        /// <param name="phase">Phase label of the check, e.g. <c>P0</c>. Required.</param>
        /// <param name="run">The check body. Must not be null; it may throw (see <see cref="Execute"/>).</param>
        public SmokeCheck(string id, string phase, Func<SmokeCheckResult> run)
            : this(id, phase, run, loud: false)
        {
        }

        /// <summary>Creates a check, saying whether running it is something the player notices.</summary>
        /// <param name="id">Stable id, e.g. <c>p0.paths</c>. Required; also the report key.</param>
        /// <param name="phase">Phase label of the check, e.g. <c>P0</c>. Required.</param>
        /// <param name="run">The check body. Must not be null; it may throw (see <see cref="Execute"/>).</param>
        /// <param name="loud">
        /// True when running the check is something the player sees or hears (see <see cref="Loud"/>).
        /// </param>
        public SmokeCheck(string id, string phase, Func<SmokeCheckResult> run, bool loud)
            : this(id, phase, run, loud, restore: null)
        {
        }

        /// <summary>Creates a check that puts back whatever running it changed.</summary>
        /// <param name="id">Stable id, e.g. <c>p0.paths</c>. Required; also the report key.</param>
        /// <param name="phase">Phase label of the check, e.g. <c>P0</c>. Required.</param>
        /// <param name="run">The check body. Must not be null; it may throw (see <see cref="Execute"/>).</param>
        /// <param name="loud">
        /// True when running the check is something the player sees or hears (see <see cref="Loud"/>).
        /// </param>
        /// <param name="restore">
        /// Optional undo, run after the body whatever the body did (see <see cref="Restore"/>).
        /// </param>
        public SmokeCheck(string id, string phase, Func<SmokeCheckResult> run, bool loud, Action? restore)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("a check id is required", nameof(id));
            }

            if (string.IsNullOrEmpty(phase))
            {
                throw new ArgumentException("a phase is required", nameof(phase));
            }

            Id = id;
            Phase = phase;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Loud = loud;
            Restore = restore;
        }

        /// <summary>Stable id; the key this check is reported under.</summary>
        public string Id { get; }

        /// <summary>Phase label of this check (<c>P0</c>, <c>P1</c>, ...).</summary>
        public string Phase { get; }

        /// <summary>The check body. Prefer <see cref="Execute"/>, which times it and cannot throw.</summary>
        public Func<SmokeCheckResult> Run { get; }

        /// <summary>
        /// True when the check is something the player notices: it plays a clip, repaints the
        /// character, opens a window. Only a <see cref="SmokeRunMode.Loud"/> run includes these
        /// (see <see cref="SmokeTriggers.Includes"/>); the hotkey, the trigger file and an
        /// automatic run all report them as skipped, so nothing the player can see happens
        /// unless someone asked for it by name (<c>smoke run loud</c>).
        /// </summary>
        public bool Loud { get; }

        /// <summary>
        /// What puts the game back the way it was before <see cref="Run"/> touched it, or null
        /// when the check changes nothing lasting.
        /// <para>
        /// A loud check used to read the old facial/motion/look value only to describe the
        /// delta and never wrote it back, so one smoke run left Satone smiling at a motion nobody
        /// chose. A check that changes visible state declares the undo here and
        /// <see cref="ExecuteAndRestore"/> runs it in a <c>finally</c> - so it runs even when the
        /// body throws, and a failing undo is reported rather than aborting the run.
        /// </para>
        /// </summary>
        public Action? Restore { get; }

        /// <summary>This check marked <see cref="Loud"/>; returns the same instance if it already is.</summary>
        public SmokeCheck AsLoud() => Loud ? this : new SmokeCheck(Id, Phase, Run, loud: true, Restore);

        /// <summary>
        /// This check with <paramref name="restore"/> as its undo; returns the same instance when
        /// it already has that undo.
        /// </summary>
        /// <param name="restore">The undo to attach, or null to drop the one it has.</param>
        public SmokeCheck WithRestore(Action? restore) =>
            ReferenceEquals(Restore, restore) ? this : new SmokeCheck(Id, Phase, Run, Loud, restore);

        /// <summary>
        /// Runs the check, times it, and reports it under <see cref="Id"/>. Never throws: a check
        /// that fails, throws, or returns nothing all become a failed result. The exception is not
        /// swallowed - its type and message become <see cref="SmokeCheckResult.Detail"/>, which the
        /// runner logs as an error line and writes into the report.
        /// </summary>
        public SmokeCheckResult Execute()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var result = Run();
                watch.Stop();

                return result is null
                    ? new SmokeCheckResult(Id, false, "check returned no result", watch.ElapsedMilliseconds)
                    : result.WithId(Id).WithElapsed(watch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                watch.Stop();
                return new SmokeCheckResult(Id, false, ex.GetType().Name + ": " + ex.Message, watch.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Runs the check and then its <see cref="Restore"/>, in a <c>finally</c>: the undo happens
        /// whatever the body did, and an undo that itself fails is handed to
        /// <paramref name="onRestoreFailed"/> instead of escaping. Never throws, except for a null
        /// <paramref name="onRestoreFailed"/>, which is a programming error caught before anything
        /// runs.
        /// </summary>
        /// <param name="onRestoreFailed">
        /// Called with this check and the exception when <see cref="Restore"/> throws. Required:
        /// the framework does not log from this assembly, so the caller is where a failed undo
        /// becomes a visible line. Not called when there is no <see cref="Restore"/>, and it must
        /// not throw.
        /// </param>
        /// <returns>The same result <see cref="Execute"/> would return.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="onRestoreFailed"/> is null.</exception>
        public SmokeCheckResult ExecuteAndRestore(Action<SmokeCheck, Exception> onRestoreFailed)
        {
            if (onRestoreFailed is null)
            {
                throw new ArgumentNullException(nameof(onRestoreFailed));
            }

            try
            {
                // Execute() already turns a throwing body into a failed result; the finally is the
                // guarantee that holds even if that ever stops being true.
                return Execute();
            }
            finally
            {
                var restore = Restore;
                if (restore != null)
                {
                    try
                    {
                        restore();
                    }
                    catch (Exception ex)
                    {
                        // Putting the character back is best-effort: a run that cannot undo itself
                        // is a warning the caller prints, never a run that stops halfway.
                        onRestoreFailed(this, ex);
                    }
                }
            }
        }
    }
}
