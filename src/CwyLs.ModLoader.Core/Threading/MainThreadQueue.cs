using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Trace;

namespace CwyLs.ModLoader.Core.Threading
{
    /// <summary>What went wrong in one piece of main-thread work.</summary>
    public sealed class MainThreadFailure
    {
        /// <summary>Creates a record.</summary>
        /// <param name="owner">Who posted the work: a feature name or a mod id.</param>
        /// <param name="label">What the work was.</param>
        /// <param name="error">What it threw.</param>
        /// <param name="isTicker">True when a per-frame ticker threw.</param>
        /// <param name="deliveredToCaller">True when the exception was also handed to an awaiting caller's task.</param>
        public MainThreadFailure(string owner, string label, Exception error, bool isTicker, bool deliveredToCaller)
        {
            Owner = owner ?? string.Empty;
            Label = label ?? string.Empty;
            Error = error ?? throw new ArgumentNullException(nameof(error));
            IsTicker = isTicker;
            DeliveredToCaller = deliveredToCaller;
        }

        /// <summary>Who posted the work.</summary>
        public string Owner { get; }

        /// <summary>What the work was.</summary>
        public string Label { get; }

        /// <summary>What it threw.</summary>
        public Exception Error { get; }

        /// <summary>True when a per-frame ticker threw (it stays registered).</summary>
        public bool IsTicker { get; }

        /// <summary>
        /// True when the exception also reached the caller through an <c>InvokeAsync</c> task, so
        /// the log line can be file-only: the caller owns the error.
        /// </summary>
        public bool DeliveredToCaller { get; }
    }

    /// <summary>Counters of a <see cref="MainThreadQueue"/>, for the console and the smoke report.</summary>
    public sealed class MainThreadStats
    {
        internal MainThreadStats(
            int frameworkPending, int modPending, int modOwners, int delayed, int tickers, long posted,
            long frameworkRan, long modRan, long failed, long dropped, long cancelled, long timedOut,
            long deferred, long pumps, double lastPumpMs, double maxPumpMs)
        {
            FrameworkPending = frameworkPending;
            ModPending = modPending;
            ModOwners = modOwners;
            Delayed = delayed;
            Tickers = tickers;
            Posted = posted;
            FrameworkRan = frameworkRan;
            ModRan = modRan;
            Failed = failed;
            Dropped = dropped;
            Cancelled = cancelled;
            TimedOut = timedOut;
            Deferred = deferred;
            Pumps = pumps;
            LastPumpMs = lastPumpMs;
            MaxPumpMs = maxPumpMs;
        }

        /// <summary>Work waiting for the next pump, both lanes.</summary>
        public int Pending => FrameworkPending + ModPending;

        /// <summary>Framework work waiting (owners without the <c>mod:</c> prefix); all of it runs next pump.</summary>
        public int FrameworkPending { get; }

        /// <summary>Mod work waiting (owners <c>mod:&lt;id&gt;</c>); it runs under the frame budget.</summary>
        public int ModPending { get; }

        /// <summary>Mods with work waiting; the mod lane serves them round-robin.</summary>
        public int ModOwners { get; }

        /// <summary>Delayed work not yet due.</summary>
        public int Delayed { get; }

        /// <summary>Registered per-frame tickers.</summary>
        public int Tickers { get; }

        /// <summary>Work accepted since start (posted, delayed and invoked).</summary>
        public long Posted { get; }

        /// <summary>Work that ran to completion, both lanes (inline <c>RunOrPost</c> work counts in its owner's lane).</summary>
        public long Ran => FrameworkRan + ModRan;

        /// <summary>Framework work that ran to completion.</summary>
        public long FrameworkRan { get; }

        /// <summary>Mod work that ran to completion.</summary>
        public long ModRan { get; }

        /// <summary>Work and ticker calls that threw.</summary>
        public long Failed { get; }

        /// <summary>Work refused because a queue was full.</summary>
        public long Dropped { get; }

        /// <summary>Work cancelled before it started (owner cancel, handle, caller token, timeout).</summary>
        public long Cancelled { get; }

        /// <summary>Invocations whose caller stopped waiting at its timeout.</summary>
        public long TimedOut { get; }

        /// <summary>Times the mod lane left work for the next frame because its budget was spent.</summary>
        public long Deferred { get; }

        /// <summary>Pumps run.</summary>
        public long Pumps { get; }

        /// <summary>Milliseconds the last pump took (both lanes and tickers).</summary>
        public double LastPumpMs { get; }

        /// <summary>Longest pump seen, in milliseconds.</summary>
        public double MaxPumpMs { get; }

