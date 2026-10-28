using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;

namespace CwyLs.ModLoader.Core.Ui
{
    /// <summary>
    /// Runs a mod's handler on the main thread with the mod's attribution: a handler that throws is
    /// logged against the mod, never thrown into the UI. Returns false when the handler did not run
    /// or threw.
    /// </summary>
    /// <param name="label">What the handler is, for the log (e.g. <c>chat button</c>).</param>
    /// <param name="handler">The handler.</param>
    public delegate bool UiEventRunner(string label, Action handler);

    /// <summary>Who asked a window to close.</summary>
    public enum CwyCloseReason
    {
        /// <summary>The round close button in the title bar.</summary>
        CloseButton,

        /// <summary>Escape while the player was in the window (<see cref="CwyWindowSpec.CloseOnEscape"/>).</summary>
        Escape,
    }

    /// <summary>Whose bubble it is.</summary>
    public enum CwyChatRole
    {
        /// <summary>The character (Satone): left, the game's dark panel.</summary>
        Character,

        /// <summary>The player: right, tinted.</summary>
        Player,

        /// <summary>A note from the mod (a time stamp, an error): centred small grey text, no bubble.</summary>
        System,
    }

    /// <summary>The outcome of <c>helper.Ui.CreateWindow</c>: the window, or why there is none.</summary>
    public sealed class CwyWindowResult
    {
        private CwyWindowResult(ICwyWindow? window, string problem)
        {
            Window = window;
            Problem = problem;
        }

        /// <summary>The window (hidden until <see cref="ICwyWindow.Show"/>), or null when refused.</summary>
        public ICwyWindow? Window { get; }

        /// <summary>Why no window was built; empty on success.</summary>
        public string Problem { get; }

        /// <summary>True when <see cref="Window"/> is set.</summary>
        public bool Ok => Window != null;

        /// <summary>A built window.</summary>
        /// <param name="window">The window.</param>
        public static CwyWindowResult Success(ICwyWindow window) =>
            new CwyWindowResult(window ?? throw new ArgumentNullException(nameof(window)), string.Empty);

        /// <summary>A refusal.</summary>
        /// <param name="problem">Why, one sentence.</param>
        public static CwyWindowResult Refused(string problem) =>
            new CwyWindowResult(null, string.IsNullOrWhiteSpace(problem) ? "refused" : problem);

        /// <summary><c>ok</c> or <c>refused: ...</c>.</summary>
        public override string ToString() => Ok ? "ok" : "refused: " + Problem;
    }

    /// <summary>
    /// A mod window in the game's own style. The window's own members are main-thread only
    /// (a call from another thread throws <see cref="InvalidOperationException"/>); its parts are
    /// safe from any thread and the view catches up at the next frame, so a reply can be streamed
    /// into a chat bubble straight from a network thread. Destroyed when the mod stops.
    /// </summary>
    public interface ICwyWindow
    {
        /// <summary>The window's id within the mod.</summary>
        string Id { get; }

        /// <summary>The owning mod's id.</summary>
        string ModId { get; }

        /// <summary>The window's object name, <c>CWY.Mod.&lt;modId&gt;.&lt;id&gt;</c>.</summary>
        string ObjectName { get; }

        /// <summary>The title.</summary>
        string Title { get; set; }

        /// <summary>Small text right-aligned in the title bar.</summary>
        string Status { get; set; }

        /// <summary>True from <see cref="Show"/> until it is hidden or closed.</summary>
        bool IsShown { get; }

        /// <summary>False once destroyed.</summary>
        bool IsAlive { get; }

        /// <summary>True when shown and not faded out with the game's hidden UI: it takes clicks and keys.</summary>
        bool IsInteractable { get; }

        /// <summary>True while one of the window's input boxes has the keyboard.</summary>
        bool HasKeyboard { get; }

        /// <summary>Where the window is, in canvas units from the top-left corner (1920x1080 reference).</summary>
        WindowRect Rect { get; }

        /// <summary>The parts, in spec order.</summary>
        IReadOnlyList<ICwyPart> Parts { get; }

        /// <summary>
        /// Raised when the close button is clicked or Escape closes the window. With
        /// <see cref="CwyWindowSpec.HideOnClose"/> the window is already hidden.
        /// </summary>
        event Action<CwyCloseReason> CloseRequested;

        /// <summary>Shows the window with the game's fade, on top of the other windows.</summary>
        void Show();

        /// <summary>Hides the window; its input boxes give the keyboard back.</summary>
        void Hide();

        /// <summary>Destroys the window. Its place stays remembered.</summary>
        void Destroy();

