using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using TMPro;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// The game's own UI assets a game-style window is drawn with: the facility windows' font and
    /// sprites (see <see cref="GameStyleAssetSet"/> for the names and where they were verified).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by name among the loaded objects (<see cref="Resources.FindObjectsOfTypeAll{T}"/>),
    /// not taken from a game window: nothing of the game's UI logic comes along, and a renamed
    /// sprite costs one part of the look instead of the window. The assets are in memory while
    /// <c>RoomScene</c> is loaded (its inactive facility windows reference them); before that the
    /// resolve fails and the caller falls back.
    /// </para>
    /// <para>
    /// A resolved set is cached and re-checked on every <see cref="TryResolve"/>: Unity reports a
    /// destroyed asset as null, and a scene reload can unload what the cache points at.
    /// </para>
    /// </remarks>
    public sealed class GameStyleAssets
    {
        private static GameStyleAssets? _cached;

        private readonly Dictionary<string, Sprite> _sprites;

        private GameStyleAssets(TMP_FontAsset font, Dictionary<string, Sprite> sprites, GameStyleAssetReport report)
        {
            Font = font;
            _sprites = sprites;
            Report = report;
        }

        /// <summary>The game's UI font (<c>Base_JP</c>: IBM Plex Sans JP Medium with the game's fallbacks).</summary>
        public TMP_FontAsset Font { get; }

        /// <summary>What was found and what was not.</summary>
        public GameStyleAssetReport Report { get; }

        /// <summary>
        /// Finds the assets, or returns the cached set when every asset in it is still alive.
        /// </summary>
        /// <param name="problem">Empty on success; otherwise which required assets are missing.</param>
        /// <returns>The assets, or null when a required one is missing.</returns>
        public static GameStyleAssets? TryResolve(out string problem)
        {
            if (_cached != null && _cached.IsLoaded)
            {
                problem = string.Empty;
                return _cached;
            }

            _cached = null;

            try
            {
                var font = FindFont(GameStyleAssetSet.Font);
                var sprites = FindSprites(GameStyleAssetSet.SpriteNames());

                var found = new List<string>(sprites.Keys);
                if (font != null)
                {
                    found.Add(GameStyleAssetSet.Font);
                }

                var report = GameStyleAssetSet.Evaluate(found);
                if (!report.CanBuild || font == null)
                {
                    problem = report.Describe();
                    return null;
                }

                problem = string.Empty;
                _cached = new GameStyleAssets(font, sprites, report);
                return _cached;
            }
            catch (Exception ex)
            {
                problem = "the game's UI assets could not be searched: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogWarning(GameStyleUi.LogPrefix + " " + problem);
                return null;
            }
        }

        /// <summary>A sprite by its game name, or null when this game build does not have it.</summary>
        /// <param name="name">One of the <see cref="GameStyleAssetSet"/> names.</param>
        public Sprite? SpriteNamed(string name) =>
            _sprites.TryGetValue(name, out var sprite) && sprite != null ? sprite : null;

        /// <summary>
        /// False once Unity unloaded any asset of the set (Unity reports a destroyed asset as
        /// null); a window built from it should then be rebuilt from a fresh resolve.
        /// </summary>
        public bool IsLoaded
        {
            get
            {
                if (Font == null)
                {
                    return false;
                }

                foreach (var sprite in _sprites.Values)
                {
                    if (sprite == null)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static TMP_FontAsset? FindFont(string name)
        {
            foreach (var asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (asset != null && string.Equals(asset.name, name, StringComparison.Ordinal))
                {
                    return asset;
                }
            }

            return null;
        }

        private static Dictionary<string, Sprite> FindSprites(IReadOnlyList<string> names)
        {
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var found = new Dictionary<string, Sprite>(StringComparer.Ordinal);

            // One pass over every loaded sprite (a few thousand in RoomScene), once per resolve.
            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null)
                {
                    continue;
                }

                var name = sprite.name;
                if (wanted.Contains(name) && !found.ContainsKey(name))
                {
                    found[name] = sprite;
                }
            }

            return found;
        }
    }
}
