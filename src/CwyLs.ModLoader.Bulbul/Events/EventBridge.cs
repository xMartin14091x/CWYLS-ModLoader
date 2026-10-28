using System;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Events;
using HarmonyLib;
using R3;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// Bridges the game's own R3 observables onto the bus. Two services publish everything the
    /// bus needs about pomodoros and music, and both already raise a subject at exactly the right
    /// moment - subscribing is more accurate than patching, because every one of the four music
    /// play paths ends in <c>onPlayMusic.OnNext</c> and two of them are <c>async</c>, so a postfix
    /// would report before the track had actually changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances arrive one of two ways: resolved from VContainer (<c>PomodoroService</c> from the
    /// room scope, <c>MusicService</c> from the project scope), or handed over by a postfix on the
    /// service's own <c>Setup</c>. Whichever wins is logged, because knowing which route worked is
    /// the difference between a five-minute and a five-hour diagnosis the day one of them stops.
    /// </para>
    /// <para>
    /// This is the framework's only subscriber to these observables. Besides publishing typed
    /// events, every discrete firing is handed on as a <see cref="GameSignal"/>
    /// (<see cref="SubscribeSignals"/>), which is how the data bus hears about pomodoro and music
    /// changes without subscribing to the game a second time. The per-frame timer update is not a
    /// signal.
    /// </para>
    /// </remarks>
    internal static class EventBridge
    {
        /// <summary>Attempts, one a second, before the bridge stops trying to resolve.</summary>
        internal const int MaxAttempts = 30;

        /// <summary>Minimum gap between two <see cref="PomodoroTick"/> events for the same timer type.</summary>
        internal const long TickThrottleMs = 1000L;

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();
        private static readonly EventThrottle TickThrottle = new EventThrottle(TickThrottleMs);
        private static readonly EventDispatcher<GameSignal> Signals =
            new EventDispatcher<GameSignal>(nameof(GameSignal), failure => CwyLsModLoader.Log.LogError("EVENT " + failure));

        private static Game.PomodoroService? _pomodoroFromPatch;
        private static global::MusicService? _musicFromPatch;
        private static PropertyInfo? _playlistProperty;
        private static int _attempts;
        private static bool _gaveUpLogged;

        /// <summary>True once the pomodoro observables are subscribed.</summary>
        internal static bool PomodoroAttached { get; private set; }

        /// <summary>True once the music observables are subscribed.</summary>
        internal static bool MusicAttached { get; private set; }

        /// <summary>How the pomodoro service was found: <c>vcontainer</c>, <c>Setup patch</c>, or empty.</summary>
        internal static string PomodoroPath { get; private set; } = string.Empty;

        /// <summary>How the music service was found: <c>vcontainer</c>, <c>Setup patch</c>, or empty.</summary>
        internal static string MusicPath { get; private set; } = string.Empty;

        /// <summary>True when both sources are attached, or the bridge has stopped trying.</summary>
        internal static bool IsComplete => (PomodoroAttached && MusicAttached) || _attempts >= MaxAttempts;

        /// <summary>How many attach attempts have been made.</summary>
        internal static int Attempts => _attempts;

        /// <summary>
        /// Listens to every discrete firing of the observables this bridge subscribes to, by the
        /// game's own names (<see cref="GameSignal"/>). For framework consumers that need those names
        /// (the data bus); mods subscribe to the typed events through <see cref="CWYEvents"/>.
        /// Handlers run on the thread the game raised the observable on (the main thread), after the
        /// typed event was published, each isolated from the others.
        /// </summary>
        /// <param name="handler">The callback.</param>
        /// <returns>The token that removes the handler.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        internal static IDisposable SubscribeSignals(Action<GameSignal> handler) => Signals.Subscribe(handler);

        /// <summary>How many signals have been handed on this session.</summary>
        internal static long SignalCount => Signals.PublishCount;

        /// <summary>Takes the pomodoro service handed over by the <c>Setup</c> postfix.</summary>
        /// <param name="service">The service instance.</param>
        internal static void CaptureFromPatch(Game.PomodoroService service)
        {
            _pomodoroFromPatch = service;
            AttachPomodoro();
        }

        /// <summary>Takes the music service handed over by the <c>Setup</c> postfix.</summary>
        /// <param name="service">The service instance.</param>
        internal static void CaptureFromPatch(global::MusicService service)
        {
            _musicFromPatch = service;
            AttachMusic();
        }

        /// <summary>
        /// Attaches whatever can be attached right now. Safe to call repeatedly; each source is
        /// attached at most once. Called once a second by <see cref="EventRunner"/>.
        /// </summary>
        internal static void TryAttach()
        {
            if (PomodoroAttached && MusicAttached)
            {
                return;
            }

            _attempts++;
            AttachPomodoro();
            AttachMusic();

            if (PomodoroAttached && MusicAttached)
            {
                return;
            }

            if (_attempts >= MaxAttempts && !_gaveUpLogged)
            {
                _gaveUpLogged = true;
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT gave up attaching game observables after {MaxAttempts} attempts; "
                    + $"pomodoro={(PomodoroAttached ? PomodoroPath : "NOT attached")}, "
                    + $"music={(MusicAttached ? MusicPath : "NOT attached")}. "
                    + "Every patch-based event is unaffected.");
            }
        }

        private static void AttachPomodoro()
        {
            if (PomodoroAttached)
            {
                return;
            }

            Game.PomodoroService? service;
            string path;
            if (_pomodoroFromPatch != null)
            {
                service = _pomodoroFromPatch;
                path = "Setup patch";
            }
            else if (GameScopes.TryRoom<Game.PomodoroService>(out var resolved) && resolved != null)
            {
                service = resolved;
                path = "vcontainer (RoomLifetimeScope)";
            }
            else
            {
                return;
            }

            try
            {
                Add(service.OnStartPomodoro.Subscribe(_ => Kind(service, PomodoroEventKind.Started, "OnStartPomodoro")));
                Add(service.OnStartWork.Subscribe(_ => Kind(service, PomodoroEventKind.WorkStarted, "OnStartWork")));
                Add(service.OnEndWork.Subscribe(_ => Kind(service, PomodoroEventKind.WorkEnded, "OnEndWork")));
                Add(service.OnStartBreak.Subscribe(_ => Kind(service, PomodoroEventKind.BreakStarted, "OnStartBreak")));
                Add(service.OnEndBreak.Subscribe(_ => Kind(service, PomodoroEventKind.BreakEnded, "OnEndBreak")));
                Add(service.OnPause.Subscribe(_ => Kind(service, PomodoroEventKind.Paused, "OnPause")));
                Add(service.OnCompletePomodoro.Subscribe(type => Typed(PomodoroEventKind.Completed, type, "OnCompletePomodoro")));
                Add(service.OnUnpause.Subscribe(type => Typed(PomodoroEventKind.Unpaused, type, "OnUnpause")));
                Add(service.OnUpdatePomodoro.Subscribe(Tick));

                PomodoroAttached = true;
                PomodoroPath = path;
                CwyLsModLoader.Log.LogDebug($"EVENT pomodoro observables attached via {path}");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT could not subscribe to PomodoroService ({path}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AttachMusic()
        {
            if (MusicAttached)
            {
                return;
            }

            global::MusicService? service;
            string path;
            if (_musicFromPatch != null)
            {
                service = _musicFromPatch;
                path = "Setup patch";
            }
            else if (GameScopes.TryProject<global::MusicService>(out var resolved) && resolved != null)
            {
                // MusicService is registered in the project scope, not the room scope
                // (NestopiSystem.DIContainers.ProjectLifetimeScope).
                service = resolved;
                path = "vcontainer (ProjectLifetimeScope)";
            }
            else
            {
                return;
            }

            try
            {
                Add(service.OnPlayMusic.Subscribe(info => Music(service, info, "OnPlayMusic")));
                Add(service.OnEnableShuffle.Subscribe(_ => Music(service, service.PlayingMusic, "OnEnableShuffle")));
                Add(service.OnDisableShuffle.Subscribe(_ => Music(service, service.PlayingMusic, "OnDisableShuffle")));

                // No typed event of its own (MusicChanged follows OnPlayMusic); subscribed here only
                // so the data bus's "event:OnChangeMusic" keeps arriving through one subscriber.
                Add(service.OnChangeMusic.Subscribe(_ => Signal(GameSignal.MusicService, "OnChangeMusic")));

                MusicAttached = true;
                MusicPath = path;
                CwyLsModLoader.Log.LogDebug($"EVENT music observables attached via {path}");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT could not subscribe to MusicService ({path}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Kind(Game.PomodoroService service, PomodoroEventKind kind, string observable)
        {
            Guard(
                () => CWYEvents.Publish(new PomodoroEvent(kind, service.CurrentPomodoroType.ToString())),
                kind.ToString());
            Signal(GameSignal.PomodoroService, observable);
        }

        private static void Typed(PomodoroEventKind kind, Game.PomodoroService.PomodoroType type, string observable)
        {
            Guard(() => CWYEvents.Publish(new PomodoroEvent(kind, type.ToString())), kind.ToString());
            Signal(GameSignal.PomodoroService, observable);
        }

        /// <summary>
        /// Hands one firing on to <see cref="SubscribeSignals"/> listeners. After the typed event,
        /// and isolated from it: a listener that throws is logged by the dispatcher and costs
        /// neither the typed event nor the R3 subscription.
        /// </summary>
        private static void Signal(string source, string observable) =>
            Guard(() => Signals.Publish(new GameSignal(source, observable)), source + "." + observable);

        private static void Tick((Game.PomodoroService.PomodoroType Type, TimeSpan Remaining, TimeSpan Total) update) =>
            Guard(
                () =>
                {
                    // The game raises this every frame (PomodoroService), so it is throttled
                    // per timer type rather than published 60 times a second.
                    var type = update.Type.ToString();
                    if (!TickThrottle.TryPass(type))
                    {
                        return;
                    }

                    // The tuple is (type, remaining, total): CurrentTimeSpan() returns the interval's
                    // configured length, not the time spent (PomodoroTimerService).
                    var remaining = update.Remaining.TotalSeconds;
                    var elapsed = Math.Max(0d, update.Total.TotalSeconds - remaining);
                    CWYEvents.Publish(new PomodoroTick(type, elapsed, remaining));
                },
                "PomodoroTick");

        private static void Music(global::MusicService service, Game.GameAudioInfo? info, string observable)
        {
            Guard(
                () =>
                {
                    if (info is null)
                    {
                        return;
                    }

                    CWYEvents.Publish(
                        new MusicChanged(info.Title ?? string.Empty, IndexOf(service, info), service.IsShuffle));
                },
                "MusicChanged");

            // The data bus has always seen OnPlayMusic whatever the track, so the signal does not
            // depend on the typed event's null check.
            Signal(GameSignal.MusicService, observable);
        }

        /// <summary>
        /// The track's position in the current playlist. Read reflectively: the property's type is
        /// <c>ObservableCollections.ObservableList&lt;T&gt;</c>, and this assembly deliberately does
        /// not reference that library - the runtime object implements <c>IList&lt;T&gt;</c>, which
        /// is all the index needs.
        /// </summary>
        private static int IndexOf(global::MusicService service, Game.GameAudioInfo info)
        {
            try
            {
                _playlistProperty ??= AccessTools.Property(typeof(global::MusicService), "CurrentPlayList");
                var playlist = _playlistProperty?.GetValue(service);
                return playlist is IList<Game.GameAudioInfo> list ? list.IndexOf(info) : -1;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not read the playlist index: {ex.GetType().Name}: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Runs a bridge callback without letting it escape into R3, which would tear the
        /// subscription down and silently end the event for the rest of the session.
        /// </summary>
        private static void Guard(Action work, string what)
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"EVENT bridge failed while publishing {what}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Add(IDisposable subscription)
        {
            // Held for the life of the process: the bus outlives every scene, and a dropped
            // reference here would be a silently dead event source.
            lock (Gate)
            {
                Subscriptions.Add(subscription);
            }
        }
    }
}
