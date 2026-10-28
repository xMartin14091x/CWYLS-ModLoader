using System;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>What a <see cref="GameStyleWindow"/> is built with.</summary>
    public sealed class GameStyleWindowOptions
    {
        /// <summary>Object name, for a scene inspector.</summary>
        public string Name { get; set; } = "CWY Window";

        /// <summary>Title shown top-left in the game's title size.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Smallest width the caller's layout works at, in canvas units.</summary>
        public float MinWidth { get; set; } = 560f;

        /// <summary>Smallest height the caller's layout works at, in canvas units.</summary>
        public float MinHeight { get; set; } = 320f;

        /// <summary>True for a resize grip in the bottom-right corner.</summary>
        public bool Resizable { get; set; } = true;

        /// <summary>Preferred width on first placement, in canvas units; 0 for the default share of the canvas.</summary>
        public float DefaultWidth { get; set; }

        /// <summary>Preferred height on first placement, in canvas units; 0 for the default share of the canvas.</summary>
        public float DefaultHeight { get; set; }

        /// <summary>
        /// A remembered place to open at instead of the default one, kept on the canvas; null
        /// for the default place.
        /// </summary>
        public WindowRect? InitialPlace { get; set; }
    }

    /// <summary>
    /// A window that looks and moves like the game's own facility windows (the note, to-do and
    /// habit-tracker widgets): the 90 % black body, the title in the game's font, the lavender
    /// separator bars, the round close button, and the grab handle at the top centre. Drag it by
    /// its title bar; resize it from the bottom-right corner. First piece of the mod-windows
    /// toolkit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the game's assets by name (<see cref="GameStyleAssets"/>) on the shared overlay
    /// canvas (<see cref="GameStyleCanvas"/>), so no game script runs inside it; only
    /// <see cref="GameUiBridge"/> reads the game's UI state and canvas scaling. When a required
    /// asset is missing, <see cref="TryCreate"/> fails and says which, and the caller falls back.
    /// </para>
    /// <para>
    /// The window lays out its own frame. Callers put their widgets into <see cref="Body"/> with
    /// the factories (<see cref="AddButton"/>, <see cref="AddTabStrip"/>, <see cref="AddTextView"/>,
    /// <see cref="AddInputLine"/>) and do per-frame work in <see cref="Updated"/> and
    /// <see cref="LateUpdated"/>, which run only while the window is shown. A handler that throws
    /// hides the window and raises <see cref="Failed"/> once, instead of throwing every frame.
    /// </para>
    /// <para>Main thread only.</para>
    /// </remarks>
    public sealed class GameStyleWindow
    {
        private readonly GameStyleWindowOptions _options;
        private readonly GameObject _root;
        private readonly RectTransform _rect;
        private readonly CanvasGroup _group;
        private readonly Image _background;
        private readonly TextMeshProUGUI _title;
        private readonly TextMeshProUGUI _status;

        private WindowRect _place;
        private WindowRect? _initialPlace;
        private Vector2 _placedFor;
        private bool _placed;
        private bool _userMoved;
        private bool _shown;
        private float _progress;
        private float _externalAlpha = 1f;
        private float _backgroundOpacity = 0.9f;
        private bool _failed;

        private GameStyleWindow(
            GameStyleWindowOptions options,
            GameStyleAssets assets,
            GameObject root,
            CanvasGroup group,
            Image background,
            TextMeshProUGUI title,
            TextMeshProUGUI status,
            RectTransform body)
        {
            _options = options;
            Assets = assets;
            _root = root;
            _rect = (RectTransform)root.transform;
            _group = group;
            _background = background;
            _title = title;
            _status = status;
            Body = body;
            _initialPlace = options.InitialPlace;
        }

        /// <summary>Raised when the close button is clicked. The window does not hide itself.</summary>
        public event Action? CloseClicked;

        /// <summary>Raised once a frame (Update) while the window is shown.</summary>
        public event Action? Updated;

        /// <summary>Raised once a frame (LateUpdate) while the window is shown, after all Updates.</summary>
        public event Action? LateUpdated;

        /// <summary>Raised once when an <see cref="Updated"/> or <see cref="LateUpdated"/> handler threw; the window is hidden by then.</summary>
        public event Action<Exception>? Failed;

        /// <summary>Raised when the player finished moving or resizing the window (its place is remembered then).</summary>
        public event Action? PlaceCommitted;

        /// <summary>The game assets the window was built with; factories for more parts use them.</summary>
        public GameStyleAssets Assets { get; }

        /// <summary>The area below the title bar and above the bottom bar. Put the window's content here.</summary>
        public RectTransform Body { get; }

        /// <summary>False once Unity destroyed the window's objects (build a new one).</summary>
        public bool IsAlive => _root != null;

        /// <summary>True from <see cref="Show"/> until <see cref="Hide"/>, fade included.</summary>
        public bool IsShown => _shown;

        /// <summary>Where the window is, in canvas units from the top-left corner.</summary>
        public WindowRect Place => _place;

        /// <summary>The window's root object; its sibling order on the overlay canvas is the window order.</summary>
        public RectTransform Root => _rect;

        /// <summary>True once the player moved or resized the window, or it opened at a remembered place.</summary>
        public bool UserPlaced => _userMoved;

        /// <summary>The window title.</summary>
        public string Title
        {
            get => _title.text;
            set => SetText(_title, value);
        }

        /// <summary>Small text in the title bar, right-aligned before the close button.</summary>
        public string Status
        {
            get => _status.text;
            set => SetText(_status, value);
        }

        /// <summary>
        /// An extra opacity multiplied into the window's, 0 to 1 - e.g. to fade with the game's
        /// hidden UI. Below 0.5 the window stops taking clicks.
        /// </summary>
        public float ExternalAlpha
        {
            get => _externalAlpha;
            set => _externalAlpha = Mathf.Clamp01(value);
        }

        /// <summary>True when the window is visible enough to take clicks and keys.</summary>
        public bool IsInteractable => _shown && _externalAlpha >= 0.5f;

        /// <summary>
        /// Builds a hidden window. Fails, saying why, when the game's UI assets or the overlay
        /// canvas are not available.
        /// </summary>
        /// <param name="options">What to build.</param>
        /// <param name="window">The window, or null.</param>
        /// <param name="problem">Empty on success; otherwise why no window could be built.</param>
        /// <returns>True when <paramref name="window"/> is ready to <see cref="Show"/>.</returns>
        public static bool TryCreate(GameStyleWindowOptions options, out GameStyleWindow? window, out string problem)
        {
            window = null;
            if (options is null)
            {
                problem = "no options";
                return false;
            }

            var assets = GameStyleAssets.TryResolve(out problem);
            if (assets == null)
            {
                return false;
            }

            var canvas = GameStyleCanvas.Ensure(out problem);
            if (canvas == null)
            {
                return false;
            }

            GameObject? root = null;
            try
            {
                window = Build(options, assets, canvas, out root);
                problem = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                problem = "the window could not be built: " + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError(GameStyleUi.LogPrefix + " " + problem);
                if (root != null)
                {
                    UnityEngine.Object.Destroy(root);
                }

                window = null;
                return false;
            }
        }

        /// <summary>Shows the window with the game's fade-and-rise, placing it the first time.</summary>
        public void Show()
        {
            if (!IsAlive)
            {
                return;
            }

            _failed = false;
            _shown = true;
            _root.transform.SetAsLastSibling();
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            ApplyPlacement(force: true);
        }

        /// <summary>Hides the window with the game's fade-and-sink; it deactivates when the fade ends.</summary>
        /// <param name="immediate">True to skip the fade.</param>
        public void Hide(bool immediate = false)
        {
            _shown = false;
            if (!IsAlive)
            {
                return;
            }

            if (immediate)
            {
                _progress = 0f;
                _group.alpha = 0f;
                _root.SetActive(false);
            }
        }

        /// <summary>Puts the window above every other window on the overlay canvas.</summary>
        public void BringToFront()
        {
            if (IsAlive)
            {
                _root.transform.SetAsLastSibling();
            }
        }

        /// <summary>Forgets where the player put the window: it goes back to its default place.</summary>
        public void ResetPlace()
        {
            _initialPlace = null;
            _userMoved = false;
            _placed = false;
            if (IsAlive && _shown)
            {
                ApplyPlacement(force: true);
            }
        }

        /// <summary>The game-style window a canvas child belongs to, or null (for hit testing).</summary>
        /// <param name="root">A child of the overlay canvas.</param>
        internal static GameStyleWindow? Of(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            var driver = root.GetComponent<WindowDriver>();
            return driver == null ? null : driver.Window;
        }

        /// <summary>Destroys the window's objects. The instance is dead afterwards.</summary>
        public void Destroy()
        {
            _shown = false;
            if (IsAlive)
            {
                UnityEngine.Object.Destroy(_root);
            }
        }

        /// <summary>
        /// Sets the body's opacity. The game's own background is 90 % black, so values above 0.9
        /// read as 0.9.
        /// </summary>
        /// <param name="opacity">0.2 (see-through) to 0.9 (the game's own look).</param>
        public void SetBackgroundOpacity(float opacity)
        {
            var wanted = Mathf.Clamp(opacity, 0.2f, 0.9f);
            if (Mathf.Approximately(wanted, _backgroundOpacity))
            {
                return;
            }

            _backgroundOpacity = wanted;
            var color = _background.color;
            color.a = _background.sprite == null ? wanted : Mathf.Clamp01(wanted / 0.9f);
            _background.color = color;
        }

        /// <summary>A pill button in the game's style, under <paramref name="parent"/> (the body or a child of it).</summary>
        /// <param name="parent">Where it goes.</param>
        /// <param name="label">Its text.</param>
        public GameStyleButton AddButton(RectTransform parent, string label) =>
            GameStyleButton.Create(parent, Assets, label);

        /// <summary>A tab row in the style of the game's settings window.</summary>
        /// <param name="parent">Where it goes.</param>
        public GameStyleTabStrip AddTabStrip(RectTransform parent) =>
            GameStyleTabStrip.Create(parent, Assets);

        /// <summary>A scrolling rich-text line view.</summary>
        /// <param name="parent">Where it goes.</param>
        public GameStyleTextView AddTextView(RectTransform parent) =>
            GameStyleTextView.Create(parent, Assets);

        /// <summary>A one-line text input.</summary>
        /// <param name="parent">Where it goes.</param>
        /// <param name="placeholder">Grey text shown while it is empty.</param>
        public GameStyleInputLine AddInputLine(RectTransform parent, string placeholder) =>
            GameStyleInputLine.Create(parent, Assets, placeholder);

        /// <summary>A mod window's text box: one or more lines, Enter/Shift+Enter rules, composition guard.</summary>
        /// <param name="parent">Where it goes.</param>
        /// <param name="placeholder">Grey text shown while it is empty.</param>
        /// <param name="options">Lines, Enter rule, length limit.</param>
        public GameStyleInputLine AddInputBox(RectTransform parent, string placeholder, GameStyleInputOptions options) =>
            GameStyleInputLine.Create(parent, Assets, placeholder, options ?? throw new ArgumentNullException(nameof(options)));

        /// <summary>A scrolling list of clickable one-line items.</summary>
        /// <param name="parent">Where it goes.</param>
        public GameStyleListView AddListView(RectTransform parent) =>
            GameStyleListView.Create(parent, Assets);

        /// <summary>Chat bubbles with streaming text, a typing indicator and bubble buttons.</summary>
        /// <param name="parent">Where it goes.</param>
        public GameStyleChatView AddChatView(RectTransform parent) =>
            GameStyleChatView.Create(parent, Assets);

        private static GameStyleWindow Build(
            GameStyleWindowOptions options,
            GameStyleAssets assets,
            RectTransform canvas,
            out GameObject root)
        {
            var rect = GameStyleUi.Child(canvas, options.Name);
            root = rect.gameObject;

            // Everything is built inactive: TextMeshPro must have its font before it wakes up.
            root.SetActive(false);

            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);

            var group = root.AddComponent<CanvasGroup>();
            group.alpha = 0f;

            var background = GameStyleUi.AddImage(rect, assets.SpriteNamed(GameStyleAssetSet.WindowBase), GameStyleUi.WindowFill, raycast: true);

            // Title bar: the drag area, with the grab handle at its top centre.
            var header = GameStyleUi.Child(rect, "Header");
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0.5f, 1f);
            header.offsetMin = new Vector2(0f, -GameStyleUi.HeaderHeight);
            header.offsetMax = Vector2.zero;
            GameStyleUi.AddImage(header, null, Color.clear, raycast: true);
            var drag = header.gameObject.AddComponent<GameStyleDragArea>();

            var grab = GameStyleUi.Child(header, "Grab");
            GameStyleUi.Pin(grab, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(24f, 16f));
            GameStyleUi.AddImage(grab, assets.SpriteNamed(GameStyleAssetSet.Grab), GameStyleUi.Lavender, raycast: false);
            var grabHover = GameStyleUi.Child(grab, "Hover");
            GameStyleUi.Stretch(grabHover);
            var grabHoverImage = GameStyleUi.AddImage(grabHover, assets.SpriteNamed(GameStyleAssetSet.GrabHover), new Color(GameStyleUi.Cyan.r, GameStyleUi.Cyan.g, GameStyleUi.Cyan.b, 0.6f), raycast: false);
            drag.Setup(grabHoverImage);

            var titleRect = GameStyleUi.Child(rect, "Title");
            titleRect.anchorMin = new Vector2(0f, 1f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.pivot = new Vector2(0f, 1f);
            titleRect.offsetMin = new Vector2(28f, -GameStyleUi.HeaderHeight + 6f);
            titleRect.offsetMax = new Vector2(-340f, -14f);
            var title = GameStyleUi.AddLabel(titleRect, assets.Font, GameStyleUi.TitleSize, GameStyleUi.Text, TextAlignmentOptions.MidlineLeft);
            title.text = options.Title;

            var statusRect = GameStyleUi.Child(rect, "Status");
            statusRect.anchorMin = new Vector2(1f, 1f);
            statusRect.anchorMax = new Vector2(1f, 1f);
            statusRect.pivot = new Vector2(1f, 0.5f);
            statusRect.anchoredPosition = new Vector2(-(GameStyleUi.CloseInset * 2f + 16f), -GameStyleUi.CloseInset - 14f);
            statusRect.sizeDelta = new Vector2(280f, 28f);
            var status = GameStyleUi.AddLabel(statusRect, assets.Font, GameStyleUi.SmallSize, GameStyleUi.TabLabel, TextAlignmentOptions.MidlineRight);

            var close = GameStyleUi.Child(rect, "Close");
            close.anchorMin = new Vector2(1f, 1f);
            close.anchorMax = new Vector2(1f, 1f);
            close.pivot = new Vector2(0.5f, 0.5f);
            close.anchoredPosition = new Vector2(-GameStyleUi.CloseInset, -GameStyleUi.CloseInset);
            close.sizeDelta = new Vector2(GameStyleUi.CloseSize, GameStyleUi.CloseSize);
            GameStyleUi.AddImage(close, assets.SpriteNamed(GameStyleAssetSet.Close), GameStyleUi.Lavender, raycast: true);
            var closeHover = GameStyleUi.Child(close, "Hover");
            GameStyleUi.Stretch(closeHover);
            var closeHoverImage = GameStyleUi.AddImage(closeHover, assets.SpriteNamed(GameStyleAssetSet.CloseHover), new Color(1f, 1f, 1f, 0.25f), raycast: false);
            var closeClick = close.gameObject.AddComponent<GameStyleClickable>();
            closeClick.Setup(closeHoverImage, close);

            // The two lavender bars of the note window: under the title, and near the bottom edge.
            var upper = GameStyleUi.Child(rect, "UpperLine");
            upper.anchorMin = new Vector2(0f, 1f);
            upper.anchorMax = new Vector2(1f, 1f);
            upper.pivot = new Vector2(0.5f, 1f);
            upper.offsetMin = new Vector2(0f, -GameStyleUi.HeaderHeight - GameStyleUi.SeparatorThickness);
            upper.offsetMax = new Vector2(0f, -GameStyleUi.HeaderHeight);
            GameStyleUi.AddImage(upper, assets.SpriteNamed(GameStyleAssetSet.Separator), GameStyleUi.Lavender, raycast: false);

            var lower = GameStyleUi.Child(rect, "BottomLine");
            lower.anchorMin = new Vector2(0f, 0f);
            lower.anchorMax = new Vector2(1f, 0f);
            lower.pivot = new Vector2(0.5f, 0f);
            lower.offsetMin = new Vector2(0f, 18f);
            lower.offsetMax = new Vector2(0f, 18f + GameStyleUi.SeparatorThickness);
            GameStyleUi.AddImage(lower, assets.SpriteNamed(GameStyleAssetSet.Separator), GameStyleUi.Lavender, raycast: false);

            var body = GameStyleUi.Child(rect, "Body");
            GameStyleUi.Stretch(body, 24f, GameStyleUi.HeaderHeight + GameStyleUi.SeparatorThickness + 14f, 24f, 32f);

            GameStyleDragArea? resize = null;
            if (options.Resizable)
            {
                var grip = GameStyleUi.Child(rect, "ResizeGrip");
                GameStyleUi.Pin(grip, new Vector2(1f, 0f), Vector2.zero, new Vector2(30f, 30f));
                GameStyleUi.AddImage(grip, null, Color.clear, raycast: true);

                var gripIcon = GameStyleUi.Child(grip, "Icon");
                GameStyleUi.Pin(gripIcon, new Vector2(1f, 0f), new Vector2(-4f, 2f), new Vector2(24f, 16f));
                var icon = GameStyleUi.AddImage(gripIcon, assets.SpriteNamed(GameStyleAssetSet.Grab), GameStyleUi.Lavender, raycast: false);
                icon.color = new Color(icon.color.r, icon.color.g, icon.color.b, 0.7f);
                var gripHover = GameStyleUi.Child(gripIcon, "Hover");
                GameStyleUi.Stretch(gripHover);
                var gripHoverImage = GameStyleUi.AddImage(gripHover, assets.SpriteNamed(GameStyleAssetSet.GrabHover), new Color(GameStyleUi.Cyan.r, GameStyleUi.Cyan.g, GameStyleUi.Cyan.b, 0.6f), raycast: false);

                resize = grip.gameObject.AddComponent<GameStyleDragArea>();
                resize.Setup(gripHoverImage);
            }

            var window = new GameStyleWindow(options, assets, root, group, background, title, status, body);

            drag.Dragged += window.OnHeaderDragged;
            drag.DragEnded += window.OnDragEnded;
            if (resize != null)
            {
                resize.Dragged += window.OnGripDragged;
                resize.DragEnded += window.OnDragEnded;
            }

            closeClick.Clicked += window.OnCloseClicked;

            var driver = root.AddComponent<WindowDriver>();
            driver.Window = window;

            window.SetBackgroundOpacity(0.9f);
            return window;
        }

        private static void SetText(TextMeshProUGUI label, string? value)
        {
            var text = value ?? string.Empty;
            if (!string.Equals(label.text, text, StringComparison.Ordinal))
            {
                label.text = text;
            }
        }

        private void OnCloseClicked() => CloseClicked?.Invoke();

        private void OnDragEnded()
        {
            if (!_userMoved)
            {
                return;
            }

            try
            {
                PlaceCommitted?.Invoke();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} window '{_options.Name}' place handler threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnHeaderDragged(Vector2 screenDelta)
        {
            var canvas = GameStyleCanvas.Size;
            WindowRectMath.ScreenToCanvasDelta(screenDelta.x, screenDelta.y, GameStyleCanvas.ScaleFactor, out var dx, out var dy);
            _place = WindowRectMath.Move(_place, dx, dy, canvas.x, canvas.y, _options.MinWidth, _options.MinHeight);
            _userMoved = true;
            ApplyRect();
        }

        private void OnGripDragged(Vector2 screenDelta)
        {
            var canvas = GameStyleCanvas.Size;
            WindowRectMath.ScreenToCanvasDelta(screenDelta.x, screenDelta.y, GameStyleCanvas.ScaleFactor, out var dx, out var dy);
            _place = WindowRectMath.Resize(_place, dx, dy, canvas.x, canvas.y, _options.MinWidth, _options.MinHeight);
            _userMoved = true;
            ApplyRect();
        }

        /// <summary>
        /// First placement, and every change of canvas size: until the user moves the window it
        /// stays at its default place for the current canvas; after that it is only kept on it.
        /// </summary>
        private void ApplyPlacement(bool force = false)
        {
            var canvas = GameStyleCanvas.Size;
            if (canvas.x < 1f || canvas.y < 1f)
            {
                return;
            }

            if (!_placed || (!_userMoved && canvas != _placedFor))
            {
                if (_initialPlace.HasValue)
                {
                    // A remembered place: kept on today's canvas, then treated as the player's own.
                    _place = WindowPlacement.Restore(_initialPlace.Value, canvas.x, canvas.y, _options.MinWidth, _options.MinHeight);
                    _initialPlace = null;
                    _userMoved = true;
                }
                else
                {
                    _place = WindowPlacement.Default(
                        canvas.x, canvas.y, _options.DefaultWidth, _options.DefaultHeight, _options.MinWidth, _options.MinHeight);
                }

                _placed = true;
            }
            else if (canvas != _placedFor)
            {
                _place = WindowRectMath.Clamp(_place, canvas.x, canvas.y, _options.MinWidth, _options.MinHeight);
            }
            else if (!force)
            {
                // Nothing moved: leave the RectTransform alone rather than dirty the layout.
                return;
            }

            _placedFor = canvas;
            ApplyRect();
        }

        private void ApplyRect()
        {
            // The game's windows rise into place as they fade in (NoteUI: y -70 to -62).
            var sink = (1f - _progress) * GameStyleUi.ShowRise;
            _rect.anchoredPosition = new Vector2(_place.Left, -(_place.Top + sink));
            _rect.sizeDelta = new Vector2(_place.Width, _place.Height);
        }

        private void Tick()
        {
            var target = _shown ? 1f : 0f;
            if (!Mathf.Approximately(_progress, target))
            {
                _progress = GameStyleUi.Approach(_progress, target, GameStyleUi.ShowSeconds, Time.unscaledDeltaTime);
                ApplyRect();
            }

            // Written only on change: a CanvasGroup change marks the canvas for a rebuild.
            var alpha = _progress * _externalAlpha;
            if (!Mathf.Approximately(_group.alpha, alpha))
            {
                _group.alpha = alpha;
            }

            var interactable = _shown && alpha >= 0.5f;
            if (_group.blocksRaycasts != interactable)
            {
                _group.blocksRaycasts = interactable;
                _group.interactable = interactable;
            }

            if (!_shown)
            {
                if (_progress <= 0f)
                {
                    _root.SetActive(false);
                }

                return;
            }

            Raise(Updated);
        }

        private void LateTick()
        {
            if (!_shown)
            {
                return;
            }

            GameStyleCanvas.FollowGameScaling();
            ApplyPlacement();
            Raise(LateUpdated);
        }

        private void Raise(Action? handler)
        {
            if (handler == null || _failed)
            {
                return;
            }

            try
            {
                handler();
            }
            catch (Exception ex)
            {
                // Once, not every frame: hide, report, and let the owner decide what replaces it.
                _failed = true;
                CwyLsModLoader.Log.LogError(
                    $"{GameStyleUi.LogPrefix} window '{_options.Name}' failed and was hidden: {ex.GetType().Name}: {ex.Message}");
                Hide(immediate: true);

                try
                {
                    Failed?.Invoke(ex);
                }
                catch (Exception inner)
                {
                    CwyLsModLoader.Log.LogError(
                        $"{GameStyleUi.LogPrefix} window '{_options.Name}' failure handler threw: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        /// <summary>Forwards Unity's frame callbacks to the window.</summary>
        private sealed class WindowDriver : MonoBehaviour
        {
            internal GameStyleWindow? Window { get; set; }

            private void Update() => Window?.Tick();

            private void LateUpdate() => Window?.LateTick();
        }
    }
}