        /// <summary>Puts the window above the other CWY windows.</summary>
        void BringToFront();

        /// <summary>Forgets the remembered place; the window moves to its default place.</summary>
        void ResetRect();

        /// <summary>The text part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no text part with that id.</exception>
        ICwyTextView Text(string partId);

        /// <summary>The list part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no list part with that id.</exception>
        ICwyListView List(string partId);

        /// <summary>The chat part with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no chat part with that id.</exception>
        ICwyChatView Chat(string partId);

        /// <summary>The input box with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no input part with that id.</exception>
        ICwyInputBox Input(string partId);

        /// <summary>The button row with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no button row with that id.</exception>
        ICwyButtonRow Buttons(string partId);

        /// <summary>The tab strip with this id.</summary>
        /// <param name="partId">The part's id.</param>
        /// <exception cref="ArgumentException">The window has no tab strip with that id.</exception>
        ICwyTabStrip Tabs(string partId);
    }

    /// <summary>What every part has.</summary>
    public interface ICwyPart
    {
        /// <summary>The part's id within the window.</summary>
        string Id { get; }

        /// <summary>What the part is.</summary>
        CwyPartKind Kind { get; }

        /// <summary>
        /// False hides the part and gives its room to the others (on top of the tab rule). Safe from
        /// any thread.
        /// </summary>
        bool Visible { get; set; }
    }

    /// <summary>
    /// A scrolling view of TextMeshPro rich-text lines that follows the newest line unless the
    /// player scrolled up. Escape text you did not write with <see cref="CwyRichText.Escape"/>.
    /// Safe from any thread.
    /// </summary>
    public interface ICwyTextView : ICwyPart
    {
        /// <summary>Lines held now.</summary>
        int Count { get; }

        /// <summary>True while the view follows the newest line (as of the last frame).</summary>
        bool IsFollowing { get; }

        /// <summary>Replaces every line.</summary>
        /// <param name="lines">The lines; null clears.</param>
        void SetLines(IReadOnlyList<string>? lines);

        /// <summary>Adds a line at the bottom; the oldest is dropped past the part's <c>MaxLines</c>.</summary>
        /// <param name="line">The line, without a line break.</param>
        void AppendLine(string line);

        /// <summary>Removes every line.</summary>
        void Clear();

        /// <summary>Jumps to the newest line and follows it again.</summary>
        void ScrollToEnd();

        /// <summary>Jumps to the first line and stays there.</summary>
        void ScrollToStart();
    }

    /// <summary>A scrolling list of one-line plain-text items the player can click. Safe from any thread.</summary>
    public interface ICwyListView : ICwyPart
    {
        /// <summary>Items held now.</summary>
        int Count { get; }

        /// <summary>A copy of the items.</summary>
        IReadOnlyList<string> Items { get; }

        /// <summary>The highlighted item, or -1. A click selects; setting it highlights without raising <see cref="ItemClicked"/>.</summary>
        int Selected { get; set; }

        /// <summary>Raised on the main thread with the index of the item the player clicked.</summary>
        event Action<int> ItemClicked;

        /// <summary>Replaces the items; the selection is cleared.</summary>
        /// <param name="items">The items; null clears.</param>
        void SetItems(IReadOnlyList<string>? items);

        /// <summary>Scrolls so the item is in view.</summary>
        /// <param name="index">The item.</param>
        void ScrollTo(int index);
    }

    /// <summary>
    /// Chat bubbles. Adding, streaming text into a bubble, the typing indicator and bubble buttons
    /// are safe from any thread; the view catches up at the next frame. The view stays on the
    /// newest bubble unless the player scrolled up; adding a player bubble always jumps down.
    /// </summary>
    public interface ICwyChatView : ICwyPart
    {
        /// <summary>Bubbles held now.</summary>
        int Count { get; }

        /// <summary>The bubbles, oldest first (a copy).</summary>
        IReadOnlyList<ICwyChatBubble> Bubbles { get; }

        /// <summary>True while the view follows the newest bubble (as of the last frame).</summary>
        bool IsFollowing { get; }

        /// <summary>Raised on the main thread when a bubble's button is clicked: the bubble and the button id.</summary>
        event Action<ICwyChatBubble, string> ButtonClicked;

        /// <summary>Adds a bubble at the bottom; the oldest is dropped past the part's <c>MaxBubbles</c>.</summary>
        /// <param name="role">Whose bubble it is.</param>
        /// <param name="text">Its first text (plain text, shown as typed); null or empty for a bubble you will stream into.</param>
        /// <returns>The bubble.</returns>
        ICwyChatBubble Add(CwyChatRole role, string? text = null);

