using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>Which part of the room a material belongs to, for <c>tex scan</c> and <c>materials.json</c>.</summary>
    public enum SceneryFamily
    {
        /// <summary>Anything else (Satone, furniture, UI, effects).</summary>
        Other = 0,

        /// <summary>The view outside the window: <c>mt_env_*</c>, <c>mt_combined_room_far</c>, the COZY <c>Skybox</c>.</summary>
        Scenery,

        /// <summary>The desk family: <c>mt_desk*</c> (desk skins, lamp table, speaker, desktop props).</summary>
        Desk,

        /// <summary>The window frame and monitor: <c>mt_window*</c>.</summary>
        Window,
    }

    /// <summary>
    /// The material families the scenery work targets, as observed in the game's materials and the
    /// renderers in its RoomScene (game 1.17.3).
    /// </summary>
    public static class SceneryMaterials
    {
        /// <summary>Patterns of the scenery family.</summary>
        public static IReadOnlyList<string> SceneryPatterns { get; } = new[] { "mt_env_*", "mt_combined_room_far", "Skybox" };

        /// <summary>Patterns of the desk family.</summary>
        public static IReadOnlyList<string> DeskPatterns { get; } = new[] { "mt_desk*" };

        /// <summary>Patterns of the window family.</summary>
        public static IReadOnlyList<string> WindowPatterns { get; } = new[] { "mt_window*" };

        /// <summary>What <c>tex scan</c> lists when given no pattern: scenery, desk and window.</summary>
        public static IReadOnlyList<string> DefaultScanPatterns { get; } =
            new[] { "mt_env_*", "mt_combined_room_far", "Skybox", "mt_desk*", "mt_window*" };

        /// <summary>The family of a material name (base name, no <c>" (Instance)"</c>).</summary>
        /// <param name="materialName">The name.</param>
        public static SceneryFamily Classify(string? materialName)
        {
            if (AnyMatch(SceneryPatterns, materialName))
            {
                return SceneryFamily.Scenery;
            }

            if (AnyMatch(DeskPatterns, materialName))
            {
                return SceneryFamily.Desk;
            }

            return AnyMatch(WindowPatterns, materialName) ? SceneryFamily.Window : SceneryFamily.Other;
        }

        /// <summary>True when the name matches any of the patterns.</summary>
        /// <param name="patterns">Name patterns.</param>
        /// <param name="materialName">The name.</param>
        public static bool AnyMatch(IEnumerable<string> patterns, string? materialName)
        {
            foreach (var pattern in patterns)
            {
                if (MaterialNamePattern.Matches(pattern, materialName))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
