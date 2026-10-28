using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Console
{
    /// <summary>
    /// The result of looking for the game's UI assets: whether a game-style window can be built,
    /// and what is missing.
    /// </summary>
    public sealed class GameStyleAssetReport
    {
        internal GameStyleAssetReport(IReadOnlyList<string> missingRequired, IReadOnlyList<string> missingOptional)
        {
            MissingRequired = missingRequired;
            MissingOptional = missingOptional;
        }

        /// <summary>True when every required asset was found.</summary>
        public bool CanBuild => MissingRequired.Count == 0;

        /// <summary>Required assets that were not found; any of them means no game-style window.</summary>
        public IReadOnlyList<string> MissingRequired { get; }

        /// <summary>Optional assets that were not found; their parts are drawn as plain tinted shapes.</summary>
        public IReadOnlyList<string> MissingOptional { get; }

        /// <summary>One phrase for the log: what is missing, or that nothing is.</summary>
        public string Describe()
        {
            if (MissingRequired.Count > 0)
            {
                return "missing " + string.Join(", ", ToArray(MissingRequired))
                    + (MissingOptional.Count > 0 ? " (and optional " + string.Join(", ", ToArray(MissingOptional)) + ")" : string.Empty);
            }

            return MissingOptional.Count > 0
                ? "built without optional " + string.Join(", ", ToArray(MissingOptional)) + " (plain shapes instead)"
                : "all game UI assets found";
        }

        private static string[] ToArray(IReadOnlyList<string> list)
        {
            var array = new string[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                array[i] = list[i];
            }

            return array;
        }
    }

    /// <summary>
    /// The game UI assets a game-style window is built from, by name, and the rule for which of
    /// them it cannot do without. Names as observed in game 1.17.2: the game's sprite assets
    /// and the <c>UI_FacilityNote</c> window in the game's RoomScene.
    /// </summary>
    /// <remarks>
    /// Assets are looked up by name rather than through the game's own windows, so nothing of the
    /// game's UI logic (DOTween, VContainer injection, localisation behaviours) comes along, and a
    /// game update that renames one sprite degrades one part instead of breaking the window.
    /// </remarks>
    public static class GameStyleAssetSet
    {
        /// <summary>TextMeshPro font of every facility window (IBM Plex Sans JP Medium, with the game's fallbacks).</summary>
        public const string Font = "Base_JP";

        /// <summary>Window background: 90 % black, drawn tiled by the game (<c>UI_FacilityNote</c>, <c>UI_FacilityHabitTracker</c>).</summary>
        public const string WindowBase = "t_ui_generalbase_rbabbable";

        /// <summary>Lavender separator bar under the title and above the bottom edge (9-slice).</summary>
        public const string Separator = "t_ui_back_bar";

        /// <summary>Close button icon.</summary>
        public const string Close = "t_ui_close";

        /// <summary>Close button hover overlay (cyan/magenta outline).</summary>
        public const string CloseHover = "t_ui_close_active";

        /// <summary>Drag handle at the top centre of every draggable window.</summary>
        public const string Grab = "t_ui_grab_horizontal";

        /// <summary>Drag handle hover overlay.</summary>
        public const string GrabHover = "t_ui_grab_horizontal_active";

        /// <summary>Pill button (9-slice horizontally), as the habit tracker's edit button.</summary>
        public const string Button = "t_ui_listchange";

        /// <summary>Pill button hover overlay.</summary>
        public const string ButtonHover = "t_ui_listchange_active";

        /// <summary>Settings-window tab.</summary>
        public const string Tab = "t_ui_settings_tab";

        /// <summary>Settings-window tab hover overlay.</summary>
        public const string TabHover = "t_ui_settings_tab_select";

        /// <summary>Settings-window selected tab.</summary>
        public const string TabActive = "t_ui_settings_tab_active";

        /// <summary>Dark rounded panel with a lavender outline (9-slice), used behind text areas.</summary>
        public const string Panel = "t_ui_generalbase_outline";

        /// <summary>Scrollbar handle (9-slice vertically).</summary>
        public const string ScrollHandle = "t_ui_scrollbar";

        /// <summary>
        /// Assets without which the window would not read as the game's: its font, its background,
        /// its separator and its close button.
        /// </summary>
        public static readonly IReadOnlyList<string> Required = new[] { Font, WindowBase, Separator, Close };

        /// <summary>Assets whose part falls back to a plain tinted shape when missing.</summary>
        public static readonly IReadOnlyList<string> Optional = new[]
        {
            CloseHover, Grab, GrabHover, Button, ButtonHover, Tab, TabHover, TabActive, Panel, ScrollHandle
        };

        /// <summary>Every sprite name (the font excluded), for a single pass over the loaded sprites.</summary>
        public static IReadOnlyList<string> SpriteNames()
        {
            var names = new List<string>(Required.Count + Optional.Count);
            foreach (var name in Required)
            {
                if (!string.Equals(name, Font, StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }

            names.AddRange(Optional);
            return names;
        }

        /// <summary>Checks what was found against what is needed.</summary>
        /// <param name="found">Names of the assets that were found; null means none.</param>
        public static GameStyleAssetReport Evaluate(IEnumerable<string>? found)
        {
            var have = new HashSet<string>(StringComparer.Ordinal);
            if (found != null)
            {
                foreach (var name in found)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        have.Add(name);
                    }
                }
            }

            return new GameStyleAssetReport(Missing(Required, have), Missing(Optional, have));
        }

        private static IReadOnlyList<string> Missing(IReadOnlyList<string> wanted, HashSet<string> have)
        {
            var missing = new List<string>();
            foreach (var name in wanted)
            {
                if (!have.Contains(name))
                {
                    missing.Add(name);
                }
            }

            return missing;
        }
    }
}
