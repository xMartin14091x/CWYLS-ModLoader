using System;
using System.Runtime.CompilerServices;
using System.Threading;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Threading;
using VContainer;
using VContainer.Unity;

namespace CwyLs.ModLoader.Bulbul.Actions
{
    /// <summary>The outcome of one <see cref="GameServices.Lookup{T}"/>. Immutable.</summary>
    /// <typeparam name="T">The service type asked for.</typeparam>
    public sealed class GameServiceLookup<T>
        where T : class
    {
        internal GameServiceLookup(T? service, string via, string problem)
        {
            Service = service;
            Via = via;
            Problem = problem;
        }

        /// <summary>The service, or null.</summary>
        public T? Service { get; }

        /// <summary>True when the service was found.</summary>
        public bool Found => Service != null;

        /// <summary>Where it came from: <see cref="GameServices.ViaRoom"/>, <see cref="GameServices.ViaProject"/>, <see cref="GameServices.ViaScene"/>; empty when not found.</summary>
        public string Via { get; }

        /// <summary>Why it was not found; empty when found.</summary>
        public string Problem { get; }

        /// <summary><c>HeroineService via room</c>, <c>HeroineService: not available (the room is not loaded)</c>.</summary>
        public override string ToString() => typeof(T).Name + (Found ? " via " + Via : ": " + Problem);
    }

    /// <summary>
    /// <c>GameServices.Resolve&lt;T&gt;()</c>: the game's own services for action
    /// handlers, from the game's VContainer scopes, without side effects.
    /// </summary>
    /// <remarks>
    /// <para>Order: the room scope (pomodoro, music facility, scenario, Satone - alive only while
    /// RoomScene is loaded), then the project scope (music service, master data, settings - alive from
    /// boot), then, for a component type, the live scene objects (<c>Object.FindObjectOfType</c>).
    /// The room scope is found as the live <c>RoomLifetimeScope</c> object, never through
    /// <c>RoomLifetimeScope.Resolve</c>/<c>GetOrCreate</c>, which instantiate a scope when none
    /// exists. Lookups use <c>TryResolve</c>, so a type the scope does not
    /// register answers false instead of throwing.</para>
    /// <para>Main thread only (Unity objects). Not cached: room services die with the room, so every
    /// call looks again (a dictionary lookup in the container). A transient registration hands out a
    /// new instance per call, as VContainer does; ask for the long-lived services.</para>
    /// <para>Fail-soft: a game update that removes one of <see cref="ActionsCoreTargets"/> switches
    /// off that lookup path with one WARN; the other paths keep working.</para>
    /// </remarks>
    public static class GameServices
    {
        /// <summary><see cref="GameServiceLookup{T}.Via"/> for the room scope.</summary>
        public const string ViaRoom = "room";

        /// <summary><see cref="GameServiceLookup{T}.Via"/> for the project scope.</summary>
        public const string ViaProject = "project";

        /// <summary><see cref="GameServiceLookup{T}.Via"/> for a live scene object.</summary>
        public const string ViaScene = "scene";

        private static int _roomPath = 1;
        private static int _projectPath = 1;
        private static LifetimeScope? _room;

        /// <summary>True when RoomScene is loaded, so room services can be asked for.</summary>
        public static bool IsRoomReady => GameScopes.IsRoomLoaded;

        /// <summary>True while the room-scope lookup is on (its seams exist).</summary>
        public static bool RoomPathOn => Volatile.Read(ref _roomPath) == 1;

        /// <summary>True while the project-scope lookup is on (its seams exist).</summary>
        public static bool ProjectPathOn => Volatile.Read(ref _projectPath) == 1;

        /// <summary>The game service of type <typeparamref name="T"/>, or null when it is not available now. Main thread only.</summary>
        /// <typeparam name="T">The registered service type (a class or an interface), or a component type.</typeparam>
        public static T? Resolve<T>()
            where T : class =>
            Lookup<T>().Service;

        /// <summary>Looks a service up and says where it came from, or why not. Main thread only; never throws.</summary>
        /// <typeparam name="T">The registered service type, or a component type.</typeparam>
        public static GameServiceLookup<T> Lookup<T>()
            where T : class
        {
            if (!MainThread.IsMainThread)
            {
                return new GameServiceLookup<T>(null, string.Empty, "game services are resolved on the main thread only (action handlers run there)");
            }

            var roomReady = GameScopes.IsRoomLoaded;
            if (roomReady && RoomPathOn)
            {
                var found = FromContainer<T>(isRoom: true);
                if (found != null)
                {
                    return new GameServiceLookup<T>(found, ViaRoom, string.Empty);
                }
            }

            if (ProjectPathOn)
            {
                var found = FromContainer<T>(isRoom: false);
                if (found != null)
                {
                    return new GameServiceLookup<T>(found, ViaProject, string.Empty);
                }
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(typeof(T)))
            {
                try
                {
                    if (UnityEngine.Object.FindObjectOfType(typeof(T)) is T component)
                    {
                        return new GameServiceLookup<T>(component, ViaScene, string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("ACTION FindObjectOfType(" + typeof(T).Name + ") failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return new GameServiceLookup<T>(
                null,
                string.Empty,
                "not available" + (roomReady ? string.Empty : " (the room is not loaded)"));
        }

        /// <summary>Switches lookup paths off whose seams are missing (from the startup check).</summary>
        internal static void UseSeams(bool room, bool project)
        {
            Volatile.Write(ref _roomPath, room ? 1 : 0);
            Volatile.Write(ref _projectPath, project ? 1 : 0);
        }

        private static T? FromContainer<T>(bool isRoom)
            where T : class
        {
            IObjectResolver? container;
            try
            {
                container = isRoom ? RoomContainer() : ProjectContainer();
            }
            catch (Exception ex) when (ex is TypeLoadException || ex is MissingMemberException)
            {
                TurnOff(isRoom, ex);
                return null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("ACTION the " + (isRoom ? ViaRoom : ViaProject) + " scope is not usable yet: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }

            if (container is null)
            {
                return null;
            }

            try
            {
                return container.TryResolve<T>(out var service) ? service : null;
            }
            catch (Exception ex)
            {
                // A registered service whose construction fails: report it, the next path may still answer.
                CwyLsModLoader.Log.LogDebug("ACTION " + typeof(T).Name + " could not be resolved from the " + (isRoom ? ViaRoom : ViaProject) + " scope: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IObjectResolver? RoomContainer()
        {
            if (_room == null)
            {
                _room = UnityEngine.Object.FindObjectOfType<global::Bulbul.RoomLifetimeScope>();
            }

            return _room != null ? _room.Container : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IObjectResolver? ProjectContainer()
        {
            var scope = global::NestopiSystem.DIContainers.ProjectLifetimeScope.GetOrCreate();
            return scope != null ? scope.Container : null;
        }

        private static void TurnOff(bool isRoom, Exception ex)
        {
            var path = isRoom ? ViaRoom : ViaProject;
            var wasOn = isRoom ? Interlocked.Exchange(ref _roomPath, 0) == 1 : Interlocked.Exchange(ref _projectPath, 0) == 1;
            if (wasOn)
            {
                ConsoleEvents.EmitAt(
                    ActionAudit.TraceSys, "services", ConsoleTags.Action, ConsoleLevel.Warn,
                    "game services: the " + path + " scope lookup is off (" + ex.GetType().Name + ": " + ex.Message + ")",
                    null,
                    ("path", path));
            }
        }
    }
}
