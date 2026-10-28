using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CwyLs.ModLoader.Core.Trace;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Reads the live game state for the periodic snapshot. Every lookup is reflective and every
    /// lookup is independent: a renamed field costs that one snapshot member and one warning, not
    /// the snapshot. This is why the plugin can be built without referencing Assembly-CSharp.
    /// </summary>
    /// <remarks>
    /// Members read (observed in game 1.17.2):
    /// <list type="bullet">
    /// <item>game clock: <c>DateService._currentDateTime</c> (global namespace) — the game clock is wall time</item>
    /// <item>heroine state: <c>Bulbul.HeroineService.GetCurrentAIState()</c></item>
    /// <item>pomodoro: <c>Bulbul.PomodoroService.CurrentPomodoroType/IsTimerRunning()/CurrentLoopCount</c> and <c>Bulbul.PomodoroTimerService.RemainTimeSpan()</c></item>
    /// <item>music: <c>MusicService.PlayingMusic/IsPause/CurrentPlayList/IsShuffle/IsRepeatOneMusic</c> (global namespace); volume from <c>Bulbul.SaveDataManager.Instance.SettingData.MusicVolumeInfo.Value</c></item>
    /// <item>environment: <c>Bulbul.EnvironmentDataService.GetCurrentPresetIndex()</c></item>
    /// <item>windows: <c>Bulbul.WindowViewService.IsActiveWindow(WindowViewType)</c></item>
    /// <item>focused control: <c>UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject</c> (UnityEngine.UI.dll)</item>
    /// <item>containers: <c>Bulbul.RoomLifetimeScope.inst</c> and <c>NestopiSystem.DIContainers.ProjectLifetimeScope.inst</c>, both private statics whose <c>Container</c> is VContainer's <c>IObjectResolver</c></item>
    /// </list>
    /// </remarks>
    internal sealed class GameStateProbe : ITraceStateProvider
    {
        private const string GameAssemblyName = "Assembly-CSharp";
        private const string RoomScope = "Bulbul.RoomLifetimeScope";
        private const string ProjectScope = "NestopiSystem.DIContainers.ProjectLifetimeScope";

        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, object?> _services = new Dictionary<string, object?>(StringComparer.Ordinal);

        private Assembly? _game;
        private FieldInfo? _dateTimeField;
        private object? _dateService;

        /// <inheritdoc />
        public TraceSnapshot Capture() =>
            new TraceSnapshot
            {
                Timestamp = TraceRuntime.Now,
                Frame = TraceRuntime.Frame,
                Realtime = DateTime.Now,
                UnityTime = TraceRuntime.Realtime,
                GameClock = ReadGameClock(),
                HeroineState = ReadHeroineState(),
                Pomodoro = ReadPomodoro(),
                Music = ReadMusic(),
                Environment = ReadEnvironment(),
                Scene = ReadScene(),
                WindowsOpen = ReadWindowsOpen(),
                FocusedControl = ReadFocusedControl(),
            };

        /// <summary>
        /// The game clock as a short string, cached by the plugin into every trace line. Falls back
        /// to wall time, which is what <c>DateService</c> itself tracks.
        /// </summary>
        public string? ReadGameClock()
        {
            try
            {
                if (_dateService is null || _dateTimeField is null)
                {
                    _dateService = Resolve(RoomScope, "DateService");
                    _dateTimeField = _dateService?.GetType()
                        .GetField("_currentDateTime", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (_dateService != null && _dateTimeField != null
                    && _dateTimeField.GetValue(_dateService) is DateTime clock && clock != default)
                {
                    return clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                WarnOnce("gameClock", ex);
            }

            return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private string? ReadHeroineState()
        {
            try
            {
                var service = Resolve(RoomScope, "Bulbul.HeroineService");
                var state = Call(service, "GetCurrentAIState");
                return state?.ToString();
            }
            catch (Exception ex)
            {
                WarnOnce("heroineState", ex);
                return null;
            }
        }

        private PomodoroSnapshot? ReadPomodoro()
        {
            try
            {
                var service = Resolve(RoomScope, "Bulbul.PomodoroService");
                if (service is null)
                {
                    return null;
                }

                var snapshot = new PomodoroSnapshot
                {
                    Type = Read(service, "CurrentPomodoroType")?.ToString(),
                    Running = Call(service, "IsTimerRunning") as bool?,
                    Loop = Read(Read(service, "CurrentLoopCount"), "CurrentValue") as int?,
                };

                var timer = Resolve(RoomScope, "Bulbul.PomodoroTimerService");
                if (Call(timer, "RemainTimeSpan") is TimeSpan remaining)
                {
                    snapshot.RemainingSeconds = remaining.TotalSeconds;
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                WarnOnce("pomodoro", ex);
                return null;
            }
        }

        private MusicSnapshot? ReadMusic()
        {
            try
            {
                // MusicService lives in the project-wide scope, not the room scope.
                var service = Resolve(ProjectScope, "MusicService");
                if (service is null)
                {
                    return null;
                }

                var playing = Read(service, "PlayingMusic");
                var paused = Read(service, "IsPause") as bool? ?? false;
                var playlist = Read(service, "CurrentPlayList");

                return new MusicSnapshot
                {
                    Current = Read(playing, "Title") as string,
                    Index = IndexInPlaylist(playlist, playing),
                    Playlist = DescribePlaylist(service, playlist),
                    IsPlaying = playing != null && !paused,
                    Volume = ReadMusicVolume(),
                };
            }
            catch (Exception ex)
            {
                WarnOnce("music", ex);
                return null;
            }
        }

        private static int? IndexInPlaylist(object? playlist, object? playing)
        {
            if (playlist is null || playing is null || !(playlist is IEnumerable items))
            {
                return null;
            }

            var index = 0;
            foreach (var item in items)
            {
                if (ReferenceEquals(item, playing))
                {
                    return index;
                }

                index++;
            }

            return -1;
        }

        private string? DescribePlaylist(object service, object? playlist)
        {
            var count = Read(playlist, "Count") as int?;
            var shuffle = Read(service, "IsShuffle") as bool? ?? false;
            var repeatOne = Read(service, "IsRepeatOneMusic") as bool? ?? false;

            var description = (count?.ToString(CultureInfo.InvariantCulture) ?? "?") + " tracks";
            if (shuffle)
            {
                description += ", shuffle";
            }

            if (repeatOne)
            {
                description += ", repeat1";
            }

            return description;
        }

        private double? ReadMusicVolume()
        {
            try
            {
                var saveType = GameType("Bulbul.SaveDataManager");
                if (saveType is null)
                {
                    return null;
                }

                // Instance creates the manager on demand; HasInstance keeps the trace read-only.
                if (!(saveType.GetProperty("HasInstance", BindingFlags.Public | BindingFlags.Static)?
                        .GetValue(null, null) is bool hasInstance) || !hasInstance)
                {
                    return null;
                }

                var manager = saveType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                var volume = Read(Read(Read(manager, "SettingData"), "MusicVolumeInfo"), "Value");
                return volume is float f ? f : (double?)null;
            }
            catch (Exception ex)
            {
                WarnOnce("music.volume", ex);
                return null;
            }
        }

        private string? ReadEnvironment()
        {
            try
            {
                var service = Resolve(RoomScope, "Bulbul.EnvironmentDataService");
                return Call(service, "GetCurrentPresetIndex") is int index
                    ? "preset " + index.ToString(CultureInfo.InvariantCulture)
                    : null;
            }
            catch (Exception ex)
            {
                WarnOnce("environment", ex);
                return null;
            }
        }

        /// <summary>
        /// The control with keyboard focus, as a hierarchy path. Read straight from Unity's
        /// EventSystem, so it works for every control in the game rather than a known list.
        /// </summary>
        private string? ReadFocusedControl()
        {
            try
            {
                var current = UnityEngine.EventSystems.EventSystem.current;
                if (current == null)
                {
                    return null;
                }

                var selected = current.currentSelectedGameObject;
                return selected == null ? null : UiTracer.PathOf(selected.transform);
            }
            catch (Exception ex)
            {
                WarnOnce("focusedControl", ex);
                return null;
            }
        }

        /// <summary>
        /// Whether the Steam API came up. <c>IsInitialized</c> is an instance property on
        /// <c>NestopiSystem.Steam.SteamManager</c>, so the manager has to be
        /// found first; null means we could not find it, never "Steam is down".
        /// </summary>
        internal bool? ReadSteamInitialized()
        {
            try
            {
                var manager = Resolve(RoomScope, "NestopiSystem.Steam.SteamManager")
                    ?? FindComponent("NestopiSystem.Steam.SteamManager");
                return Read(manager, "IsInitialized") as bool?;
            }
            catch (Exception ex)
            {
                WarnOnce("steam", ex);
                return null;
            }
        }

        private string? ReadScene()
        {
            try
            {
                return SceneManager.GetActiveScene().name;
            }
            catch (Exception ex)
            {
                WarnOnce("scene", ex);
                return null;
            }
        }

        private IReadOnlyList<string>? ReadWindowsOpen()
        {
            try
            {
                var service = Resolve(RoomScope, "Bulbul.WindowViewService") ?? FindComponent("Bulbul.WindowViewService");
                var enumType = GameType("Bulbul.WindowViewType");
                if (service is null || enumType is null)
                {
                    return null;
                }

                var isActive = service.GetType().GetMethod(
                    "IsActiveWindow", BindingFlags.Public | BindingFlags.Instance, null, new[] { enumType }, null);
                if (isActive is null)
                {
                    WarnOnce("windowsOpen", null, "WindowViewService.IsActiveWindow(WindowViewType) is gone");
                    return null;
                }

                var open = new List<string>();
                foreach (var value in Enum.GetValues(enumType))
                {
                    if (isActive.Invoke(service, new[] { value }) is bool active && active)
                    {
                        open.Add(value.ToString() ?? "?");
                    }
                }

                return open;
            }
            catch (Exception ex)
            {
                WarnOnce("windowsOpen", ex);
                return null;
            }
        }

        // ----- reflection plumbing -------------------------------------------------------------

        private Assembly? Game
        {
            get
            {
                if (_game != null)
                {
                    return _game;
                }

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(assembly.GetName().Name, GameAssemblyName, StringComparison.Ordinal))
                    {
                        return _game = assembly;
                    }
                }

                return null;
            }
        }

        private Type? GameType(string fullName)
        {
            try
            {
                return Game?.GetType(fullName, throwOnError: false);
            }
            catch (Exception ex)
            {
                WarnOnce("type:" + fullName, ex);
                return null;
            }
        }

        /// <summary>Resolves a service out of one of the game's two VContainer scopes, and caches it.</summary>
        private object? Resolve(string scopeTypeName, string serviceTypeName)
        {
            var key = scopeTypeName + "|" + serviceTypeName;
            if (_services.TryGetValue(key, out var cached) && cached != null
                && !(cached is Object destroyed && destroyed == null))
            {
                return cached;
            }

            var container = Container(scopeTypeName);
            var serviceType = GameType(serviceTypeName);
            if (container is null || serviceType is null)
            {
                return null;
            }

            try
            {
                var tryResolve = FindTryResolve(container.GetType());
                if (tryResolve is null)
                {
                    WarnOnce("resolve:" + key, null, "IObjectResolver.TryResolve(Type, out object, object) is gone");
                    return null;
                }

                var args = new object?[] { serviceType, null, null };
                var found = tryResolve.Invoke(container, args) as bool? ?? false;
                var resolved = found ? args[1] : null;
                _services[key] = resolved;
                return resolved;
            }
            catch (Exception ex)
            {
                WarnOnce("resolve:" + key, ex);
                return null;
            }
        }

        private static MethodInfo? FindTryResolve(Type containerType)
        {
            foreach (var method in containerType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!string.Equals(method.Name, "TryResolve", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 3 && parameters[0].ParameterType == typeof(Type) && parameters[1].IsOut)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>Reads the <c>Container</c> of a LifetimeScope held in its own private static <c>inst</c>.</summary>
        private object? Container(string scopeTypeName)
        {
            try
            {
                var scopeType = GameType(scopeTypeName);
                var instField = scopeType?.GetField("inst", BindingFlags.NonPublic | BindingFlags.Static);
                var scope = instField?.GetValue(null);

                // A destroyed LifetimeScope is a live C# reference but a dead Unity object.
                if (scope is Object unityScope && unityScope == null)
                {
                    return null;
                }

                return scope is null ? null : Read(scope, "Container");
            }
            catch (Exception ex)
            {
                WarnOnce("container:" + scopeTypeName, ex);
                return null;
            }
        }

        /// <summary>Last-resort lookup for a MonoBehaviour service that is not in a container.</summary>
        private object? FindComponent(string typeName)
        {
            try
            {
                // Only a UnityEngine.Object can be found in the scene. Asking for anything else (the game's
                // SteamManager is a plain class) makes Unity log an error and return nothing.
                var type = GameType(typeName);
                return type is null || !typeof(Object).IsAssignableFrom(type) ? null : Object.FindObjectOfType(type);
            }
            catch (Exception ex)
            {
                WarnOnce("find:" + typeName, ex);
                return null;
            }
        }

        /// <summary>Reads a public property or field by name; null target or missing member yields null.</summary>
        private object? Read(object? target, string memberName)
        {
            if (target is null)
            {
                return null;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                var type = target.GetType();

                var property = type.GetProperty(memberName, flags);
                if (property != null && property.CanRead)
                {
                    return property.GetValue(target, null);
                }

                return type.GetField(memberName, flags)?.GetValue(target);
            }
            catch (Exception ex)
            {
                WarnOnce("read:" + target.GetType().Name + "." + memberName, ex);
                return null;
            }
        }

        /// <summary>Calls a public no-argument method by name; null target or missing method yields null.</summary>
        private object? Call(object? target, string methodName)
        {
            if (target is null)
            {
                return null;
            }

            try
            {
                var method = target.GetType().GetMethod(
                    methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                return method?.Invoke(target, null);
            }
            catch (Exception ex)
            {
                WarnOnce("call:" + target.GetType().Name + "." + methodName, ex);
                return null;
            }
        }

        /// <summary>Logs a resolution failure once per field, so a missing member costs one line per session.</summary>
        private void WarnOnce(string field, Exception? ex, string? reason = null)
        {
            if (!_warned.Add(field))
            {
                return;
            }

            CwyLsModLoader.Log.LogWarning(
                $"TRACE: snapshot field '{field}' is unavailable and will be written as null: {reason ?? ex?.Message ?? "unknown"}");
        }
    }
}
