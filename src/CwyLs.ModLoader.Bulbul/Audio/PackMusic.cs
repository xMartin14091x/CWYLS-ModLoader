using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Audio;
using CwyLs.ModLoader.Core.Logging;
using HarmonyLib;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// Pack music: tracks from <c>audio/music/</c> join the game's music list, and the game's music
    /// save never learns about them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Joining the list.</b> A prefix on <c>MusicService.Load(IReadOnlyCollection&lt;GameAudioInfo&gt;)</c>
    /// (its one caller is <c>EntryBehavior.LoadAsync</c>, after the save and the master data are
    /// loaded) adds every pack track whose clip has decoded, built with the game's own
    /// <c>GameAudioInfo.CreateNormal</c>, unlocked, with a <c>cwy-…</c> UUID
    /// (<see cref="PackMusicUuid"/>). They go after the game's tracks, or at the
    /// position the player gave them last session (<see cref="PackMusicPlacement"/>) - never first,
    /// which would make a pack track the one that starts at launch. A clip that decodes after the load
    /// is added through <c>MusicService.AddMusicItem</c>.
    /// </para>
    /// <para>
    /// <b>The save.</b> <c>MusicSettingV2</c> (favourites, playlist order, exclusions) is written by
    /// <c>SaveDataManager.SaveMusicSetting</c> (synchronously through <c>SaveDataIO.Save</c>) and
    /// Steam Cloud copies the file to every machine. A prefix strips every <c>cwy-…</c> UUID right
    /// before the write and a finalizer (it runs even when
    /// the save throws) puts them back, so the live game keeps them and the file stays vanilla. Should
    /// the strip ever fail with a pack UUID still in a list, the prefix skips that one save rather than
    /// write it. What was stripped is remembered in <c>BepInEx/data/audio/pack-music.json</c> (UUIDs and
    /// indices only) and re-inserted at the next load. Pack music is never added unless the guard is in
    /// place. A prefix and a finalizer, not a postfix: <c>SaveDataHook</c> owns the one postfix per save
    /// method.
    /// </para>
    /// </remarks>
    internal static class PackMusic
    {
        /// <summary>The state file, under <c>BepInEx/data/audio/</c>.</summary>
        internal const string StateFileName = "pack-music.json";

        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> Problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per thread, so a prefix and its finalizer always pair up even if the game ever saved off the
        // main thread (it does not in 1.17.3: SaveDataIO.Save is synchronous on the caller).
        [ThreadStatic]
        private static Stack<MusicSaveStash?>? _stashes;
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static PackMusicPlan _plan = PackMusicPlan.Empty;
        private static PackMusicState _remembered = PackMusicState.Empty;
        private static string _rememberedJson = string.Empty;
        private static global::MusicService? _service;
        private static bool _loadPatched;
        private static bool _guardPatched;

        /// <summary>True once the load prefix and the save guard are both in place.</summary>
        internal static bool Patched => _loadPatched && _guardPatched;

        /// <summary>What was patched, or why not.</summary>
        internal static string Detail { get; private set; } = "not patched";

        /// <summary>True once the game's music load ran this session.</summary>
        internal static bool LoadSeen { get; private set; }

        /// <summary>How many saves the guard stripped pack UUIDs from.</summary>
        internal static int StrippedSaves { get; private set; }

        /// <summary>How many times the guard ran.</summary>
        internal static int GuardRuns { get; private set; }

        /// <summary>The current plan (built against the game's music list once it is known).</summary>
        internal static PackMusicPlan Plan => _plan;

        /// <summary>The music service captured by the load prefix, or null.</summary>
        internal static global::MusicService? Service => _service;

        /// <summary>Applies the load prefix and the save guard.</summary>
        /// <param name="harmony">The feature's Harmony instance.</param>
        /// <returns>True when both are in place.</returns>
        internal static bool Apply(Harmony harmony)
        {
            try
            {
                var load = AccessTools.Method(typeof(global::MusicService), nameof(global::MusicService.Load), new[] { typeof(IReadOnlyCollection<Game.GameAudioInfo>) });
                var save = AccessTools.Method(typeof(Game.SaveDataManager), nameof(Game.SaveDataManager.SaveMusicSetting), Type.EmptyTypes);
                if (load is null || save is null)
                {
                    Detail = "PATCH TARGET MISSING: " + (load is null ? "MusicService.Load" : "SaveDataManager.SaveMusicSetting");
                    return false;
                }

                // The guard first: pack music is never added without it.
                harmony.Patch(
                    save,
                    prefix: new HarmonyMethod(typeof(PackMusic), nameof(SavePrefix)),
                    finalizer: new HarmonyMethod(typeof(PackMusic), nameof(SaveFinalizer)));
                _guardPatched = true;

                harmony.Patch(load, prefix: new HarmonyMethod(typeof(PackMusic), nameof(LoadPrefix)));
                _loadPatched = true;
                Detail = "prefixed MusicService.Load; guarded SaveDataManager.SaveMusicSetting (prefix + finalizer)";
                return true;
            }
            catch (Exception ex)
            {
                Detail = "could not patch the music seams: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Reads the remembered state file once at startup.</summary>
        internal static void LoadState()
        {
            var path = StatePath();
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var json = File.ReadAllText(path);
                var parsed = PackMusicState.Parse(json);
                if (!parsed.IsValid)
                {
                    CwyLsModLoader.Log.LogWarning("AUDIO " + StateFileName + " was not read (" + parsed.Problem + "); pack-track favourites start fresh");
                    return;
                }

                _remembered = parsed.State;
                _rememberedJson = json;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO could not read " + path + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>Sets the tracks of the current packs (planned against the game's music list).</summary>
        /// <param name="plan">The plan.</param>
        internal static void SetPlan(PackMusicPlan plan)
        {
            _plan = plan ?? PackMusicPlan.Empty;
        }

        /// <summary>Records a decoded clip (or a failure) for a track, and lists it late when the game's load already ran.</summary>
        /// <param name="uuid">The track's UUID.</param>
        /// <param name="clip">The clip, or null.</param>
        /// <param name="failure">Why it failed, when it did.</param>
        internal static void OnClip(string uuid, AudioClip? clip, string failure)
        {
            if (clip == null)
            {
                Clips.Remove(uuid);
                Problems[uuid] = failure;
                return;
            }

            Clips[uuid] = clip;
            Problems.Remove(uuid);
        }

        /// <summary>
        /// Lists every planned track that is decoded and not listed yet, when the game's load already
        /// ran (late clips, a reload). Main thread.
        /// </summary>
        /// <param name="trigger">What asked, for the log.</param>
        /// <returns>How many were added.</returns>
        internal static int AddLate(string trigger)
        {
            var service = _service;
            if (!LoadSeen || service is null || !Patched || !AudioFeature.PackMusicEnabled)
            {
                return 0;
            }

            var added = 0;
            foreach (var track in _plan.Tracks)
            {
                if (Listed.Contains(track.Uuid) || !Clips.TryGetValue(track.Uuid, out var clip) || clip == null)
                {
                    continue;
                }

                if (Contains(service, track.Uuid))
                {
                    Listed.Add(track.Uuid);
                    continue;
                }

                try
                {
                    var info = Build(track, clip, FavoriteNow(track.Uuid));
                    if (service.AddMusicItem(info))
                    {
                        Listed.Add(track.Uuid);
                        added++;
                        Announce(track, clip, "late:" + trigger);
                    }
                    else
                    {
                        Problems[track.Uuid] = "MusicService.AddMusicItem refused it";
                        WarnOnce("additem:" + track.Uuid, "AUDIO the game refused to add the pack track '" + track.Title + "' (" + track.Uuid + ")");
                    }
                }
                catch (Exception ex)
                {
                    Problems[track.Uuid] = ex.GetType().Name + ": " + ex.Message;
                    CwyLsModLoader.Log.LogError("AUDIO could not add the pack track '" + track.Title + "': " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            return added;
        }

        /// <summary>Every planned track with its state.</summary>
        internal static List<MusicTrackInfo> Infos()
        {
            var infos = new List<MusicTrackInfo>(_plan.Tracks.Count);
            var service = _service;
            foreach (var track in _plan.Tracks)
            {
                Clips.TryGetValue(track.Uuid, out var clip);
                var loaded = clip != null;
                var listed = Listed.Contains(track.Uuid) || (service != null && Contains(service, track.Uuid));
                Problems.TryGetValue(track.Uuid, out var problem);
                if (!listed && problem is null)
                {
                    problem = !Patched ? "the music seams are not patched"
                        : !loaded ? "the clip has not decoded yet"
                        : !LoadSeen ? "the game has not loaded its music list yet"
                        : "not listed yet";
                }

                infos.Add(new MusicTrackInfo(track, loaded, listed, loaded ? SafeLength(clip!) : 0, listed ? string.Empty : problem));
            }

            return infos;
        }

        /// <summary>True when the game's music list holds this UUID.</summary>
        internal static bool Contains(global::MusicService service, string uuid)
        {
            try
            {
                foreach (var music in service.AllMusicList)
                {
                    if (music != null && string.Equals(music.UUID, uuid, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read MusicService.AllMusicList: " + ex.GetType().Name + ": " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Harmony prefix on <c>MusicService.Load</c>: re-inserts the remembered pack-track state into
        /// the live music setting and merges the decoded pack tracks into the list the game loads.
        /// Never throws into the game; on any failure the game's own list goes through unchanged.
        /// </summary>
        /// <param name="__instance">The music service.</param>
        /// <param name="__0">The list the game is about to load.</param>
        private static void LoadPrefix(global::MusicService __instance, ref IReadOnlyCollection<Game.GameAudioInfo> __0)
        {
            try
            {
                _service = __instance;
                LoadSeen = true;
                Listed.Clear();

                if (!AudioFeature.Enabled || !AudioFeature.PackMusicEnabled || !Patched)
                {
                    return;
                }

                // The game's music list is loaded by now (EntryBehavior waits for it): plan for real.
                AudioFeature.RefreshMusicPlan("musicLoad");
                if (_plan.Tracks.Count == 0 || __0 is null)
                {
                    return;
                }

                var setting = Game.SaveDataManager.HasInstance ? Game.SaveDataManager.Instance.MusicSetting : null;
                var installed = InstalledUuids();
                if (setting != null)
                {
                    var reinserted = MusicSaveGuard.Reinsert(setting.FavoriteAudioUUIDs, setting.PlaylistOrder, setting.ExcludedFromPlaylistUUIDs, _remembered, installed);
                    if (reinserted > 0)
                    {
                        ConsoleEvents.File(AudioFeature.TraceSys, "musicState", ("reinserted", reinserted));
                    }
                }

                var ready = new Dictionary<string, Game.GameAudioInfo>(StringComparer.Ordinal);
                var readyOrder = new List<string>();
                foreach (var track in _plan.Tracks)
                {
                    if (!Clips.TryGetValue(track.Uuid, out var clip) || clip == null)
                    {
                        continue;
                    }

                    ready[track.Uuid] = Build(track, clip, setting != null && setting.FavoriteAudioUUIDs.Contains(track.Uuid));
                    readyOrder.Add(track.Uuid);
                }

                if (ready.Count == 0)
                {
                    return;
                }

                var game = new List<Game.GameAudioInfo>(__0);
                var gameOrder = new List<string>(game.Count);
                foreach (var item in game)
                {
                    gameOrder.Add(item?.UUID ?? string.Empty);
                }

                var placed = PackMusicPlacement.Place(gameOrder, setting?.PlaylistOrder, readyOrder);
                var merged = new List<Game.GameAudioInfo>(placed.Count);
                var next = 0;
                foreach (var uuid in placed)
                {
                    if (uuid != null && ready.TryGetValue(uuid, out var pack))
                    {
                        merged.Add(pack);
                    }
                    else if (next < game.Count)
                    {
                        merged.Add(game[next++]);
                    }
                }

                // Placement keeps every game item; anything it could not pair stays, in order.
                while (next < game.Count)
                {
                    merged.Add(game[next++]);
                }

                __0 = merged;
                foreach (var uuid in readyOrder)
                {
                    Listed.Add(uuid);
                    var track = _plan.Find(uuid);
                    if (track != null && Clips.TryGetValue(uuid, out var clip))
                    {
                        Announce(track, clip, "load");
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("AUDIO pack music was not added (the game's own list loads unchanged): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Harmony prefix on <c>SaveDataManager.SaveMusicSetting</c>: takes every <c>cwy-…</c> UUID out of
        /// the three lists for the write. Returns false - skipping this one save - only if a pack UUID
        /// would still reach the file.
        /// </summary>
        /// <param name="__instance">The save manager.</param>
        private static bool SavePrefix(Game.SaveDataManager __instance)
        {
            MusicSaveStash? stash = null;
            var write = true;
            try
            {
                GuardRuns++;
                var setting = __instance?.MusicSetting;
                if (setting != null)
                {
                    stash = MusicSaveGuard.Strip(setting.FavoriteAudioUUIDs, setting.PlaylistOrder, setting.ExcludedFromPlaylistUUIDs);
                    write = !HasPackUuid(setting);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("AUDIO save guard: could not strip pack UUIDs: " + ex.GetType().Name + ": " + ex.Message);
                try
                {
                    var setting = __instance?.MusicSetting;
                    write = setting is null || !HasPackUuid(setting);
                }
                catch (Exception inner)
                {
                    CwyLsModLoader.Log.LogError("AUDIO save guard: could not check the music setting: " + inner.GetType().Name + ": " + inner.Message);
                    write = false;
                }
            }

            (_stashes ??= new Stack<MusicSaveStash?>()).Push(stash);
            if (!write)
            {
                CwyLsModLoader.Log.LogError("AUDIO save guard: a pack UUID would have reached the music save, so this music save was skipped; the next one retries");
            }

            return write;
        }

        /// <summary>
        /// Harmony finalizer on <c>SaveDataManager.SaveMusicSetting</c>: puts the stripped UUIDs back and
        /// remembers them in our own file. Runs even when the save threw; passes the exception on.
        /// </summary>
        /// <param name="__exception">The save's exception, or null.</param>
        private static Exception? SaveFinalizer(Exception? __exception)
        {
            try
            {
                var stash = _stashes != null && _stashes.Count > 0 ? _stashes.Pop() : null;
                if (stash is null)
                {
                    return __exception;
                }

                MusicSaveGuard.Restore(stash);
                if (!stash.IsEmpty)
                {
                    StrippedSaves++;
                    ConsoleEvents.File(AudioFeature.TraceSys, "saveguard", ("stripped", stash.RemovedCount), ("saves", StrippedSaves));
                }

                if (_plan.Tracks.Count > 0 || !_remembered.IsEmpty)
                {
                    Remember(PackMusicState.Merge(MusicSaveGuard.Capture(stash), _remembered, InstalledUuids()));
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError("AUDIO save guard: could not restore the pack UUIDs after the save: " + ex.GetType().Name + ": " + ex.Message);
            }

            return __exception;
        }

        private static bool HasPackUuid(Game.MusicSettingV2 setting)
        {
            foreach (var list in new[] { setting.FavoriteAudioUUIDs, setting.PlaylistOrder, setting.ExcludedFromPlaylistUUIDs })
            {
                if (list is null)
                {
                    continue;
                }

                foreach (var uuid in list)
                {
                    if (PackMusicUuid.IsPackUuid(uuid))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Game.GameAudioInfo Build(PackMusicTrack track, AudioClip clip, bool favorite)
        {
            try
            {
                if (!string.Equals(clip.name, track.Title, StringComparison.Ordinal))
                {
                    clip.name = track.Title;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not rename the clip of '" + track.Title + "': " + ex.GetType().Name + ": " + ex.Message);
            }

            var tag = (Game.AudioTag)(int)track.Tag;
            var info = Game.GameAudioInfo.CreateNormal(clip, tag, track.Title, track.Credit, track.Uuid, false, string.Empty, string.Empty);
            info.IsUnlocked = true;
            if (favorite)
            {
                info.Tag |= Game.AudioTag.Favorite;
            }

            return info;
        }

        private static bool FavoriteNow(string uuid)
        {
            try
            {
                return Game.SaveDataManager.HasInstance && Game.SaveDataManager.Instance.MusicSetting.FavoriteAudioUUIDs.Contains(uuid);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read the favourites: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static HashSet<string> InstalledUuids()
        {
            var installed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in _plan.Tracks)
            {
                installed.Add(track.Uuid);
            }

            return installed;
        }

        private static void Remember(PackMusicState state)
        {
            var json = state.ToJson();
            if (string.Equals(json, _rememberedJson, StringComparison.Ordinal))
            {
                _remembered = state;
                return;
            }

            var path = StatePath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp~";
                File.WriteAllText(temp, json);
                if (File.Exists(path))
                {
                    File.Replace(temp, path, null);
                }
                else
                {
                    File.Move(temp, path);
                }

                _remembered = state;
                _rememberedJson = json;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning("AUDIO could not write " + path + " (pack-track favourites are not remembered): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Announce(PackMusicTrack track, AudioClip clip, string via)
        {
            ConsoleEvents.Emit(
                AudioFeature.TraceSys,
                "music",
                ConsoleTags.Audio,
                "+ music '" + track.Title + "' <- " + track.PackId,
                "len,tag,uuid,via",
                ("title", track.Title),
                ("pack", track.PackId),
                ("path", track.RelativePath),
                ("len", AudioListFormatter.Seconds(SafeLength(clip))),
                ("tag", track.Tag.ToString()),
                ("uuid", track.Uuid),
                ("via", via));
        }

        private static double SafeLength(AudioClip clip)
        {
            try
            {
                return clip.length;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read a clip length: " + ex.GetType().Name + ": " + ex.Message);
                return 0;
            }
        }

        private static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key))
            {
                CwyLsModLoader.Log.LogWarning(message);
            }
        }

        private static string StatePath() => Path.Combine(ModLoaderPaths.DataRoot, "audio", StateFileName);
    }
}
