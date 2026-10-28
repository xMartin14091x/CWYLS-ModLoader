using System;
using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.Events
{
    /// <summary>Which pomodoro transition a <see cref="PomodoroEvent"/> reports.</summary>
    public enum PomodoroEventKind
    {
        /// <summary>A pomodoro session was started by the player.</summary>
        Started,

        /// <summary>A work interval began.</summary>
        WorkStarted,

        /// <summary>A work interval ended.</summary>
        WorkEnded,

        /// <summary>A break interval began.</summary>
        BreakStarted,

        /// <summary>A break interval ended.</summary>
        BreakEnded,

        /// <summary>The whole pomodoro finished.</summary>
        Completed,

        /// <summary>The timer was paused.</summary>
        Paused,

        /// <summary>The timer was resumed.</summary>
        Unpaused
    }

    /// <summary>Why Satone is speaking, as far as the framework can tell.</summary>
    public enum LineSource
    {
        /// <summary>No scenario was playing, or the scenario type is not one we recognise.</summary>
        Other,

        /// <summary>A scripted story, event or special scenario.</summary>
        Story,

        /// <summary>An unprompted remark - short talk, time-of-day comment, session start/end.</summary>
        SelfTalk,

        /// <summary>A pomodoro announcement (work start, break start, finish).</summary>
        Pomodoro,

        /// <summary>A reaction to the player clicking her.</summary>
        ClickReaction,

        /// <summary>The tutorial.</summary>
        Tutorial
    }

    /// <summary>
    /// Satone's action state changed. Published from the postfix on <c>HeroineAI.ChangeState</c>.
    /// </summary>
    /// <param name="Previous">Name of the state she was in, e.g. <c>WantTalk</c>.</param>
    /// <param name="Next">Name of the state she moved to, e.g. <c>BreakMovie</c>.</param>
    /// <param name="PreviousValue">Numeric value of <paramref name="Previous"/>.</param>
    /// <param name="NextValue">Numeric value of <paramref name="Next"/>.</param>
    public sealed record SatoneStateChanged(string Previous, string Next, int PreviousValue, int NextValue)
        : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Previous + " -> " + Next;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["prev"] = Previous,
                ["next"] = Next,
                ["prevValue"] = PreviousValue,
                ["nextValue"] = NextValue
            };
    }

    /// <summary>
    /// Satone started speaking a line. Published from the postfix on <c>VoiceManager.Play</c>, the
    /// one point every voice in the game passes through, enriched from the master data and from
    /// whichever scenario is playing.
    /// </summary>
    /// <param name="VoiceName">Clip name, e.g. <c>Voice_Tutorial_1_001</c>. Never null.</param>
    /// <param name="Text">The line in the player's current language, or null if it could not be resolved.</param>
    /// <param name="TextJa">The Japanese original, or null if it could not be resolved.</param>
    /// <param name="LocalizationId">
    /// Master-data row id the text came from, or null when the clip has no row at all - see
    /// <see cref="LineSpoken.NoTextInMaster"/>.
    /// </param>
    /// <param name="Language">Language <paramref name="Text"/> is in, e.g. <c>English</c>.</param>
    /// <param name="IsStory">True when the caller asked for story priority (interrupts an ongoing line).</param>
    /// <param name="ScenarioType">Scenario type playing at the time, or null when none is.</param>
    /// <param name="ScenarioGroupId">Scenario group id playing at the time, or null when none is.</param>
    /// <param name="Source">Classification of <paramref name="ScenarioType"/>.</param>
    /// <param name="MoveMouth">Whether the game was asked to run the mouth-talk motion.</param>
    /// <param name="DurationSec">Clip length in seconds, or 0 when the clip could not be measured.</param>
    /// <param name="Attribution">
    /// How <paramref name="LocalizationId"/> was resolved - one of the <see cref="LineAttribution"/>
    /// values. <see cref="LineAttribution.Row"/> means the scenario reader's own row answered and
    /// the id is exact; <see cref="LineAttribution.VoiceIndex"/> means the reverse voice-name index
    /// answered, which is ambiguous when two rows name the same clip.
    /// </param>
    public sealed record LineSpoken(
        string VoiceName,
        string? Text,
        string? TextJa,
        string? LocalizationId,
        string Language,
        bool IsStory,
        string? ScenarioType,
        string? ScenarioGroupId,
        LineSource Source,
        bool MoveMouth,
        double DurationSec,
        string Attribution = LineAttribution.Unknown) : ICwyEvent
    {
        /// <summary>How much of the text the compact line shows before it is cut.</summary>
        public const int DescribeTextLimit = 60;

        /// <summary>What <see cref="Describe"/> prints instead of a quote for an unknown clip.</summary>
        public const string NoTextMarker = "(no text in master)";

        /// <summary>
        /// True when the clip has no row in the game's master data, so no text could come from it:
        /// <see cref="Text"/> and <see cref="TextJa"/> are the mod's own or null. This is the
        /// normal state of a clip a content pack registered or a mod generated, and it is
        /// marked explicitly so an unknown voice name reads as "not in the game's data" rather
        /// than as a failed lookup.
        /// </summary>
        public bool NoTextInMaster => LocalizationId is null;

        /// <inheritdoc />
        public string Describe()
        {
            var where = ScenarioGroupId is null
                ? Source.ToString()
                : Source + " " + ScenarioGroupId;

            var text = Text ?? TextJa;
            var quoted = text is null
                ? (NoTextInMaster ? " " + NoTextMarker : string.Empty)
                : " \"" + Clip(text) + "\"";
            var japanese = TextJa is null || string.Equals(TextJa, Text, StringComparison.Ordinal)
                ? string.Empty
                : " / 「" + Clip(TextJa) + "」";

            return VoiceName + " [" + where + "]" + quoted + japanese
                + " " + DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
            ["text"] = Text,
            ["textJa"] = TextJa,
            ["source"] = Source.ToString(),
            ["durationSec"] = DurationSec,
            ["voice"] = VoiceName,
            ["localizationId"] = LocalizationId,
            ["scenarioGroupId"] = ScenarioGroupId,
            ["scenarioType"] = ScenarioType,
            ["isStory"] = IsStory,
            ["moveMouth"] = MoveMouth,
            ["language"] = Language,
            ["noTextInMaster"] = NoTextInMaster,
            ["attribution"] = Attribution,
        };

        private static string Clip(string text)
        {
            var single = text.Replace('\n', ' ').Replace('\r', ' ');
            return single.Length <= DescribeTextLimit
                ? single
                : single.Substring(0, DescribeTextLimit) + "…";
        }
    }

    /// <summary>
    /// Satone stopped speaking a line - the clip finished, or it was cancelled.
    /// </summary>
    /// <param name="VoiceName">Clip name of the line that ended.</param>
    /// <param name="DurationSec">How long she actually spoke, in seconds.</param>
    public sealed record LineEnded(string VoiceName, double DurationSec) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() =>
            VoiceName + " " + DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + "s";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["voice"] = VoiceName,
                ["durationSec"] = DurationSec
            };
    }

    /// <summary>A scenario started playing. Published from the postfix on <c>ScenarioReader.StartReady</c>.</summary>
    /// <param name="ScenarioType">Scenario type name, e.g. <c>Tutorial</c>.</param>
    /// <param name="GroupId">Scenario group id, e.g. <c>tutorial_01</c>.</param>
    /// <param name="ReadMode">Read mode name: <c>Click</c> or <c>Auto</c>.</param>
    public sealed record ScenarioStarted(string ScenarioType, string GroupId, string ReadMode) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => ScenarioType + " group=" + GroupId + " mode=" + ReadMode;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["scenarioType"] = ScenarioType,
                ["groupId"] = GroupId,
                ["readMode"] = ReadMode
            };
    }

    /// <summary>A scenario finished. Published from the postfix on <c>ScenarioReader.EndStory</c>.</summary>
    /// <param name="GroupId">Scenario group id that ended, or null if the reader had none.</param>
    public sealed record ScenarioEnded(string? GroupId) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => "group=" + (GroupId ?? "?");

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?> { ["groupId"] = GroupId };
    }

    /// <summary>A pomodoro transition. Bridged from <c>PomodoroService</c>'s R3 observables.</summary>
    /// <param name="Kind">Which transition.</param>
    /// <param name="PomodoroType">The service's current type: <c>Work</c>, <c>Break</c> or <c>Complete</c>.</param>
    public sealed record PomodoroEvent(PomodoroEventKind Kind, string PomodoroType) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Kind + " (" + PomodoroType + ")";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["kind"] = Kind.ToString(),
                ["pomodoroType"] = PomodoroType
            };
    }

    /// <summary>
    /// The pomodoro timer advanced. The game raises this every frame; the bus throttles it to
    /// roughly one per second per timer type (see <c>CWYEvents</c>).
    /// </summary>
    /// <param name="PomodoroType"><c>Work</c> or <c>Break</c>.</param>
    /// <param name="ElapsedSec">Seconds elapsed in the current interval.</param>
    /// <param name="RemainingSec">Seconds left in the current interval.</param>
    public sealed record PomodoroTick(string PomodoroType, double ElapsedSec, double RemainingSec) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() =>
            PomodoroType + " " + ElapsedSec.ToString("0", CultureInfo.InvariantCulture) + "s elapsed, "
            + RemainingSec.ToString("0", CultureInfo.InvariantCulture) + "s left";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["pomodoroType"] = PomodoroType,
                ["elapsedSec"] = ElapsedSec,
                ["remainingSec"] = RemainingSec
            };
    }

    /// <summary>A window-view object was turned on or off.</summary>
    /// <param name="Window">Window view name, e.g. <c>Sakura</c>.</param>
    /// <param name="Active">True when it was switched on.</param>
    public sealed record WindowChanged(string Window, bool Active) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Window + (Active ? " on" : " off");

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["window"] = Window,
                ["active"] = Active
            };
    }

    /// <summary>A track started playing. Bridged from <c>MusicService.OnPlayMusic</c>.</summary>
    /// <param name="Title">Track title as the game shows it.</param>
    /// <param name="Index">Position in the current playlist, or -1 when it could not be determined.</param>
    /// <param name="IsShuffle">Whether shuffle is on.</param>
    public sealed record MusicChanged(string Title, int Index, bool IsShuffle) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() =>
            "#" + Index.ToString(CultureInfo.InvariantCulture) + " " + Title + (IsShuffle ? " (shuffle)" : string.Empty);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["title"] = Title,
                ["index"] = Index,
                ["isShuffle"] = IsShuffle
            };
    }

    /// <summary>
    /// The game wrote a section of the player's save. Published from a postfix on every
    /// <c>SaveDataManager.Save*</c> method.
    /// </summary>
    /// <param name="Section">The save method's name without its <c>Save</c> prefix, e.g. <c>TodoList</c>.</param>
    public sealed record SaveSection(string Section) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Section;

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?> { ["section"] = Section };
    }

    /// <summary>A Unity scene was loaded or unloaded.</summary>
    /// <param name="Scene">Scene name, e.g. <c>RoomScene</c>.</param>
    /// <param name="Loaded">True for a load, false for an unload.</param>
    public sealed record SceneChanged(string Scene, bool Loaded) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Scene + (Loaded ? " loaded" : " unloaded");

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["scene"] = Scene,
                ["loaded"] = Loaded
            };
    }

    /// <summary>
    /// One second of wall-clock time passed. The game's own clock is <c>DateTime.Now</c>
    /// (<c>DateService.Setup</c>), so this is the same value the game shows.
    /// </summary>
    /// <param name="Time">Local time at the tick.</param>
    public sealed record GameClockTick(DateTime Time) : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>
            {
                ["time"] = Time.ToString("o", CultureInfo.InvariantCulture)
            };
    }

    /// <summary>
    /// The room is up and its services can be resolved: the first moment a mod may safely touch
    /// Satone. Published once per <c>RoomScene</c> load.
    /// </summary>
    public sealed record RoomReady : ICwyEvent
    {
        /// <inheritdoc />
        public string Describe() => "room ready";

        /// <inheritdoc />
        public IReadOnlyDictionary<string, object?> Fields() =>
            new Dictionary<string, object?>();
    }
}
