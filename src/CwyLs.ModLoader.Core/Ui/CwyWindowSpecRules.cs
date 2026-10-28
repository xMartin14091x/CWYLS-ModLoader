using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// The rules a <see cref="CwyWindowSpec"/> must follow, the names derived from it, and the
    /// parts' default heights. Pure, so every rule is unit-tested.
    /// </summary>
    public static class CwyWindowSpecRules
    {
        /// <summary>Longest window, part or button id.</summary>
        public const int MaxIdLength = 48;

        /// <summary>Most parts in one window.</summary>
        public const int MaxParts = 32;

        /// <summary>Most tabs in one tab strip.</summary>
        public const int MaxTabs = 8;

        /// <summary>Most visible lines of an input box.</summary>
        public const int MaxInputLines = 12;

        /// <summary>Highest <see cref="CwyInputSpec.MaxLength"/>.</summary>
        public const int MaxInputLength = 20000;

        /// <summary>Highest <see cref="CwyTextSpec.MaxLines"/>.</summary>
        public const int MaxTextLines = 5000;

        /// <summary>Highest <see cref="CwyChatSpec.MaxBubbles"/>.</summary>
        public const int MaxChatBubbles = 1000;

        /// <summary>Smallest <see cref="CwyWindowSpec.MinWidth"/> accepted; lower values are raised to it.</summary>
        public const float FloorMinWidth = 240f;

        /// <summary>Smallest <see cref="CwyWindowSpec.MinHeight"/> accepted; lower values are raised to it.</summary>
        public const float FloorMinHeight = 160f;

        /// <summary>Height of a one-line input box, a button row and the console's input row.</summary>
        public const float RowHeight = 44f;

        /// <summary>Height of a tab strip (the console's).</summary>
        public const float TabsHeight = 40f;

        /// <summary>Height of one text line in a multi-line input box (18 pt, with room for Thai marks).</summary>
        public const float InputLineHeight = 26f;

        /// <summary>Top and bottom padding of a multi-line input box together.</summary>
        public const float InputPadding = 18f;

        /// <summary>The window frame around the body: title bar, separator and bottom bar (GameStyleWindow).</summary>
        public const float FrameHeight = 116f;

        /// <summary>The window frame left and right of the body together.</summary>
        public const float FrameWidth = 48f;

        /// <summary>Prefix of every mod window's object name.</summary>
        public const string RootPrefix = "CWY.Mod.";

        /// <summary>
        /// True when <paramref name="id"/> is a usable window, part or button id: 1-48 of letters,
        /// digits, <c>.</c>, <c>_</c> or <c>-</c>, not starting with a dot.
        /// </summary>
        /// <param name="id">The id.</param>
        public static bool IsValidId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id!.Length > MaxIdLength || id[0] == '.')
            {
                return false;
            }

            foreach (var c in id)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                    || c == '.' || c == '_' || c == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The window's object name: <c>CWY.Mod.&lt;modId&gt;.&lt;windowId&gt;</c>.</summary>
        /// <param name="modId">The owning mod's id.</param>
        /// <param name="windowId">The window's id.</param>
        public static string RootName(string modId, string windowId) => RootPrefix + modId + "." + windowId;

        /// <summary>The key a window is registered and remembered under: <c>&lt;modId&gt;/&lt;windowId&gt;</c>.</summary>
        /// <param name="modId">The owning mod's id.</param>
        /// <param name="windowId">The window's id.</param>
        public static string Key(string modId, string windowId) => modId + "/" + windowId;

        /// <summary>
        /// The fixed height a part takes, or 0 when it shares the room left over (text, list and chat
        /// parts without an explicit <see cref="CwyPartSpec.Height"/>).
        /// </summary>
        /// <param name="part">The part.</param>
        public static float FixedHeight(CwyPartSpec part)
        {
            if (part is null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            if (part.Height > 0f)
            {
                return part.Height;
            }

            switch (part.Kind)
            {
                case CwyPartKind.Input:
                    var lines = part is CwyInputSpec input ? Math.Max(1, input.Lines) : 1;
                    return lines == 1 ? RowHeight : InputPadding + (lines * InputLineHeight);
                case CwyPartKind.Buttons:
                    return RowHeight;
                case CwyPartKind.Tabs:
                    return TabsHeight;
                default:
                    return 0f;
            }
        }

        /// <summary>The smallest window height that fits the parts on every tab (frame included).</summary>
        /// <param name="spec">A valid spec.</param>
        public static float MinimumHeight(CwyWindowSpec spec)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            var heights = new float[spec.Parts.Count];
            var tabs = new int[spec.Parts.Count];
            var tabCount = 0;
            for (var i = 0; i < spec.Parts.Count; i++)
            {
                var part = spec.Parts[i];
                heights[i] = FixedHeight(part);
                tabs[i] = part.Tab;
                if (part is CwyTabsSpec strip)
                {
                    tabCount = strip.Labels.Count;
                }
            }

            var body = StackLayout.MinimumHeight(heights, tabs, tabCount, StackLayout.Gap, StackLayout.MinFillHeight);
            return Math.Max(Math.Max(FloorMinHeight, spec.MinHeight), FrameHeight + body);
        }

        /// <summary>The smallest window width (never below <see cref="FloorMinWidth"/>).</summary>
        /// <param name="spec">A valid spec.</param>
        public static float MinimumWidth(CwyWindowSpec spec)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            return Math.Max(FloorMinWidth, spec.MinWidth);
        }

        /// <summary>Everything wrong with a spec, one sentence each; empty when it can be built.</summary>
        /// <param name="spec">The spec.</param>
        /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
        public static IReadOnlyList<string> Validate(CwyWindowSpec spec)
        {
            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            var problems = new List<string>();
            if (!IsValidId(spec.Id))
            {
                problems.Add("window id '" + spec.Id + "' " + IdRule);
            }

            CheckSize(problems, "Width", spec.Width, allowZero: true);
            CheckSize(problems, "Height", spec.Height, allowZero: true);
            CheckSize(problems, "MinWidth", spec.MinWidth, allowZero: true);
            CheckSize(problems, "MinHeight", spec.MinHeight, allowZero: true);

            if (spec.Parts.Count > MaxParts)
            {
                problems.Add("a window has at most " + MaxParts + " parts, this one has " + spec.Parts.Count);
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CwyTabsSpec? tabs = null;
            for (var i = 0; i < spec.Parts.Count; i++)
            {
                var part = spec.Parts[i];
                if (part is null)
                {
                    problems.Add("part " + i + " is null");
                    continue;
                }

                var name = DescribePart(part);
                if (!IsValidId(part.Id))
                {
                    problems.Add(name + ": id " + IdRule);
                }
                else if (!ids.Add(part.Id))
                {
                    problems.Add(name + ": another part already uses this id");
                }

                CheckSize(problems, name + " Height", part.Height, allowZero: true);

                if (part is CwyTabsSpec strip)
                {
                    if (tabs != null)
                    {
                        problems.Add(name + ": a window has at most one tab strip");
                    }

                    tabs ??= strip;
                }

                CheckPart(problems, name, part);
            }

            for (var i = 0; i < spec.Parts.Count; i++)
            {
                var part = spec.Parts[i];
                if (part is null)
                {
                    continue;
                }

                var name = DescribePart(part);
                if (part is CwyTabsSpec)
                {
                    if (part.Tab != -1)
                    {
                        problems.Add(name + ": the tab strip shows on every tab (Tab must stay -1)");
                    }

                    continue;
                }

                if (part.Tab < -1)
                {
                    problems.Add(name + ": Tab " + part.Tab + " is not a tab index (-1 for every tab)");
                }
                else if (part.Tab >= 0 && tabs is null)
                {
                    problems.Add(name + ": Tab " + part.Tab + " is set but the window has no tab strip");
                }
                else if (part.Tab >= 0 && tabs != null && part.Tab >= tabs.Labels.Count)
                {
                    problems.Add(name + ": Tab " + part.Tab + " is past the last tab (" + (tabs.Labels.Count - 1) + ")");
                }
            }

            return problems;
        }

        private const string IdRule = "must be 1-48 letters, digits, '.', '_' or '-', not starting with '.'";

        private static string DescribePart(CwyPartSpec part) =>
            part.Kind.ToString().ToLowerInvariant() + " part '" + part.Id + "'";

        private static void CheckPart(List<string> problems, string name, CwyPartSpec part)
        {
            switch (part)
            {
                case CwyTextSpec text when text.MaxLines < 1 || text.MaxLines > MaxTextLines:
                    problems.Add(name + ": MaxLines must be 1-" + MaxTextLines);
                    break;
                case CwyChatSpec chat when chat.MaxBubbles < 1 || chat.MaxBubbles > MaxChatBubbles:
                    problems.Add(name + ": MaxBubbles must be 1-" + MaxChatBubbles);
                    break;
                case CwyInputSpec input:
                    if (input.Lines < 1 || input.Lines > MaxInputLines)
                    {
                        problems.Add(name + ": Lines must be 1-" + MaxInputLines);
                    }

                    if (input.MaxLength < 0 || input.MaxLength > MaxInputLength)
                    {
                        problems.Add(name + ": MaxLength must be 0 (no limit) to " + MaxInputLength);
                    }

                    break;
                case CwyButtonsSpec row:
                    CheckButtons(problems, name, row);
                    break;
                case CwyTabsSpec strip:
                    if (strip.Labels.Count < 1 || strip.Labels.Count > MaxTabs)
                    {
                        problems.Add(name + ": a tab strip has 1-" + MaxTabs + " tabs");
                    }
                    else if (strip.Selected < 0 || strip.Selected >= strip.Labels.Count)
                    {
                        problems.Add(name + ": Selected " + strip.Selected + " is not one of its tabs");
                    }

                    for (var i = 0; i < strip.Labels.Count; i++)
                    {
                        if (strip.Labels[i] is null)
                        {
                            problems.Add(name + ": tab " + i + " has no label");
                        }
                    }

                    break;
            }
        }

        private static void CheckButtons(List<string> problems, string name, CwyButtonsSpec row)
        {
            if (row.Buttons.Count == 0)
            {
                problems.Add(name + ": a button row needs at least one button");
                return;
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < row.Buttons.Count; i++)
            {
                var button = row.Buttons[i];
                if (button is null)
                {
                    problems.Add(name + ": button " + i + " is null");
                }
                else if (!IsValidId(button.Id))
                {
                    problems.Add(name + ": button id '" + button.Id + "' " + IdRule);
                }
                else if (!ids.Add(button.Id))
                {
                    problems.Add(name + ": two buttons use the id '" + button.Id + "'");
                }
            }
        }

        private static void CheckSize(List<string> problems, string what, float value, bool allowZero)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || (!allowZero && value == 0f))
            {
                problems.Add(what + " " + value.ToString(CultureInfo.InvariantCulture) + " must be a finite size of 0 or more");
            }
        }
    }
}
