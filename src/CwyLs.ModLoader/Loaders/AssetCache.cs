using System;
using System.Collections.Generic;
using System.IO;
using Object = UnityEngine.Object;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Process-wide cache of assets built from pack files, keyed by path plus the file's last-write
    /// time. The timestamp is the point: a pack author editing a PNG and alt-tabbing back into the
    /// game gets the new texture on the next load, with no reload command and no stale-cache
    /// debugging session.
    /// <para>
    /// Static for the same reason <c>SmokeRegistry</c> is: the plugins that load packs are separate
    /// BepInEx assemblies. Every member takes the same lock.
    /// </para>
    /// </summary>
    public static class AssetCache
    {
        /// <summary>Timestamp value meaning "could not be determined"; never cached under it.</summary>
        public const long UnknownStamp = 0L;

        private sealed class CacheEntry
        {
            public CacheEntry(Object asset, long stamp)
            {
                Asset = asset;
                Stamp = stamp;
            }

            public Object Asset { get; }

            public long Stamp { get; }
        }

        private static readonly object Gate = new object();

        private static readonly Dictionary<string, CacheEntry> Entries =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        /// <summary>How many assets are cached.</summary>
        public static int Count
        {
            get
            {
                lock (Gate)
                {
                    return Entries.Count;
                }
            }
        }

        /// <summary>
        /// The file's last-write time in UTC ticks, or <see cref="UnknownStamp"/> when the file is
        /// missing or unreadable (which is reported by the loader, not here).
        /// </summary>
        public static long StampOf(string path)
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : UnknownStamp;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"cache: could not stat {path}: {ex.GetType().Name}: {ex.Message}; it will not be cached");
                return UnknownStamp;
            }
        }

        /// <summary>
        /// Looks up a cached asset.
        /// </summary>
        /// <typeparam name="T">The asset type expected; a cached asset of another type is a miss.</typeparam>
        /// <param name="path">The file the asset was built from.</param>
        /// <param name="stamp">Its current <see cref="StampOf"/>. A different one evicts the entry.</param>
        /// <param name="asset">The cached asset, or null.</param>
        /// <param name="variant">
        /// Distinguishes entries built from the same file with different options; see
        /// <see cref="TextureLoadOptions.CacheTag"/>. Null for assets with no options.
        /// </param>
        /// <returns>True when <paramref name="asset"/> was found and is still alive.</returns>
        public static bool TryGet<T>(string path, long stamp, out T? asset, string? variant = null)
            where T : Object
        {
            asset = null;

            if (string.IsNullOrEmpty(path) || stamp == UnknownStamp)
            {
                return false;
            }

            var key = KeyOf(path, variant);

            lock (Gate)
            {
                if (!Entries.TryGetValue(key, out var entry))
                {
                    return false;
                }

                // Unity's == is the one that matters here: it is also true for an asset that was
                // destroyed behind our back (a scene teardown, a Resources.UnloadUnusedAssets),
                // which must be a miss and not a dead reference handed to a material.
                if (entry.Stamp != stamp || entry.Asset == null)
                {
                    Entries.Remove(key);
                    return false;
                }

                asset = entry.Asset as T;
                if (asset is null)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Caches an asset. A <see cref="UnknownStamp"/> is not cached, so a file whose timestamp
        /// could not be read is reloaded every time rather than pinned forever.
        /// </summary>
        /// <param name="path">The file the asset was built from.</param>
        /// <param name="stamp">Its <see cref="StampOf"/> at load time.</param>
        /// <param name="asset">The asset.</param>
        /// <param name="variant">See <see cref="TryGet{T}"/>.</param>
        /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="asset"/> is null.</exception>
        public static void Store(string path, long stamp, Object asset, string? variant = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("a path is required", nameof(path));
            }

            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            if (stamp == UnknownStamp)
            {
                return;
            }

            lock (Gate)
            {
                Entries[KeyOf(path, variant)] = new CacheEntry(asset, stamp);
            }
        }

        /// <summary>
        /// Forgets every cached asset. For development (a config key, a console command) - the
        /// next load of each file rebuilds it.
        /// </summary>
        /// <remarks>
        /// Deliberately does not destroy the assets. By the time this is called a texture is
        /// probably already assigned to a live lilToon material and a clip to the game's voice
        /// cache; destroying those would leave the game holding nulls. Unity collects
        /// them once nothing references them.
        /// </remarks>
        public static void Clear()
        {
            lock (Gate)
            {
                var count = Entries.Count;
                Entries.Clear();
                CwyLsModLoader.Log.LogDebug($"asset cache cleared ({count} entr{(count == 1 ? "y" : "ies")})");
            }
        }

        private static string KeyOf(string path, string? variant) =>
            string.IsNullOrEmpty(variant) ? path : path + "|" + variant;
    }
}
