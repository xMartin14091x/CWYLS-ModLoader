using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Logging
{
    /// <summary>
    /// The one-word console tags. One tag per event family; the viewer
    /// maps tag to colour, so a two-word tag would make the grammar ambiguous.
    /// </summary>
    /// <remarks>
    /// Everything a person watching the game should see has a tag here. Anything not in this list -
    /// ticks, <c>phase=</c> diagnostics, polling notices, per-keystroke input, aggregates - stays in
    /// the trace file. The rule: the console answers "what happened in the game", the trace file
    /// answers "what did our code do about it".
    /// <para>
    /// A tag is <c>[A-Z][A-Z0-9]{1,11}</c> (the viewer's grammar). Every tag here has a colour in
    /// <c>tools/mac/watch-log.py</c> (<c>TAGS</c>); <c>LoggingConsoleTagsTests</c> keeps the two
    /// lists equal. The feature tags further down are named by their producers
    /// (<c>ConsoleEvents.Emit</c> with the tag), so <see cref="ConsoleEventMap"/> needs no rows for them.
    /// </para>
    /// </remarks>
    public static class ConsoleTags
    {
        /// <summary>Satone spoke a line.</summary>
        public const string Speech = "SPEECH";

        /// <summary>A mod's own line, queued or finished through <c>Satone.Say</c>.</summary>
        public const string Say = "SAY";

        /// <summary>Satone's action state changed.</summary>
        public const string State = "STATE";

        /// <summary>A facial expression was requested.</summary>
        public const string Face = "FACE";

        /// <summary>A motion was requested.</summary>
        public const string Motion = "MOTION";

        /// <summary>A gaze change was requested.</summary>
        public const string Look = "LOOK";

        /// <summary>Face/motion/gaze were put back the way the game had them.</summary>
        public const string Restore = "RESTORE";

        /// <summary>
        /// A line in a pack-declared language reached the game's own text box (the only place the
        /// framework shows text).
        /// </summary>
        public const string Subtitle = "SUBTITLE";

        /// <summary>A music track started.</summary>
        public const string Music = "MUSIC";

        /// <summary>A pomodoro transition.</summary>
        public const string Pomodoro = "POMODORO";

        /// <summary>A scenario started or ended.</summary>
        public const string Scenario = "SCENARIO";

        /// <summary>A Unity scene was loaded or unloaded.</summary>
        public const string Scene = "SCENE";

        /// <summary>A window view was switched on or off.</summary>
        public const string Window = "WINDOW";

        /// <summary>
        /// The display language was chosen or re-applied, a pack-declared language got its entry in
        /// the game's language selector, or its fonts were set up.
        /// </summary>
        public const string Language = "LANGUAGE";

        /// <summary>The room is up and its services can be resolved.</summary>
        public const string Ready = "READY";

        /// <summary>A UI button was pressed.</summary>
        public const string Button = "BUTTON";

        /// <summary>Text was submitted into a field.</summary>
        public const string Typed = "TYPED";

        /// <summary>The player clicked.</summary>
        public const string Click = "CLICK";

        /// <summary>A frame-rate, memory or focus reading worth interrupting for.</summary>
        public const string Health = "HEALTH";

        /// <summary>A Unity error, exception or failed assertion.</summary>
        public const string Unity = "UNITY";

        /// <summary>
        /// Player data changed, was snapshotted, or the data bus became ready. (A save itself has no
        /// tag: <c>event/SaveSection</c> is file-only.)
        /// </summary>
        public const string Data = "DATA";

        /// <summary>A voice clip was registered or played.</summary>
        public const string Voice = "VOICE";

        /// <summary>Textures were applied or reloaded.</summary>
        public const string Texture = "TEXTURE";

        /// <summary>The pack scan result.</summary>
        public const string Packs = "PACKS";

        /// <summary>Master-data rows merged into the game's loader.</summary>
        public const string MasterData = "MASTERDATA";

        /// <summary>The hotkey table.</summary>
        public const string Keys = "KEYS";

        /// <summary>A smoke run and its failures.</summary>
        public const string Smoke = "SMOKE";

        /// <summary>The in-game console overlay.</summary>
        public const string Console = "CONSOLE";

        /// <summary>One startup summary per feature.</summary>
        public const string Setup = "SETUP";

        /// <summary>The trace plugin's own status.</summary>
        public const string Trace = "TRACE";

        /// <summary>A direct <c>LogWarning</c> the sink wrapped.</summary>
        public const string Warn = "WARN";

        /// <summary>A direct <c>LogError</c> or <c>LogFatal</c> the sink wrapped.</summary>
        public const string Error = "ERROR";

        /// <summary>What a record with no usable tag prints as.</summary>
        public const string Unknown = "NOTE";

        /// <summary>
        /// What the framework says about code mods: loaded, skipped, failed, deprecated calls, an
        /// unavailable facet (sys <c>mods</c>).
        /// </summary>
        public const string Mods = "MODS";

        /// <summary>What a code mod says itself through <c>helper.Log</c> (sys <c>mod</c>, event name = mod id).</summary>
        public const string Mod = "MOD";

        /// <summary>An action ran, was refused or failed: the audit line (the action runner and the action families).</summary>
        public const string Action = "ACTION";

        /// <summary>One request to the local game API, or the API switching on/off.</summary>
        public const string Api = "API";

        /// <summary>
        /// A script command row ran, a scene was played from the console, or a pack scene was linted.
        /// Not <see cref="Scene"/>, which already means a Unity scene load; scene
        /// starts and ends stay <see cref="Scenario"/>.
        /// </summary>
        public const string Script = "SCRIPT";

        /// <summary>A cutscene trigger fired, was queued or was skipped.</summary>
        public const string Trigger = "TRIGGER";

        /// <summary>A condition was evaluated for a reason worth showing, overridden or found invalid.</summary>
        public const string Cond = "COND";

        /// <summary>A pack clip replaced or added an ambient, SE or music sound.</summary>
        public const string Audio = "AUDIO";

        /// <summary>Pack sprites were applied to the game's UI, or restored (D-UI).</summary>
        public const string UiSkin = "UISKIN";

        /// <summary>EditData patches were applied to master tables, or refused.</summary>
        public const string EditData = "EDITDATA";

        /// <summary>Microphone capture state and utterances.</summary>
        public const string Mic = "MIC";

        /// <summary>The camera was moved to a preset or handed back.</summary>
        public const string Camera = "CAMERA";

        /// <summary>
        /// Every tag above, in declaration order. For parity checks (viewer colours, the in-game
        /// console's families) and documentation.
        /// </summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            Speech, Say, State, Face, Motion, Look, Restore, Subtitle, Music, Pomodoro, Scenario, Scene,
            Window, Language, Ready, Button, Typed, Click, Health, Unity, Data, Voice, Texture, Packs,
            MasterData, Keys, Smoke, Console, Setup, Trace, Warn, Error, Unknown,
            Mods, Mod, Action, Api, Script, Trigger, Cond, Audio, UiSkin, EditData, Mic, Camera,
        };
    }
}
