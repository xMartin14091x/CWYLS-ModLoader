using System;
using System.Diagnostics;
using System.IO;
using CwyLs.ModLoader.Core.Loaders;
using UnityEngine;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Turns a pack PNG into a <c>Texture2D</c>, synchronously. PNG decoding is fast enough to do
    /// on the main thread for the sizes a character texture comes in, and the texture feature assigns the result
    /// to a live material in the same frame a scene finishes loading, so an async path would only
    /// add a frame of the wrong texture.
    /// <para>
    /// The loaded texture's <c>name</c> is the file name without its extension. That is not
    /// cosmetic: it is how the texture feature identifies which slot a texture belongs to and how a human reading
    /// a scene inspector tells our texture from the game's.
    /// </para>
    /// </summary>
    public static class TextureLoader
    {
        /// <summary>
        /// Loads a PNG.
        /// </summary>
        /// <param name="absolutePath">Full path to the file.</param>
        /// <param name="options">How to build the texture; null means <see cref="TextureLoadOptions.Default"/>.</param>
        /// <param name="report">
        /// Report to record the attempt in; null means <see cref="LoaderDiagnostics.Session"/>.
        /// </param>
        /// <returns>
        /// The texture, or null when the file is missing, is not a PNG, or would not decode.
        /// Every one of those is a recorded <see cref="LoadEntry"/> and a warning in the log, never
        /// an exception: one bad file in a pack must not stop the other forty.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="absolutePath"/> is blank.</exception>
        public static Texture2D? Load(string absolutePath, TextureLoadOptions? options = null, LoadReport? report = null)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                // An empty path is a bug in the caller, not a broken pack file, so it is not a
                // report entry - there is nothing to report it against.
                throw new ArgumentException("a path is required", nameof(absolutePath));
            }

            var settings = options ?? TextureLoadOptions.Default;
            var watch = Stopwatch.StartNew();

            try
            {
                if (!File.Exists(absolutePath))
                {
                    return Fail(report, absolutePath, "file not found", watch);
                }

                var stamp = AssetCache.StampOf(absolutePath);
                if (AssetCache.TryGet<Texture2D>(absolutePath, stamp, out var cached, settings.CacheTag) &&
                    cached is not null)
                {
                    LoaderDiagnostics.Record(
                        report,
                        LoadEntry.Success(
                            absolutePath,
                            AssetKind.Texture,
                            $"cached {cached.width}x{cached.height} [{settings.CacheTag}]",
                            watch.ElapsedMilliseconds));
                    return cached;
                }

                var bytes = File.ReadAllBytes(absolutePath);

                var detection = FileKindDetector.Detect(absolutePath, bytes);
                if (detection.Format != AssetFormat.Png)
                {
                    return Fail(report, absolutePath, detection.Reason, watch, bytes.Length);
                }

                var name = AssetKey.FromPath(absolutePath).Name;

                // 2x2 is a placeholder: LoadImage resizes the texture to whatever the PNG holds.
                // The mip and colour-space flags, however, are fixed at construction time and
                // cannot be changed afterwards, which is why they are options and not arguments.
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, settings.MipMaps, settings.Linear)
                {
                    name = name,
                };

                if (!ImageConversion.LoadImage(texture, bytes, markNonReadable: !settings.ReadableAfterLoad))
                {
                    UnityEngine.Object.Destroy(texture);
                    return Fail(report, absolutePath, "the PNG decoder rejected the file", watch, bytes.Length);
                }

                // LoadImage replaces the texture's contents; re-stamping the name costs nothing and
                // means the name can never be lost to a Unity version that resets it.
                texture.name = name;

                AssetCache.Store(absolutePath, stamp, texture, settings.CacheTag);

                LoaderDiagnostics.Record(
                    report,
                    LoadEntry.Success(
                        absolutePath,
                        AssetKind.Texture,
                        $"{texture.width}x{texture.height} {texture.format} mips={texture.mipmapCount} [{settings.CacheTag}] as '{name}'",
                        watch.ElapsedMilliseconds,
                        bytes.Length));

                return texture;
            }
            catch (Exception ex)
            {
                // Unreadable file, out of texture memory, a decoder that threw: all of them are
                // this one file's problem. Fail records the entry and logs the warning.
                return Fail(report, absolutePath, $"{ex.GetType().Name}: {ex.Message}", watch);
            }
        }

        private static Texture2D? Fail(LoadReport? report, string path, string detail, Stopwatch watch, long bytes = 0)
        {
            LoaderDiagnostics.Record(
                report, LoadEntry.Failure(path, AssetKind.Texture, detail, watch.ElapsedMilliseconds, bytes));
            return null;
        }
    }
}
