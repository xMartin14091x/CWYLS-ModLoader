using System.Globalization;
using CwyLs.ModLoader.Core.Scenario;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// The four game enums a master-data pack can name, as <see cref="EnumTable"/>s.
    /// <para>
    /// These match the game's own enums in game v1.17.2 - each table names its game type -
    /// so that a pack can write <c>"command": "VoiceText"</c> and be told, off
    /// the engine and in a unit test, whether that is a real member. If a game update renumbers a
    /// member these tables go stale, which is the same failure the patch-target registry
    /// reports for methods; update them when the game's enums change.
    /// </para>
    /// </summary>
    public static class MasterDataEnums
    {
        /// <summary>
        /// <c>Bulbul.MasterData.CommandType</c> - what one novel row does.
        /// </summary>
        public static EnumTable Command { get; } = new EnumTable(
            "Bulbul.MasterData.CommandType",
                ("Text", 0),
                ("WaitForSeconds", 1),
                ("WaitInput", 2),
                ("Selection", 3),
                ("Jump", 4),
                ("PlayBGM", 5),
                ("PlaySE", 6),
                ("PlayAmbientBGM", 7),
                ("StopBGM", 8),
                ("StopSE", 9),
                ("StopAmbientBGM", 10),
                ("ChangeWindowView", 11),
                ("EndScenario", 12),
                ("StartSelectOnce", 13),
                ("Tutorial", 14),
                ("TutorialSelection", 15),
                ("TutorialFocusPomodoro", 16),
                ("TutorialFocusTodo", 17),
                ("TutorialFocusNote", 18),
                ("TutorialOpenExplanation", 19),
                ("VoiceText", 20),
                ("ChangeMotion", 21),
                ("PomodoroVoiceText", 22),
                ("TalkLogOnlyPlayBGM", 25),
                ("PlayMusicServiceBGM", 26),
                ("ChangeLayout", 30),
                ("ChangeVolumeBGM", 40),
                ("ReadNextForScript", 50),
                ("DeactivateNormalText", 60),
                ("IfBetweenDateTime", 70),
                ("AddTextLoop", 80),
                ("StartTextLoop", 81),
                ("BlackFadeOut", 90),
                ("StartEndCredits", 91),
                ("FinishEndCredits", 92),
                ("SetDirectionDelaySeconds", 100),
                ("DirectionGlitch1", 200),
                ("DirectionScreenMove", 205),
                ("DirectionBlackOut", 210),
                ("DirectionMonochromeNoise", 215),
                ("DirectionChromaticAberration", 216),
                ("DirectionAdjustAllUnstable", 219),
                ("CameraStop_1", 220),
                ("ScreenSharing_1_Play", 230),
                ("ScreenSharing_1_End", 231),
                ("VoiceDefectRatio", 240),
                ("DirectionGameStartCamera", 250),
                ("SunglassesShineForSatone", 500),
                ("SunglassesShineForKouchan", 501),
                ("ChangeHat", 1000));

        /// <summary>
        /// <c>Bulbul.MasterData.ScenarioType</c> - when and how a scenario group is played.
        /// <c>Event_Player_Birthday</c> is an alias of <c>Event_Player_HappyBirthday</c> (both 103).
        /// </summary>
        public static EnumTable Scenario { get; } = new EnumTable(
            "Bulbul.MasterData.ScenarioType",
                ("None", 0),
                ("MainScenario", 1),
                ("AfterScenario", 2),
                ("DLCScenario", 3),
                ("SpeakWord_PomodoroStart", 4),
                ("SpeakWord_PomodoroBreak", 5),
                ("SpeakWord_PomodoroFinish", 6),
                ("ShortConversation", 7),
                ("HeroineClickNormal", 8),
                ("HeroineClickWork", 9),
                ("HeroineClickBreak", 10),
                ("GameStart_First_CameraTouch", 11),
                ("GameStart_LessTowDays_CameraTouch", 12),
                ("GameStart_LessTowDays", 13),
                ("GameStart_LessHarfMonth_CameraTouch", 14),
                ("GameStart_GreaterHarfMonth_CameraTouch", 15),
                ("GameStart_GreaterMonth_CameraTouch", 16),
                ("GameStart_GreaterYear_AND_PlayOneHundred_CameraTouch", 17),
                ("Tutorial", 18),
                ("GameEnd", 19),
                ("HeroineSelfShortTalkNormal", 20),
                ("HeroineSelfShortTalkBreak", 21),
                ("MotionScenario", 22),
                ("SmallTalk", 23),
                ("ExtraScenario", 24),
                ("Photograph_GameStart_CameraTouch", 25),
                ("GameDemo_Talk", 40),
                ("Special_AlterEgo", 50),
                ("Special_BearsRestaurant", 51),
                ("Special_Valentine2026", 52),
                ("Special_LunaNewYear2026", 53),
                ("Special_NearSpring2026", 54),
                ("Special_MagicalPrincess", 55),
                ("Special_StatisticsDev", 56),
                ("Event_2025_Christmas_GameStart", 100),
                ("Event_2025_NewYear_Countdown", 101),
                ("Event_2026_AprilFool", 102),
                ("Event_Player_HappyBirthday", 103),
                ("Event_Player_Birthday", 103),
                ("HeroineClickWork_Morning", 500),
                ("HeroineClickWork_Noon", 501),
                ("HeroineClickWork_Evening", 502),
                ("HeroineClickWork_Night", 503),
                ("HeroineClickBreak_Morning", 550),
                ("HeroineClickBreak_Noon", 551),
                ("HeroineClickBreak_Evening", 552),
                ("HeroineClickBreak_Night", 553),
                ("SpeakWord_Pomodoro_WorkContinuousStart", 1000),
                ("SpeakWord_Pomodoro_WorkLongStart", 1001),
                ("SpeakWord_Pomodoro_ShortWorkedBreakStart", 1100),
                ("SpeakWord_Pomodoro_LongWorkedBreakStart", 1101),
                ("SpeakWord_Pomodoro_LongWorkFinish", 1200),
                ("SpeakWord_Pomodoro_ShortWorkFinish", 1201),
                ("SpeakWord_Pomodoro_MidwayFinish", 1202),
                ("GameStart_LessTowDays_CameraTouch_Morning", 1400),
                ("GameStart_LessTowDays_Morning", 1401),
                ("GameStart_LessTowDays_CameraTouch_Noon", 1402),
                ("GameStart_LessTowDays_Noon", 1403),
                ("GameStart_LessTowDays_CameraTouch_Evening", 1404),
                ("GameStart_LessTowDays_Evening", 1405),
                ("GameStart_LessTowDays_CameraTouch_Night", 1406),
                ("GameStart_LessTowDays_Night", 1407),
                ("GameLongTimeEnd", 1500),
                ("GameShortTimeEnd", 1501),
                ("HeroineSelfShortTalkBreak_Morning", 2000),
                ("HeroineSelfShortTalkBreak_Noon", 2001),
                ("HeroineSelfShortTalkBreak_Evening", 2002),
                ("HeroineSelfShortTalkBreak_Night", 2003),
                ("HeroineSelfShortTalk_CurrentTime", 2050),
                ("HeroineSelfShortTalk_WorkedTime", 2100),
                ("SpeakWord_LeaveChair", 2500),
                ("SpeakWord_LeaveChair_For_Drink", 2501),
                ("SpeakWord_LeaveChair_For_Snack", 2502),
                ("SpeakWord_Snack_Eat_Start", 2600),
                ("SpeakWord_Bread_Eat_Start", 2601),
                ("SpeakWord_Player_LeaveChair_Reaction_Common", 2700),
                ("SpeakWord_Player_LeaveChair_Reaction_Break", 2701),
                ("SpeakWord_Player_LeaveChair_Reaction_Work", 2702),
                ("SpeakWord_Player_Comeback_Reaction_Common", 2750),
                ("SpeakWord_Player_Comeback_Reaction_Break", 2751),
                ("SpeakWord_Player_Comeback_Reaction_Work", 2752),
                ("SpeakWord_CountupStart", 3000),
                ("SpeakWord_Countup_WorkContinuousStart", 3001),
                ("SpeakWord_CountupBreak", 3100),
                ("SpeakWord_Countup_ShortWorkedBreakStart", 3101),
                ("SpeakWord_Countup_LongWorkedBreakStart", 3102),
                ("SpeakWord_CountupFinish", 3200),
                ("SpeakWord_Countup_ShortWorkFinish", 3201),
                ("SpeakWord_Countup_LongWorkFinish", 3202));

        /// <summary>
        /// <c>Bulbul.MasterData.TalkerType</c> - who speaks a row.
        /// </summary>
        public static EnumTable Talker { get; } = new EnumTable(
            "Bulbul.MasterData.TalkerType",
                ("None", 0),
                ("Heroine", 1));

        /// <summary>
        /// <c>DG.Tweening.Ease</c> - the easing of a row's look-at tween. DOTween's own enum, used
        /// by <c>NovelData.LookEaseType</c>; note <c>Unset</c> is 0 and <c>Linear</c> is 1, so the
        /// game's own rows (which store 0) mean <c>Unset</c>, not <c>Linear</c>.
        /// </summary>
        public static EnumTable Ease { get; } = new EnumTable(
            "DG.Tweening.Ease",
                ("Unset", 0),
                ("Linear", 1),
                ("InSine", 2),
                ("OutSine", 3),
                ("InOutSine", 4),
                ("InQuad", 5),
                ("OutQuad", 6),
                ("InOutQuad", 7),
                ("InCubic", 8),
                ("OutCubic", 9),
                ("InOutCubic", 10),
                ("InQuart", 11),
                ("OutQuart", 12),
                ("InOutQuart", 13),
                ("InQuint", 14),
                ("OutQuint", 15),
                ("InOutQuint", 16),
                ("InExpo", 17),
                ("OutExpo", 18),
                ("InOutExpo", 19),
                ("InCirc", 20),
                ("OutCirc", 21),
                ("InOutCirc", 22),
                ("InElastic", 23),
                ("OutElastic", 24),
                ("InOutElastic", 25),
                ("InBack", 26),
                ("OutBack", 27),
                ("InOutBack", 28),
                ("InBounce", 29),
                ("OutBounce", 30),
                ("InOutBounce", 31),
                ("Flash", 32),
                ("InFlash", 33),
                ("OutFlash", 34),
                ("InOutFlash", 35),
                ("INTERNAL_Zero", 36),
                ("INTERNAL_Custom", 37));

        /// <summary>Every table, for diagnostics and tests.</summary>
        public static EnumTable[] All => new[] { Command, Scenario, Talker, Ease };

        /// <summary>
        /// Resolves a novel row's <c>Command</c> token: a member of <see cref="Command"/> (name or
        /// number) first, then a CWY script command. A dotted name such as
        /// <c>cwy.do</c> or <c>my.mod.wave</c> gets its number in 5000-5999 from
        /// <see cref="ScenarioCommandRegistry.Shared"/> - reserved on first sight, so a row parsed
        /// before the mod that handles it has loaded still points at the right command. A number in
        /// 5000-5999 is accepted only when it is a built-in or already handed out.
        /// </summary>
        /// <remarks>
        /// <see cref="Command"/> itself stays the game's 50 members, so everything that lists or counts
        /// the game's enum is unchanged. The pack parser calls this for the <c>Command</c> column.
        /// </remarks>
        /// <param name="token">The token as written in the pack (name or decimal text).</param>
        /// <param name="value">The command number, or 0.</param>
        /// <returns>True when the token names a game command or a script command.</returns>
        public static bool TryParseCommand(string? token, out int value) =>
            TryParseCommand(token, ScenarioCommandRegistry.Shared, out value);

        /// <summary>As <see cref="TryParseCommand(string, out int)"/>, against a given registry (tests).</summary>
        /// <param name="token">The token as written in the pack.</param>
        /// <param name="registry">The script-command registry.</param>
        /// <param name="value">The command number, or 0.</param>
        public static bool TryParseCommand(string? token, ScenarioCommandRegistry registry, out int value)
        {
            if (Command.TryParseText(token, out value))
            {
                return true;
            }

            value = 0;
            var text = token?.Trim();
            if (string.IsNullOrEmpty(text) || registry is null)
            {
                return false;
            }

            if (text!.IndexOf('.') >= 0)
            {
                return registry.TryResolve(text, out value);
            }

            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && IsCommandDefined(number, registry))
            {
                value = number;
                return true;
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="value"/> is a game command number or a known script command
        /// number (a built-in, or one already handed out) - what the parser accepts for a numeric
        /// <c>Command</c>.
        /// </summary>
        /// <param name="value">A command number.</param>
        public static bool IsCommandDefined(int value) => IsCommandDefined(value, ScenarioCommandRegistry.Shared);

        /// <summary>As <see cref="IsCommandDefined(int)"/>, against a given registry (tests).</summary>
        /// <param name="value">A command number.</param>
        /// <param name="registry">The script-command registry.</param>
        public static bool IsCommandDefined(int value, ScenarioCommandRegistry registry) =>
            Command.IsDefined(value) || (registry != null && registry.IsKnownId(value));

        /// <summary>A command number's name: the game member, or the script command's name, or the number.</summary>
        /// <param name="value">A command number.</param>
        public static string CommandName(int value)
        {
            if (Command.TryName(value, out var name) && name != null)
            {
                return name;
            }

            return ScenarioCommandRegistry.Shared.TryGetName(value, out var script) && script != null
                ? script
                : value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
