using System;
using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// The engine-free half of a mod window: its settings copied from the spec (later changes to the
    /// spec do not reach a built window), its part models, the tab rule and the body layout. The
    /// game adapter builds the view on top. Framework plumbing.
    /// </summary>
    public sealed class WindowModel
    {
        private readonly List<PartModel> _parts = new List<PartModel>();
        private readonly float[] _fixedHeights;
        private readonly bool[] _visible;

        /// <summary>Builds the model of a valid spec.</summary>
        /// <param name="modId">The owning mod's id.</param>
        /// <param name="spec">The spec (<see cref="CwyWindowSpecRules.Validate"/> must find nothing).</param>
        /// <param name="run">Runs the mod's event handlers with its attribution.</param>
        /// <exception cref="ArgumentException">The spec breaks a rule (the message lists them).</exception>
        public WindowModel(string modId, CwyWindowSpec spec, UiEventRunner run)
        {
            if (string.IsNullOrEmpty(modId))
            {
                throw new ArgumentException("a mod id is required", nameof(modId));
            }

            if (spec is null)
            {
                throw new ArgumentNullException(nameof(spec));
            }

            if (run is null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            var problems = CwyWindowSpecRules.Validate(spec);
            if (problems.Count > 0)
            {
                throw new ArgumentException("the window spec is invalid: " + string.Join("; ", problems), nameof(spec));
            }

            ModId = modId;
            Id = spec.Id;
            Key = CwyWindowSpecRules.Key(modId, spec.Id);
            RootName = CwyWindowSpecRules.RootName(modId, spec.Id);
            Title = spec.Title ?? string.Empty;
            Width = spec.Width;
            Height = spec.Height;
            MinWidth = CwyWindowSpecRules.MinimumWidth(spec);
            MinHeight = CwyWindowSpecRules.MinimumHeight(spec);
            Resizable = spec.Resizable;
            RememberRect = spec.RememberRect;
            FollowHideUi = spec.FollowHideUi;
            CloseOnEscape = spec.CloseOnEscape;
            HideOnClose = spec.HideOnClose;

            foreach (var part in spec.Parts)
            {
                var model = Create(part, run);
                _parts.Add(model);
                if (model is TabStripModel strip)
                {
                    TabStrip = strip;
                }
            }

            _fixedHeights = new float[_parts.Count];
            _visible = new bool[_parts.Count];
            for (var i = 0; i < _parts.Count; i++)
            {
                _fixedHeights[i] = _parts[i].FixedHeight;
            }
        }

        /// <summary>The owning mod's id.</summary>
        public string ModId { get; }

        /// <summary>The window's id within the mod.</summary>
        public string Id { get; }

        /// <summary><c>&lt;modId&gt;/&lt;windowId&gt;</c>.</summary>
        public string Key { get; }

        /// <summary><c>CWY.Mod.&lt;modId&gt;.&lt;windowId&gt;</c>.</summary>
        public string RootName { get; }

        /// <summary>The first title.</summary>
        public string Title { get; }

        /// <summary>Preferred width, or 0.</summary>
        public float Width { get; }

        /// <summary>Preferred height, or 0.</summary>
        public float Height { get; }

        /// <summary>Smallest width.</summary>
        public float MinWidth { get; }

        /// <summary>Smallest height (fits the parts of the busiest tab).</summary>
        public float MinHeight { get; }

        /// <summary>Has a resize grip.</summary>
        public bool Resizable { get; }

        /// <summary>Its place is remembered.</summary>
        public bool RememberRect { get; }

        /// <summary>Follows the game's Hide UI.</summary>
        public bool FollowHideUi { get; }

        /// <summary>Escape closes it.</summary>
        public bool CloseOnEscape { get; }

        /// <summary>Closing hides it.</summary>
        public bool HideOnClose { get; }

        /// <summary>The parts in spec order.</summary>
        public IReadOnlyList<PartModel> Parts => _parts;

        /// <summary>The tab strip, or null.</summary>
        public TabStripModel? TabStrip { get; }

        /// <summary>True when a part is shown now: visible, and on the selected tab (or on every tab).</summary>
        /// <param name="part">One of <see cref="Parts"/>.</param>
        public bool IsShown(PartModel part)
        {
            if (part is null)
            {
                throw new ArgumentNullException(nameof(part));
            }

            if (!part.Visible)
            {
                return false;
            }

            return part.Tab < 0 || TabStrip is null || part.Tab == TabStrip.Selected;
        }

        /// <summary>Lays the shown parts out in a body of this height.</summary>
        /// <param name="bodyHeight">The body's height.</param>
        /// <returns>One slot per part, in order.</returns>
        public StackSlot[] Layout(float bodyHeight)
        {
            for (var i = 0; i < _parts.Count; i++)
            {
                _visible[i] = IsShown(_parts[i]);
            }

            return StackLayout.Plan(bodyHeight, StackLayout.Gap, _fixedHeights, _visible);
        }

        /// <summary>The text part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public TextViewModel Text(string partId) => Find<TextViewModel>(partId, CwyPartKind.Text);

        /// <summary>The list part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public ListViewModel List(string partId) => Find<ListViewModel>(partId, CwyPartKind.List);

        /// <summary>The chat part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public ChatViewModel Chat(string partId) => Find<ChatViewModel>(partId, CwyPartKind.Chat);

        /// <summary>The input part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public InputBoxModel Input(string partId) => Find<InputBoxModel>(partId, CwyPartKind.Input);

        /// <summary>The button row with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public ButtonRowModel Buttons(string partId) => Find<ButtonRowModel>(partId, CwyPartKind.Buttons);

        /// <summary>The tab strip with this id.</summary>
        /// <param name="partId">The part's id.</param>
        public TabStripModel Tabs(string partId) => Find<TabStripModel>(partId, CwyPartKind.Tabs);

        private T Find<T>(string partId, CwyPartKind kind)
            where T : PartModel
        {
            foreach (var part in _parts)
            {
                if (part is T typed && string.Equals(part.Id, partId, StringComparison.OrdinalIgnoreCase))
                {
                    return typed;
                }
            }

            var have = new StringBuilder();
            foreach (var part in _parts)
            {
                if (have.Length > 0)
                {
                    have.Append(", ");
                }

                have.Append(part.Id).Append(':').Append(part.Kind.ToString().ToLowerInvariant());
            }

            throw new ArgumentException(
                "window '" + Key + "' has no " + kind.ToString().ToLowerInvariant() + " part '" + partId + "' (parts: "
                + (have.Length == 0 ? "none" : have.ToString()) + ")",
                nameof(partId));
        }

        private static PartModel Create(CwyPartSpec part, UiEventRunner run)
        {
            switch (part)
            {
                case CwyTextSpec text:
                    return new TextViewModel(text, run);
                case CwyListSpec list:
                    return new ListViewModel(list, run);
                case CwyChatSpec chat:
                    return new ChatViewModel(chat, run);
                case CwyInputSpec input:
                    return new InputBoxModel(input, run);
                case CwyButtonsSpec buttons:
                    return new ButtonRowModel(buttons, run);
                case CwyTabsSpec tabs:
                    return new TabStripModel(tabs, run);
                default:
                    throw new ArgumentException("part '" + part.Id + "' is of an unknown spec type " + part.GetType().Name, nameof(part));
            }
        }
    }
}
