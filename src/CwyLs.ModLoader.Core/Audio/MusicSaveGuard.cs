using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>
    /// What <see cref="MusicSaveGuard.Strip"/> took out of the music setting's lists, so
    /// <see cref="MusicSaveGuard.Restore"/> can put it back exactly.
    /// </summary>
    public sealed class MusicSaveStash
    {
        private readonly List<Removal> _removals = new List<Removal>();

        internal MusicSaveStash()
        {
        }

        /// <summary>How many entries were removed across the three lists.</summary>
        public int RemovedCount
        {
            get
            {
                var count = 0;
                foreach (var removal in _removals)
                {
                    count += removal.Entries.Count;
                }

                return count;
            }
        }

        /// <summary>True when nothing was removed.</summary>
        public bool IsEmpty => RemovedCount == 0;

        /// <summary>True once <see cref="MusicSaveGuard.Restore"/> ran.</summary>
        public bool Restored { get; internal set; }

        internal IReadOnlyList<Removal> Removals => _removals;

        internal void Add(Removal removal) => _removals.Add(removal);

        /// <summary>One list and what was removed from it: (index in the original list, value), ascending.</summary>
        internal sealed class Removal
        {
            internal Removal(MusicSaveList list, IList<string> target)
            {
                List = list;
                Target = target;
            }

            internal MusicSaveList List { get; }

            internal IList<string> Target { get; }

            internal List<KeyValuePair<int, string>> Entries { get; } = new List<KeyValuePair<int, string>>();
        }
    }

    /// <summary>The three UUID lists of the game's <c>MusicSettingV2</c>.</summary>
    public enum MusicSaveList
    {
        /// <summary><c>FavoriteAudioUUIDs</c>.</summary>
        Favorites,

        /// <summary><c>PlaylistOrder</c>.</summary>
        PlaylistOrder,

        /// <summary><c>ExcludedFromPlaylistUUIDs</c>.</summary>
        Excluded,
    }

    /// <summary>
    /// Keeps the game's music save vanilla: pack-music UUIDs (<c>cwy-…</c>) are taken out of
    /// <c>MusicSettingV2</c> right before the game writes it and put back right after, so the live
    /// game keeps them (favourites, order and exclusions work all session) while the file - which
    /// Steam Cloud copies to every machine - never contains one. What was taken out is remembered in
    /// our own file (<see cref="PackMusicState"/>) and re-inserted at the next launch.
    /// </summary>
    public static class MusicSaveGuard
    {
        /// <summary>
        /// Removes every pack-music UUID from the three lists, in place. Null lists are skipped.
        /// </summary>
        /// <param name="favorites"><c>FavoriteAudioUUIDs</c>.</param>
        /// <param name="playlistOrder"><c>PlaylistOrder</c>.</param>
        /// <param name="excluded"><c>ExcludedFromPlaylistUUIDs</c>.</param>
        /// <returns>What was removed, for <see cref="Restore"/>; never null.</returns>
        public static MusicSaveStash Strip(IList<string>? favorites, IList<string>? playlistOrder, IList<string>? excluded)
        {
            var stash = new MusicSaveStash();
            StripOne(stash, MusicSaveList.Favorites, favorites);
            StripOne(stash, MusicSaveList.PlaylistOrder, playlistOrder);
            StripOne(stash, MusicSaveList.Excluded, excluded);
            return stash;
        }

        /// <summary>
        /// Puts back what <see cref="Strip"/> removed, at the same indices, so each list is exactly what
        /// it was. Restoring twice does nothing the second time.
        /// </summary>
        /// <param name="stash">The stash.</param>
        public static void Restore(MusicSaveStash? stash)
        {
            if (stash is null || stash.Restored)
            {
                return;
            }

            stash.Restored = true;
            foreach (var removal in stash.Removals)
            {
                // Ascending original indices: inserting each at its own index rebuilds the original.
                foreach (var entry in removal.Entries)
                {
                    var at = Math.Min(Math.Max(entry.Key, 0), removal.Target.Count);
                    removal.Target.Insert(at, entry.Value);
                }
            }
        }

        /// <summary>
        /// The pack-music part of the lists, as our own state to remember: favourites, exclusions and
        /// each UUID's position in the playlist order (its index in the unstripped list).
        /// </summary>
        /// <param name="stash">A stash from <see cref="Strip"/>.</param>
        public static PackMusicState Capture(MusicSaveStash? stash)
        {
            var favorites = new List<string>();
            var excluded = new List<string>();
            var order = new List<PackMusicOrder>();
            if (stash is null)
            {
                return new PackMusicState(favorites, excluded, order);
            }

            foreach (var removal in stash.Removals)
            {
                foreach (var entry in removal.Entries)
                {
                    switch (removal.List)
                    {
                        case MusicSaveList.Favorites:
                            AddOnce(favorites, entry.Value);
                            break;
                        case MusicSaveList.Excluded:
                            AddOnce(excluded, entry.Value);
                            break;
                        default:
                            if (!ContainsUuid(order, entry.Value))
                            {
                                order.Add(new PackMusicOrder(entry.Value, entry.Key));
                            }

                            break;
                    }
                }
            }

            return new PackMusicState(favorites, excluded, order);
        }

        /// <summary>
        /// Puts remembered pack-music UUIDs back into the live lists at launch, for the pack tracks
        /// that are installed now. Nothing already present is added twice.
        /// </summary>
        /// <param name="favorites"><c>FavoriteAudioUUIDs</c>.</param>
        /// <param name="playlistOrder"><c>PlaylistOrder</c>.</param>
        /// <param name="excluded"><c>ExcludedFromPlaylistUUIDs</c>.</param>
        /// <param name="state">The remembered state.</param>
        /// <param name="installed">The UUIDs of the pack tracks installed now.</param>
        /// <returns>How many entries were inserted.</returns>
        public static int Reinsert(
            IList<string>? favorites,
            IList<string>? playlistOrder,
            IList<string>? excluded,
            PackMusicState? state,
            ICollection<string>? installed)
        {
            if (state is null || installed is null || installed.Count == 0)
            {
                return 0;
            }

            var inserted = 0;
            if (favorites != null)
            {
                foreach (var uuid in state.Favorites)
                {
                    if (installed.Contains(uuid) && !favorites.Contains(uuid))
                    {
                        favorites.Add(uuid);
                        inserted++;
                    }
                }
            }

            if (excluded != null)
            {
                foreach (var uuid in state.Excluded)
                {
                    if (installed.Contains(uuid) && !excluded.Contains(uuid))
                    {
                        excluded.Add(uuid);
                        inserted++;
                    }
                }
            }

            if (playlistOrder != null)
            {
                var ordered = new List<PackMusicOrder>(state.Order);
                ordered.Sort((a, b) => a.Index.CompareTo(b.Index));
                foreach (var entry in ordered)
                {
                    if (installed.Contains(entry.Uuid) && !playlistOrder.Contains(entry.Uuid))
                    {
                        playlistOrder.Insert(Math.Min(Math.Max(entry.Index, 0), playlistOrder.Count), entry.Uuid);
                        inserted++;
                    }
                }
            }

            return inserted;
        }

        private static void StripOne(MusicSaveStash stash, MusicSaveList kind, IList<string>? list)
        {
            if (list is null)
            {
                return;
            }

            var removal = new MusicSaveStash.Removal(kind, list);
            for (var i = 0; i < list.Count; i++)
            {
                if (PackMusicUuid.IsPackUuid(list[i]))
                {
                    removal.Entries.Add(new KeyValuePair<int, string>(i, list[i]));
                }
            }

            // Remove from the back so the recorded indices stay the original ones.
            for (var i = removal.Entries.Count - 1; i >= 0; i--)
            {
                list.RemoveAt(removal.Entries[i].Key);
            }

            if (removal.Entries.Count > 0)
            {
                stash.Add(removal);
            }
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!list.Contains(value))
            {
                list.Add(value);
            }
        }

        private static bool ContainsUuid(List<PackMusicOrder> order, string uuid)
        {
            foreach (var entry in order)
            {
                if (string.Equals(entry.Uuid, uuid, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A pack track's remembered position in the playlist order.</summary>
    public sealed class PackMusicOrder
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="uuid">The <c>cwy-…</c> UUID.</param>
        /// <param name="index">Its index in the playlist order.</param>
        public PackMusicOrder(string uuid, int index)
        {
            Uuid = uuid ?? string.Empty;
            Index = index < 0 ? 0 : index;
        }

        /// <summary>The UUID.</summary>
        public string Uuid { get; }

        /// <summary>The index.</summary>
        public int Index { get; }
    }

    /// <summary>
    /// What the player did with pack tracks (favourites, exclusions, order), kept in our own file
    /// <c>BepInEx/data/audio/pack-music.json</c> instead of the game's save. UUIDs and indices only.
    /// </summary>
    public sealed class PackMusicState
    {
        /// <summary>The file format version.</summary>
        public const int Schema = 1;

        /// <summary>Creates a state.</summary>
        /// <param name="favorites">Favourite UUIDs.</param>
        /// <param name="excluded">Excluded UUIDs.</param>
        /// <param name="order">Playlist positions.</param>
        public PackMusicState(IEnumerable<string>? favorites, IEnumerable<string>? excluded, IEnumerable<PackMusicOrder>? order)
        {
            Favorites = OnlyPackUuids(favorites);
            Excluded = OnlyPackUuids(excluded);
            var positions = new List<PackMusicOrder>();
            if (order != null)
            {
                foreach (var entry in order)
                {
                    if (entry != null && PackMusicUuid.IsPackUuid(entry.Uuid))
                    {
                        positions.Add(entry);
                    }
                }
            }

            Order = positions;
        }

        /// <summary>An empty state.</summary>
        public static PackMusicState Empty { get; } = new PackMusicState(null, null, null);

        /// <summary>Favourite pack-track UUIDs.</summary>
        public IReadOnlyList<string> Favorites { get; }

        /// <summary>Excluded pack-track UUIDs.</summary>
        public IReadOnlyList<string> Excluded { get; }

        /// <summary>Playlist positions.</summary>
        public IReadOnlyList<PackMusicOrder> Order { get; }

        /// <summary>True when there is nothing to remember.</summary>
        public bool IsEmpty => Favorites.Count == 0 && Excluded.Count == 0 && Order.Count == 0;

        /// <summary>
        /// Combines a fresh capture with the previous file: tracks installed now take the fresh
        /// values (absent from the capture = no longer a favourite); tracks not installed now keep
        /// what the previous file said, so removing a pack for a while does not forget its tracks.
        /// </summary>
        /// <param name="current">The capture from this save.</param>
        /// <param name="previous">What the file held.</param>
        /// <param name="installed">UUIDs of the pack tracks installed now.</param>
        public static PackMusicState Merge(PackMusicState? current, PackMusicState? previous, ICollection<string>? installed)
        {
            var now = current ?? Empty;
            var before = previous ?? Empty;
            var live = installed ?? (ICollection<string>)Array.Empty<string>();

            var favorites = new List<string>(now.Favorites);
            var excluded = new List<string>(now.Excluded);
            var order = new List<PackMusicOrder>(now.Order);

            foreach (var uuid in before.Favorites)
            {
                if (!live.Contains(uuid) && !favorites.Contains(uuid))
                {
                    favorites.Add(uuid);
                }
            }

            foreach (var uuid in before.Excluded)
            {
                if (!live.Contains(uuid) && !excluded.Contains(uuid))
                {
                    excluded.Add(uuid);
                }
            }

            foreach (var entry in before.Order)
            {
                if (!live.Contains(entry.Uuid) && order.TrueForAll(e => !string.Equals(e.Uuid, entry.Uuid, StringComparison.Ordinal)))
                {
                    order.Add(entry);
                }
            }

            return new PackMusicState(favorites, excluded, order);
        }

        /// <summary>The file's JSON.</summary>
        public string ToJson()
        {
            var order = new List<object?>();
            foreach (var entry in Order)
            {
                order.Add(new Dictionary<string, object?> { ["uuid"] = entry.Uuid, ["index"] = entry.Index });
            }

            var root = new Dictionary<string, object?>
            {
                ["schema"] = Schema,
                ["favorites"] = new List<string>(Favorites),
                ["excluded"] = new List<string>(Excluded),
                ["order"] = order,
            };

            return JsonWriter.Write(root);
        }

        /// <summary>Reads the file's JSON. Never throws.</summary>
        /// <param name="json">The text.</param>
        /// <returns>The state, or an empty state with the problem.</returns>
        public static PackMusicStateParse Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new PackMusicStateParse(Empty, string.Empty);
            }

            if (!JsonReader.TryParse(json!, out var root, out var error))
            {
                return new PackMusicStateParse(Empty, error ?? "not JSON");
            }

            if (!(root is JsonObject obj))
            {
                return new PackMusicStateParse(Empty, "the top-level value must be an object");
            }

            if (!obj.TryGetValue("schema", out var schema) || !(schema is long n) || n != Schema)
            {
                return new PackMusicStateParse(Empty, "schema must be " + Schema);
            }

            var favorites = Strings(obj, "favorites");
            var excluded = Strings(obj, "excluded");
            var order = new List<PackMusicOrder>();
            if (obj.TryGetValue("order", out var orderValue) && orderValue is IList<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is JsonObject entry
                        && entry.TryGetValue("uuid", out var uuid) && uuid is string text
                        && entry.TryGetValue("index", out var index) && index is long at)
                    {
                        order.Add(new PackMusicOrder(text, at > int.MaxValue ? int.MaxValue : (int)at));
                    }
                }
            }

            return new PackMusicStateParse(new PackMusicState(favorites, excluded, order), string.Empty);
        }

        private static List<string> Strings(JsonObject obj, string member)
        {
            var result = new List<string>();
            if (obj.TryGetValue(member, out var value) && value is IList<object?> list)
            {
                foreach (var item in list)
                {
                    if (item is string text)
                    {
                        result.Add(text);
                    }
                }
            }

            return result;
        }

        private static IReadOnlyList<string> OnlyPackUuids(IEnumerable<string>? uuids)
        {
            var result = new List<string>();
            if (uuids is null)
            {
                return result;
            }

            foreach (var uuid in uuids)
            {
                if (PackMusicUuid.IsPackUuid(uuid) && !result.Contains(uuid))
                {
                    result.Add(uuid);
                }
            }

            return result;
        }
    }

    /// <summary>The result of <see cref="PackMusicState.Parse"/>.</summary>
    public sealed class PackMusicStateParse
    {
        internal PackMusicStateParse(PackMusicState state, string problem)
        {
            State = state;
            Problem = problem;
        }

        /// <summary>The state (empty on a problem).</summary>
        public PackMusicState State { get; }

        /// <summary>Why the file was not read; empty when it was.</summary>
        public string Problem { get; }

        /// <summary>True when there was no problem.</summary>
        public bool IsValid => Problem.Length == 0;
    }
}
