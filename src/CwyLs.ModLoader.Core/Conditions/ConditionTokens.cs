using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Conditions
{
    /// <summary>
    /// The built-in condition tokens (time of day, weekday, date range, pomodoro state, language,
    /// scenery, story progress, player level; the condition provider adds <c>hasPack</c>, <c>flag:&lt;name&gt;</c> and <c>random</c>). Only the framework may register
    /// these names; mods register <c>&lt;modId&gt;/&lt;name&gt;</c> tokens.
    /// </summary>
    /// <remarks>
    /// This table is the contract: the names, how values compare and when they change. The value
    /// vocabularies (which words <c>pomodoro</c> or <c>scenery</c> report) belong to the condition
    /// provider, which documents them in the author's guide.
    /// </remarks>
    public static class ConditionTokens
    {
        /// <summary>Local clock time <c>HH:mm</c>; ranges may wrap past midnight.</summary>
        public const string Time = "time";

        /// <summary>Local hour, 0-23.</summary>
        public const string Hour = "hour";

        /// <summary>Day of the week.</summary>
        public const string Weekday = "weekday";

        /// <summary>Local date.</summary>
        public const string Date = "date";

        /// <summary>The game's time of day.</summary>
        public const string TimeOfDay = "timeOfDay";

        /// <summary>The timer's state.</summary>
        public const string Pomodoro = "pomodoro";

        /// <summary>The display language code.</summary>
        public const string Language = "language";

        /// <summary>The active window views / scenery.</summary>
        public const string Scenery = "scenery";

        /// <summary>The last main-story episode the player finished.</summary>
        public const string Story = "story";

        /// <summary>The player's level.</summary>
        public const string Level = "level";

        /// <summary>The ids of the loaded packs and code mods.</summary>
        public const string HasPack = "hasPack";

        /// <summary>A named flag: <c>flag:&lt;name&gt;</c>.</summary>
        public const string Flag = "flag";

        /// <summary>A roll 0-99.</summary>
        public const string Random = "random";

        private static readonly ConditionTokenInfo[] Table =
        {
            new ConditionTokenInfo(Time, ConditionValueKind.Time, UpdatePoint.TimeChanged,
                "The local clock time as HH:mm; a range like 22:00..06:00 wraps past midnight."),
            new ConditionTokenInfo(Hour, ConditionValueKind.Integer, UpdatePoint.TimeChanged,
                "The local hour, 0 to 23."),
            new ConditionTokenInfo(Weekday, ConditionValueKind.Weekday, UpdatePoint.DayStarted,
                "The day of the week: Mon, Tue, Wed, Thu, Fri, Sat, Sun."),
            new ConditionTokenInfo(Date, ConditionValueKind.Date, UpdatePoint.DayStarted,
                "The local date as MM-dd (or yyyy-MM-dd); a range like 12-24..01-02 wraps past New Year."),
            new ConditionTokenInfo(TimeOfDay, ConditionValueKind.Text, UpdatePoint.TimeChanged | UpdatePoint.SceneryChanged,
                "The game's time of day, as the window view shows it."),
            new ConditionTokenInfo(Pomodoro, ConditionValueKind.Text, UpdatePoint.PomodoroChanged,
                "The timer's state (for example idle, work, break)."),
            new ConditionTokenInfo(Language, ConditionValueKind.Text, UpdatePoint.LanguageChanged,
                "The display language code, including pack languages (en, ja, th, ...)."),
            new ConditionTokenInfo(Scenery, ConditionValueKind.Text, UpdatePoint.SceneryChanged,
                "The window views and weather currently shown."),
            new ConditionTokenInfo(Story, ConditionValueKind.Number, UpdatePoint.StoryProgressed | UpdatePoint.RoomReady,
                "The last main-story episode the player finished (ScenarioProgressData.FinishReadMainEpisodeNumber)."),
            new ConditionTokenInfo(Level, ConditionValueKind.Integer, UpdatePoint.LevelChanged | UpdatePoint.RoomReady,
                "The player's level."),
            new ConditionTokenInfo(HasPack, ConditionValueKind.Text, UpdatePoint.PacksChanged,
                "The ids of every loaded content pack and code mod."),
            new ConditionTokenInfo(Flag, ConditionValueKind.Boolean, UpdatePoint.FlagChanged,
                "flag:<name> is true once the flag was set (by cwy.flag or a mod) and not cleared.",
                ConditionArgumentRule.Required),
            new ConditionTokenInfo(Random, ConditionValueKind.Integer, UpdatePoint.DayStarted,
                "A roll from 0 to 99, e.g. \"<30\" for a 30 % chance; the provider documents when it re-rolls."),
        };

        private static readonly Dictionary<string, ConditionTokenInfo> ByName = Index();

        /// <summary>Every built-in token, in the order above.</summary>
        public static IReadOnlyList<ConditionTokenInfo> BuiltIn => Table;

        /// <summary>True when <paramref name="name"/> is a built-in token name (case-insensitive).</summary>
        /// <param name="name">A token name without argument.</param>
        public static bool IsBuiltIn(string? name) => name != null && ByName.ContainsKey(name);

        /// <summary>The built-in token with this name (case-insensitive).</summary>
        /// <param name="name">A token name without argument.</param>
        /// <param name="info">The token, or null.</param>
        public static bool TryGetBuiltIn(string? name, out ConditionTokenInfo? info)
        {
            info = null;
            return name != null && ByName.TryGetValue(name, out info);
        }

        private static Dictionary<string, ConditionTokenInfo> Index()
        {
            var index = new Dictionary<string, ConditionTokenInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in Table)
            {
                index.Add(info.Name, info);
            }

            return index;
        }
    }
}