        /// <summary>
        /// One line for the console:
        /// <c>pending=0 delayed=2 tickers=3 ran=120 failed=0 dropped=0 cancelled=0 timedOut=0 deferred=0 maxPumpMs=0.4; framework pending=0 ran=100; mods pending=0 ran=20 owners=0</c>.
        /// </summary>
        public override string ToString() =>
            "pending=" + Pending + " delayed=" + Delayed + " tickers=" + Tickers + " ran=" + Ran +
            " failed=" + Failed + " dropped=" + Dropped + " cancelled=" + Cancelled + " timedOut=" + TimedOut +
            " deferred=" + Deferred + " maxPumpMs=" + MaxPumpMs.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
            "; framework pending=" + FrameworkPending + " ran=" + FrameworkRan +
            "; mods pending=" + ModPending + " ran=" + ModRan + " owners=" + ModOwners;
    }

    /// <summary>
    /// The one main-thread dispatcher of the framework (it replaced twelve hand-rolled host
    /// queues). Any thread posts work; the Unity side calls
    /// <see cref="Pump"/> once a frame on the main thread. Pure: no Unity, a caller-supplied
    /// clock, so budgets, delays, timeouts and isolation are unit-tested.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Two lanes</b>. Work of a framework owner (any owner without the
    /// <see cref="ModOwnerPrefix"/>) runs first and in full every pump, as the per-feature pumps it
    /// replaced did, so a mod backlog can never hold back, say, Satone's restore. Work of mods
    /// (<c>mod:&lt;id&gt;</c>) runs after it, under the time budget, one item per mod in turn
    /// (round-robin), and at least one item per pump so a backlog always moves.</item>
    /// <item><b>Order.</b> Within a lane (and within one mod), work runs in the order it became
    /// ready: posted work when it was posted, delayed work when it fell due. So a delayed item runs
    /// before anything posted after its due time, and after what was posted before it. Work posted
    /// while a pump runs waits for the next pump, so a job that re-posts itself cannot spin a frame
    /// forever. Tickers run every pump, after both lanes, regardless of the budget.</item>
    /// <item><b>Isolation.</b> Nothing that runs here can throw out of <see cref="Pump"/>: every
    /// exception goes to the failure callback (and, for an invocation, to the caller's task).</item>
    /// <item><b>Timeouts are the caller's.</b> An invocation's timeout is a wall-clock timer on
    /// the caller's side, so a paused or stalled frame loop cannot hang the caller. Work that has
    /// not started when the timer fires is cancelled; work already running finishes and its
    /// result is discarded.</item>
    /// <item><b>Owners.</b> Every item names its owner (a feature or a mod id). Queues are capped
    /// per owner as well as in total, so one runaway mod cannot starve the others, and
    /// <see cref="Cancel"/> drops everything an owner still has waiting (mod shutdown).</item>
    /// </list>
    /// </remarks>
    public sealed class MainThreadQueue
    {
        /// <summary>Owners that start with this are mods and run in the budgeted, round-robin mod lane.</summary>
        public const string ModOwnerPrefix = "mod:";

        /// <summary>Most items waiting in total.</summary>
        public const int DefaultCapacity = 2048;

        /// <summary>Most items one owner may have waiting.</summary>
        public const int DefaultOwnerCapacity = 256;

        /// <summary>Most delayed items in total.</summary>
        public const int DefaultDelayedCapacity = 512;

        /// <summary>Most delayed items one owner may have.</summary>
        public const int DefaultOwnerDelayedCapacity = 64;

        private const int StateWaiting = 0;
        private const int StateStarted = 1;
        private const int StateCancelled = 2;

        private readonly object _gate = new object();
        private readonly Func<double> _clock;
        private readonly Action<MainThreadFailure>? _onFailure;
        private readonly int _capacity;
        private readonly int _ownerCapacity;
        private readonly int _delayedCapacity;
        private readonly int _ownerDelayedCapacity;

        // Framework lane: one list, ordered by (Ready, Sequence).
        private readonly LinkedList<WorkItem> _framework = new LinkedList<WorkItem>();

        // Mod lane: one list per mod owner that has work, and the round-robin ring of those owners.
        private readonly Dictionary<string, LinkedList<WorkItem>> _modQueues = new Dictionary<string, LinkedList<WorkItem>>(StringComparer.Ordinal);
        private readonly List<string> _modRing = new List<string>();
        private int _modCursor;

        private readonly List<WorkItem> _delayed = new List<WorkItem>();
        private readonly Dictionary<string, int> _pendingByOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _delayedByOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _droppedByOwner = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly List<Ticker> _tickers = new List<Ticker>();

        private Ticker[] _tickerSnapshot = Array.Empty<Ticker>();
        private int _mainThreadId = -1;
        private int _pendingTotal;
        private bool _closed;
        private long _sequence;
        private long _posted;
        private long _frameworkRan;
        private long _modRan;
        private long _failed;
        private long _dropped;
        private long _cancelled;
        private long _timedOut;
        private long _deferred;
        private long _pumps;
        private double _lastPumpMs;
        private double _maxPumpMs;

