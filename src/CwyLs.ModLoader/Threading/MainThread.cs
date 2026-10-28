using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Threading
{
    /// <summary>
    /// The framework's one main-thread dispatcher: run work on Unity's main thread
    /// from any thread, now or after a delay, fire-and-forget or awaited with a timeout, plus
    /// per-frame tickers. Every feature host and every mod uses this instead of a queue of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The logic is <see cref="MainThreadQueue"/> in Core (unit-tested); this class owns the one
    /// instance, the clock (a <see cref="Stopwatch"/>, readable from any thread), the frame budget
    /// (<c>[MainThread] FrameBudgetMs</c>) and the hidden <c>DontDestroyOnLoad</c> host that pumps
    /// it once a frame (<see cref="MainThreadHost"/>; plugin components die ~0.2 s after boot in
    /// this game).
    /// </para>
    /// <para>
    /// Owners: framework features pass a short name (<c>satone.motion</c>); a mod's helper passes
    /// <c>mod:&lt;id&gt;</c>, so its failures are attributed to it and its work is dropped when it
    /// stops. Framework work runs in full every frame; mod work runs after it, within
    /// <see cref="FrameBudgetMs"/>, one mod at a time in turn (two lanes).
    /// </para>
    /// </remarks>
    public static class MainThread
    {
        /// <summary>Owner of framework work that names no feature.</summary>
        public const string FrameworkOwner = "cwy";

        /// <summary>Prefix of every mod's owner name; such work runs in the budgeted mod lane.</summary>
        public const string ModOwnerPrefix = MainThreadQueue.ModOwnerPrefix;

        /// <summary>Config section of the dispatcher settings.</summary>
        public const string ConfigSection = "MainThread";

        /// <summary>Config key of the per-frame budget for mod work.</summary>
        public const string ConfigKeyFrameBudgetMs = "FrameBudgetMs";

        /// <summary>Default per-frame budget for mod work, in milliseconds.</summary>
        public const float DefaultFrameBudgetMs = 4f;

        /// <summary>
        /// The caller-side wall-clock timeout an invocation gets when it names none: 2 s, at least
        /// 20 frames at the game's lowest frame-rate setting and 48 at its unfocused default of 24 fps.
        /// Pass <see cref="Timeout.InfiniteTimeSpan"/> to wait forever.
        /// </summary>
        public static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(2);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static readonly ModFailureLimiter FrameworkFailures = new ModFailureLimiter();
        private static readonly ModFailureLimiter DropWarnings = new ModFailureLimiter();
        private static readonly MainThreadQueue Queue =
            new MainThreadQueue(() => Clock.Elapsed.TotalSeconds, OnFailure);

        private static readonly object Gate = new object();
        private static ConfigEntry<float>? _frameBudget;
        private static bool _closeHooked;

        /// <summary>
        /// Where failures of mod-owned work go (set by the mod loader). Returns true when it
        /// reported the failure; framework-owned failures are logged here.
        /// </summary>
        internal static Func<MainThreadFailure, bool>? ModFailureRoute { get; set; }

        /// <summary>Monotonic seconds since the framework loaded. Safe on any thread.</summary>
        public static double Now => Queue.Now;

        /// <summary>True when the caller is on Unity's main thread.</summary>
        public static bool IsMainThread => Queue.IsMainThread;

        /// <summary>True once <see cref="Arm"/> has captured the main thread.</summary>
        public static bool IsArmed => Queue.HasMainThread;

        /// <summary>True while the hidden host that pumps the queue is alive.</summary>
        public static bool HasHost => MainThreadHost.IsAlive;

        /// <summary><c>Time.frameCount</c> at the last pump; 0 before the first. Safe on any thread.</summary>
        public static int Frame => Volatile.Read(ref _frame);

        /// <summary>The per-frame budget for mod work, in milliseconds (<c>[MainThread] FrameBudgetMs</c>).</summary>
        public static float FrameBudgetMs => Mathf.Clamp(_frameBudget?.Value ?? DefaultFrameBudgetMs, 0.5f, 50f);

        /// <summary>The dispatcher's counters, both lanes.</summary>
        public static MainThreadStats Stats => Queue.Snapshot();

        private static int _frame;

        /// <summary>
        /// Captures the main thread, binds <c>[MainThread] FrameBudgetMs</c>, creates the host and
        /// closes the queue on <c>Application.quitting</c>. Called from <c>ModLoaderPlugin.Awake</c>;
        /// later calls (feature hosts arming themselves) are cheap and change nothing. Framework only.
        /// </summary>
        /// <param name="config">Config file to bind the budget in; null leaves it at the default.</param>
        internal static void Arm(ConfigFile? config = null)
        {
            if (!Queue.HasMainThread)
            {
                Queue.CaptureMainThread();
            }

            bool hookClose;
            lock (Gate)
            {
                if (_frameBudget is null && config != null)
                {
                    _frameBudget = config.Bind(
                        ConfigSection,
                        ConfigKeyFrameBudgetMs,
                        DefaultFrameBudgetMs,
                        new ConfigDescription(
                            "Milliseconds per frame the shared main-thread dispatcher may spend on work that mods " +
                            "queue. Framework work (Satone, subtitles, the loader) always runs in full first. At " +
                            "least one mod item runs every frame, mods taking turns; the rest waits for the next " +
                            "frame. Per-frame tickers are not counted.",
                            new AcceptableValueRange<float>(0.5f, 50f)));
                }

                hookClose = !_closeHooked;
                _closeHooked = true;
            }

            if (hookClose)
            {
                Application.quitting += Queue.Close;
            }

            MainThreadHost.Ensure();
        }

        /// <summary>Queues work for the next pump, even on the main thread.</summary>
        /// <param name="label">What the work is, for failure lines.</param>
        /// <param name="work">The work; its exceptions are logged, never rethrown.</param>
        /// <param name="owner">Who is posting; see the class remarks.</param>
        /// <returns>True when queued; false when the queue was full (logged) or the game is quitting.</returns>
        public static bool Post(string label, Action work, string owner = FrameworkOwner) =>
            Accepted(Queue.Post(owner, label, work), owner, label);

        /// <summary>Runs work now on the main thread, otherwise queues it for the next pump.</summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exceptions are logged, never rethrown.</param>
        /// <param name="owner">Who is posting.</param>
        /// <returns>True when it ran or was queued.</returns>
        public static bool RunOrPost(string label, Action work, string owner = FrameworkOwner) =>
            Accepted(Queue.RunOrPost(owner, label, work), owner, label);

        /// <summary>
        /// Runs framework work on the first pump at least <paramref name="seconds"/> from now (real time).
        /// </summary>
        /// <param name="seconds">The delay; unaffected by <c>Time.timeScale</c> or a paused game. Zero or less is <see cref="Post"/>.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exceptions are logged, never rethrown.</param>
        /// <returns>
        /// A handle: dispose it to cancel the work before it runs. <see cref="DelayedWork.IsAccepted"/>
        /// is false when the delayed queue was full (logged) or the game is quitting.
        /// </returns>
        public static DelayedWork PostAfter(double seconds, string label, Action work) =>
            PostAfterFor(FrameworkOwner, seconds, label, work);

        /// <summary>
        /// Runs work on the first pump at least <paramref name="seconds"/> from now, under a named
        /// owner (a feature such as <c>satone.motion</c>, or a mod's <c>mod:&lt;id&gt;</c>). Framework only.
        /// </summary>
        /// <param name="owner">Who is posting.</param>
        /// <param name="seconds">The delay; zero or less is <see cref="Post"/>.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exceptions are logged, never rethrown.</param>
        /// <returns>A cancel handle; see <see cref="PostAfter(double, string, Action)"/>.</returns>
        internal static DelayedWork PostAfterFor(string owner, double seconds, string label, Action work)
        {
            var handle = Queue.PostAfter(seconds, owner, label, work);
            Accepted(handle.IsAccepted, owner, label);
            return handle;
        }

        /// <summary>Runs work on the main thread; the task ends when it ran, threw, timed out or was cancelled.</summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exception faults the task.</param>
        /// <param name="timeout">
        /// Wall-clock limit on the caller's side; null means <see cref="DefaultInvokeTimeout"/> (2 s),
        /// <see cref="Timeout.InfiniteTimeSpan"/> (or anything above ~24.8 days) waits forever. Work not
        /// started by then is skipped.
        /// </param>
        /// <param name="cancellationToken">Stops the wait and, if not yet started, the work.</param>
        /// <param name="owner">Who is asking.</param>
        /// <returns>The task; a refusal faults it with <see cref="MainThreadRejectedException"/>.</returns>
        public static Task InvokeAsync(
            string label, Action work, TimeSpan? timeout = null, CancellationToken cancellationToken = default,
            string owner = FrameworkOwner) =>
            Queue.InvokeAsync(owner, label, work, timeout ?? DefaultInvokeTimeout, cancellationToken);

        /// <summary>Runs a function on the main thread and hands back its result through a task.</summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function; its exception faults the task.</param>
        /// <param name="timeout">Wall-clock limit on the caller's side; null means <see cref="DefaultInvokeTimeout"/>.</param>
        /// <param name="cancellationToken">Stops the wait and, if not yet started, the work.</param>
        /// <param name="owner">Who is asking.</param>
        /// <returns>The task; a refusal faults it with <see cref="MainThreadRejectedException"/>.</returns>
        public static Task<T> InvokeAsync<T>(
            string label, Func<T> work, TimeSpan? timeout = null, CancellationToken cancellationToken = default,
            string owner = FrameworkOwner) =>
            Queue.InvokeAsync(owner, label, work, timeout ?? DefaultInvokeTimeout, cancellationToken);

        /// <summary>
        /// Runs a function on the main thread and blocks until it returns: inline on the main
        /// thread, otherwise up to <paramref name="timeout"/>.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function; its exception is rethrown to the caller.</param>
        /// <param name="timeout">How long a background caller waits.</param>
        /// <param name="owner">Who is asking.</param>
        /// <returns>The function's result.</returns>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The dispatcher refused the work.</exception>
        public static T Invoke<T>(string label, Func<T> work, TimeSpan timeout, string owner = FrameworkOwner) =>
            Queue.Invoke(owner, label, work, timeout);

        /// <summary>
        /// Runs work on the main thread and blocks until it is done: inline on the main thread,
        /// otherwise up to <paramref name="timeout"/>.
        /// </summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exception is rethrown to the caller.</param>
        /// <param name="timeout">How long a background caller waits.</param>
        /// <param name="owner">Who is asking.</param>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The dispatcher refused the work.</exception>
        public static void Invoke(string label, Action work, TimeSpan timeout, string owner = FrameworkOwner) =>
            Queue.Invoke(owner, label, work, timeout);

        /// <summary>Adds a per-frame callback; dispose the handle to remove it. Exceptions are logged, never rethrown.</summary>
        /// <param name="label">What it is.</param>
        /// <param name="tick">The callback, on the main thread, after the frame's queued work.</param>
        /// <param name="owner">Who registers it.</param>
        /// <returns>Dispose to remove the ticker.</returns>
        public static IDisposable AddTicker(string label, Action tick, string owner = FrameworkOwner) =>
            Queue.AddTicker(owner, label, tick);

        /// <summary>Drops every waiting and delayed item of one owner. Framework only (a mod's helper does it when the mod stops).</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many were dropped.</returns>
        internal static int Cancel(string owner) => Queue.Cancel(owner);

        /// <summary>Drops one owner's delayed items that have not run yet, due or not. Framework only.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many were dropped.</returns>
        internal static int CancelDelayed(string owner) => Queue.CancelDelayed(owner);

        /// <summary>Removes every ticker one owner registered. Framework only.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many were removed.</returns>
        internal static int RemoveTickers(string owner) => Queue.RemoveTickers(owner);

        /// <summary>Items one owner has waiting.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>The count.</returns>
        public static int PendingOf(string owner) => Queue.PendingOf(owner);

        /// <summary>Delayed items one owner has.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>The count.</returns>
        public static int DelayedOf(string owner) => Queue.DelayedOf(owner);

        /// <summary>Items refused for one owner because a queue was full.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>The count.</returns>
        public static long DroppedOf(string owner) => Queue.DroppedOf(owner);

        /// <summary>The owner name a mod's work runs under: <c>mod:&lt;id&gt;</c>.</summary>
        /// <param name="modId">The mod id.</param>
        /// <returns>The owner name.</returns>
        public static string ModOwner(string modId) => ModOwnerPrefix + modId;

        /// <summary>One frame of work. Called by <see cref="MainThreadHost"/> and nothing else.</summary>
        internal static void PumpFrame()
        {
            Volatile.Write(ref _frame, Time.frameCount);
            Queue.Pump(FrameBudgetMs / 1000.0);
        }

        /// <summary>
        /// Logs a refused post: one warning per owner per 10 s (the next carries the count of the
        /// quiet ones), file-only while the game is quitting.
        /// </summary>
        private static bool Accepted(bool accepted, string owner, string label)
        {
            if (!accepted)
            {
                if (Queue.IsClosed)
                {
                    ConsoleEvents.File("mainthread", "refusedQuitting", ("owner", owner), ("label", label));
                    return false;
                }

                if (DropWarnings.ShouldLog(ModFailureLimiter.SiteKey(owner, "drop", null), Now, out var quiet))
                {
                    CwyLsModLoader.Log.LogWarning(
                        "MAINTHREAD queue for '" + owner + "' is full; dropped '" + label + "' (dropped so far: " +
                        Queue.DroppedOf(owner) + (quiet > 0 ? "; " + quiet + " drop warning(s) not shown" : string.Empty) + ")");
                }
                else
                {
                    ConsoleEvents.File("mainthread", "dropped", ("owner", owner), ("label", label));
                }

                return false;
            }

            if (!MainThreadHost.IsAlive && !IsMainThread)
            {
                MainThreadHost.WarnNotPumping(label);
            }

            return true;
        }

        private static void OnFailure(MainThreadFailure failure)
        {
            if (failure.DeliveredToCaller)
            {
                // The awaiting caller owns the exception; the file keeps a record of it.
                ConsoleEvents.File(
                    "mainthread", "invokeFailed",
                    ("owner", failure.Owner),
                    ("label", failure.Label),
                    ("error", failure.Error.GetType().Name),
                    ("message", failure.Error.Message));
                return;
            }

            if (ModFailureRoute?.Invoke(failure) == true)
            {
                return;
            }

            var site = ModFailureLimiter.SiteKey(failure.Owner, failure.IsTicker ? "tick" : "work", failure.Label);
            if (!FrameworkFailures.ShouldLog(site, Now, out var suppressed))
            {
                return;
            }

            CwyLsModLoader.Log.LogError(
                "MAINTHREAD " + failure.Owner + " '" + failure.Label + "' threw " + failure.Error.GetType().Name + ": " +
                ModConsoleLines.OneLine(failure.Error.Message) +
                (suppressed > 0 ? " (+" + suppressed + " earlier)" : string.Empty));
            ConsoleEvents.File(
                "mainthread", "failed",
                ("owner", failure.Owner),
                ("label", failure.Label),
                ("ticker", failure.IsTicker),
                ("stack", failure.Error.ToString()));
        }
    }
}
