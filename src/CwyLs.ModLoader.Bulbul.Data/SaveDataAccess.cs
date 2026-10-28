using System;
using CwyLs.ModLoader;
using Game = global::Bulbul;
using Scopes = global::NestopiSystem.DIContainers;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// Finds the live <c>Bulbul.SaveDataManager</c>, and remembers how it was found.
    /// </summary>
    /// <remarks>
    /// <para>Three paths, tried in this order, so a game update that breaks one still leaves two:</para>
    /// <list type="number">
    /// <item><description>
    /// <c>harmony</c> - captured from a postfix on <c>SaveDataManager.Load()</c>. The method is
    /// <c>async UniTask</c>, so the postfix runs when the outer method hands back its awaitable,
    /// i.e. at its first <c>await</c>, long before the data is actually loaded - which is exactly
    /// what we want for grabbing the instance, and exactly why it is not the readiness signal.
    /// </description></item>
    /// <item><description>
    /// <c>singleton</c> - <c>SaveDataManager.Instance</c>. As of game 1.17.x the class is a
    /// plain lazy static singleton, <em>not</em> a VContainer registration, and its getter
    /// constructs one on demand. We therefore read <c>HasInstance</c> first and never force
    /// construction, or we would hand out an empty save file as if it were the player's.
    /// </description></item>
    /// <item><description>
    /// <c>container</c> - <c>ProjectLifetimeScope.TryResolve</c>. Kept as a last resort in case a
    /// future version moves the manager into DI; it is expected to miss today.
    /// </description></item>
    /// </list>
    /// <para>
    /// Readiness is a separate question, answered by a postfix on
    /// <c>SaveDataManager.LoadAllPlatformSetting()</c>. That method is called from exactly one
    /// place - the middle of <c>Load()</c>'s synchronous block, after player/pomodoro/todo/habit/
    /// note/setting have loaded - and nowhere else (<c>LoadSetting()</c> is not usable for this:
    /// <c>EntryBehavior</c> also calls it well before any save data is read). The RoomScene load
    /// is a belt-and-braces fallback, since the game only loads that scene once <c>Load()</c> has
    /// completed.
    /// </para>
    /// </remarks>
    internal static class SaveDataAccess
    {
        private static readonly object Gate = new object();

        private static Game.SaveDataManager? _captured;
        private static string _path = "none";
        private static bool _pathLogged;

        /// <summary>True once a postfix proved the game finished loading save data.</summary>
        internal static bool LoadObserved { get; private set; }

        /// <summary>True once RoomScene has loaded, which implies the same thing.</summary>
        internal static bool SceneObserved { get; set; }

        /// <summary>Which of the three paths produced the instance; <c>none</c> until one does.</summary>
        internal static string ResolutionPath => _path;

        /// <summary>Records the instance a Harmony postfix handed us.</summary>
        internal static void CaptureFromPatch(Game.SaveDataManager? instance)
        {
            if (instance is null)
            {
                return;
            }

            lock (Gate)
            {
                _captured = instance;
            }
        }

        /// <summary>Records that the game finished loading the player's save data.</summary>
        internal static void MarkLoaded() => LoadObserved = true;

        /// <summary>
        /// Returns the live manager, or null when the game has not built one yet. Logs the
        /// winning path once, at Info, so a launch log says how the bus attached itself.
        /// </summary>
        internal static Game.SaveDataManager? Resolve()
        {
            Game.SaveDataManager? found;
            string path;

            lock (Gate)
            {
                found = _captured;
                path = "harmony";
            }

            if (found is null && Game.SaveDataManager.HasInstance)
            {
                found = Game.SaveDataManager.Instance;
                path = "singleton";
            }

            if (found is null)
            {
                found = FromContainer();
                path = "container";
            }

            if (found is null)
            {
                return null;
            }

            if (!_pathLogged)
            {
                _pathLogged = true;
                _path = path;
                CwyLsModLoader.Log.LogDebug($"DATA SaveDataManager obtained via {path}");
            }

            return found;
        }

        private static int _containerFailures;

        private static Game.SaveDataManager? FromContainer()
        {
            // The container path never succeeds in this game (SaveDataManager is not registered); after a few
            // failures stop probing it every frame instead of throwing and logging at 45/s.
            if (_containerFailures >= 3)
            {
                return null;
            }

            try
            {
                // ProjectLifetimeScope.TryResolve dereferences a static that is null before the
                // scope awakes, so this is a throwing call, not a false-returning one.
                return Scopes.ProjectLifetimeScope.TryResolve<Game.SaveDataManager>(out var resolved)
                    ? resolved
                    : null;
            }
            catch (Exception ex)
            {
                if (++_containerFailures == 1)
                {
                    CwyLsModLoader.Log.LogDebug(
                        $"DATA container lookup for SaveDataManager failed (will stop probing after 3 failures): {ex.GetType().Name}: {ex.Message}");
                }
                return null;
            }
        }
    }
}