        /// <summary>Creates a dispatcher.</summary>
        /// <param name="clock">Monotonic seconds, safe to read from any thread (e.g. a Stopwatch).</param>
        /// <param name="onFailure">
        /// Called on the pumping thread for every exception work or a ticker throws. Its own
        /// exceptions are reported to <see cref="TraceDiagnostics"/> and swallowed.
        /// </param>
        /// <param name="capacity">Most items waiting in total.</param>
        /// <param name="ownerCapacity">Most items one owner may have waiting.</param>
        /// <param name="delayedCapacity">Most delayed items in total.</param>
        /// <param name="ownerDelayedCapacity">Most delayed items per owner.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public MainThreadQueue(
            Func<double> clock,
            Action<MainThreadFailure>? onFailure = null,
            int capacity = DefaultCapacity,
            int ownerCapacity = DefaultOwnerCapacity,
            int delayedCapacity = DefaultDelayedCapacity,
            int ownerDelayedCapacity = DefaultOwnerDelayedCapacity)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _onFailure = onFailure;
            _capacity = Math.Max(1, capacity);
            _ownerCapacity = Math.Max(1, ownerCapacity);
            _delayedCapacity = Math.Max(1, delayedCapacity);
            _ownerDelayedCapacity = Math.Max(1, ownerDelayedCapacity);
        }

        /// <summary>The clock this dispatcher measures delays and budgets with, in seconds.</summary>
        public double Now => _clock();

        /// <summary>True once <see cref="CaptureMainThread"/> has run.</summary>
        public bool HasMainThread => Volatile.Read(ref _mainThreadId) >= 0;

        /// <summary>True when the calling thread is the one captured as the main thread.</summary>
        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _mainThreadId);

        /// <summary>True once <see cref="Close"/> has run: nothing new is queued.</summary>
        public bool IsClosed
        {
            get { lock (_gate) { return _closed; } }
        }

        /// <summary>Items waiting for the next pump, both lanes.</summary>
        public int PendingCount
        {
            get { lock (_gate) { return _pendingTotal; } }
        }

        /// <summary>Delayed items not yet due.</summary>
        public int DelayedCount
        {
            get { lock (_gate) { return _delayed.Count; } }
        }

        /// <summary>Registered tickers.</summary>
        public int TickerCount
        {
            get { lock (_gate) { return _tickers.Count; } }
        }

        /// <summary>True when <paramref name="owner"/> is a mod (<see cref="ModOwnerPrefix"/>), whose work runs in the mod lane.</summary>
        /// <param name="owner">An owner name.</param>
        public static bool IsModOwner(string? owner) =>
            owner != null && owner.StartsWith(ModOwnerPrefix, StringComparison.Ordinal);

        /// <summary>Records the calling thread as the main thread. Call from the main thread once.</summary>
        public void CaptureMainThread() => Volatile.Write(ref _mainThreadId, Thread.CurrentThread.ManagedThreadId);

        /// <summary>
        /// Stops accepting work (the game is quitting): posts return false, <c>PostAfter</c> returns
        /// a refused handle and invocations fault with <see cref="MainThreadRejectedException"/>
        /// (<see cref="MainThreadRejectReason.ShuttingDown"/>). Work already queued stays and still
        /// runs if a pump comes. Idempotent.
        /// </summary>
        public void Close()
        {
            lock (_gate)
            {
                _closed = true;
            }
        }

        /// <summary>Items one owner has waiting.</summary>
        /// <param name="owner">The owner.</param>
        public int PendingOf(string owner)
        {
            lock (_gate)
            {
                return _pendingByOwner.TryGetValue(owner ?? string.Empty, out var count) ? count : 0;
            }
        }

        /// <summary>Delayed items one owner has.</summary>
        /// <param name="owner">The owner.</param>
        public int DelayedOf(string owner)
        {
            lock (_gate)
            {
                return _delayedByOwner.TryGetValue(owner ?? string.Empty, out var count) ? count : 0;
            }
        }

        /// <summary>Items refused for one owner because a queue was full.</summary>
        /// <param name="owner">The owner.</param>
        public long DroppedOf(string owner)
        {
            lock (_gate)
            {
                return _droppedByOwner.TryGetValue(owner ?? string.Empty, out var count) ? count : 0;
            }
        }

        /// <summary>
        /// Queues work for the next pump, even when called on the main thread.
        /// </summary>
        /// <param name="owner">Who is posting: a feature name or a mod id.</param>
        /// <param name="label">What the work is, for failure lines.</param>
        /// <param name="work">The work. Its exceptions go to the failure callback.</param>
        /// <returns>True when queued; false when the owner's or the total queue was full, or the queue is closed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public bool Post(string owner, string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return Enqueue(new WorkItem(owner, label, work, null)) is null;
        }

        /// <summary>
        /// Runs work now when called on the main thread (exceptions reported, never rethrown),
        /// otherwise queues it like <see cref="Post"/>. The call shape the Satone API has always had.
        /// </summary>
        /// <param name="owner">Who is posting.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <returns>True when it ran or was queued; false when the queue was full or closed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public bool RunOrPost(string owner, string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (!IsMainThread)
            {
                return Post(owner, label, work);
            }

            var item = new WorkItem(owner, label, work, null);
            Interlocked.Increment(ref _posted);
            Execute(item);
            return true;
        }

        /// <summary>
        /// Queues work to run on the first pump at least <paramref name="seconds"/> from now.
        /// </summary>
        /// <param name="seconds">The delay on <see cref="Now"/>'s clock. Zero, less or NaN is a plain <see cref="Post"/>.</param>
        /// <param name="owner">Who is posting.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <returns>
        /// A handle: dispose it to cancel the work before it runs. When the dispatcher refused the
        /// work, <see cref="DelayedWork.IsAccepted"/> is false and <see cref="DelayedWork.RejectReason"/> says why.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        public DelayedWork PostAfter(double seconds, string owner, string label, Action work)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            var item = new WorkItem(owner, label, work, null);
            if (!(seconds > 0))
            {
                var refused = Enqueue(item);
                return refused is null ? new DelayedWork(this, item) : new DelayedWork(refused.Value);
            }

            lock (_gate)
            {
                if (_closed)
                {
                    return new DelayedWork(MainThreadRejectReason.ShuttingDown);
                }

                if (_delayed.Count >= _delayedCapacity || Count(_delayedByOwner, item.Owner) >= _ownerDelayedCapacity)
                {
                    DropLocked(item.Owner);
                    return new DelayedWork(MainThreadRejectReason.QueueFull);
                }

                item.Sequence = ++_sequence;
                item.Due = _clock() + seconds;
                item.FromDelayed = true;
                _delayed.Add(item);
                Add(_delayedByOwner, item.Owner, 1);
            }

            Interlocked.Increment(ref _posted);
            return new DelayedWork(this, item);
        }

        /// <summary>Runs work on the main thread and completes the task with its outcome.</summary>
        /// <param name="owner">Who is asking.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <param name="timeout">
        /// Wall-clock limit on the caller's side; null or <see cref="Timeout.InfiniteTimeSpan"/>
        /// waits forever, and so does anything above <see cref="int.MaxValue"/> milliseconds (about
        /// 24.8 days, the timer's limit). When it passes, the task faults with <see cref="TimeoutException"/>.
        /// </param>
        /// <param name="cancellationToken">Cancels the wait (and the work, if it has not started).</param>
        /// <returns>
        /// A task that completes when the work ran, faults with the work's exception (or a
        /// <see cref="MainThreadRejectedException"/> when it was refused), or ends at the timeout or
        /// cancellation. Always queued, even on the main thread, so awaiting it from the main thread
        /// cannot deadlock.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not infinite.</exception>
        public Task InvokeAsync(string owner, string label, Action work, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            return InvokeAsync<bool>(owner, label, () => { work(); return true; }, timeout, cancellationToken);
        }

        /// <summary>Runs a function on the main thread and completes the task with its result.</summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="owner">Who is asking.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function.</param>
        /// <param name="timeout">See <see cref="InvokeAsync(string, string, Action, TimeSpan?, CancellationToken)"/>.</param>
        /// <param name="cancellationToken">Cancels the wait (and the work, if it has not started).</param>
        /// <returns>See <see cref="InvokeAsync(string, string, Action, TimeSpan?, CancellationToken)"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is negative and not infinite.</exception>
        public Task<T> InvokeAsync<T>(string owner, string label, Func<T> work, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            // Validated and clamped before anything is queued: a refusal after Enqueue would leave
            // work running that the caller was told had failed.
            var limit = EffectiveTimeout(timeout);

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return completion.Task;
            }

            var item = new WorkItem(
                owner,
                label,
                () => completion.TrySetResult(work()),
                error => completion.TrySetException(error))
            {
                Cancelled = () => completion.TrySetCanceled(),
            };

            var refused = Enqueue(item);
            if (refused != null)
            {
                completion.TrySetException(new MainThreadRejectedException(refused.Value, item.Owner, item.Label));
                return completion.Task;
            }

            Arm(item, completion, item.Label, limit, cancellationToken);
            return completion.Task;
        }

        /// <summary>
        /// Runs a function on the main thread and waits for it: inline when called on the main
        /// thread, otherwise queued and waited for up to <paramref name="timeout"/>.
        /// </summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="owner">Who is asking.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function.</param>
        /// <param name="timeout">How long a background caller waits. Required: a blocked caller must always come back.</param>
        /// <returns>The function's result.</returns>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The dispatcher refused the work.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        /// <remarks>The work's own exception is rethrown to the caller.</remarks>
        public T Invoke<T>(string owner, string label, Func<T> work, TimeSpan timeout)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            if (IsMainThread)
            {
                return work();
            }

            return InvokeAsync(owner, label, work, timeout).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Runs work on the main thread and waits for it: inline when called on the main thread,
        /// otherwise queued and waited for up to <paramref name="timeout"/>.
        /// </summary>
        /// <param name="owner">Who is asking.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <param name="timeout">How long a background caller waits.</param>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The dispatcher refused the work.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="work"/> is null.</exception>
        /// <remarks>The work's own exception is rethrown to the caller.</remarks>
        public void Invoke(string owner, string label, Action work, TimeSpan timeout)
        {
            if (work is null)
            {
                throw new ArgumentNullException(nameof(work));
            }

            Invoke<bool>(owner, label, () => { work(); return true; }, timeout);
        }

        /// <summary>
        /// Adds a callback that runs once per pump, after the queued work, until the returned
        /// handle is disposed. A ticker that throws is reported and stays registered.
        /// </summary>
        /// <param name="owner">Who registers it.</param>
        /// <param name="label">What it is.</param>
        /// <param name="tick">The callback.</param>
        /// <returns>Dispose to remove it; disposing twice is harmless.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="tick"/> is null.</exception>
        public IDisposable AddTicker(string owner, string label, Action tick)
        {
            if (tick is null)
            {
                throw new ArgumentNullException(nameof(tick));
            }

            var ticker = new Ticker(this, owner ?? string.Empty, label ?? string.Empty, tick);
            lock (_gate)
            {
                _tickers.Add(ticker);
                _tickerSnapshot = _tickers.ToArray();
            }

            return ticker;
        }

        /// <summary>Drops every waiting and delayed item of one owner; their tasks end as cancelled.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many items were dropped.</returns>
        public int Cancel(string owner) => CancelWhere(owner ?? string.Empty, delayedOnly: false);

        /// <summary>
        /// Drops one owner's delayed items that have not run: those not yet due, and those already
        /// due but still waiting in a lane (a restore promoted and then held back by the
        /// budget used to escape the cancel).
        /// </summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many were dropped.</returns>
        public int CancelDelayed(string owner) => CancelWhere(owner ?? string.Empty, delayedOnly: true);

        /// <summary>Removes every ticker one owner registered.</summary>
        /// <param name="owner">The owner.</param>
        /// <returns>How many were removed.</returns>
        public int RemoveTickers(string owner)
        {
            var key = owner ?? string.Empty;
            lock (_gate)
            {
                var removed = _tickers.RemoveAll(t => string.Equals(t.Owner, key, StringComparison.Ordinal));
                if (removed > 0)
                {
                    _tickerSnapshot = _tickers.ToArray();
                }

                return removed;
            }
        }

        /// <summary>
        /// One frame of work: moves due delayed items into their lane, runs every framework item
        /// that was waiting, then mod items round-robin until the budget is spent (at least one),
        /// then every ticker. Main thread only. Never throws.
        /// </summary>
        /// <param name="budgetSeconds">
        /// Time allowed for the mod lane this frame, measured from the moment it starts. Framework
        /// work and tickers are not counted. Zero, less or NaN runs one mod item.
        /// </param>
        /// <returns>How many items ran and how many were left for the next pump.</returns>
        public MainThreadPumpResult Pump(double budgetSeconds)
        {
            if (!(budgetSeconds > 0))
            {
                budgetSeconds = 0;
            }

            var start = _clock();
            long cutoff;
            lock (_gate)
            {
                PromoteDueLocked(start);
                cutoff = _sequence;
            }

            var ran = 0;

            // Framework lane: everything that was waiting when the pump began, unbudgeted.
            while (true)
            {
                WorkItem? item;
                lock (_gate)
                {
                    item = TakeFrameworkLocked(cutoff);
                }

                if (item is null)
                {
                    break;
                }

                Execute(item);
                ran++;
            }

            // Mod lane: round-robin across mods, within the budget, at least one item.
            var modStart = _clock();
            var modTaken = 0;
            while (true)
            {
                if (modTaken > 0 && _clock() - modStart >= budgetSeconds)
                {
                    bool more;
                    lock (_gate)
                    {
                        more = HasEligibleModLocked(cutoff);
                    }

                    if (more)
                    {
                        Interlocked.Increment(ref _deferred);
                    }

                    break;
                }

                WorkItem? item;
                lock (_gate)
                {
                    item = TakeModLocked(cutoff);
                }

                if (item is null)
                {
                    break;
                }

                Execute(item);
                modTaken++;
            }

            ran += modTaken;

            int left;
            lock (_gate)
            {
                left = _pendingTotal;
            }

            RunTickers();

            var elapsedMs = (_clock() - start) * 1000.0;
            lock (_gate)
            {
                _pumps++;
                _lastPumpMs = elapsedMs;
                if (elapsedMs > _maxPumpMs)
                {
                    _maxPumpMs = elapsedMs;
                }
            }

            return new MainThreadPumpResult(ran, left);
        }

        /// <summary>A snapshot of the counters.</summary>
        public MainThreadStats Snapshot()
        {
            lock (_gate)
            {
                return new MainThreadStats(
                    _framework.Count, _pendingTotal - _framework.Count, _modRing.Count, _delayed.Count, _tickers.Count,
                    Interlocked.Read(ref _posted), Interlocked.Read(ref _frameworkRan), Interlocked.Read(ref _modRan),
                    Interlocked.Read(ref _failed), _dropped, Interlocked.Read(ref _cancelled), Interlocked.Read(ref _timedOut),
                    Interlocked.Read(ref _deferred), _pumps, _lastPumpMs, _maxPumpMs);
            }
        }

        /// <summary>
        /// The timeout an invocation actually uses: null for "wait forever" (null, infinite, or
        /// longer than a timer can count), otherwise the value.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Negative and not infinite.</exception>
        internal static TimeSpan? EffectiveTimeout(TimeSpan? timeout)
        {
            if (!timeout.HasValue || timeout.Value == Timeout.InfiniteTimeSpan)
            {
                return null;
            }

            if (timeout.Value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "a timeout must be positive, or null / infinite");
            }

            return timeout.Value.TotalMilliseconds > int.MaxValue ? (TimeSpan?)null : timeout.Value;
        }

        /// <summary>Drops one delayed item if it has not started. Called by <see cref="DelayedWork.Cancel"/>.</summary>
        internal bool CancelItem(WorkItem item)
        {
            if (!item.TryCancel())
            {
                return false;
            }

            lock (_gate)
            {
                if (_delayed.Remove(item))
                {
                    Add(_delayedByOwner, item.Owner, -1);
                }
                else
                {
                    RemoveFromLaneLocked(item);
                }
            }

            Interlocked.Increment(ref _cancelled);
            item.Cancelled?.Invoke();
            return true;
        }

        /// <summary>Queues an item in its lane.</summary>
        /// <returns>Null when queued; otherwise why not.</returns>
        private MainThreadRejectReason? Enqueue(WorkItem item)
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return MainThreadRejectReason.ShuttingDown;
                }

                if (_pendingTotal >= _capacity || Count(_pendingByOwner, item.Owner) >= _ownerCapacity)
                {
                    DropLocked(item.Owner);
                    return MainThreadRejectReason.QueueFull;
                }

                item.Sequence = ++_sequence;
                item.Ready = _clock();
                AddToLaneLocked(item);
            }

            Interlocked.Increment(ref _posted);
            return null;
        }

        private void AddToLaneLocked(WorkItem item)
        {
            LinkedList<WorkItem> lane;
            if (item.IsMod)
            {
                if (!_modQueues.TryGetValue(item.Owner, out lane!))
                {
                    lane = new LinkedList<WorkItem>();
                    _modQueues[item.Owner] = lane;
                    _modRing.Add(item.Owner);
                }
            }
            else
            {
                lane = _framework;
            }

            if (item.FromDelayed)
            {
                // Ready order: after everything that became ready before it, before anything
                // posted after its due time. Scans from the tail, where new work is.
                var node = lane.Last;
                while (node != null && ReadyLater(node.Value, item))
                {
                    node = node.Previous;
                }

                item.Node = node is null ? lane.AddFirst(item) : lane.AddAfter(node, item);
            }
            else
            {
                item.Node = lane.AddLast(item);
            }

            _pendingTotal++;
            Add(_pendingByOwner, item.Owner, 1);
        }

        private static bool ReadyLater(WorkItem a, WorkItem b) =>
            a.Ready > b.Ready || (a.Ready == b.Ready && a.Sequence > b.Sequence);

        /// <summary>Removes an item from whichever lane holds it. False when it is in none (already taken).</summary>
        private bool RemoveFromLaneLocked(WorkItem item)
        {
            var node = item.Node;
            if (node?.List is null)
            {
                return false;
            }

            var lane = node.List;
            lane.Remove(node);
            item.Node = null;
            _pendingTotal--;
            Add(_pendingByOwner, item.Owner, -1);
            if (item.IsMod && lane.Count == 0)
            {
                RemoveModOwnerLocked(item.Owner);
            }

            return true;
        }

        private void RemoveModOwnerLocked(string owner)
        {
            _modQueues.Remove(owner);
            var index = _modRing.IndexOf(owner);
            if (index < 0)
            {
                return;
            }

            _modRing.RemoveAt(index);
            if (index < _modCursor)
            {
                _modCursor--;
            }

            if (_modCursor >= _modRing.Count)
            {
                _modCursor = 0;
            }
        }

        private WorkItem? TakeFrameworkLocked(long cutoff)
        {
            var head = _framework.First;
            if (head is null || head.Value.Sequence > cutoff)
            {
                return null;
            }

            _framework.RemoveFirst();
            head.Value.Node = null;
            _pendingTotal--;
            Add(_pendingByOwner, head.Value.Owner, -1);
            return head.Value;
        }

        /// <summary>The next mod item, one mod at a time in ring order; null when no mod has work that was waiting at the pump's start.</summary>
        private WorkItem? TakeModLocked(long cutoff)
        {
            var count = _modRing.Count;
            for (var i = 0; i < count; i++)
            {
                var index = (_modCursor + i) % count;
                var owner = _modRing[index];
                var lane = _modQueues[owner];
                var head = lane.First;
                if (head is null || head.Value.Sequence > cutoff)
                {
                    continue;
                }

                lane.RemoveFirst();
                head.Value.Node = null;
                _pendingTotal--;
                Add(_pendingByOwner, owner, -1);

                if (lane.Count == 0)
                {
                    _modQueues.Remove(owner);
                    _modRing.RemoveAt(index);
                    _modCursor = _modRing.Count == 0 ? 0 : index % _modRing.Count;
                }
                else
                {
                    _modCursor = (index + 1) % count;
                }

                return head.Value;
            }

            return null;
        }

        private bool HasEligibleModLocked(long cutoff)
        {
            foreach (var owner in _modRing)
            {
                var head = _modQueues[owner].First;
                if (head != null && head.Value.Sequence <= cutoff)
                {
                    return true;
                }
            }

            return false;
        }

        private void Arm<T>(WorkItem item, TaskCompletionSource<T> completion, string label, TimeSpan? timeout, CancellationToken cancellationToken)
        {
            var hasTimeout = timeout.HasValue;
            if (!hasTimeout && !cancellationToken.CanBeCanceled)
            {
                return;
            }

            var timer = hasTimeout ? new CancellationTokenSource(timeout!.Value) : null;
            var linked = timer != null && cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(timer.Token, cancellationToken)
                : null;
            var token = linked?.Token ?? timer?.Token ?? cancellationToken;

            var registration = token.Register(() =>
            {
                var notStarted = item.TryCancel();
                if (notStarted)
                {
                    lock (_gate)
                    {
                        RemoveFromLaneLocked(item);
                    }

                    Interlocked.Increment(ref _cancelled);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                if (completion.Task.IsCompleted)
                {
                    return;
                }

                // Counted before the task completes, so a caller that reads the stats as soon as
                // its await returns sees its own timeout.
                Interlocked.Increment(ref _timedOut);
                completion.TrySetException(new TimeoutException(
                    "the main thread did not " + (notStarted ? "run" : "finish") + " '" + label + "' within " +
                    timeout!.Value.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                    (notStarted ? string.Empty : "; it is still running and its result will be discarded")));
            });

            completion.Task.ContinueWith(
                _ =>
                {
                    registration.Dispose();
                    linked?.Dispose();
                    timer?.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void Execute(WorkItem item)
        {
            if (!item.TryStart())
            {
                return;
            }

            try
            {
                item.Body();
                if (item.IsMod)
                {
                    Interlocked.Increment(ref _modRan);
                }
                else
                {
                    Interlocked.Increment(ref _frameworkRan);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failed);
                var delivered = false;
                if (item.Fault != null)
                {
                    item.Fault(ex);
                    delivered = true;
                }

                Report(new MainThreadFailure(item.Owner, item.Label, ex, isTicker: false, deliveredToCaller: delivered));
            }
        }

        private void RunTickers()
        {
            Ticker[] tickers;
            lock (_gate)
            {
                tickers = _tickerSnapshot;
            }

            foreach (var ticker in tickers)
            {
                if (ticker.Removed)
                {
                    continue;
                }

                try
                {
                    ticker.Tick();
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    Report(new MainThreadFailure(ticker.Owner, ticker.Label, ex, isTicker: true, deliveredToCaller: false));
                }
            }
        }

        private void Report(MainThreadFailure failure)
        {
            if (_onFailure is null)
            {
                TraceDiagnostics.Report(
                    "main thread: " + failure.Owner + " '" + failure.Label + "' threw", failure.Error);
                return;
            }

            try
            {
                _onFailure(failure);
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("main thread: the failure callback threw while reporting " + failure.Label, ex);
            }
        }

        private void PromoteDueLocked(double now)
        {
            if (_delayed.Count == 0)
            {
                return;
            }

            List<WorkItem>? due = null;
            for (var i = _delayed.Count - 1; i >= 0; i--)
            {
                var item = _delayed[i];
                if (item.Due > now)
                {
                    continue;
                }

                (due ??= new List<WorkItem>()).Add(item);
                _delayed.RemoveAt(i);
                Add(_delayedByOwner, item.Owner, -1);
            }

            if (due is null)
            {
                return;
            }

            // Due order first, then posting order for items due at the same moment.
            due.Sort((a, b) => a.Due != b.Due ? a.Due.CompareTo(b.Due) : a.Sequence.CompareTo(b.Sequence));
            foreach (var item in due)
            {
                item.Ready = item.Due;
                AddToLaneLocked(item);
            }
        }

        private int CancelWhere(string owner, bool delayedOnly)
        {
            var dropped = new List<WorkItem>();
            lock (_gate)
            {
                for (var i = _delayed.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_delayed[i].Owner, owner, StringComparison.Ordinal))
                    {
                        dropped.Add(_delayed[i]);
                        _delayed.RemoveAt(i);
                        Add(_delayedByOwner, owner, -1);
                    }
                }

                LinkedList<WorkItem>? lane;
                if (IsModOwner(owner))
                {
                    _modQueues.TryGetValue(owner, out lane);
                }
                else
                {
                    lane = _framework;
                }

                var node = lane?.First;
                while (node != null)
                {
                    var next = node.Next;
                    var item = node.Value;
                    if (string.Equals(item.Owner, owner, StringComparison.Ordinal) && (!delayedOnly || item.FromDelayed))
                    {
                        dropped.Add(item);
                        RemoveFromLaneLocked(item);
                    }

                    node = next;
                }
            }

            foreach (var item in dropped)
            {
                if (item.TryCancel())
                {
                    Interlocked.Increment(ref _cancelled);
                    item.Cancelled?.Invoke();
                }
            }

            return dropped.Count;
        }

        private void DropLocked(string owner)
        {
            _dropped++;
            Add(_droppedByOwner, owner, 1);
        }

        private static int Count(Dictionary<string, int> counts, string owner) =>
            counts.TryGetValue(owner, out var count) ? count : 0;

        private static void Add(Dictionary<string, int> counts, string owner, int delta)
        {
            var value = Count(counts, owner) + delta;
            if (value <= 0)
            {
                counts.Remove(owner);
            }
            else
            {
                counts[owner] = value;
            }
        }

        private static void Add(Dictionary<string, long> counts, string owner, long delta) =>
            counts[owner] = (counts.TryGetValue(owner, out var value) ? value : 0) + delta;

        private void RemoveTicker(Ticker ticker)
        {
            lock (_gate)
            {
                if (_tickers.Remove(ticker))
                {
                    _tickerSnapshot = _tickers.ToArray();
                }
            }
        }

        /// <summary>One queued piece of work. Internal so <see cref="DelayedWork"/> can hold one.</summary>
        internal sealed class WorkItem
        {
            private int _state;

            internal WorkItem(string? owner, string? label, Action body, Action<Exception>? fault)
            {
                Owner = owner ?? string.Empty;
                Label = label ?? string.Empty;
                Body = body;
                Fault = fault;
                IsMod = IsModOwner(Owner);
            }

            internal string Owner { get; }

            internal string Label { get; }

            internal Action Body { get; }

            internal Action<Exception>? Fault { get; }

            /// <summary>True for a <c>mod:</c> owner: the budgeted, round-robin lane.</summary>
            internal bool IsMod { get; }

            /// <summary>Posting order; assigned under the queue lock when the item is accepted.</summary>
            internal long Sequence { get; set; }

            /// <summary>When the item became ready to run: its post time, or its due time once promoted.</summary>
            internal double Ready { get; set; }

            internal double Due { get; set; }

            /// <summary>True when the item was posted with a delay (even after it was promoted into a lane).</summary>
            internal bool FromDelayed { get; set; }

            /// <summary>The item's node while it waits in a lane; null otherwise.</summary>
            internal LinkedListNode<WorkItem>? Node { get; set; }

            /// <summary>Called when the item is dropped before it started (an awaiting task ends as cancelled).</summary>
            internal Action? Cancelled { get; set; }

            /// <summary>True until the item starts or is cancelled.</summary>
            internal bool IsWaiting => Volatile.Read(ref _state) == StateWaiting;

            internal bool TryStart() => Interlocked.CompareExchange(ref _state, StateStarted, StateWaiting) == StateWaiting;

            internal bool TryCancel() => Interlocked.CompareExchange(ref _state, StateCancelled, StateWaiting) == StateWaiting;
        }

        private sealed class Ticker : IDisposable
        {
            private readonly MainThreadQueue _owner;
            private int _removed;

            internal Ticker(MainThreadQueue owner, string ownerName, string label, Action tick)
            {
                _owner = owner;
                Owner = ownerName;
                Label = label;
                Tick = tick;
            }

            internal string Owner { get; }

            internal string Label { get; }

            internal Action Tick { get; }

            internal bool Removed => Volatile.Read(ref _removed) != 0;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _removed, 1) == 0)
                {
                    _owner.RemoveTicker(this);
                }
            }
        }
    }

    /// <summary>What one <see cref="MainThreadQueue.Pump"/> did.</summary>
    public readonly struct MainThreadPumpResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="ran">Items that ran (or were skipped as cancelled) this pump.</param>
        /// <param name="left">Items left for the next pump.</param>
        public MainThreadPumpResult(int ran, int left)
        {
            Ran = ran;
            Left = left;
        }

        /// <summary>Items taken off the queue this pump.</summary>
        public int Ran { get; }

        /// <summary>Items left for the next pump.</summary>
        public int Left { get; }
    }
}
