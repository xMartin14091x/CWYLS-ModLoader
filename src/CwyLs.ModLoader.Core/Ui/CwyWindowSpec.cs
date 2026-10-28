using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>The kinds of part a mod window is built from.</summary>
    public enum CwyPartKind
    {
        /// <summary>A scrolling view of rich-text lines (<see cref="ICwyTextView"/>).</summary>
        Text,

        /// <summary>A scrolling list of clickable one-line items (<see cref="ICwyListView"/>).</summary>
        List,

        /// <summary>Chat bubbles that can stream text, show a typing indicator and carry buttons (<see cref="ICwyChatView"/>).</summary>
        Chat,

        /// <summary>A text box with input-method support, one or several lines (<see cref="ICwyInputBox"/>).</summary>
        Input,

        /// <summary>A row of game-style buttons (<see cref="ICwyButtonRow"/>).</summary>
        Buttons,

        /// <summary>A tab strip that switches which parts are shown (<see cref="ICwyTabStrip"/>).</summary>
        Tabs,
    }

    /// <summary>
    /// What a mod window is built with: its id, title, size rules and parts, stacked top to bottom
    /// in the order of <see cref="Parts"/>. Hand it to <c>helper.Ui.CreateWindow</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// var spec = new CwyWindowSpec("chat")
    /// {
    ///     Title = "Satone",
    ///     Width = 640f,
    ///     Height = 720f,
    ///     CloseOnEscape = false,               // Esc only takes the keyboard away
    ///     Parts =
    ///     {
    ///         new CwyChatSpec("log"),
    ///         new CwyInputSpec("say") { Placeholder = "Type here", Lines = 3 },
    ///         new CwyButtonsSpec("actions") { Buttons = { new CwyButtonSpec("send", "Send") } },
    ///     },
    /// };
    /// </code>
    /// </example>
    public sealed class CwyWindowSpec
    {
        /// <summary>Default smallest width, in canvas units (1920x1080 reference).</summary>
        public const float DefaultMinWidth = 420f;

        /// <summary>Default smallest height, in canvas units.</summary>
        public const float DefaultMinHeight = 280f;

        /// <summary>Creates a spec.</summary>
        /// <param name="id">
        /// The window's id within the mod, e.g. <c>chat</c>: 1-48 letters, digits, <c>.</c>, <c>_</c>
        /// or <c>-</c>, not starting with a dot. It names the window's object
        /// (<c>CWY.Mod.&lt;modId&gt;.&lt;id&gt;</c>) and the key its remembered place is stored under.
        /// </param>
        public CwyWindowSpec(string id)
        {
            Id = id ?? string.Empty;
        }

        /// <summary>The window's id within the mod.</summary>
        public string Id { get; }

        /// <summary>The title, top-left in the game's title style.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Preferred width in canvas units on first open; 0 uses the default share of the screen.</summary>
        public float Width { get; set; }

        /// <summary>Preferred height in canvas units on first open; 0 uses the default share of the screen.</summary>
        public float Height { get; set; }

        /// <summary>Smallest width the player can resize to. Raised automatically when the parts need more.</summary>
        public float MinWidth { get; set; } = DefaultMinWidth;

        /// <summary>Smallest height the player can resize to. Raised automatically when the parts need more.</summary>
        public float MinHeight { get; set; } = DefaultMinHeight;

        /// <summary>True for a resize grip in the bottom-right corner.</summary>
        public bool Resizable { get; set; } = true;

        /// <summary>True to reopen the window where the player last left it (also across game restarts).</summary>
        public bool RememberRect { get; set; } = true;

        /// <summary>True to fade out with the game's Hide UI button and come back with it.</summary>
        public bool FollowHideUi { get; set; } = true;

        /// <summary>
        /// What Escape does while the player is in this window. True: the window closes (as its
        /// close button does). False: Escape only takes the keyboard away from the window's text box,
        /// and an Escape with no text box selected is the game's again.
        /// </summary>
        public bool CloseOnEscape { get; set; } = true;

        /// <summary>
        /// True: the close button and Escape hide the window, then raise
        /// <see cref="ICwyWindow.CloseRequested"/>. False: they only raise it and the mod decides.
        /// </summary>
        public bool HideOnClose { get; set; } = true;

        /// <summary>The parts, top to bottom.</summary>
        public IList<CwyPartSpec> Parts { get; } = new List<CwyPartSpec>();
    }

    /// <summary>One part of a mod window. Use one of the derived specs.</summary>
    public abstract class CwyPartSpec
    {
        /// <summary>Creates a part spec.</summary>
        /// <param name="id">The part's id within the window (same rule as a window id).</param>
        /// <param name="kind">What the part is.</param>
        private protected CwyPartSpec(string id, CwyPartKind kind)
        {
            Id = id ?? string.Empty;
            Kind = kind;
        }

        /// <summary>The part's id within the window.</summary>
        public string Id { get; }

        /// <summary>What the part is.</summary>
        public CwyPartKind Kind { get; }

        /// <summary>
        /// Fixed height in canvas units. 0 means the part's default: text, list and chat parts share
        /// the room the fixed parts leave; inputs, buttons and tabs have their own fixed height.
        /// </summary>
        public float Height { get; set; }

        /// <summary>The tab (index into the window's <see cref="CwyTabsSpec"/>) this part shows on; -1 for every tab.</summary>
        public int Tab { get; set; } = -1;
    }

    /// <summary>A scrolling view of rich-text lines, following the newest line unless scrolled up.</summary>
    public sealed class CwyTextSpec : CwyPartSpec
    {
        /// <summary>Creates a text part.</summary>
        /// <param name="id">The part's id.</param>
        public CwyTextSpec(string id)
            : base(id, CwyPartKind.Text)
        {
        }

        /// <summary>Lines kept; the oldest are dropped beyond this (1-5000).</summary>
        public int MaxLines { get; set; } = 500;
    }

    /// <summary>A scrolling list of clickable one-line plain-text items.</summary>
    public sealed class CwyListSpec : CwyPartSpec
    {
        /// <summary>Creates a list part.</summary>
        /// <param name="id">The part's id.</param>
        public CwyListSpec(string id)
            : base(id, CwyPartKind.List)
        {
        }
    }

    /// <summary>Chat bubbles: the character's on the left, the player's on the right, system notes centred.</summary>
    public sealed class CwyChatSpec : CwyPartSpec
    {
        /// <summary>Creates a chat part.</summary>
        /// <param name="id">The part's id.</param>
        public CwyChatSpec(string id)
            : base(id, CwyPartKind.Chat)
        {
        }

        /// <summary>Bubbles kept; the oldest are dropped beyond this (1-1000).</summary>
        public int MaxBubbles { get; set; } = 200;
    }

    /// <summary>
    /// A text box in the game's style. Typing goes through the operating system's input method
    /// (Thai, Japanese composition), and no hotkey fires while it has the keyboard.
    /// </summary>
    public sealed class CwyInputSpec : CwyPartSpec
    {
        /// <summary>Creates an input part.</summary>
        /// <param name="id">The part's id.</param>
        public CwyInputSpec(string id)
            : base(id, CwyPartKind.Input)
        {
        }

        /// <summary>Grey text shown while the box is empty.</summary>
        public string Placeholder { get; set; } = string.Empty;

        /// <summary>Visible lines (1-12). 1 is a one-line box; more lines wrap and scroll inside the box.</summary>
        public int Lines { get; set; } = 1;

        /// <summary>Most characters the box accepts; 0 for no limit (up to 20000).</summary>
        public int MaxLength { get; set; }

        /// <summary>
        /// True (default): Enter sends and Shift+Enter starts a new line. False: Enter starts a new
        /// line and Ctrl+Enter (Command+Enter on a Mac) sends. A one-line box always sends on Enter.
        /// </summary>
        public bool SubmitOnEnter { get; set; } = true;

        /// <summary>True to empty the box after it sent its text.</summary>
        public bool ClearOnSubmit { get; set; } = true;
    }

    /// <summary>A row of game-style buttons, left to right.</summary>
    public sealed class CwyButtonsSpec : CwyPartSpec
    {
        /// <summary>Creates a button row.</summary>
        /// <param name="id">The part's id.</param>
        public CwyButtonsSpec(string id)
            : base(id, CwyPartKind.Buttons)
        {
        }

        /// <summary>The buttons, left to right (at least one).</summary>
        public IList<CwyButtonSpec> Buttons { get; } = new List<CwyButtonSpec>();
    }

    /// <summary>One button: a stable id the click reports, and the label shown.</summary>
    public sealed class CwyButtonSpec
    {
        /// <summary>Creates a button.</summary>
        /// <param name="id">What a click reports (same rule as a part id).</param>
        /// <param name="label">The text on the button.</param>
        public CwyButtonSpec(string id, string label)
        {
            Id = id ?? string.Empty;
            Label = label ?? string.Empty;
        }

        /// <summary>What a click reports.</summary>
        public string Id { get; }

        /// <summary>The text on the button.</summary>
        public string Label { get; }

        /// <summary><c>id "label"</c>.</summary>
        public override string ToString() => Id + " \"" + Label + "\"";
    }

    /// <summary>
    /// A tab strip in the style of the game's settings window. Parts whose <see cref="CwyPartSpec.Tab"/>
    /// is an index show only while that tab is selected. At most one per window.
    /// </summary>
    public sealed class CwyTabsSpec : CwyPartSpec
    {
        /// <summary>Creates a tab strip.</summary>
        /// <param name="id">The part's id.</param>
        public CwyTabsSpec(string id)
            : base(id, CwyPartKind.Tabs)
        {
        }

        /// <summary>The tab labels, left to right (1-8).</summary>
        public IList<string> Labels { get; } = new List<string>();

        /// <summary>The tab selected when the window is built.</summary>
        public int Selected { get; set; }
    }
}
