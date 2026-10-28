using System;
using System.Collections.Generic;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using R3;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Bridges the game's own R3 observables onto the bus, so a mod hears about things that never
    /// touch a save file - a pomodoro starting, the music changing - through the same
    /// <see cref="GameData.Changed"/> stream as a save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pomodoro and music observables are subscribed once, by the event bus's
    /// <c>EventBridge</c>, which hands every firing on as a <see cref="GameSignal"/>. This class
    /// listens to those signals (<see cref="GameDataSignals"/> picks the ones that are data changes)
    /// instead of subscribing to the same observables a second time. It still subscribes to the note
    /// and to-do observables itself: nothing else in the framework does.
    /// </para>
    /// <para>
    /// The services live in the room's DI scope, which only exists once RoomScene is up, so
    /// <see cref="TryAttach"/> is called repeatedly by <see cref="DataRunner"/> until every source
    /// is attached or the attempt budget runs out. Each source is attached independently: one that
    /// a game update removes does not cost the others.
    /// </para>
    /// <para>
    /// Some of these overlap with the save notifications on purpose - completing a todo fires both
    /// <c>event:OnCompleteTodo</c> and <c>save:SaveTodoList</c>. The <see cref="GameDataChanged.Reason"/>
    /// tells them apart, and the snapshot writer's debounce collapses them into one file write.
    /// </para>
    /// </remarks>
    internal static class GameEventBridge
    {
        /// <summary>Attempts before the bridge gives up and stops retrying.</summary>
        private const int MaxAttempts = 30;

        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static IDisposable? _signals;
        private static bool _notes;
        private static bool _todos;
        private static int _attempts;
        private static bool _gaveUpLogged;

        /// <summary>
        /// True once the event bridge has attached the pomodoro service; its signals then reach
        /// the data bus.
        /// </summary>
        private static bool Pomodoro => _signals != null && EventBridge.PomodoroAttached;

        /// <summary>True once the event bridge has attached the music service.</summary>
        private static bool Music => _signals != null && EventBridge.MusicAttached;

        /// <summary>True once every source is attached, or the bridge has stopped trying.</summary>
        internal static bool IsComplete =>
            (Pomodoro && _notes && _todos && Music) || _attempts >= MaxAttempts;

        /// <summary>Names of the sources currently attached, for the startup summary and smoke checks.</summary>
        internal static IReadOnlyList<string> Attached
        {
            get
            {
                var names = new List<string>(4);
                if (Pomodoro)
                {
                    names.Add("PomodoroService");
                }

                if (_notes)
                {
                    names.Add("NoteService");
                }

                if (_todos)
                {
                    names.Add("FacilityTodo");
                }

                if (Music)
                {
                    names.Add("MusicService");
                }

                return names;
            }
        }

        /// <summary>
        /// Attaches whatever can be attached right now. Safe to call repeatedly; each source is
        /// attached at most once.
        /// </summary>
        internal static void TryAttach()
        {
            AttachSignals();

            if (Pomodoro && _notes && _todos && Music)
            {
                return;
            }

            _attempts++;

            AttachNotes();
            AttachTodos();

            if (Pomodoro && _notes && _todos && Music)
            {
                CwyLsModLoader.Log.LogDebug(
                    "DATA game events attached: " + string.Join(", ", new List<string>(Attached).ToArray()));
                return;
            }

            if (_attempts >= MaxAttempts && !_gaveUpLogged)
            {
                _gaveUpLogged = true;
                CwyLsModLoader.Log.LogWarning(
                    "DATA gave up attaching some game events after " + MaxAttempts + " attempts; attached: " +
                    (Attached.Count == 0 ? "none" : string.Join(", ", new List<string>(Attached).ToArray())) +
                    ". Save-based change notifications are unaffected.");
            }
        }

        /// <summary>
        /// Listens to the event bridge's signals, once. The bridge attaches the pomodoro and music
        /// services on its own schedule; from then on their firings arrive here.
        /// </summary>
        private static void AttachSignals()
        {
            if (_signals != null)
            {
                return;
            }

            try
            {
                _signals = EventBridge.SubscribeSignals(OnSignal);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"DATA could not listen to the event bridge: {ex.GetType().Name}: {ex.Message}. " +
                    "Pomodoro and music changes will not be reported; saves still are.");
            }
        }

        private static void OnSignal(GameSignal signal)
        {
            if (!GameDataSignals.TryMap(signal, out var section, out var reason))
            {
                return;
            }

            GameData.Raise(section, reason);
        }

        private static void AttachNotes()
        {
            if (_notes || !TryResolveRoom<global::NoteService>(out var service) || service is null)
            {
                return;
            }

            Add(service.OnAddPage.Subscribe(_ => Raise("OnAddPage")));
            Add(service.OnRemovePage.Subscribe(_ => Raise("OnRemovePage")));
            Add(service.OnEndEditNote.Subscribe(_ => Raise("OnEndEditNote")));
            _notes = true;

            static void Raise(string name) => GameData.Raise(GameDataSection.Notes, GameDataSignals.ReasonPrefix + name);
        }

        private static void AttachTodos()
        {
            if (_todos || !TryResolveRoom<Game.FacilityTodo>(out var facility) || facility is null)
            {
                return;
            }

            Add(facility.OnAddTodo.Subscribe(_ => Raise("OnAddTodo")));
            Add(facility.OnRemoveTodo.Subscribe(_ => Raise("OnRemoveTodo")));
            Add(facility.OnCompleteTodo.Subscribe(_ => Raise("OnCompleteTodo")));
            Add(facility.OnUncompleteTodo.Subscribe(_ => Raise("OnUncompleteTodo")));
            _todos = true;

            static void Raise(string name) => GameData.Raise(GameDataSection.Todos, GameDataSignals.ReasonPrefix + name);
        }

        private static bool TryResolveRoom<T>(out T? resolved)
            where T : class
        {
            try
            {
                // RoomLifetimeScope.Resolve throws until the room scene's scope exists, which is
                // the normal state for the first seconds of a session.
                resolved = Game.RoomLifetimeScope.Resolve<T>();
                return resolved != null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"DATA {typeof(T).Name} not resolvable yet: {ex.GetType().Name}: {ex.Message}");
                resolved = null;
                return false;
            }
        }

        private static void Add(IDisposable subscription)
        {
            // Kept for the life of the process: the bus outlives every scene, and a dropped
            // reference here would be a silently dead event source.
            lock (Subscriptions)
            {
                Subscriptions.Add(subscription);
            }
        }
    }
}
