using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Audio;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>One game row bound for reading and writing its clip.</summary>
    internal sealed class RowBinding
    {
        private readonly Func<AudioClip?> _get;
        private readonly Action<AudioClip?> _set;

        internal RowBinding(AudioRowDescriptor descriptor, object row, Func<AudioClip?> get, Action<AudioClip?> set)
        {
            Descriptor = descriptor;
            Row = row;
            _get = get;
            _set = set;
        }

        /// <summary>The row as data.</summary>
        internal AudioRowDescriptor Descriptor { get; }

        /// <summary>The game's row object (identity only).</summary>
        internal object Row { get; }

        internal string Key => Descriptor.Key;

        internal AudioTable Table => Descriptor.Table;

        /// <summary>The row's clip now; null when it has none or Unity destroyed it.</summary>
        internal AudioClip? Clip
        {
            get
            {
                var clip = _get();
                return clip != null ? clip : null;
            }
        }

        internal void SetClip(AudioClip? clip) => _set(clip);
    }

    /// <summary>
    /// Reads the game's five sound tables from <c>MasterDataLoader</c> as
    /// <see cref="RowBinding"/>s, and its music list as the soundtrack catalog. The lists are read live
    /// every time: a master-data edit may rebuild them, and a new master-data load replaces them.
    /// </summary>
    internal static class AudioRows
    {
        /// <summary>Every row of the five tables; empty while the lists are not loaded.</summary>
        /// <param name="loader">The game's loader.</param>
        /// <param name="original">
        /// Given a row key and the row's clip now, the game's own clip for that row (the swap book's
        /// original when the row holds one of our clips), so the descriptor describes the game's clip.
        /// </param>
        internal static List<RowBinding> Read(Game.MasterDataLoader? loader, Func<string, AudioClip?, AudioClip?> original)
        {
            var rows = new List<RowBinding>();
            if (loader is null)
            {
                return rows;
            }

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            if (loader.AmbientMasterList != null)
            {
                foreach (var row in loader.AmbientMasterList)
                {
                    if (row != null)
                    {
                        var r = row;
                        Add(rows, seen, original, AudioTable.Ambient, r.AmbientSound.ToString(), r, () => r.AudioClip, c => r.AudioClip = c);
                    }
                }
            }

            if (loader.AmbientSeMasterList != null)
            {
                foreach (var row in loader.AmbientSeMasterList)
                {
                    if (row != null)
                    {
                        var r = row;
                        Add(rows, seen, original, AudioTable.AmbientSe, r.AmbientSeSound.ToString(), r, () => r.AudioClip, c => r.AudioClip = c);
                    }
                }
            }

            if (loader.SystemSeMasterList != null)
            {
                foreach (var row in loader.SystemSeMasterList)
                {
                    if (row != null)
                    {
                        var r = row;
                        Add(rows, seen, original, AudioTable.SystemSe, r.SeSound.ToString(), r, () => r.AudioClip, c => r.AudioClip = c);
                    }
                }
            }

            if (loader.TimerSeMasterList != null)
            {
                foreach (var row in loader.TimerSeMasterList)
                {
                    if (row != null)
                    {
                        var r = row;
                        Add(rows, seen, original, AudioTable.TimerSe, r.SeSound.ToString(), r, () => r.AudioClip, c => r.AudioClip = c);
                    }
                }
            }

            if (loader.DirectionMusicDataList != null)
            {
                foreach (var row in loader.DirectionMusicDataList)
                {
                    if (row != null && !string.IsNullOrWhiteSpace(row.Title))
                    {
                        var r = row;
                        Add(rows, seen, original, AudioTable.DirectionMusic, r.Title, r, () => r.AudioClip, c => r.AudioClip = c);
                    }
                }
            }

            return rows;
        }

        /// <summary>True when the loader holds the five lists (a load finished and was not released).</summary>
        /// <param name="loader">The game's loader.</param>
        internal static bool IsLoaded(Game.MasterDataLoader? loader) =>
            loader != null && loader.IsLoaded
            && loader.AmbientMasterList != null && loader.AmbientSeMasterList != null
            && loader.SystemSeMasterList != null && loader.TimerSeMasterList != null
            && loader.DirectionMusicDataList != null;

        /// <summary>The game's music list as the soundtrack catalog; empty while it is not loaded.</summary>
        /// <param name="loader">The game's loader.</param>
        internal static SoundtrackCatalog Soundtrack(Game.MasterDataLoader? loader)
        {
            var list = loader?.MusicDataList;
            if (list is null)
            {
                return SoundtrackCatalog.Empty;
            }

            var tracks = new List<SoundtrackTrack>(list.Count);
            foreach (var music in list)
            {
                if (music is null)
                {
                    continue;
                }

                string clipName;
                try
                {
                    clipName = music.AudioClipName;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO could not read a music clip name: " + ex.GetType().Name + ": " + ex.Message);
                    clipName = string.Empty;
                }

                tracks.Add(new SoundtrackTrack(
                    music.Title,
                    music.Credit,
                    clipName,
                    music.UUID,
                    (music.Tag & Game.AudioTag.Original) != 0));
            }

            return new SoundtrackCatalog(tracks);
        }

        private static void Add(
            List<RowBinding> rows,
            Dictionary<string, int> seen,
            Func<string, AudioClip?, AudioClip?> original,
            AudioTable table,
            string name,
            object row,
            Func<AudioClip?> get,
            Action<AudioClip?> set)
        {
            // Two rows with one name (never in 1.17.3) get distinct keys, so neither is lost.
            var key = AudioRowDescriptor.KeyOf(table, name);
            if (seen.TryGetValue(key, out var count))
            {
                count++;
                seen[key] = count;
                name = name + "#" + count;
            }
            else
            {
                seen[key] = 1;
            }

            AudioClip? clip = null;
            try
            {
                clip = get();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug("AUDIO could not read the clip of " + key + ": " + ex.GetType().Name + ": " + ex.Message);
            }

            key = AudioRowDescriptor.KeyOf(table, name);

            // Unity's == also catches a destroyed clip; hand the book a real null for it.
            clip = original(key, clip != null ? clip : null);

            string clipName = string.Empty;
            double seconds = 0;
            if (clip != null)
            {
                try
                {
                    clipName = clip.name;
                    seconds = clip.length;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug("AUDIO could not read the clip of " + key + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            rows.Add(new RowBinding(new AudioRowDescriptor(table, name, clipName, seconds), row, get, set));
        }
    }
}
