using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CwyLs.ModLoader.Core.Audio;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>One KanKikuchi <c>AudioPlayer</c> and its <c>AudioSource</c>.</summary>
    internal sealed class PlayerRef
    {
        internal PlayerRef(object player, AudioSource source, string manager)
        {
            Player = player;
            Source = source;
            Manager = manager;
        }

        internal object Player { get; }

        internal AudioSource Source { get; }

        internal string Manager { get; }
    }

    /// <summary>
    /// The live players of the game's audio managers, through reflection on <c>AudioManager.dll</c>
    /// (not referenced at compile time, like the voice seam): which player holds which clip, and moving
    /// a playing loop from one clip to another at the same position (the A/B switch).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Why a swap must move the players and not only the row: ambient loops are started once and then
    /// kept alive at volume 0 when switched off (<c>EnvironmentApplicationService.ApplySound</c>),
    /// and <c>RoomAmbientSoundService.Stop</c> finds the player by clip <em>reference</em>
    /// (<c>AmbientBGMManager.Stop(AudioClip)</c>).
    /// A row swapped under a playing loop would leave that loop unstoppable. So every swap - pack apply,
    /// A/B, restore - moves the players that hold the old clip, with the game's own
    /// <c>AudioPlayer.TryReplaceAudioClip</c> (keeps state, volume, pitch, loop
    /// and the sample position), then seeks to the mapped position when the lengths differ. Without the
    /// players (a seam missing) loop rows are not replaced at all.
    /// </para>
    /// <para>
    /// Managers per table: ambient loops play on <c>AmbientBGMManager</c> (room,
    /// <c>RoomAmbientSoundService</c>) and <c>ScenarioAmbientBGMManager</c> (story,
    /// <c>ScenarioReader</c>); ambient one-shots on <c>AmbientSEManager</c>/<c>AmbientSEDuckingManager</c>
    /// (<c>AmbientSeService</c>); system sounds on <c>SEManager</c> (<c>SystemSeService</c>); timer
    /// sounds on <c>TimerSEManager</c> (<c>TimerSeService</c>); story music on
    /// <c>ScenarioMusicManager</c> (<c>ScenarioReader</c>).
    /// </para>
    /// </remarks>
    internal static class AudioPlayers
    {
        private static readonly object Gate = new object();

        private static bool _resolved;
        private static string _error = string.Empty;
        private static Type? _playerType;
        private static PropertyInfo? _audioSource;
        private static PropertyInfo? _state;
        private static MethodInfo? _tryReplace;
        private static readonly Dictionary<string, Manager?> Managers = new Dictionary<string, Manager?>(StringComparer.Ordinal);

        /// <summary>True when the player members resolved.</summary>
        internal static bool Available => TryResolve(out _);

        /// <summary>Why the players are not available; empty when they are.</summary>
        internal static string Error
        {
            get
            {
                TryResolve(out var error);
                return error;
            }
        }

        /// <summary>The manager type names that hold a table's clips.</summary>
        /// <param name="table">The table.</param>
        internal static string[] ManagersOf(AudioTable table)
        {
            switch (table)
            {
                case AudioTable.Ambient: return new[] { "AmbientBGMManager", "ScenarioAmbientBGMManager" };
                case AudioTable.AmbientSe: return new[] { "AmbientSEManager", "AmbientSEDuckingManager" };
                case AudioTable.SystemSe: return new[] { "SEManager" };
                case AudioTable.TimerSe: return new[] { "TimerSEManager" };
                case AudioTable.DirectionMusic: return new[] { "ScenarioMusicManager" };
                default: return new string[0];
            }
        }

        /// <summary>Every player of the table's managers that exist right now. Never null; empty when unavailable.</summary>
        /// <param name="table">The table.</param>
        internal static List<PlayerRef> PlayersOf(AudioTable table)
        {
            var players = new List<PlayerRef>();
            if (!TryResolve(out _))
            {
                return players;
            }

            foreach (var name in ManagersOf(table))
            {
                var manager = ManagerOf(name);
                if (manager is null)
                {
                    continue;
                }

                var instance = manager.Instance();
                if (instance is null)
                {
                    continue;
                }

                IEnumerable? list;
                try
                {
                    list = manager.PlayerList!.GetValue(instance, null) as IEnumerable;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO could not read " + name + ".AudioPlayerList: " + Unwrap(ex));
                    continue;
                }

                if (list is null)
                {
                    continue;
                }

                foreach (var player in list)
                {
                    if (player is null)
                    {
                        continue;
                    }

                    AudioSource? source = null;
                    try
                    {
                        source = _audioSource!.GetValue(player, null) as AudioSource;
                    }
                    catch (Exception ex)
                    {
                        CwyLsModLoader.Log.LogDebug("AUDIO could not read AudioPlayer.AudioSource: " + Unwrap(ex));
                    }

                    if (source != null)
                    {
                        players.Add(new PlayerRef(player, source, name));
                    }
                }
            }

            return players;
        }

        /// <summary>The player's state name (<c>Wait</c>, <c>Playing</c>, <c>Pause</c>...), or empty.</summary>
        /// <param name="player">The player.</param>
        internal static string StateOf(PlayerRef player)
        {
            try
            {
                return _state?.GetValue(player.Player, null)?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read AudioPlayer.CurrentState: " + Unwrap(ex));
                return string.Empty;
            }
        }

        /// <summary>
        /// Moves every player of the table that holds <paramref name="from"/> to <paramref name="to"/>:
        /// the game's <c>TryReplaceAudioClip</c>, then a seek to the same position (wrapped for a
        /// loop) when the clips differ in length or rate; a player the game call refuses is restarted
        /// on the new clip from the start.
        /// </summary>
        /// <param name="table">The table.</param>
        /// <param name="from">The clip being left.</param>
        /// <param name="to">The clip to play.</param>
        /// <returns>What happened.</returns>
        internal static MoveResult Move(AudioTable table, AudioClip from, AudioClip to)
        {
            var result = new MoveResult();
            if (from == null || to == null || ReferenceEquals(from, to))
            {
                return result;
            }

            foreach (var player in PlayersOf(table))
            {
                var source = player.Source;
                if (source == null || !ReferenceEquals(source.clip, from))
                {
                    continue;
                }

                var position = SafeTime(source);
                var wasPlaying = source.isPlaying;
                var loop = source.loop;
                bool replaced;
                try
                {
                    replaced = _tryReplace != null && (bool)_tryReplace.Invoke(player.Player, new object[] { from, to });
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning("AUDIO " + player.Manager + ": TryReplaceAudioClip failed (" + Unwrap(ex) + "); restarting the player on the new clip");
                    replaced = false;
                }

                if (replaced)
                {
                    var mapped = LoopPosition.Map(position, to.length, loop);
                    Seek(source, to, mapped);
                    result.Moved++;
                    result.Position = mapped;
                    continue;
                }

                try
                {
                    source.Stop();
                    source.clip = to;
                    to.LoadAudioData();
                    source.time = 0f;
                    if (wasPlaying)
                    {
                        source.Play();
                    }

                    result.Restarted++;
                    result.Position = 0;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    CwyLsModLoader.Log.LogWarning("AUDIO " + player.Manager + ": could not restart the player on the new clip: " + Unwrap(ex));
                }
            }

            return result;
        }

        /// <summary>What the table's players hold of these two clips right now.</summary>
        /// <param name="table">The table.</param>
        /// <param name="original">The game's clip.</param>
        /// <param name="replacement">The replacement, or null.</param>
        /// <param name="count">How many players hold either.</param>
        /// <param name="volume">The loudest such player's volume; -1 when none.</param>
        internal static AudioPlaying Describe(AudioTable table, AudioClip? original, AudioClip? replacement, out int count, out float volume)
        {
            count = 0;
            volume = -1f;
            if (!TryResolve(out _))
            {
                return AudioPlaying.Unknown;
            }

            var playing = AudioPlaying.None;
            foreach (var player in PlayersOf(table))
            {
                var clip = player.Source.clip;
                if (clip == null)
                {
                    continue;
                }

                var isOriginal = original != null && ReferenceEquals(clip, original);
                var isReplacement = replacement != null && ReferenceEquals(clip, replacement);
                if (!isOriginal && !isReplacement)
                {
                    continue;
                }

                count++;
                if (player.Source.volume > volume)
                {
                    volume = player.Source.volume;
                }

                // A replacement anywhere wins the summary: it is what the A/B switch is about.
                if (isReplacement || playing == AudioPlaying.None)
                {
                    playing = isReplacement ? AudioPlaying.Replacement : AudioPlaying.Original;
                }
            }

            return playing;
        }

        private static void Seek(AudioSource source, AudioClip to, double seconds)
        {
            try
            {
                var samples = LoopPosition.ToSamples(seconds, to.frequency, to.samples);
                if (samples > 0 && Math.Abs(source.timeSamples - samples) > to.frequency / 100)
                {
                    source.timeSamples = samples;
                }
            }
            catch (Exception ex)
            {
                // Seeking is cosmetic: the game call already kept the old sample index (clamped).
                CwyLsModLoader.Log.LogDebug("AUDIO seek after the switch failed: " + Unwrap(ex));
            }
        }

        private static double SafeTime(AudioSource source)
        {
            try
            {
                return source.time;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read the playback position: " + Unwrap(ex));
                return 0;
            }
        }

        private static Manager? ManagerOf(string name)
        {
            lock (Gate)
            {
                if (Managers.TryGetValue(name, out var cached))
                {
                    return cached;
                }

                Manager? manager = null;
                try
                {
                    var assembly = FindAssembly();
                    var type = assembly?.GetType(AudioTargets.AudioNamespace + name, false);
                    var singleton = assembly?.GetType(AudioTargets.AudioNamespace + "SingletonMonoBehaviour`1", false);
                    if (type != null && singleton != null)
                    {
                        var closed = singleton.MakeGenericType(type);
                        var field = closed.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
                        var list = type.GetProperty("AudioPlayerList", BindingFlags.Public | BindingFlags.Instance);
                        if (list != null)
                        {
                            manager = new Manager(type, field, list);
                        }
                    }

                    if (manager is null)
                    {
                        CwyLsModLoader.Log.LogWarning("AUDIO the audio manager " + name + " was not found; its players are not switched");
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning("AUDIO could not resolve the audio manager " + name + ": " + Unwrap(ex));
                }

                Managers[name] = manager;
                return manager;
            }
        }

        private static bool TryResolve(out string error)
        {
            lock (Gate)
            {
                if (_resolved)
                {
                    error = _error;
                    return _error.Length == 0;
                }

                try
                {
                    var assembly = FindAssembly();
                    if (assembly is null)
                    {
                        // Not loaded yet (Mono loads it when the game first touches audio): try again later.
                        error = "AudioManager.dll is not loaded yet";
                        return false;
                    }

                    _playerType = assembly.GetType(AudioTargets.AudioNamespace + "AudioPlayer", false);
                    _audioSource = _playerType?.GetProperty("AudioSource", BindingFlags.Public | BindingFlags.Instance);
                    _state = _playerType?.GetProperty("CurrentState", BindingFlags.Public | BindingFlags.Instance);
                    _tryReplace = _playerType?.GetMethod(
                        "TryReplaceAudioClip",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(AudioClip), typeof(AudioClip) },
                        null);

                    _error = _playerType is null ? "KanKikuchi.AudioManager.AudioPlayer is missing"
                        : _audioSource is null ? "AudioPlayer.AudioSource is missing"
                        : _tryReplace is null ? "AudioPlayer.TryReplaceAudioClip(AudioClip, AudioClip) is missing"
                        : string.Empty;
                }
                catch (Exception ex)
                {
                    _error = "the audio players could not be resolved: " + Unwrap(ex);
                }

                _resolved = true;
                if (_error.Length > 0)
                {
                    CwyLsModLoader.Log.LogWarning("AUDIO " + _error + "; ambient loops and story music are not replaced (a loop swapped under a playing player could not be stopped)");
                }

                error = _error;
                return _error.Length == 0;
            }
        }

        private static Assembly? FindAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, AudioTargets.AudioAssembly, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            try
            {
                return Assembly.Load(AudioTargets.AudioAssembly);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO AudioManager.dll could not be loaded by name: " + Unwrap(ex));
                return null;
            }
        }

        private static string Unwrap(Exception ex)
        {
            var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            return inner.GetType().Name + ": " + inner.Message;
        }

        /// <summary>The counts of one <see cref="Move"/>.</summary>
        internal sealed class MoveResult
        {
            internal int Moved { get; set; }

            internal int Restarted { get; set; }

            internal int Failed { get; set; }

            internal double Position { get; set; } = -1;

            internal int Total => Moved + Restarted;
        }

        private sealed class Manager
        {
            private readonly Type _type;
            private readonly FieldInfo? _instanceField;

            internal Manager(Type type, FieldInfo? instanceField, PropertyInfo playerList)
            {
                _type = type;
                _instanceField = instanceField;
                PlayerList = playerList;
            }

            internal PropertyInfo? PlayerList { get; }

            /// <summary>The live manager, or null while it does not exist (Unity-null counts as absent).</summary>
            internal object? Instance()
            {
                try
                {
                    var instance = _instanceField?.GetValue(null) as UnityEngine.Object;
                    if (instance != null)
                    {
                        return instance;
                    }

                    // The singleton caches lazily (SingletonMonoBehaviour); before anything asked
                    // for it the field is still null although the object exists.
                    var found = UnityEngine.Object.FindObjectOfType(_type);
                    return found != null ? found : null;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO could not find " + _type.Name + ": " + Unwrap(ex));
                    return null;
                }
            }
        }
    }
}
