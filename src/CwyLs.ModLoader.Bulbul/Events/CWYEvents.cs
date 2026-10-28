using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// The typed game event bus: everything the framework knows about what is
    /// happening in the game, in one place, for mods to subscribe to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Subscribe with <see cref="Subscribe{T}"/> for one event type, or <see cref="SubscribeAny"/>
    /// for all of them. Dispose the returned token to unsubscribe - and never throw from a handler;
    /// an exception is caught, logged with your method's name and dropped, but the cost of throwing
    /// inside the game's own call stack is yours to avoid. See <c>Events/README.md</c>.
    /// </para>
    /// <para>
    /// Threading: every handler is called on the Unity main thread. Publishers that are already on
    /// it (all of the Harmony postfixes, in practice) call through directly; anything else is queued
    /// and drained by <see cref="EventRunner"/> on the next frame.
    /// </para>
    /// <para>
    /// Every published event is also emitted once to <see cref="TraceBus"/> so the diagnostic
    /// stream records it whether or not any mod is listening: <see cref="LineSpoken"/> as
    /// <c>satone/speech</c>, <see cref="SatoneStateChanged"/> as <c>satone/state</c>, and every
    /// other event as <c>event/&lt;EventTypeName&gt;</c>.
    /// </para>
    /// </remarks>
    public static class CWYEvents
    {
        /// <summary>Config section the bus binds its settings in.</summary>
        public const string ConfigSection = "Events";

        /// <summary>Prefix of every mirrored console line, so <c>LogOutput.log</c> is greppable.</summary>
        public const string LogPrefix = "EVENT";

        /// <summary>Subsystem tag every event is emitted to <see cref="TraceBus"/> under.</summary>
        public const string TraceSystem = "event";

        /// <summary>Subsystem tag the curated Satone lines are emitted under.</summary>
        public const string SatoneTraceSystem = "satone";

        /// <summary>Bus event name a <see cref="LineSpoken"/> is published under: <c>satone/speech</c>.</summary>
        public const string SatoneSpeechEvent = "speech";

        /// <summary>Bus event name a <see cref="SatoneStateChanged"/> is published under: <c>satone/state</c>.</summary>
        public const string SatoneStateEvent = "state";

        /// <summary>
        /// How many cross-thread publishes may wait for the main thread before the oldest are
        /// dropped. Reached only if the runner is gone, in which case dropping beats leaking.
        /// </summary>
        public const int QueueCapacity = 4096;

        private static readonly Dictionary<Type, object> Channels = new Dictionary<Type, object>();
        private static readonly EventDispatcher<AnyEvent> AnyChannel =
            new EventDispatcher<AnyEvent>(nameof(AnyEvent), ReportFailure);

        private static readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        private static int _mainThreadId = -1;
        private static int _queued;
        private static long _dropped;

        static CWYEvents()
        {
            Add<SatoneStateChanged>();
            Add<LineSpoken>();
            Add<LineEnded>();
            Add<ScenarioStarted>();
            Add<ScenarioEnded>();
            Add<PomodoroEvent>();
            Add<PomodoroTick>();
            Add<WindowChanged>();
            Add<MusicChanged>();
            Add<SaveSection>();
            Add<SceneChanged>();
            Add<GameClockTick>();
            Add<RoomReady>();
        }

        /// <summary>
        /// True once <see cref="EventBus.Initialize"/> has applied the patches and armed the host.
        /// Subscribing before that is allowed - the channels exist from class load - it just means
        /// no event has been produced yet.
        /// </summary>
        public static bool IsReady { get; internal set; }

        /// <summary>The most recent <see cref="LineSpoken"/>, or null if Satone has not spoken yet.</summary>
        public static LineSpoken? LastLine { get; private set; }

        /// <summary>
        /// How many <see cref="LineSpoken"/> events carried a translated text, a Japanese text and
        /// a recognised <see cref="LineSource"/>. The <c>p1.events.speech_detail</c> smoke check
        /// reads this.
        /// </summary>
        public static long DetailedLineCount { get; private set; }

        /// <summary>Cross-thread publishes dropped because the queue was full.</summary>
        public static long DroppedFromQueue => Interlocked.Read(ref _dropped);

        /// <summary>Every event type the bus publishes, in declaration order.</summary>
        public static IReadOnlyList<Type> EventTypes
        {
            get
            {
                var types = new List<Type>(Channels.Count);
                foreach (var key in Channels.Keys)
                {
                    types.Add(key);
                }

                return types;
            }
        }

        /// <summary>
        /// Subscribes to one event type. Dispose the returned token to unsubscribe; disposing twice
        /// is harmless.
        /// </summary>
        /// <typeparam name="T">One of the event types listed in <c>Events/README.md</c>.</typeparam>
        /// <param name="handler">
        /// Your callback, invoked on the Unity main thread. It must not throw: an exception is
        /// caught and logged, and the other subscribers still run.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is not one of the bus's event types.</exception>
        public static IDisposable Subscribe<T>(Action<T> handler)
            where T : ICwyEvent =>
            Channel<T>().Subscribe(handler);

        /// <summary>
        /// Subscribes to every event as a <c>(name, args)</c> pair. Use this to log or forward the
        /// whole stream; use <see cref="Subscribe{T}"/> when you care about one thing.
        /// </summary>
        /// <param name="handler">Your callback, invoked on the Unity main thread. It must not throw.</param>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public static IDisposable SubscribeAny(Action<AnyEvent> handler) => AnyChannel.Subscribe(handler);

        /// <summary>How many events of type <typeparamref name="T"/> have been published this session.</summary>
        /// <typeparam name="T">One of the bus's event types.</typeparam>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is not one of the bus's event types.</exception>
        public static long CountOf<T>()
            where T : ICwyEvent =>
            Channel<T>().PublishCount;

        /// <summary>How many handlers are subscribed to <typeparamref name="T"/>.</summary>
        /// <typeparam name="T">One of the bus's event types.</typeparam>
        /// <exception cref="ArgumentException"><typeparamref name="T"/> is not one of the bus's event types.</exception>
        public static int SubscriberCountOf<T>()
            where T : ICwyEvent =>
            Channel<T>().SubscriberCount;

        /// <summary>Total events published across every channel this session.</summary>
        public static long TotalPublished => AnyChannel.PublishCount;

        /// <summary>
        /// Publishes an event. Internal: only the framework's own sources produce events, so a
        /// mod cannot fake one and mislead every other mod on the bus.
        /// </summary>
        /// <typeparam name="T">The event type.</typeparam>
        /// <param name="args">The event arguments; a null is ignored rather than dispatched.</param>
        internal static void Publish<T>(T args)
            where T : ICwyEvent
        {
            if (args is null)
            {
                return;
            }

            // Harmony postfixes run on whatever thread called the game method. Everything the bus
            // hands to a subscriber is promised to be on the main thread, so anything else waits
            // for the runner's next Update.
            if (_mainThreadId >= 0 && Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                Enqueue(() => PublishNow(args));
                return;
            }

            PublishNow(args);
        }

        /// <summary>
        /// Records the thread that counts as the Unity main thread. Called from the plugin's Awake,
        /// which BepInEx runs on it.
        /// </summary>
        internal static void CaptureMainThread() => _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        /// <summary>
        /// Binds the bus's settings from the plugin's .cfg. Kept here so the descriptions a modder
        /// reads live next to the properties they set.
        /// </summary>
        /// <param name="config">The plugin's config file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
        internal static void BindConfig(ConfigFile config)
        {
            if (config is null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            // The Events.MirrorToConsole and Events.MirrorTicks settings, and the [Obsolete]
            // CWYEvents properties of the same names, were removed. Each event kind has one console
            // owner now, so "print everything again, differently" cannot be switched on.
        }

        /// <summary>
        /// Runs the queued cross-thread publishes. Called once per frame by <see cref="EventRunner"/>;
        /// never throws.
        /// </summary>
        internal static void PumpMainThread()
        {
            while (MainThreadQueue.TryDequeue(out var work))
            {
                Interlocked.Decrement(ref _queued);
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // PublishNow already isolates subscribers, so reaching here means the bus
                    // itself failed. Log it and keep draining: one bad event must not stall the rest.
                    CwyLsModLoader.Log.LogError(
                        $"EVENT queued publish failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static void PublishNow<T>(T args)
            where T : ICwyEvent
        {
            var name = typeof(T).Name;

            if (args is LineSpoken line)
            {
                LastLine = line;
                if (!string.IsNullOrEmpty(line.Text)
                    && !string.IsNullOrEmpty(line.TextJa)
                    && line.Source != LineSource.Other)
                {
                    DetailedLineCount++;
                }
            }

            // Order matters: the diagnostic stream records the event before any mod sees it, so a
            // mod that hangs or crashes the frame still leaves evidence of what it was handed.
            Trace(name, args);

            Channel<T>().Publish(args);
            AnyChannel.Publish(new AnyEvent(name, args));
        }

        private static void Trace(string name, ICwyEvent args)
        {
            var payload = args.Fields();

            // One bus publish per event. The two Satone events go out under sys "satone" with the
            // curated names the diagnostic console formats (SatoneConsoleLines.Speech/State);
            // everything else goes out under sys "event" with the event type's name. They used to
            // be published under both, which put an identical payload on the bus twice - 28 pairs
            // of them in a single session - and left the console correct only because the mirror
            // allow-list happens not to list event/LineSpoken. The typed CWYEvents channels are
            // unchanged: a subscriber saw one event then and sees one now.
            switch (args)
            {
                case LineSpoken:
                    TraceBus.Emit(SatoneTraceSystem, SatoneSpeechEvent, payload);
                    break;
                case SatoneStateChanged:
                    TraceBus.Emit(SatoneTraceSystem, SatoneStateEvent, payload);
                    break;
                default:
                    TraceBus.Emit(TraceSystem, name, payload);
                    break;
            }
        }

        /// <summary>
        /// <see cref="ICwyEvent.Describe"/> without letting a broken implementation break the log
        /// line it was being written for.
        /// </summary>
        private static string Safe(ICwyEvent args)
        {
            try
            {
                return args.Describe();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT {args.GetType().Name}.Describe() threw: {ex.GetType().Name}: {ex.Message}");
                return "<undescribable>";
            }
        }

        private static void Enqueue(Action work)
        {
            if (Interlocked.Increment(ref _queued) > QueueCapacity)
            {
                Interlocked.Decrement(ref _queued);
                if (Interlocked.Increment(ref _dropped) == 1L)
                {
                    CwyLsModLoader.Log.LogWarning(
                        $"EVENT main-thread queue is full ({QueueCapacity}); off-thread events are being "
                        + "dropped. The event runner is probably gone.");
                }

                return;
            }

            MainThreadQueue.Enqueue(work);
        }

        private static void Add<T>()
            where T : ICwyEvent =>
            Channels[typeof(T)] = new EventDispatcher<T>(typeof(T).Name, ReportFailure);

        private static EventDispatcher<T> Channel<T>()
            where T : ICwyEvent
        {
            if (Channels.TryGetValue(typeof(T), out var channel))
            {
                return (EventDispatcher<T>)channel;
            }

            throw new ArgumentException(
                typeof(T).FullName + " is not one of the event bus's event types", nameof(T));
        }

        private static void ReportFailure(EventDispatchFailure failure) =>
            CwyLsModLoader.Log.LogError(failure.ToString());
    }
}
