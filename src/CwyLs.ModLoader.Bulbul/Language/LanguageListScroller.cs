using System;
using System.Runtime.CompilerServices;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.Language
{
    /// <summary>
    /// Lives on the language list's scroll view (<see cref="LanguageSelectorList"/>). Each time the
    /// pulldown opens it starts the list from the top, as the game shows it, and scrolls just far
    /// enough that the selected language's row is whole in view.
    /// </summary>
    /// <remarks>
    /// Watches <c>PulldownListUI.IsOpen</c> in <c>LateUpdate</c>, so the open
    /// is seen in the same frame as the click that caused it (the event system runs in
    /// <c>Update</c>), whichever path opened it; the game's open tween then reveals the list already
    /// at its offset. Runs only while the settings window is active, one bool read per frame.
    /// </remarks>
    internal sealed class LanguageListScroller : MonoBehaviour
    {
        private PulldownListUI? _pulldown;
        private ScrollRect? _scroll;
        private RectTransform? _content;
        private RectTransform? _band;
        private RectTransform? _page;
        private VerticalLayoutGroup? _layout;
        private string _ownPrefix = string.Empty;
        private float _openHeight;
        private float _listTop;
        private bool _wasOpen;
        private bool _stopped;

        /// <summary>Connects the scroller to the pieces <see cref="LanguageSelectorList"/> built.</summary>
        /// <param name="pulldown">The language pulldown.</param>
        /// <param name="scroll">The list's scroll view (on this object).</param>
        /// <param name="content">The game's <c>Content</c>, now inside the viewport.</param>
        /// <param name="layout">Its layout group.</param>
        /// <param name="ownPrefix">Name prefix of our entries.</param>
        /// <param name="openHeight">The game's open height.</param>
        /// <param name="listTop">Distance from the pulldown's top edge to the list's top.</param>
        /// <param name="page">The settings page's clipping viewport, for the <c>pageCut</c> diagnostic; may be null.</param>
        internal void Bind(
            PulldownListUI pulldown,
            ScrollRect scroll,
            RectTransform content,
            VerticalLayoutGroup layout,
            string ownPrefix,
            float openHeight,
            float listTop,
            RectTransform? page)
        {
            _pulldown = pulldown;
            _scroll = scroll;
            _content = content;
            _page = page;
            _band = (RectTransform)transform;
            _layout = layout;
            _ownPrefix = ownPrefix;
            _openHeight = openHeight;
            _listTop = listTop;
        }

        /// <summary>
        /// Re-measures the rows, keeps the visible area at the plan's height, and returns the plan.
        /// </summary>
        internal SelectorListPlan Refresh()
        {
            if (_content == null || _layout == null || _band == null)
            {
                return SelectorListLayout.Plan(_openHeight, _listTop, Array.Empty<float>(), 0, 0f, 0f, 0f);
            }

            var plan = LanguageSelectorList.PlanFor(_content, _layout, _ownPrefix, _openHeight, _listTop);
            if (plan.IsUsable && Math.Abs(_band.sizeDelta.y - plan.ViewportHeight) > 0.01f)
            {
                _band.sizeDelta = new Vector2(_band.sizeDelta.x, plan.ViewportHeight);
            }

            return plan;
        }

        private void OnEnable()
        {
            _wasOpen = false;
        }

        private void LateUpdate()
        {
            if (_stopped || _pulldown == null)
            {
                return;
            }

            try
            {
                var open = IsOpen(_pulldown);
                if (open && !_wasOpen)
                {
                    OnOpened();
                }

                _wasOpen = open;
            }
            catch (Exception ex)
            {
                // Once, then stay out of the way: the list still scrolls by hand.
                _stopped = true;
                CwyLsModLoader.Log.LogWarning(LanguageFeature.LogPrefix + " the language list stopped scrolling to the selection on open: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>A separate, never-inlined call, so a game update that removes <c>IsOpen</c> fails here, inside the caller's catch.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsOpen(PulldownListUI pulldown) => pulldown.IsOpen;

        private void OnOpened()
        {
            if (_content == null || _scroll == null)
            {
                return;
            }

            var plan = Refresh();
            if (!plan.IsUsable)
            {
                return;
            }

            var row = LanguageSelectorList.SelectedRow(_content);
            var offset = plan.Reveal(row, 0f);

            _scroll.StopMovement();
            var position = _content.anchoredPosition;
            _content.anchoredPosition = new Vector2(position.x, offset);

            var cut = PageCut(plan);
            ConsoleEvents.File(
                LanguageFeature.TraceSys,
                "list.opened",
                ("row", row),
                ("offset", Math.Round(offset, 1)),
                ("maxOffset", Math.Round(plan.MaxOffset, 1)),
                ("whole", row < 0 || plan.IsWhollyVisible(row, offset)),
                ("pageCut", float.IsNaN(cut) ? (object)"-" : Math.Round(cut, 1)));
        }

        /// <summary>
        /// How much of the visible list lies below the settings page's clipping viewport with the
        /// page where it is now: 0 when the whole list can be seen and clicked. NaN when unknown.
        /// </summary>
        private float PageCut(SelectorListPlan plan)
        {
            var frame = transform.parent as RectTransform;
            if (_page == null || frame == null)
            {
                return float.NaN;
            }

            var bounds = frame.rect;
            var bottom = frame.TransformPoint(new Vector3(bounds.xMin, bounds.yMax - plan.ListTop - plan.ViewportHeight, 0f));
            var inPage = _page.InverseTransformPoint(bottom);
            return Math.Max(0f, _page.rect.yMin - inPage.y);
        }
    }
}
