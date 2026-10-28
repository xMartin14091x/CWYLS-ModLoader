using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// A clickable part that feels like the game's buttons: a hover overlay that fades in and out
    /// (the game's <c>InteractableUI</c>), and a scale that grows under the pointer and shrinks
    /// while pressed (<c>HoldButtonAnimation</c>). Needs a raycast-target Graphic on its object.
    /// </summary>
    /// <remarks>
    /// Deliberately not a <c>UnityEngine.UI.Button</c>: the trace plugin mirrors every
    /// <c>Button.Press</c> to the console as a <c>BUTTON</c> line, and the console's own tabs and
    /// buttons must not fill the console with lines about themselves.
    /// </remarks>
    public sealed class GameStyleClickable : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        private Image? _overlay;
        private RectTransform? _scaleTarget;
        private float _overlayOn = 1f;
        private float _overlayAlpha;
        private float _overlayTarget;
        private float _scale = 1f;
        private float _scaleTarget01 = 1f;
        private bool _hovered;
        private bool _pressed;

        /// <summary>Raised on a left click.</summary>
        public event Action? Clicked;

        /// <summary>Raised when the pointer enters or leaves.</summary>
        public event Action<bool>? HoverChanged;

        /// <summary>True while the pointer is over the part.</summary>
        public bool IsHovered => _hovered;

        /// <summary>
        /// Wires the part. Call once, right after <c>AddComponent</c>.
        /// </summary>
        /// <param name="overlay">
        /// Image faded in under the pointer, or null for none. Its current alpha is the "hovered"
        /// value (1 for a game sprite, lower for a plain stand-in); it rests at 0.
        /// </param>
        /// <param name="scaleTarget">What grows and shrinks, or null for no scale animation.</param>
        public void Setup(Image? overlay, RectTransform? scaleTarget)
        {
            _overlay = overlay;
            _scaleTarget = scaleTarget;
            if (_overlay != null)
            {
                _overlayOn = _overlay.color.a;
                SetAlpha(_overlay, 0f);
            }
        }

        /// <inheritdoc />
        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            _overlayTarget = 1f;
            _scaleTarget01 = _pressed ? GameStyleUi.PressScale : GameStyleUi.HoverScale;
            HoverChanged?.Invoke(true);
        }

        /// <inheritdoc />
        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _overlayTarget = 0f;
            _scaleTarget01 = _pressed ? GameStyleUi.PressScale : 1f;
            HoverChanged?.Invoke(false);
        }

        /// <inheritdoc />
        public void OnPointerDown(PointerEventData eventData)
        {
            _pressed = true;
            _scaleTarget01 = GameStyleUi.PressScale;
        }

        /// <inheritdoc />
        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
            _scaleTarget01 = _hovered ? GameStyleUi.HoverScale : 1f;
        }

        /// <inheritdoc />
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            try
            {
                Clicked?.Invoke();
            }
            catch (Exception ex)
            {
                // A handler that throws must not take the EventSystem's frame down with it.
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} a click handler on '{name}' threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnDisable()
        {
            _hovered = false;
            _pressed = false;
            _overlayTarget = 0f;
            _overlayAlpha = 0f;
            _scaleTarget01 = 1f;
            _scale = 1f;

            if (_overlay != null)
            {
                SetAlpha(_overlay, 0f);
            }

            if (_scaleTarget != null)
            {
                _scaleTarget.localScale = Vector3.one;
            }
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;

            if (_overlay != null && !Mathf.Approximately(_overlayAlpha, _overlayTarget))
            {
                var seconds = _overlayTarget > _overlayAlpha ? GameStyleUi.HoverInSeconds : GameStyleUi.HoverOutSeconds;
                _overlayAlpha = GameStyleUi.Approach(_overlayAlpha, _overlayTarget, seconds, dt);
                SetAlpha(_overlay, _overlayAlpha * _overlayOn);
            }

            if (_scaleTarget != null && !Mathf.Approximately(_scale, _scaleTarget01))
            {
                // Full 0.95 -> 1.05 swing in the game's 0.1 s.
                _scale = Mathf.MoveTowards(_scale, _scaleTarget01, dt * 0.1f / GameStyleUi.ScaleSeconds);
                _scaleTarget.localScale = new Vector3(_scale, _scale, 1f);
            }
        }

        private static void SetAlpha(Graphic graphic, float alpha)
        {
            var color = graphic.color;
            color.a = alpha;
            graphic.color = color;
        }
    }

    /// <summary>
    /// A part the pointer drags: reports each movement in screen pixels, and fades an optional
    /// overlay in while the pointer is over it (the game's drag handle lights up the same way).
    /// Needs a raycast-target Graphic on its object.
    /// </summary>
    public sealed class GameStyleDragArea : MonoBehaviour,
        IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        private Image? _overlay;
        private float _overlayOn = 1f;
        private float _alpha;
        private float _target;
        private bool _dragging;

        /// <summary>Raised for every pointer movement during a drag, in screen pixels (Y up).</summary>
        public event Action<Vector2>? Dragged;

        /// <summary>Raised when a drag ends.</summary>
        public event Action? DragEnded;

        /// <summary>Wires the part. Call once, right after <c>AddComponent</c>.</summary>
        /// <param name="overlay">
        /// Image faded in under the pointer and while dragging, or null. Its current alpha is the
        /// "on" value; it rests at 0.
        /// </param>
        public void Setup(Image? overlay)
        {
            _overlay = overlay;
            if (_overlay != null)
            {
                var color = _overlay.color;
                _overlayOn = color.a;
                color.a = 0f;
                _overlay.color = color;
            }
        }

        /// <inheritdoc />
        public void OnInitializePotentialDrag(PointerEventData eventData)
        {
            // A window follows the pointer from the first pixel, as the game's windows do.
            eventData.useDragThreshold = false;
        }

        /// <inheritdoc />
        public void OnBeginDrag(PointerEventData eventData)
        {
            _dragging = true;
            _target = 1f;
        }

        /// <inheritdoc />
        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            try
            {
                Dragged?.Invoke(eventData.delta);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} a drag handler on '{name}' threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void OnEndDrag(PointerEventData eventData)
        {
            _dragging = false;
            _target = eventData.hovered.Contains(gameObject) ? 1f : 0f;

            try
            {
                DragEnded?.Invoke();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} a drag-end handler on '{name}' threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <inheritdoc />
        public void OnPointerEnter(PointerEventData eventData) => _target = 1f;

        /// <inheritdoc />
        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_dragging)
            {
                _target = 0f;
            }
        }

        private void OnDisable()
        {
            _dragging = false;
            _target = 0f;
            _alpha = 0f;
            if (_overlay != null)
            {
                var color = _overlay.color;
                color.a = 0f;
                _overlay.color = color;
            }
        }

        private void Update()
        {
            if (_overlay == null || Mathf.Approximately(_alpha, _target))
            {
                return;
            }

            var seconds = _target > _alpha ? GameStyleUi.HoverInSeconds : GameStyleUi.HoverOutSeconds;
            _alpha = GameStyleUi.Approach(_alpha, _target, seconds, Time.unscaledDeltaTime);
            var color = _overlay.color;
            color.a = _alpha * _overlayOn;
            _overlay.color = color;
        }
    }

    /// <summary>Reports mouse-wheel movement over its object. Needs a raycast-target Graphic.</summary>
    public sealed class GameStyleScrollArea : MonoBehaviour, IScrollHandler
    {
        /// <summary>Raised per wheel event: positive is up (towards older lines), in wheel notches.</summary>
        public event Action<float>? Scrolled;

        /// <inheritdoc />
        public void OnScroll(PointerEventData eventData)
        {
            try
            {
                Scrolled?.Invoke(eventData.scrollDelta.y);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} a scroll handler on '{name}' threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
