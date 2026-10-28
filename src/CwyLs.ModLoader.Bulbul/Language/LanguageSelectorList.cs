using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.UI;
using CwyLs.ModLoader.Core.Language;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>How the language selector's list shows more entries than the game sized it for.</summary>
    internal enum SelectorListMode
    {
        /// <summary>Not arranged: the settings window has not been set up with pack entries yet.</summary>
        None,

        /// <summary>The list scrolls inside the game's own open height (the normal case).</summary>
        Scroll,

        /// <summary>Every row fits in the game's open height, so nothing was changed.</summary>
        Fits,

        /// <summary>Fallback: the pulldown was made taller; the settings page may clip its last rows.</summary>
        Grown,
    }

    /// <summary>
    /// Makes the language selector's list scroll inside the pulldown's own open height
    /// instead of making the pulldown taller, which pushed the added rows below the settings page's
    /// clipping mask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scene (the game's RoomScene, game 1.17.3): <c>LanguagePulldownList</c> (<c>PulldownListUI</c>,
    /// <c>_openPullDownSizeDeltaY</c> 338.4) &gt; <c>Pulldown</c> (<c>_pullDownParentRect</c>: an
    /// <c>Image</c> + <c>Mask</c> whose height <c>OpenPullDown</c>/<c>ClosePullDown</c> tween between 40
    /// and 338.4) &gt; <c>TodoGroupText (TMP)</c> (the caption) &gt;
    /// <c>Content</c> (<c>VerticalLayoutGroup</c> spacing 0.5, <c>ContentSizeFitter</c> preferred height)
    /// &gt; the seven entries, 39.8 tall. The pulldown sits inside
    /// <c>UI_FacilitySetting/General/ScrollView/Viewport</c>, whose <c>Mask</c> ends just below the
    /// game's open list, so an eighth row there is cut by the page and cannot be clicked.
    /// </para>
    /// <para>
    /// The arrangement mirrors the game's own scrollable pulldown (the to-do list picker,
    /// <c>UI_FacilityTodo/TodoGroupSelect/PullDown/Scroll View</c>): <c>Content</c> moves into a
    /// <c>RectMask2D</c> viewport exactly where it was and as tall as the game's own rows, under a
    /// <c>ScrollRect</c> with the settings page's own scroll settings and a copy of its scrollbar.
    /// The pulldown's open height, animation and position are untouched. Every check runs before
    /// anything changes; a failure while building puts <c>Content</c> back and the caller falls back
    /// to growing the pulldown (the pre-fix behaviour) with one warning.
    /// </para>
    /// </remarks>
    internal static class LanguageSelectorList
    {
        /// <summary>Name of the scroll view object we add inside the pulldown.</summary>
        internal const string ListName = "LanguageList_cwy";

        /// <summary>Name of the clipping viewport the game's <c>Content</c> moves into.</summary>
        internal const string ViewportName = "LanguageListViewport_cwy";

        /// <summary>Name of the list's scrollbar.</summary>
        internal const string ScrollbarName = "LanguageListScrollbar_cwy";

        // The game's open height per pulldown instance, read before anything could change it, so a
        // second setup of the same window (or a fallback that grew it) still plans from the game's value.
        private static readonly Dictionary<int, float> GameOpenHeights = new Dictionary<int, float>();

        private static PulldownListUI? _pulldown;
        private static LanguageListScroller? _scroller;

        /// <summary>How the last setup arranged the list.</summary>
        internal static SelectorListMode Mode { get; private set; } = SelectorListMode.None;

        /// <summary>The plan the list was arranged with, or null (not arranged, or grown).</summary>
        internal static SelectorListPlan? Plan { get; private set; }

        /// <summary>The game's open height of the pulldown, or NaN before it was read.</summary>
        internal static float GameOpenHeight { get; private set; } = float.NaN;

        /// <summary>Where the scrollbar came from: <c>game</c> (a copy of the settings page's), <c>minimal</c>, or <c>none</c>.</summary>
        internal static string ScrollbarSource { get; private set; } = "none";

        /// <summary>Why the list could not scroll (fallback), or empty.</summary>
        internal static string FallbackReason { get; private set; } = string.Empty;

        /// <summary>One phrase for the smoke report and the setup line. Never null.</summary>
        internal static string Detail
        {
            get
            {
                switch (Mode)
                {
                    case SelectorListMode.Scroll:
                        return "list scrolls (" + Plan + "), open height " + Round(GameOpenHeight) + " kept, scrollbar " + ScrollbarSource;
                    case SelectorListMode.Fits:
                        return "list fits the game's open height " + Round(GameOpenHeight) + " (" + Plan + ")";
                    case SelectorListMode.Grown:
                        return "list cannot scroll (" + FallbackReason + "); pulldown grown instead";
                    default:
                        return "list not arranged yet";
                }
            }
        }

        /// <summary>
        /// Arranges the list of <paramref name="content"/> so every row can be scrolled into the
        /// game's open height. Idempotent: a second call for an arranged list only re-measures it.
        /// </summary>
        /// <param name="pulldown">The language pulldown.</param>
        /// <param name="content">The entries' parent (the game's <c>Content</c>).</param>
        /// <param name="ownPrefix">Name prefix of the entries this framework added.</param>
        /// <param name="reason">Why it could not, or empty. When this is false the call changed nothing (a failed build is undone).</param>
        /// <returns>True when the list scrolls or already fits.</returns>
        internal static bool TryArrange(PulldownListUI pulldown, RectTransform content, string ownPrefix, out string reason)
        {
            _pulldown = pulldown;
            Mode = SelectorListMode.None;
            Plan = null;
            FallbackReason = string.Empty;

            if (!TryReadGameOpenHeight(pulldown, out var openHeight))
            {
                reason = "PulldownListUI._openPullDownSizeDeltaY is not readable";
                return false;
            }

            GameOpenHeight = openHeight;

            var frame = ReadField<RectTransform>(pulldown, "_pullDownParentRect");
            reason = CheckStructure(frame, content, out var layout);
            if (reason.Length > 0 || frame == null || layout == null)
            {
                return false;
            }

            // Already arranged (a second setup of the same window): re-measure only. GetComponent,
            // not GetComponentInParent, which skips inactive objects - and the window is inactive here.
            var viewportParent = content.parent;
            var existing = viewportParent != null && viewportParent.name == ViewportName && viewportParent.parent != null
                ? viewportParent.parent.GetComponent<LanguageListScroller>()
                : null;
            if (existing != null)
            {
                var again = existing.Refresh();
                if (!again.IsUsable)
                {
                    reason = again.Problem!;
                    return false;
                }

                _scroller = existing;
                Plan = again;
                Mode = SelectorListMode.Scroll;
                return true;
            }

            if (!Measure(frame, content, out var listLeft, out var listWidth, out var listTop, out reason))
            {
                return false;
            }

            var plan = PlanFor(content, layout, ownPrefix, openHeight, listTop);
            if (!plan.IsUsable)
            {
                reason = plan.Problem!;
                return false;
            }

            if (!plan.Scrolls)
            {
                Plan = plan;
                Mode = SelectorListMode.Fits;
                return true;
            }

            if (!Build(pulldown, frame, content, layout, ownPrefix, plan, listLeft, listWidth, out reason))
            {
                return false;
            }

            Plan = plan;
            Mode = SelectorListMode.Scroll;
            return true;
        }

        /// <summary>Records the fallback: the caller made the pulldown taller.</summary>
        /// <param name="reason">Why the list could not scroll.</param>
        internal static void MarkGrown(string reason)
        {
            Mode = SelectorListMode.Grown;
            Plan = null;
            FallbackReason = reason;
        }

        /// <summary>
        /// What is wrong with the arranged list now, or empty: the fallback was taken, our objects
        /// were destroyed, the game's open height was changed, or a row cannot be scrolled whole
        /// into view. Read-only, for <c>pb.language.entry</c>.
        /// </summary>
        internal static string Problem()
        {
            switch (Mode)
            {
                case SelectorListMode.Grown:
                    return "the list cannot scroll (" + FallbackReason + "), so the settings page may clip its last rows";
                case SelectorListMode.Scroll:
                    break;
                default:
                    return string.Empty;
            }

            if (_scroller == null || _pulldown == null)
            {
                return "the list's scroll view was destroyed";
            }

            var field = AccessTools.Field(typeof(PulldownListUI), "_openPullDownSizeDeltaY");
            if (field?.GetValue(_pulldown) is float now && Math.Abs(now - GameOpenHeight) > SelectorListLayout.FitTolerance)
            {
                return "the pulldown's open height is " + Round(now) + ", not the game's " + Round(GameOpenHeight);
            }

            var plan = Plan;
            if (plan == null)
            {
                return "the list has no plan";
            }

            for (var i = plan.GameRows; i < plan.TotalRows; i++)
            {
                if (!plan.IsWhollyVisible(i, plan.Reveal(i, 0f)))
                {
                    return "row " + i + " cannot be scrolled whole into view";
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// The rows the list's layout places, in order: active children that do not ignore layout.
        /// Uses <c>activeSelf</c>, because the settings window is inactive when it is set up.
        /// </summary>
        /// <param name="content">The entries' parent.</param>
        internal static List<RectTransform> Rows(RectTransform content)
        {
            var rows = new List<RectTransform>(content.childCount);
            for (var i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i) as RectTransform;
                if (child == null || !child.gameObject.activeSelf)
                {
                    continue;
                }

                var element = child.GetComponent<LayoutElement>();
                if (element != null && element.enabled && element.ignoreLayout)
                {
                    continue;
                }

                rows.Add(child);
            }

            return rows;
        }

        /// <summary>
        /// Plans the list from the scene as it is now: row heights the way the layout reads them
        /// (<c>sizeDelta.y</c> when it does not control child height), the game's rows being the
        /// leading ones that are not ours.
        /// </summary>
        /// <param name="content">The entries' parent.</param>
        /// <param name="layout">Its layout group.</param>
        /// <param name="ownPrefix">Name prefix of our entries.</param>
        /// <param name="openHeight">The game's open height.</param>
        /// <param name="listTop">Distance from the pulldown's top edge to the list's top.</param>
        internal static SelectorListPlan PlanFor(RectTransform content, VerticalLayoutGroup layout, string ownPrefix, float openHeight, float listTop)
        {
            var rows = Rows(content);
            var heights = new float[rows.Count];
            var gameRows = 0;
            var leading = true;
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var height = layout.childControlHeight ? LayoutUtility.GetPreferredHeight(row) : row.sizeDelta.y;
                heights[i] = layout.childScaleHeight ? height * row.localScale.y : height;

                if (leading && row.name.StartsWith(ownPrefix, StringComparison.Ordinal))
                {
                    leading = false;
                }

                if (leading)
                {
                    gameRows++;
                }
            }

            return SelectorListLayout.Plan(openHeight, listTop, heights, gameRows, layout.spacing, layout.padding.top, layout.padding.bottom);
        }

        /// <summary>The selected language's row: the first row whose highlight is on, or -1.</summary>
        /// <param name="content">The entries' parent.</param>
        internal static int SelectedRow(RectTransform content)
        {
            var rows = Rows(content);
            for (var i = 0; i < rows.Count; i++)
            {
                var entry = rows[i].GetComponent<global::Bulbul.InteractableUI>();
                if (entry != null && entry.IsUsing)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Everything the arrangement relies on, checked before anything changes. Empty when all hold.</summary>
        private static string CheckStructure(RectTransform? frame, RectTransform content, out VerticalLayoutGroup? layout)
        {
            layout = null;
            if (frame == null)
            {
                return "PulldownListUI._pullDownParentRect is not readable";
            }

            if (content == frame || !content.IsChildOf(frame))
            {
                return "the entries are not inside the pulldown's animated frame '" + frame.name + "'";
            }

            layout = content.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
            {
                return "'" + content.name + "' has no VerticalLayoutGroup";
            }

            if (layout.reverseArrangement)
            {
                return "'" + content.name + "' lays its rows out bottom to top";
            }

            var fitter = content.GetComponent<ContentSizeFitter>();
            if (fitter == null || fitter.verticalFit != ContentSizeFitter.FitMode.PreferredSize)
            {
                return "'" + content.name + "' is not sized to its rows (ContentSizeFitter vertical PreferredSize)";
            }

            if (Math.Abs(content.pivot.y - 1f) > 0.001f)
            {
                return "'" + content.name + "' does not grow down from its top (pivot y " + content.pivot.y + ")";
            }

            for (Transform? t = content; t != null && t != frame; t = t.parent)
            {
                if ((t.localScale - Vector3.one).sqrMagnitude > 1e-6f || Quaternion.Angle(t.localRotation, Quaternion.identity) > 0.01f)
                {
                    return "'" + t.name + "' is scaled or rotated inside the pulldown";
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Where the list sits in the pulldown's frame: its left edge and width, and its top below
        /// the frame's top edge. The top corners do not depend on the list's height (pivot y = 1,
        /// checked), which is stale until the settings window is first shown.
        /// </summary>
        private static bool Measure(RectTransform frame, RectTransform content, out float left, out float width, out float top, out string reason)
        {
            var corners = new Vector3[4];
            content.GetWorldCorners(corners);
            var topLeft = frame.InverseTransformPoint(corners[1]);
            var topRight = frame.InverseTransformPoint(corners[2]);
            var bounds = frame.rect;

            left = topLeft.x - bounds.xMin;
            width = topRight.x - topLeft.x;
            top = bounds.yMax - topLeft.y;

            if (float.IsNaN(left) || float.IsNaN(top) || float.IsNaN(width) || width <= 0f)
            {
                reason = "the list's position in the pulldown is not measurable (left " + left + ", top " + top + ", width " + width + ")";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        /// <summary>Builds the scroll view around <paramref name="content"/>; on any failure puts everything back.</summary>
        private static bool Build(
            PulldownListUI pulldown,
            RectTransform frame,
            RectTransform content,
            VerticalLayoutGroup layout,
            string ownPrefix,
            SelectorListPlan plan,
            float listLeft,
            float listWidth,
            out string reason)
        {
            var saved = new SavedRect(content);
            RectTransform? list = null;
            try
            {
                // The list band: the frame's full width from the list's top, as tall as the visible
                // rows. Its transparent image catches the wheel and drags in the gaps between rows,
                // so they scroll the list and not the settings page behind it.
                list = GameStyleUi.Child(frame, ListName);
                list.SetAsLastSibling();
                list.anchorMin = new Vector2(0f, 1f);
                list.anchorMax = new Vector2(1f, 1f);
                list.pivot = new Vector2(0f, 1f);
                list.anchoredPosition = new Vector2(0f, -plan.ListTop);
                list.sizeDelta = new Vector2(0f, plan.ViewportHeight);
                GameStyleUi.AddImage(list, null, new Color(1f, 1f, 1f, 0f), raycast: true);

                // The viewport: exactly where Content was, so every row keeps its position.
                var viewport = GameStyleUi.Child(list, ViewportName);
                viewport.anchorMin = new Vector2(0f, 0f);
                viewport.anchorMax = new Vector2(0f, 1f);
                viewport.pivot = new Vector2(0f, 1f);
                viewport.anchoredPosition = new Vector2(listLeft, 0f);
                viewport.sizeDelta = new Vector2(listWidth, 0f);
                viewport.gameObject.AddComponent<RectMask2D>();

                var settings = SettingsScroll(pulldown);
                var bar = CreateScrollbar(settings, list, out var source);

                content.SetParent(viewport, false);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0f, 1f);
                content.anchoredPosition = Vector2.zero;
                content.sizeDelta = new Vector2(0f, plan.ContentHeight);

                var scroll = list.gameObject.AddComponent<ScrollRect>();
                scroll.content = content;
                scroll.viewport = viewport;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.inertia = settings == null || settings.inertia;
                scroll.decelerationRate = settings != null ? settings.decelerationRate : 0.135f;
                scroll.scrollSensitivity = settings != null ? settings.scrollSensitivity : 50f;
                scroll.verticalScrollbar = bar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

                var scroller = list.gameObject.AddComponent<LanguageListScroller>();
                var page = settings == null ? null : settings.viewport != null ? settings.viewport : settings.transform as RectTransform;
                scroller.Bind(pulldown, scroll, content, layout, ownPrefix, GameOpenHeight, plan.ListTop, page);

                _scroller = scroller;
                ScrollbarSource = source;
                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "building the scroll view failed: " + ex.GetType().Name + ": " + ex.Message;
                Undo(saved, content, list);
                return false;
            }
        }

        /// <summary>
        /// The list's scrollbar: a copy of the settings page's own (the game's pill handle on its
        /// dark track), placed like the game's to-do list picker places its scrollbar; a plain one
        /// when the page has none.
        /// </summary>
        private static Scrollbar CreateScrollbar(ScrollRect? settings, RectTransform list, out string source)
        {
            var template = settings != null ? settings.verticalScrollbar : null;
            if (template != null)
            {
                GameObject? copy = null;
                try
                {
                    copy = UnityEngine.Object.Instantiate(template.gameObject, list, false);
                    copy.name = ScrollbarName;
                    var bar = copy.GetComponent<Scrollbar>();
                    if (bar != null && bar.handleRect != null && copy.transform is RectTransform rect)
                    {
                        Place(rect);
                        source = "game";
                        return bar;
                    }

                    CwyLsModLoader.Log.LogDebug(LanguageFeature.LogPrefix + " the settings page's scrollbar has no handle; using a plain one");
                    UnityEngine.Object.Destroy(copy);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " could not copy the settings page's scrollbar (" + ex.GetType().Name + ": " + ex.Message + "); using a plain one");
                    if (copy != null)
                    {
                        UnityEngine.Object.Destroy(copy);
                    }
                }
            }

            source = "minimal";
            return BuildMinimalScrollbar(list);
        }

        /// <summary>Against the frame's right edge, as tall as the visible rows.</summary>
        private static void Place(RectTransform rect)
        {
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-SelectorListLayout.ScrollbarRightInset, 0f);
            rect.sizeDelta = new Vector2(SelectorListLayout.ScrollbarWidth, 0f);
        }

        /// <summary>The game's scrollbar layout (sliding area inset 10 each end) with plain colours.</summary>
        private static Scrollbar BuildMinimalScrollbar(RectTransform list)
        {
            var track = GameStyleUi.Child(list, ScrollbarName);
            Place(track);
            GameStyleUi.AddImage(track, null, new Color(0f, 0f, 0f, 0.5f), raycast: true);

            var area = GameStyleUi.Child(track, "Sliding Area");
            GameStyleUi.Stretch(area, 0f, 10f, 0f, 10f);

            var handle = GameStyleUi.Child(area, "Handle");
            GameStyleUi.Stretch(handle, 0f, -10f, 0f, -10f);
            var handleImage = GameStyleUi.AddImage(handle, null, Color.white, raycast: true);

            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            return bar;
        }

        /// <summary>The settings page's scroll view: the first <c>ScrollRect</c> above the pulldown.</summary>
        private static ScrollRect? SettingsScroll(PulldownListUI pulldown)
        {
            for (var t = pulldown.transform.parent; t != null; t = t.parent)
            {
                var scroll = t.GetComponent<ScrollRect>();
                if (scroll != null)
                {
                    return scroll;
                }
            }

            return null;
        }

        /// <summary>Puts <c>Content</c> back where it was and removes what was built.</summary>
        private static void Undo(SavedRect saved, RectTransform content, RectTransform? list)
        {
            try
            {
                saved.Restore(content);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not put the language list back: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (list == null)
            {
                return;
            }

            try
            {
                UnityEngine.Object.Destroy(list.gameObject);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(LanguageFeature.LogPrefix + " could not remove the half-built list scroll view: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>The game's open height of this pulldown, read the first time this instance is seen.</summary>
        private static bool TryReadGameOpenHeight(PulldownListUI pulldown, out float height)
        {
            var id = pulldown.GetInstanceID();
            if (GameOpenHeights.TryGetValue(id, out height))
            {
                return true;
            }

            try
            {
                var field = AccessTools.Field(typeof(PulldownListUI), "_openPullDownSizeDeltaY");
                if (field?.GetValue(pulldown) is float value)
                {
                    GameOpenHeights[id] = value;
                    height = value;
                    return true;
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " PulldownListUI._openPullDownSizeDeltaY is not readable: " + ex.GetType().Name + ": " + ex.Message);
            }

            height = float.NaN;
            return false;
        }

        private static T? ReadField<T>(PulldownListUI pulldown, string name)
            where T : class
        {
            try
            {
                return AccessTools.Field(typeof(PulldownListUI), name)?.GetValue(pulldown) as T;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " PulldownListUI." + name + " is not readable: " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private static string Round(float value) =>
            float.IsNaN(value) ? "?" : Math.Round(value, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Where <c>Content</c> was, so a failed build can put it back exactly.</summary>
        private readonly struct SavedRect
        {
            private readonly Transform? _parent;
            private readonly int _sibling;
            private readonly Vector2 _anchorMin;
            private readonly Vector2 _anchorMax;
            private readonly Vector2 _pivot;
            private readonly Vector2 _position;
            private readonly Vector2 _size;

            internal SavedRect(RectTransform rect)
            {
                _parent = rect.parent;
                _sibling = rect.GetSiblingIndex();
                _anchorMin = rect.anchorMin;
                _anchorMax = rect.anchorMax;
                _pivot = rect.pivot;
                _position = rect.anchoredPosition;
                _size = rect.sizeDelta;
            }

            internal void Restore(RectTransform rect)
            {
                if (rect.parent != _parent)
                {
                    rect.SetParent(_parent, false);
                    rect.SetSiblingIndex(_sibling);
                }

                rect.anchorMin = _anchorMin;
                rect.anchorMax = _anchorMax;
                rect.pivot = _pivot;
                rect.anchoredPosition = _position;
                rect.sizeDelta = _size;
            }
        }
    }
}
