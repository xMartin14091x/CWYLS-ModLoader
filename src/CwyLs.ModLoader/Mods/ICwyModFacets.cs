using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Satone;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Core.Ui;
using CwyLs.ModLoader.Loaders;
using UnityEngine;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>A mod's log. Lines carry the mod id; only the framework implements this interface.</summary>
    public interface ICwyModLog
    {
        /// <summary>Detail for the trace file only; never on the console.</summary>
        /// <param name="message">One line.</param>
        void Trace(string message);

        /// <summary>A normal console line: <c>MOD  &lt;id&gt;: message</c>.</summary>
        /// <param name="message">One line.</param>
        void Info(string message);

        /// <summary>A warning line.</summary>
        /// <param name="message">One line.</param>
        void Warn(string message);

        /// <summary>An error line; the exception's stack goes to the trace file.</summary>
        /// <param name="message">One line.</param>
        /// <param name="error">The exception, or null.</param>
        void Error(string message, Exception? error = null);
    }

    /// <summary>
    /// A mod's own persistent files in <c>BepInEx/data/mods/&lt;id&gt;/</c>. A key is a file name:
    /// letters, digits, <c>_ . -</c>, 1 to 64 characters, not starting with a dot, not a Windows
    /// device name (<c>con</c>, <c>nul.txt</c>, <c>com1</c> ...) and not ending in a dot. Keys are
    /// case-insensitive: <c>Save.json</c> and <c>save.json</c> are one key, stored in lower case,
    /// on every operating system. A write goes to a temporary file, is flushed to disk, then
    /// replaces the real file, so a crash never leaves half a file under the key. Thread-safe.
    /// </summary>
    public interface ICwyModData
    {
        /// <summary>Absolute path of the folder (created on the first write).</summary>
        string DirectoryPath { get; }

        /// <summary>True when the key has a file.</summary>
        /// <param name="key">A data key.</param>
        /// <exception cref="ArgumentException">The key breaks the naming rule.</exception>
        bool Exists(string key);

        /// <summary>The file's text (UTF-8), or null when it does not exist.</summary>
        /// <param name="key">A data key.</param>
        /// <exception cref="ArgumentException">The key breaks the naming rule.</exception>
        string? ReadText(string key);

        /// <summary>Writes the file atomically (UTF-8, no BOM).</summary>
        /// <param name="key">A data key.</param>
        /// <param name="text">The content; null writes an empty file.</param>
        /// <exception cref="ArgumentException">The key breaks the naming rule.</exception>
        void WriteText(string key, string? text);

        /// <summary>
        /// The file parsed as strict JSON: <c>JsonObject</c>, lists, strings, <c>long</c>/<c>double</c>,
        /// booleans or null; null when the file does not exist.
        /// </summary>
        /// <param name="key">A data key.</param>
        /// <exception cref="ArgumentException">The key breaks the naming rule.</exception>
        /// <exception cref="Core.Json.JsonParseException">The file is not valid JSON (the message has line:column).</exception>
        object? ReadJson(string key);

        /// <summary>Writes <paramref name="value"/> as JSON (public properties and fields, depth 12), atomically.</summary>
        /// <param name="key">A data key.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="ArgumentException">The key breaks the naming rule.</exception>
        void WriteJson(string key, object? value);

        /// <summary>Deletes the file.</summary>
        /// <param name="key">A data key.</param>
        /// <returns>True when a file was deleted.</returns>
        bool Delete(string key);

        /// <summary>Every key that has a file, in lower case, sorted.</summary>
        IReadOnlyList<string> Keys();
    }

    /// <summary>
    /// Game events and frame ticks. Handlers run on the main thread; a handler that throws is
    /// logged against the mod (rate-limited) and keeps its subscription. Everything is removed
    /// when the mod stops; dispose a handle to stop earlier.
    /// </summary>
    public interface ICwyModEvents
    {
        /// <summary>True when the game adapter supplies game events.</summary>
        bool IsAvailable { get; }

        /// <summary>Subscribes to one typed game event, e.g. <c>RoomReady</c> or <c>LineSpoken</c>.</summary>
        /// <typeparam name="T">One of the event records in <c>CwyLs.ModLoader.Core.Events</c>.</typeparam>
        /// <param name="handler">Your callback.</param>
        /// <returns>Dispose to unsubscribe. A no-op handle when events are unavailable (logged once).</returns>
        IDisposable Subscribe<T>(Action<T> handler)
            where T : ICwyEvent;

        /// <summary>Subscribes to every game event as a name and a payload.</summary>
        /// <param name="handler">Your callback.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable SubscribeAny(Action<AnyEvent> handler);

        /// <summary>A callback once a frame, after the frame's queued main-thread work.</summary>
        /// <param name="handler">Your callback. Keep it cheap: it runs 60 times a second.</param>
        /// <returns>Dispose to stop.</returns>
        IDisposable EveryFrame(Action handler);

        /// <summary>
        /// A callback once every mod has entered and its API is published: the first moment
        /// <c>ModRegistry.GetApi</c> works. Registered after that moment, it runs on the next frame.
        /// </summary>
        /// <param name="handler">Your callback.</param>
        /// <returns>Dispose to cancel before it runs.</returns>
        IDisposable OnModsReady(Action handler);
    }

    /// <summary>
    /// Satone, through the framework's Satone API. Safe from any
    /// thread: off the main thread a call is queued for the next frame.
    /// </summary>
    public interface ICwyModSatone
    {
        /// <summary>True when the game adapter supplies Satone.</summary>
        bool IsAvailable { get; }

        /// <summary>Changes her expression by name, alias or id (e.g. <c>smile</c>).</summary>
        /// <param name="nameOrId">Expression name, alias or id.</param>
        /// <returns>True when the request was accepted.</returns>
        bool Face(string nameOrId);

        /// <summary>Returns her expression to neutral.</summary>
        bool FaceReset();

        /// <summary>Plays a body motion by name, alias or id.</summary>
        /// <param name="nameOrId">Motion name, alias or id.</param>
        /// <param name="trigger">Fire the Animator trigger instead of setting the motion value.</param>
        bool Motion(string nameOrId, bool trigger = false);

        /// <summary>Turns her gaze towards the player.</summary>
        /// <param name="scale">0 looks away, 1 looks straight at the player.</param>
        /// <param name="seconds">How long the turn takes.</param>
        bool LookAt(float scale, float seconds);

        /// <summary>Hands the gaze back to the game.</summary>
        bool LookReset();

        /// <summary>Hands her back to the game (expression, motion, gaze), the game's own way.</summary>
        /// <param name="scope">Which channels to restore.</param>
        RestoreResult RestoreDefaults(RestoreScope scope = RestoreScope.All);

        /// <summary>Makes her say a new line of text (text box, no voice).</summary>
        /// <param name="text">What she says.</param>
        /// <returns>A handle; await <c>Completion</c> for the outcome. A refused line comes back completed as Rejected.</returns>
        SayHandle Say(string text);

        /// <summary>Makes her say a line described by a request: translations, voice name, expression, motion, gaze.</summary>
        /// <param name="request">The line.</param>
        /// <returns>A handle, as for <see cref="Say(string)"/>.</returns>
        SayHandle Say(SayRequest request);

        /// <summary>Makes her say a line with a clip you made (e.g. synthesized speech), registered under the request's voice name.</summary>
        /// <param name="request">The line.</param>
        /// <param name="clip">The clip to speak.</param>
        /// <returns>A handle, as for <see cref="Say(string)"/>.</returns>
        SayHandle SayWithClip(SayRequest request, AudioClip clip);

        /// <summary>True while one of the framework's lines is being spoken.</summary>
        bool IsSaying { get; }

        /// <summary>True when she can be interrupted now (the game's own <c>IsPossibleTalk</c>).</summary>
        bool CanInteract { get; }

        /// <summary>Her current AI state name, e.g. <c>work_pc</c>; <c>unknown</c> when the room is not loaded.</summary>
        string State { get; }
    }

    /// <summary>The player's data, read-only.</summary>
    public interface ICwyModGameData
    {
        /// <summary>True when the data plugin supplies the bus.</summary>
        bool IsAvailable { get; }

        /// <summary>True once the game has loaded the player's save; before that, values are defaults.</summary>
        bool IsReady { get; }

        /// <summary>
        /// A complete immutable snapshot (todos, notes, habits, pomodoro, music, ...). Main thread
        /// only; from elsewhere use <c>helper.MainThread.InvokeAsync(() =&gt; helper.GameData.Snapshot())</c>.
        /// </summary>
        /// <returns>The snapshot, or null when the data bus is unavailable.</returns>
        /// <exception cref="InvalidOperationException">Called off the main thread.</exception>
        GameDataSnapshot? Snapshot();

        /// <summary>Called after a section of the player's data changed. Keep it short: it runs inside the game's save.</summary>
        /// <param name="handler">Your callback.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable OnChanged(Action<GameDataChanged> handler);
    }

    /// <summary>The display language.</summary>
    public interface ICwyModLanguage
    {
        /// <summary>True when the game adapter supplies the language state.</summary>
        bool IsAvailable { get; }

        /// <summary>The code being displayed: a pack code such as <c>th</c>, or a game one (<c>en</c>, <c>ja</c>, ...); empty before the save loads.</summary>
        string CurrentCode { get; }

        /// <summary>True when a pack-declared language (not one of the game's own) is being displayed.</summary>
        bool IsPackLanguage { get; }
    }

    /// <summary>Files inside this mod's own folder. Paths are relative with forward slashes and cannot leave the folder.</summary>
    public interface ICwyModContent
    {
        /// <summary>Absolute path of the mod folder.</summary>
        string DirectoryPath { get; }

        /// <summary>The absolute path of a file in the mod folder.</summary>
        /// <param name="relativePath">E.g. <c>assets/icon.png</c>.</param>
        /// <exception cref="ArgumentException">The path is blank or leaves the folder.</exception>
        string GetPath(string relativePath);

        /// <summary>True when the file exists.</summary>
        /// <param name="relativePath">A relative path.</param>
        bool Exists(string relativePath);

        /// <summary>The file's text (UTF-8), or null when it does not exist.</summary>
        /// <param name="relativePath">A relative path.</param>
        string? ReadText(string relativePath);

        /// <summary>The file parsed as strict JSON, or null when it does not exist.</summary>
        /// <param name="relativePath">A relative path.</param>
        /// <exception cref="Core.Json.JsonParseException">The file is not valid JSON.</exception>
        object? ReadJson(string relativePath);

        /// <summary>A PNG as a texture (cached by path and time), or null when it could not load (logged). Main thread only.</summary>
        /// <param name="relativePath">A relative path.</param>
        /// <param name="options">Colour space, mipmaps, readability; null for the defaults.</param>
        Texture2D? LoadTexture(string relativePath, TextureLoadOptions? options = null);

        /// <summary>A 16-bit PCM WAV as a clip, synchronously, or null when it could not load (logged). Main thread only.</summary>
        /// <param name="relativePath">A relative path.</param>
        AudioClip? LoadWav(string relativePath);

        /// <summary>An OGG or WAV as a clip, asynchronously; <paramref name="onDone"/> gets null on failure (logged).</summary>
        /// <param name="relativePath">A relative path.</param>
        /// <param name="onDone">Called on the main thread with the clip.</param>
        void LoadAudioAsync(string relativePath, Action<AudioClip?> onDone);
    }

    /// <summary>
    /// Hotkeys. The framework id of a mod's hotkey is <c>mod:&lt;modId&gt;/&lt;id&gt;</c> - a separator no
    /// mod or framework id contains, so it can never collide with <c>console.toggle</c> or another
    /// mod's key. Chords are bound in the mod's config.
    /// </summary>
    public interface ICwyModInput
    {
        /// <summary>
        /// Registers a function with a primary and an optional alternate chord, written like
        /// <c>Ctrl+Shift+H</c> or <c>F7</c>. Keys never fire while a text field has focus. The
        /// player can rebind both in <c>BepInEx/config/cwy.mod.&lt;id&gt;.cfg</c>, section <c>[Hotkeys]</c>.
        /// </summary>
        /// <param name="id">Short id, e.g. <c>greet</c>; letters, digits, <c>. _ -</c>.</param>
        /// <param name="description">One sentence: what pressing it does.</param>
        /// <param name="primaryChord">Default primary chord, e.g. <c>Ctrl+Shift+H</c>.</param>
        /// <param name="alternateChord">Default alternate chord, or null for none.</param>
        /// <param name="onPressed">What to run, on the main thread.</param>
        /// <returns>
        /// The handle (name <c>mod:&lt;modId&gt;/&lt;id&gt;</c>); refused, with the reason, when the id or a
        /// chord is unusable (also logged). Disposing it stops the callback; the key stays listed in
        /// the hotkey table until the game restarts.
        /// </returns>
        CwyRegistration Register(string id, string description, string primaryChord, string? alternateChord, Action onPressed);
    }

    /// <summary>Console commands (Ctrl+Shift+C). A mod cannot replace a command someone else registered.</summary>
    public interface ICwyModConsole
    {
        /// <summary>Registers a command, listed under the mod's id in <c>help</c>.</summary>
        /// <param name="name">What the player types; no spaces.</param>
        /// <param name="description">One short sentence.</param>
        /// <param name="usage">The argument shape, e.g. <c>hello [name]</c>; also drives Tab completion.</param>
        /// <param name="handler">Gets the arguments after the name, returns the text to print. Runs on the main thread.</param>
        /// <returns>
        /// The handle; refused, with the reason, when the name is unusable or taken by another owner
        /// (also logged). Disposing it removes the command.
        /// </returns>
        CwyRegistration Register(string name, string description, string usage, Func<IReadOnlyList<string>, string?> handler);
    }

    /// <summary>
    /// What a simple mod window is built with: a text view and an optional one-line input. Superseded by
    /// <see cref="CwyWindowSpec"/>, which adds lists, chat bubbles, multi-line input, buttons
    /// and tabs; a window built from these options is a spec with a text part <c>lines</c> and an input
    /// part <c>input</c>.
    /// </summary>
    public sealed class CwyWindowOptions
    {
        /// <summary>The title, top-left in the game's title style.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>Smallest width in canvas units (1920×1080 reference).</summary>
        public float MinWidth { get; set; } = 560f;

        /// <summary>Smallest height in canvas units.</summary>
        public float MinHeight { get; set; } = 320f;

        /// <summary>Placeholder of a one-line input at the bottom; null for no input line.</summary>
        public string? InputPlaceholder { get; set; }
    }

    /// <summary>
    /// Windows in the game's own style: text views, lists, chat
    /// bubbles with streaming text, multi-line input with the operating system's input method
    /// (Thai, Japanese), buttons and tabs. The windows follow the game's Hide UI, never take clicks
    /// outside their own rectangle, reopen where the player left them, and keep typed keys from the
    /// game: while a window's text box has the keyboard no hotkey fires and Escape belongs to the
    /// window. Building a window is main-thread only; the parts are safe from any thread.
    /// </summary>
    public interface ICwyModUi : ICwyModFacet
    {
        /// <summary>True when the game adapter supplies mod windows and they are switched on (<c>[Ui] Enabled</c>).</summary>
        new bool IsAvailable { get; }

        /// <summary>This mod's windows that are not destroyed, oldest first (a copy).</summary>
        IReadOnlyList<ICwyWindow> Windows { get; }

        /// <summary>
        /// Builds a hidden window from a spec; call <see cref="ICwyWindow.Show"/> to open it. Main
        /// thread only, and the game's UI assets must be loaded (build it at <c>RoomReady</c> or later).
        /// </summary>
        /// <param name="spec">What to build. The window copies it; later changes to the spec do nothing.</param>
        /// <returns>The window, or the reason there is none (also logged once as a <c>MODS</c> warning).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="spec"/> is null.</exception>
        CwyWindowResult CreateWindow(CwyWindowSpec spec);

        /// <summary>This mod's window with this id, or null.</summary>
        /// <param name="windowId">The window's id.</param>
        ICwyWindow? Find(string windowId);

        /// <summary>Builds a hidden simple window: a scrolling rich-text view, and an input line when asked for.</summary>
        /// <param name="options">What to build.</param>
        /// <param name="problem">Why no window could be built, or empty.</param>
        /// <returns>The window, or null.</returns>
        [CwyDeprecated("0.9.0", DeprecationStage.Notice, Replacement = "helper.Ui.CreateWindow(CwyWindowSpec)")]
        ICwyModWindow? CreateWindow(CwyWindowOptions options, out string problem);
    }

    /// <summary>
    /// One simple game-style window: a text view and an optional input line. Main thread only; destroyed
    /// when the mod stops. New code uses <see cref="ICwyWindow"/> from <see cref="ICwyModUi.CreateWindow(CwyWindowSpec)"/>.
    /// </summary>
    public interface ICwyModWindow
    {
        /// <summary>The title.</summary>
        string Title { get; set; }

        /// <summary>Small text right-aligned in the title bar.</summary>
        string Status { get; set; }

        /// <summary>True while shown.</summary>
        bool IsShown { get; }

        /// <summary>False once destroyed (by <see cref="Destroy"/> or by Unity).</summary>
        bool IsAlive { get; }

        /// <summary>Raised when the close button is clicked or Escape closes it; the window does not hide itself.</summary>
        event Action CloseClicked;

        /// <summary>Raised when the input line is submitted (Enter), with its text.</summary>
        event Action<string> Submitted;

        /// <summary>Shows the window with the game's fade.</summary>
        void Show();

        /// <summary>Hides the window.</summary>
        void Hide();

        /// <summary>Destroys the window.</summary>
        void Destroy();

        /// <summary>Replaces the text view's lines (TextMeshPro rich text, one entry per line).</summary>
        /// <param name="lines">The lines; null clears.</param>
        void SetLines(IReadOnlyList<string>? lines);

        /// <summary>Adds one line at the bottom; the view follows it unless scrolled up.</summary>
        /// <param name="line">The line.</param>
        void AppendLine(string line);
    }

    /// <summary>The shared main-thread dispatcher, with the mod as owner of its work.</summary>
    public interface ICwyModMainThread
    {
        /// <summary>True on Unity's main thread.</summary>
        bool IsMainThread { get; }

        /// <summary>Monotonic seconds; safe on any thread.</summary>
        double Now { get; }

        /// <summary>Queues work for the next frame. Exceptions are logged against the mod.</summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <returns>False when the mod's queue is full.</returns>
        bool Post(string label, Action work);

        /// <summary>Runs work now on the main thread, otherwise queues it.</summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        bool RunOrPost(string label, Action work);

        /// <summary>Runs work on the main thread after a real-time delay.</summary>
        /// <param name="seconds">The delay; zero or less runs it on the next frame.</param>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <returns>
        /// A handle: dispose it (or <c>Cancel()</c>) to drop the work before it runs.
        /// <c>IsAccepted</c> is false when the mod's delayed queue was full (logged).
        /// </returns>
        /// <exception cref="MainThreadRejectedException">The mod is not running any more.</exception>
        DelayedWork PostAfter(double seconds, string label, Action work);

        /// <summary>
        /// Runs work on the main thread; the task faults with its exception, a <see cref="TimeoutException"/>,
        /// or a <see cref="MainThreadRejectedException"/> when the framework refused it (queue full, mod not running).
        /// </summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work.</param>
        /// <param name="timeout">
        /// Wall-clock limit on your side; null means the framework default (2 s),
        /// <see cref="Timeout.InfiniteTimeSpan"/> waits forever. Work not started by then is skipped, never run late.
        /// </param>
        /// <param name="cancellationToken">Stops the wait.</param>
        Task InvokeAsync(string label, Action work, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        /// <summary>Runs a function on the main thread and returns its result through a task.</summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function.</param>
        /// <param name="timeout">Wall-clock limit on your side; null means the framework default (2 s).</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        Task<T> InvokeAsync<T>(string label, Func<T> work, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        /// <summary>Runs a function on the main thread and blocks for it (inline on the main thread).</summary>
        /// <typeparam name="T">The result type.</typeparam>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The function.</param>
        /// <param name="timeout">How long a background caller waits.</param>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The framework refused it (queue full, mod not running).</exception>
        T Invoke<T>(string label, Func<T> work, TimeSpan timeout);

        /// <summary>Runs work on the main thread and blocks until it is done (inline on the main thread).</summary>
        /// <param name="label">What the work is.</param>
        /// <param name="work">The work; its exception is rethrown to the caller.</param>
        /// <param name="timeout">How long a background caller waits.</param>
        /// <exception cref="TimeoutException">The main thread did not run it in time.</exception>
        /// <exception cref="MainThreadRejectedException">The framework refused it (queue full, mod not running).</exception>
        void Invoke(string label, Action work, TimeSpan timeout);
    }
}