        /// <summary>Removes every bubble.</summary>
        void Clear();

        /// <summary>Jumps to the newest bubble and follows again.</summary>
        void ScrollToEnd();
    }

    /// <summary>One chat bubble. Every member is safe from any thread.</summary>
    public interface ICwyChatBubble
    {
        /// <summary>A number unique within the chat part, increasing.</summary>
        long Id { get; }

        /// <summary>Whose bubble it is.</summary>
        CwyChatRole Role { get; }

        /// <summary>The text so far (plain text).</summary>
        string Text { get; }

        /// <summary>
        /// True shows the animated typing dots after the text (alone while the text is empty).
        /// Set it before streaming and clear it when the reply is complete.
        /// </summary>
        bool Typing { get; set; }

        /// <summary>False once the bubble was dropped or cleared; changes then do nothing.</summary>
        bool IsAlive { get; }

        /// <summary>True when the text reached the bubble's limit and later text was dropped.</summary>
        bool IsTruncated { get; }

        /// <summary>Anything the mod wants to keep with the bubble (a voice clip id, a message id).</summary>
        object? Tag { get; set; }

        /// <summary>The bubble's buttons, left to right (a copy).</summary>
        IReadOnlyList<CwyButtonSpec> Buttons { get; }

        /// <summary>Adds text at the end: the streaming call, one token at a time.</summary>
        /// <param name="text">The text to add.</param>
        void Append(string text);

        /// <summary>Replaces the text.</summary>
        /// <param name="text">The new text; null is empty.</param>
        void SetText(string? text);

        /// <summary>Adds a small button under the text, or relabels the button with that id.</summary>
        /// <param name="id">What a click reports (same rule as a part id).</param>
        /// <param name="label">The button text, e.g. <c>replay</c>.</param>
        /// <exception cref="ArgumentException">The id breaks the id rule.</exception>
        void AddButton(string id, string label);

        /// <summary>Removes a button.</summary>
        /// <param name="id">The button's id.</param>
        /// <returns>True when there was one.</returns>
        bool RemoveButton(string id);
    }

    /// <summary>
    /// A text box. Safe from any thread: changes, <see cref="Focus"/> and <see cref="Release"/> are
    /// applied at the next frame.
    /// </summary>
    public interface ICwyInputBox : ICwyPart
    {
        /// <summary>The text; setting it puts the caret at the end.</summary>
        string Text { get; set; }

        /// <summary>Grey text shown while the box is empty.</summary>
        string Placeholder { get; set; }

        /// <summary>False greys the box out and takes no typing (e.g. while a reply streams).</summary>
        bool Enabled { get; set; }

        /// <summary>True while the box has the keyboard (as of the last frame).</summary>
        bool IsFocused { get; }

        /// <summary>True while the operating system's input method holds an unfinished composition.</summary>
        bool IsComposing { get; }

        /// <summary>Raised on the main thread with the text when the player sends it (Enter, see <see cref="CwyInputSpec.SubmitOnEnter"/>).</summary>
        event Action<string> Submitted;

        /// <summary>Gives the box the keyboard at the next frame.</summary>
        void Focus();

        /// <summary>Takes the keyboard away from the box at the next frame, so keys are the game's again.</summary>
        void Release();
    }

    /// <summary>A row of buttons. Safe from any thread.</summary>
    public interface ICwyButtonRow : ICwyPart
    {
        /// <summary>The buttons, left to right, with their current labels (a copy).</summary>
        IReadOnlyList<CwyButtonSpec> Buttons { get; }

        /// <summary>Raised on the main thread with the id of the clicked button.</summary>
        event Action<string> Clicked;

        /// <summary>Changes a button's label.</summary>
        /// <param name="id">The button's id.</param>
        /// <param name="label">The new label.</param>
        /// <returns>False when the row has no button with that id.</returns>
        bool SetLabel(string id, string label);

        /// <summary>Shows or hides one button; the others close the gap.</summary>
        /// <param name="id">The button's id.</param>
        /// <param name="visible">True to show it.</param>
        /// <returns>False when the row has no button with that id.</returns>
        bool SetButtonVisible(string id, bool visible);
    }

    /// <summary>A tab strip. Safe from any thread.</summary>
    public interface ICwyTabStrip : ICwyPart
    {
        /// <summary>The tab labels.</summary>
        IReadOnlyList<string> Labels { get; }

        /// <summary>The selected tab; setting it switches the parts shown without raising <see cref="SelectedChanged"/>.</summary>
        int Selected { get; set; }

        /// <summary>Raised on the main thread with the tab the player picked.</summary>
        event Action<int> SelectedChanged;
    }
}
