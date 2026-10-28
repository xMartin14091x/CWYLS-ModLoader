using System;
using CwyLs.ModLoader;
using UnityEngine.SceneManagement;
using Game = global::Bulbul;
using Scopes = global::NestopiSystem.DIContainers;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// Gets hold of the game's VContainer-registered services. Two scopes matter:
    /// <c>ProjectLifetimeScope</c> (master data, localization, language, music - alive from boot)
    /// and <c>RoomLifetimeScope</c> (pomodoro, scenario, Satone - alive only while RoomScene is).
    /// </summary>
    /// <remarks>
    /// <c>RoomLifetimeScope.GetOrCreate</c> will <em>instantiate</em> a scope from Resources when
    /// none exists, which would be a side effect of merely asking a
    /// question, so every room lookup here is gated on RoomScene actually being loaded.
    /// </remarks>
    internal static class GameScopes
    {
        /// <summary>The scene whose presence means the room scope exists.</summary>
        internal const string RoomSceneName = "RoomScene";

        /// <summary>True when RoomScene is loaded, so the room scope may be asked for a service.</summary>
        internal static bool IsRoomLoaded
        {
            get
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.isLoaded && scene.name == RoomSceneName)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Resolves a service from the project scope. Returns false - quietly, at Debug - while the
        /// scope does not exist yet, which is the normal state for the first second of a session.
        /// </summary>
        /// <typeparam name="T">The registered service type.</typeparam>
        /// <param name="resolved">The instance, or null.</param>
        internal static bool TryProject<T>(out T? resolved)
            where T : class
        {
            try
            {
                resolved = Scopes.ProjectLifetimeScope.TryResolve<T>(out var found) ? found : null;
                return resolved != null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"EVENT {typeof(T).Name} not resolvable from the project scope yet: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                resolved = null;
                return false;
            }
        }

        /// <summary>
        /// Resolves a service from the room scope, but only once RoomScene is loaded.
        /// </summary>
        /// <typeparam name="T">The registered service type.</typeparam>
        /// <param name="resolved">The instance, or null.</param>
        internal static bool TryRoom<T>(out T? resolved)
            where T : class
        {
            resolved = null;
            if (!IsRoomLoaded)
            {
                return false;
            }

            try
            {
                resolved = Game.RoomLifetimeScope.Resolve<T>();
                return resolved != null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    $"EVENT {typeof(T).Name} not resolvable from the room scope yet: "
                    + $"{ex.GetType().Name}: {ex.Message}");
                resolved = null;
                return false;
            }
        }
    }
}
