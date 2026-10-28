using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Ui;
using CwyLs.ModLoader.Mods;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.UI
{
    /// <summary>
    /// One mod window: a <see cref="GameStyleWindow"/> whose body is a stack of parts drawn
    /// from a <see cref="WindowModel"/>. The window's own members are main-thread only; the parts are
    /// the Core models, safe from any thread, and the views catch up once a frame while shown.
    /// Registered with <see cref="ModWindows"/>, which orders the windows, follows Hide UI, remembers
    /// places and routes Escape.
    /// </summary>
    internal sealed class ModWindow : ICwyWindow
    {
        private readonly ModWindowOwner _owner;
        private readonly List<PartView> _views = new List<PartView>();
        private bool[] _shown;
        private float _laidOutHeight = -1f;
        private volatile bool _alive = true;

        private ModWindow(ModWindowOwner owner, WindowModel model, GameStyleWindow frame)
        {
            _owner = owner;
            Model = model;
            Frame = frame;
            _shown = new bool[model.Parts.Count];

            foreach (var part in model.Parts)
            {
                _views.Add(PartView.Create(part, frame));
            }

            frame.CloseClicked += () => RequestClose(CwyCloseReason.CloseButton);
            frame.Updated += Tick;
            frame.PlaceCommitted += () => ModWindows.RememberPlace(this);
            frame.Failed += ex => ConsoleEvents.File(
                ModWindows.TraceSys, "windowFailed", ("window", Model.Key), ("error", ex.GetType().Name + ": " + ex.Message));
        }

        /// <inheritdoc />
        public event Action<CwyCloseReason>? CloseRequested;

        /// <summary>The engine-free half.</summary>
        internal WindowModel Model { get; }

        /// <summary>The game-style frame.</summary>
        internal GameStyleWindow Frame { get; }

        /// <summary><c>&lt;modId&gt;/&lt;windowId&gt;</c>.</summary>
        internal string Key => Model.Key;

        /// <inheritdoc />
        public string Id => Model.Id;

        /// <inheritdoc />
        public string ModId => Model.ModId;

        /// <inheritdoc />
        public string ObjectName => Model.RootName;

        /// <inheritdoc />
        public string Title
        {
            get
            {
                Require("Title");
                return Frame.Title;
            }

            set
            {
                Require("Title");
                Frame.Title = value ?? string.Empty;
            }
        }

        /// <inheritdoc />
        public string Status
        {
            get
            {
                Require("Status");
                return Frame.Status;
            }

            set
            {
                Require("Status");
                Frame.Status = value ?? string.Empty;
            }
        }

        /// <inheritdoc />
        public bool IsShown => _alive && Frame.IsShown;

        /// <inheritdoc />
        public bool IsAlive => _alive;

        /// <inheritdoc />
        public bool IsInteractable => _alive && Frame.IsInteractable;

        /// <inheritdoc />
        public bool HasKeyboard
        {
            get
            {
                foreach (var part in Model.Parts)
                {
                    if (part is InputBoxModel input && input.IsFocused)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <inheritdoc />
        public WindowRect Rect => Frame.Place;

        /// <inheritdoc />
        public IReadOnlyList<ICwyPart> Parts => Model.Parts;

        /// <summary>The input view that has the keyboard, or null. Main thread.</summary>
        internal InputPartView? KeyboardInput
        {
            get
            {
                foreach (var view in _views)
                {
                    if (view is InputPartView input && input.HasKeyboard)
                    {
                        return input;
                    }
                }

                return null;
            }
        }

        /// <summary>Builds a hidden window, or says why not. Main thread.</summary>
        /// <param name="owner">The owner.</param>
        /// <param name="spec">A valid spec.</param>
        /// <param name="remembered">Where to open, or null for the default place.</param>
        internal static CwyWindowResult Build(ModWindowOwner owner, CwyWindowSpec spec, WindowRect? remembered)
        {
            WindowModel model;
            try
            {
                model = new WindowModel(owner.ModId, spec, owner.Run);
            }
            catch (ArgumentException ex)
            {
                return CwyWindowResult.Refused(ex.Message);
            }

            var options = new GameStyleWindowOptions
            {
                Name = model.RootName,
                Title = model.Title,
                MinWidth = model.MinWidth,
                MinHeight = model.MinHeight,
                Resizable = model.Resizable,
                DefaultWidth = model.Width,
                DefaultHeight = model.Height,
                InitialPlace = remembered,
            };

            if (!GameStyleWindow.TryCreate(options, out var frame, out var problem) || frame is null)
            {
                return CwyWindowResult.Refused(problem);
            }

            try
            {
                return CwyWindowResult.Success(new ModWindow(owner, model, frame));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    GameStyleUi.LogPrefix + " " + model.Key + ": the window's parts could not be built: " + ex);
                frame.Destroy();
                return CwyWindowResult.Refused("the window's parts could not be built: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <inheritdoc />
        public void Show()
        {
            Require("Show");
            if (!_alive || !ModWindows.Enabled)
            {
                // [Ui] Enabled=false: windows stay hidden until it is switched back on.
                return;
            }

            Frame.Show();
            ModWindows.Shown(this);
        }

        /// <inheritdoc />
        public void Hide()
        {
            Require("Hide");
            HideInternal();
        }

        /// <inheritdoc />
        public void Destroy()
        {
            Require("Destroy");
            if (!_alive)
            {
                return;
            }

            ReleaseKeyboard();
            _alive = false;
            Frame.Destroy();
            ModWindows.Unregister(this);
        }

        /// <inheritdoc />
        public void BringToFront()
        {
            Require("BringToFront");
            if (_alive)
            {
                ModWindows.Activate(this);
            }
        }

        /// <inheritdoc />
        public void ResetRect()
        {
            Require("ResetRect");
            ModWindows.Forget(Key);
            Frame.ResetPlace();
        }

        /// <inheritdoc />
        public ICwyTextView Text(string partId) => Model.Text(partId);

        /// <inheritdoc />
        public ICwyListView List(string partId) => Model.List(partId);

        /// <inheritdoc />
        public ICwyChatView Chat(string partId) => Model.Chat(partId);

        /// <inheritdoc />
        public ICwyInputBox Input(string partId) => Model.Input(partId);

        /// <inheritdoc />
        public ICwyButtonRow Buttons(string partId) => Model.Buttons(partId);

        /// <inheritdoc />
        public ICwyTabStrip Tabs(string partId) => Model.Tabs(partId);

        /// <inheritdoc />
        public override string ToString() => Key;

        /// <summary>The close button or Escape: hides when the spec says so, then tells the mod.</summary>
        /// <param name="reason">Who asked.</param>
        internal void RequestClose(CwyCloseReason reason)
        {
            if (!_alive)
            {
                return;
            }

            ConsoleEvents.File(ModWindows.TraceSys, "close", ("window", Key), ("reason", reason), ("hide", Model.HideOnClose));
            if (Model.HideOnClose)
            {
                HideInternal();
            }

            var handlers = CloseRequested;
            if (handlers != null)
            {
                _owner.Run("CloseRequested", () => handlers(reason));
            }
        }

        /// <summary>Every input box gives the keyboard back. Main thread.</summary>
        internal void ReleaseKeyboard()
        {
            foreach (var view in _views)
            {
                if (view is InputPartView input)
                {
                    input.ReleaseNow();
                }
            }
        }

        /// <summary>False once Unity destroyed the frame (then the window is dead and unregistered). Main thread.</summary>
        internal bool CheckAlive()
        {
            if (_alive && !Frame.IsAlive)
            {
                _alive = false;
                ConsoleEvents.File(ModWindows.TraceSys, "lost", ("window", Key));
            }

            return _alive;
        }

        private static void Require(string member)
        {
            if (!MainThread.IsMainThread)
            {
                throw new InvalidOperationException(
                    "ICwyWindow." + member + " runs on the main thread; call it from an event, a tick or helper.MainThread.Post " +
                    "(the parts - text, list, chat, input, buttons, tabs - are safe from any thread)");
            }
        }

        private void HideInternal()
        {
            if (!_alive)
            {
                return;
            }

            ReleaseKeyboard();
            Frame.Hide();
            ModWindows.Hidden(this);
        }

        /// <summary>Once a frame while shown: re-lay the body when its size or the shown parts changed, then let each part catch up.</summary>
        private void Tick()
        {
            var height = Frame.Body.rect.height;
            var changed = !Mathf.Approximately(height, _laidOutHeight);
            for (var i = 0; i < _views.Count; i++)
            {
                var shown = Model.IsShown(_views[i].Model);
                if (shown != _shown[i])
                {
                    _shown[i] = shown;
                    changed = true;
                }
            }

            if (changed)
            {
                _laidOutHeight = height;
                var slots = Model.Layout(height);
                for (var i = 0; i < _views.Count; i++)
                {
                    _views[i].Place(slots[i]);
                }
            }

            var now = MainThread.Now;
            foreach (var view in _views)
            {
                if (view.IsPlacedVisible)
                {
                    view.Tick(now);
                }
            }
        }
    }
}
