using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// F3 golden corpus: one exact console line per event kind.
    /// <para>
    /// Every line here is produced by the code that produces it in the game - the producer helper
    /// (<see cref="ConsoleEvents"/>), the routing table (<see cref="ConsoleEventMap"/>) and the
    /// formatter - with the clock pinned so the string can be asserted character for character.
    /// The same corpus is written to <c>tools/mac/tests/console-golden.txt</c> and the viewer is
    /// run over it with <c>--strict</c>, which is what stops the Python parser and the C# grammar
    /// from drifting apart.
    /// </para>
    /// </summary>
    [Collection("TraceBus")]
    public class LoggingConsoleGoldenTests
    {
        /// <summary>The pinned stamp every golden line carries: <c>14:02:11.019</c>.</summary>
        private static readonly DateTimeOffset At =
            new DateTimeOffset(2026, 9, 23, 14, 2, 11, 19, TimeSpan.FromHours(7));

        // ---- the corpus -----------------------------------------------------------------------

        /// <summary>Every golden case: a name, the record, and the exact line it must format to.</summary>
        public static IEnumerable<object[]> Cases()
        {
            foreach (var (name, record, expected) in Corpus())
            {
                yield return new object[] { name, record, expected };
            }
        }

        private static IEnumerable<(string Name, ConsoleRecord Record, string Expected)> Corpus()
        {
            // SPEECH - the line the user reads. Text, Japanese, and the five fields that survive
            // however long the sentence is.
            yield return ("speech",
                Bus("satone", "speech", Speech()),
                "14:02:11.019  SPEECH  \"I made you some tea.\" 「お茶を淹れたよ。」  src=Tutorial dur=2.3s " +
                "voice=Tutorial_1_001 id=Tutorial_1_001 attr=row");

            yield return ("speech.no_master_row",
                Bus("satone", "speech", Speech(text: null, textJa: null, id: null, voice: "Voice_cwy_beep",
                    source: "Other", duration: 0.4d, attribution: "none")),
                "14:02:11.019  SPEECH  -  src=Other dur=0.4s voice=cwy_beep id=- attr=none");

            yield return ("speech.same_japanese",
                Bus("satone", "speech", Speech(text: "お茶を淹れたよ。", textJa: "お茶を淹れたよ。")),
                "14:02:11.019  SPEECH  \"お茶を淹れたよ。\"  src=Tutorial dur=2.3s voice=Tutorial_1_001 " +
                "id=Tutorial_1_001 attr=row");

            yield return ("state",
                Bus("satone", "state", new Dictionary<string, object?>
                {
                    ["prev"] = "WantTalk",
                    ["next"] = "BreakMovie",
                    ["prevValue"] = 3,
                    ["nextValue"] = 7,
                }),
                "14:02:11.019  STATE  WantTalk -> BreakMovie");

            // SAY - a mod's own line, announced once when it starts.
            yield return ("say",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "say", ConsoleTags.Say,
                    "#7 \"Hello from the Mod Loader.\"",
                    "via:phase,voice,facial:facialName,motion:motionName",
                    ("phase", "scenario"),
                    ("id", 7),
                    ("voice", "Voice_CWY_Test"),
                    ("facialName", "smile"),
                    ("motionName", "wild001_good"))),
                "14:02:11.019  SAY  #7 \"Hello from the Mod Loader.\"  via=scenario voice=Voice_CWY_Test " +
                "facial=smile motion=wild001_good");

            yield return ("say.rejected",
                Emitted(() => ConsoleEvents.EmitAt(
                    "satone", "say", ConsoleTags.Say, ConsoleLevel.Warn,
                    "#8 rejected \"Hello again.\"",
                    "detail,voice",
                    ("detail", "the game is already playing Tutorial tutorial_01"),
                    ("voice", null))),
                "14:02:11.019  SAY  #8 rejected \"Hello again.\"  " +
                "detail=\"the game is already playing Tutorial tutorial_01\" voice=-");

            yield return ("face",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "face", ConsoleTags.Face, "smile", "id",
                    ("phase", "called"), ("id", 10))),
                "14:02:11.019  FACE  smile  id=10");

            yield return ("motion",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "motion", ConsoleTags.Motion, "wild001_good", "id",
                    ("phase", "called"), ("id", 61))),
                "14:02:11.019  MOTION  wild001_good  id=61");

            yield return ("look",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "look", ConsoleTags.Look, "1", "id",
                    ("phase", "called"), ("id", -1))),
                "14:02:11.019  LOOK  1  id=-1");

            yield return ("restore",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "restore", ConsoleTags.Restore,
                    "face in 0.77s, motion re-dispatched (WorkTyping), gaze reset",
                    "scope,state:motionState,gaze,detail",
                    ("phase", "done"),
                    ("scope", "face+motion+gaze"),
                    ("motionState", "WorkTyping"),
                    ("gaze", true),
                    ("detail", string.Empty))),
                "14:02:11.019  RESTORE  face in 0.77s, motion re-dispatched (WorkTyping), gaze reset  " +
                "scope=face+motion+gaze state=WorkTyping gaze=true detail=-");

            // SUBTITLE - a pack-language line reached the game's own text box. Same call as the
            // producer (Subtitles/SubtitleNative.cs, StartText watch); the overlay panel and its
            // via=overlay / via=native lines are gone since B-THAI (C5 retired those cases).
            yield return ("subtitle",
                Emitted(() => ConsoleEvents.Emit(
                    "satone", "subtitle", ConsoleTags.Subtitle, "\"สวัสดีจาก Mod Loader\"",
                    "lang,via,id:localizationId,min:minSeconds",
                    (ConsolePayload.MirrorKey, true),
                    ("text", "สวัสดีจาก Mod Loader"),
                    ("lang", "th"),
                    ("via", "textbox"),
                    ("localizationId", "cwy_say_n_001"),
                    ("minSeconds", Math.Round(ReadingTime.MinimumFor("สวัสดีจาก Mod Loader"), 1)),
                    ("shown", true))),
                "14:02:11.019  SUBTITLE  \"สวัสดีจาก Mod Loader\"  lang=th via=textbox id=cwy_say_n_001 min=2.5");

            // ---- the game's own events -------------------------------------------------------

            yield return ("music",
                Bus("event", "MusicChanged", new Dictionary<string, object?>
                {
                    ["title"] = "Sleeping Bloom",
                    ["index"] = 0,
                    ["isShuffle"] = true,
                }),
                "14:02:11.019  MUSIC  \"Sleeping Bloom\"  index=0 shuffle=true");

            yield return ("pomodoro",
                Bus("event", "PomodoroEvent", new Dictionary<string, object?>
                {
                    ["kind"] = "WorkStarted",
                    ["pomodoroType"] = "Work",
                }),
                "14:02:11.019  POMODORO  WorkStarted  type=Work");

            yield return ("scenario.start",
                Bus("event", "ScenarioStarted", new Dictionary<string, object?>
                {
                    ["scenarioType"] = "GameStart_LessTowDays",
                    ["groupId"] = "game_start_02_001",
                    ["readMode"] = "Auto",
                }),
                "14:02:11.019  SCENARIO  start game_start_02_001  type=GameStart_LessTowDays mode=Auto");

            yield return ("scenario.end",
                Bus("event", "ScenarioEnded", new Dictionary<string, object?> { ["groupId"] = "game_start_02_001" }),
                "14:02:11.019  SCENARIO  end game_start_02_001");

            yield return ("scene.loaded",
                Bus("event", "SceneChanged", new Dictionary<string, object?>
                {
                    ["scene"] = "RoomScene",
                    ["loaded"] = true,
                }),
                "14:02:11.019  SCENE  RoomScene loaded");

            yield return ("scene.unloaded",
                Bus("event", "SceneChanged", new Dictionary<string, object?>
                {
                    ["scene"] = "Boot",
                    ["loaded"] = false,
                }),
                "14:02:11.019  SCENE  Boot unloaded");

            yield return ("window.on",
                Bus("event", "WindowChanged", new Dictionary<string, object?>
                {
                    ["window"] = "Sakura",
                    ["active"] = true,
                }),
                "14:02:11.019  WINDOW  Sakura on");

            yield return ("window.off",
                Bus("event", "WindowChanged", new Dictionary<string, object?>
                {
                    ["window"] = "Day",
                    ["active"] = false,
                }),
                "14:02:11.019  WINDOW  Day off");

            yield return ("ready",
                Bus("event", "RoomReady", new Dictionary<string, object?>()),
                "14:02:11.019  READY  room ready");

            // LANGUAGE (F5) - Thai as an eighth entry in the game's own selector. The producer
            // names its own tag, primary and field order, so these need no ConsoleEventMap entry.
            yield return ("language.selected",
                Emitted(() => ConsoleEvents.Emit(
                    "language", "selected", ConsoleTags.Language,
                    "selected th (persisted as en)",
                    "via,value,saved",
                    ("via", "selector"),
                    ("value", 100),
                    ("saved", "English"))),
                "14:02:11.019  LANGUAGE  selected th (persisted as en)  via=selector value=100 saved=English");

            // B-LANG-FIX: the game's open height is untouched (338.4) and the list scrolls inside it.
            // Same call as Language/LanguageSelector.cs; the plan fields after "list" are file-only.
            yield return ("language.entry",
                Emitted(() => ConsoleEvents.Emit(
                    "language", "entry", ConsoleTags.Language,
                    "selector entry added",
                    "caption,index,pulldown,list",
                    ("caption", "ไทย"),
                    ("index", 7),
                    ("pulldown", 338.4d),
                    ("list", "scroll"),
                    ("rows", "7/8"),
                    ("viewport", 281.6d),
                    ("maxOffset", 40.3d),
                    ("listTop", 0d),
                    ("scrollbar", "settings"),
                    ("code", "th"),
                    ("value", 100))),
                "14:02:11.019  LANGUAGE  selector entry added  caption=ไทย index=7 pulldown=338.4 list=scroll");

            yield return ("language.steam",
                Emitted(() => ConsoleEvents.Emit(
                    "language", "steam", ConsoleTags.Language,
                    "steam ui language = thai",
                    "follow",
                    ("follow", false))),
                "14:02:11.019  LANGUAGE  steam ui language = thai  follow=false");

            // ---- interaction ------------------------------------------------------------------

            yield return ("click",
                Record(TraceConsoleLines.Click(At, "612", "380")),
                "14:02:11.019  CLICK  (612, 380)");

            yield return ("button",
                Record(TraceConsoleLines.Ui(At, "Button", "press", "Canvas/Home/StartButton", "Start", null)),
                "14:02:11.019  BUTTON  \"Start\"  path=Canvas/Home/StartButton control=Button event=press");

            yield return ("typed",
                Record(TraceConsoleLines.Ui(At, "TMP_InputField", "submit", "Canvas/Note/Input", null, "buy milk")),
                "14:02:11.019  TYPED  \"buy milk\"  path=Canvas/Note/Input control=TMP_InputField event=submit");

            // ---- health and Unity ---------------------------------------------------------------

            yield return ("health.spike",
                Bus("health", "spike", new Dictionary<string, object?>
                {
                    ["ms"] = 303.4d,
                    ["frame"] = 2,
                    ["cause"] = null,
                }),
                "14:02:11.019  HEALTH  spike 303.4ms  frame=2 cause=-");

            yield return ("health.lowFps",
                Bus("health", "lowFps", new Dictionary<string, object?>
                {
                    ["fpsAvg"] = 18.2d,
                    ["spikes"] = 4,
                    ["cause"] = "focus-change",
                }),
                "14:02:11.019  HEALTH  lowFps 18.2  spikes=4 cause=focus-change");

            yield return ("health.focus",
                Bus("health", "focus", new Dictionary<string, object?> { ["focused"] = false }),
                "14:02:11.019  HEALTH  focus off");

            yield return ("health.lowMemory",
                Bus("health", "lowMemory", new Dictionary<string, object?>()),
                "14:02:11.019  HEALTH  lowMemory");

            yield return ("unity.exception",
                Bus("unity", "Exception", new Dictionary<string, object?>
                {
                    ["message"] = "NullReferenceException: Object reference not set to an instance of an object",
                    ["count"] = 1,
                }),
                "14:02:11.019  UNITY  Exception \"NullReferenceException: Object reference not set to an " +
                "instance of an object\"  count=1");

            // ---- the framework's own features ---------------------------------------------------

            yield return ("data.changed",
                Emitted(() => ConsoleEvents.Emit(
                    "data", "changed", ConsoleTags.Data, "Todos changed", "reason",
                    ("section", "Todos"), ("reason", "SaveTodoList"))),
                "14:02:11.019  DATA  Todos changed  reason=SaveTodoList");

            yield return ("data.snapshot",
                Emitted(() => ConsoleEvents.Emit(
                    "data", "snapshot", ConsoleTags.Data, "snapshot written",
                    "todos,notes:notePages,habits,bytes",
                    ("todos", 12), ("notePages", 3), ("habits", 2), ("bytes", 48213))),
                "14:02:11.019  DATA  snapshot written  todos=12 notes=3 habits=2 bytes=48213");

            yield return ("data.ready",
                Emitted(() => ConsoleEvents.Emit(
                    "data", "ready", ConsoleTags.Data, "bus ready", "via,patched",
                    ("via", "RoomLifetimeScope"), ("patched", 46))),
                "14:02:11.019  DATA  bus ready  via=RoomLifetimeScope patched=46");

            yield return ("voice.registered",
                Emitted(() => ConsoleEvents.Emit(
                    "voice", "registered", ConsoleTags.Voice, "+ Voice_CWY_Test", "via,source",
                    ("via", "wav"), ("source", "cwy.example"))),
                "14:02:11.019  VOICE  + Voice_CWY_Test  via=wav source=cwy.example");

            yield return ("voice.override",
                Emitted(() => ConsoleEvents.Emit(
                    "voice", "override", ConsoleTags.Voice, "~ Voice_Tutorial_1_001", "via,source,previous",
                    ("via", "ogg"), ("source", "cwy.example"), ("previous", "game"))),
                "14:02:11.019  VOICE  ~ Voice_Tutorial_1_001  via=ogg source=cwy.example previous=game");

            yield return ("voice.refused",
                Emitted(() => ConsoleEvents.EmitAt(
                    "voice", "refused", ConsoleTags.Voice, ConsoleLevel.Warn,
                    "! Voice_CWY_Test", "source,outcome,reason",
                    ("source", "cwy.other"), ("outcome", "KeptExisting"), ("reason", "a later pack owns this name"))),
                "14:02:11.019  VOICE  ! Voice_CWY_Test  source=cwy.other outcome=KeptExisting " +
                "reason=\"a later pack owns this name\"");

            yield return ("voice.play",
                Emitted(() => ConsoleEvents.Emit(
                    "voice", "play", ConsoleTags.Voice, "play Voice_CWY_Test", "via,mouth:moveMouth,registered",
                    ("via", "RoomLifetimeScope"), ("moveMouth", true), ("registered", true))),
                "14:02:11.019  VOICE  play Voice_CWY_Test  via=RoomLifetimeScope mouth=true registered=true");

            yield return ("texture.applied",
                Emitted(() => ConsoleEvents.Emit(
                    "textures", "applied", ConsoleTags.Texture, "1 rule applied", "failed,trigger,mode,files",
                    ("applied", 1), ("failed", 0), ("trigger", "RoomScene+2s"), ("mode", "Shared"), ("files", 1))),
                "14:02:11.019  TEXTURE  1 rule applied  failed=0 trigger=RoomScene+2s mode=Shared files=1");

            yield return ("texture.reload",
                Emitted(() => ConsoleEvents.Emit(
                    "textures", "reload", ConsoleTags.Texture, "reloaded 1 changed", "applied,failed,ms,trigger",
                    ("applied", 1), ("failed", 0), ("ms", 58), ("trigger", "key"))),
                "14:02:11.019  TEXTURE  reloaded 1 changed  applied=1 failed=0 ms=58 trigger=key");

            yield return ("texture.restored",
                Emitted(() => ConsoleEvents.Emit(
                    "textures", "restored", ConsoleTags.Texture, "restored 3 of 3 slot(s)", null,
                    ("restored", 3), ("slots", 3))),
                "14:02:11.019  TEXTURE  restored 3 of 3 slot(s)");

            yield return ("packs",
                Emitted(() => ConsoleEvents.Emit(
                    "packs", "scan", ConsoleTags.Packs, "1 loaded (cwy.example@0.1.0); 0 skipped",
                    "loaded,skipped", ("loaded", 1), ("skipped", 0))),
                "14:02:11.019  PACKS  1 loaded (cwy.example@0.1.0); 0 skipped  loaded=1 skipped=0");

            // PackManager's scan line passes PackResolution.Summary, which starts "PACKS: " for plain
            // logs; the console drops the repeated tag (ConsoleEventMap.WithoutTagEcho, C5) instead
            // of printing "PACKS  PACKS: 2 loaded".
            yield return ("packs.summary",
                Emitted(() => ConsoleEvents.Emit(
                    "packs", "scan", ConsoleTags.Packs,
                    "PACKS: 2 loaded (cwy.example@0.1.0, my.thai@0.1.0); 0 skipped",
                    "loaded,skipped", ("loaded", 2), ("skipped", 0))),
                "14:02:11.019  PACKS  2 loaded (cwy.example@0.1.0, my.thai@0.1.0); 0 skipped  loaded=2 skipped=0");

            yield return ("masterdata",
                Emitted(() => ConsoleEvents.Emit(
                    "data", "masterdata", ConsoleTags.MasterData,
                    "localization +1/~0, novel +2/~0, groups +1/~0",
                    "trigger,packs,rows:localizationTotal,rejected",
                    ("trigger", "startup"), ("packs", 1), ("localizationTotal", 1357),
                    ("rejected", Array.Empty<string>()))),
                "14:02:11.019  MASTERDATA  localization +1/~0, novel +2/~0, groups +1/~0  trigger=startup " +
                "packs=1 rows=1357 rejected=-");

            // C1-FRONTDOOR / W0-CONTRACTS: the code-mod loader (MODS, ModLoader) and a mod's own
            // log line (MOD, the helper's Log facet). Path/assembly/mode/stack stay in the trace file.
            yield return ("mods.loaded",
                Emitted(() => ConsoleEvents.Emit(
                    "mods", "loaded", ConsoleTags.Mods, "cwy.sample.hello@0.1.0 loaded", "type,ms",
                    ("mod", "cwy.sample.hello"), ("type", "CwySample.Hello.ModEntry"), ("ms", 1.2d),
                    ("assembly", "CwySampleHello, Version=0.1.0.0"), ("mode", "LoadFrom"),
                    ("path", "CWYLS Mods/cwy.sample.hello/CwySampleHello.dll"))),
                "14:02:11.019  MODS  cwy.sample.hello@0.1.0 loaded  type=CwySample.Hello.ModEntry ms=1.2");

            yield return ("mod.info",
                Emitted(() => ConsoleEvents.Emit(
                    "mod", "cwy.sample.hello", ConsoleTags.Mod, "cwy.sample.hello: ready", null,
                    ("mod", "cwy.sample.hello"), ("stack", (object?)null))),
                "14:02:11.019  MOD  cwy.sample.hello: ready");

            yield return ("keys",
                Emitted(() => ConsoleEvents.Emit(
                    "keys", "table", ConsoleTags.Keys,
                    "smoke.run = Ctrl+Shift+S | F9, console.toggle = Ctrl+Shift+C | F10",
                    "count", ("count", 2))),
                "14:02:11.019  KEYS  smoke.run = Ctrl+Shift+S | F9, console.toggle = Ctrl+Shift+C | F10  count=2");

            yield return ("smoke.run",
                Emitted(() => ConsoleEvents.Emit(
                    "smoke", "run", ConsoleTags.Smoke, "ok=true",
                    "passed,mode,trigger,skipped,session,pass",
                    ("ok", true), ("passed", "46/49"), ("mode", "quiet"), ("trigger", "key"),
                    ("skipped", 3), ("session", "5ce36f09"), ("pass", null))),
                "14:02:11.019  SMOKE  ok=true  passed=46/49 mode=quiet trigger=key skipped=3 " +
                "session=5ce36f09 pass=-");

            yield return ("smoke.failed",
                Emitted(() => ConsoleEvents.EmitAt(
                    "smoke", "failed", ConsoleTags.Smoke, ConsoleLevel.Error,
                    "FAIL p1.textures.applied", "detail",
                    ("id", "p1.textures.applied"),
                    ("detail", "0/1 rule(s) on an active renderer"))),
                "14:02:11.019  SMOKE  FAIL p1.textures.applied  detail=\"0/1 rule(s) on an active renderer\"");

            yield return ("console",
                Emitted(() => ConsoleEvents.Emit(
                    "console", "ready", ConsoleTags.Console, "15 command(s)", "toggle,commands",
                    ("toggle", "Ctrl+Shift+C | F10"), ("commands", 15))),
                "14:02:11.019  CONSOLE  15 command(s)  toggle=\"Ctrl+Shift+C | F10\" commands=15");

            yield return ("setup.loader",
                Emitted(() => ConsoleEvents.Setup(
                    "loader", "CWYLS Mod Loader v0.9.0",
                    ("unity", "2022.3.62f2"), ("game", "1.17.3"))),
                "14:02:11.019  SETUP  CWYLS Mod Loader v0.9.0  src=loader unity=2022.3.62f2 game=1.17.3");

            yield return ("setup.subtitles",
                Emitted(() => ConsoleEvents.Setup(
                    "subtitles", "ready",
                    ("mode", "Native"), ("lang", "th"), ("fallback", "en,ja"))),
                "14:02:11.019  SETUP  ready  src=subtitles mode=Native lang=th fallback=en,ja");

            yield return ("trace",
                Record(TraceConsoleLines.Status(At, "patched 2626 methods across 433 classes in 4487 ms; 0 failed")),
                "14:02:11.019  TRACE  patched 2626 methods across 433 classes in 4487 ms; 0 failed");

            // ---- what the sink makes of a line that was logged directly --------------------------

            yield return ("warn",
                new ConsoleRecord(
                    At,
                    ConsoleTags.Warn,
                    "SATONE face smile timed out after 2.0s",
                    new[] { new ConsoleField("src", "game-adapter") },
                    ConsoleLevel.Warn),
                "14:02:11.019  WARN  SATONE face smile timed out after 2.0s  src=game-adapter");

            yield return ("error",
                new ConsoleRecord(
                    At,
                    ConsoleTags.Error,
                    "PATCH TARGET MISSING Bulbul.HeroineService.PlayVoice",
                    new[] { new ConsoleField("src", "mod-loader") },
                    ConsoleLevel.Error),
                "14:02:11.019  ERROR  PATCH TARGET MISSING Bulbul.HeroineService.PlayVoice  src=mod-loader");
        }

        // ---- the tests -------------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(Cases))]
        public void Every_event_kind_formats_to_its_golden_line(string name, ConsoleRecord record, string expected)
        {
            Assert.Equal(expected, ConsoleRecordFormatter.Format(record));
            Assert.DoesNotContain("\n", ConsoleRecordFormatter.Format(record));
            Assert.False(string.IsNullOrEmpty(name));
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Every_golden_line_round_trips_through_the_parser(string name, ConsoleRecord record, string expected)
        {
            Assert.True(ConsoleRecordParser.TryParse(expected, At, out var parsed), name);
            Assert.NotNull(parsed);
            Assert.Equal(record.Tag, parsed!.Tag);
            Assert.Equal(expected, ConsoleRecordFormatter.Format(parsed));
            Assert.Equal(record.Fields.Count, parsed.Fields.Count);
        }

        /// <summary>
        /// The in-game overlay has no room for a timestamp column, so it drops a fixed 14
        /// characters (<c>HH:mm:ss.fff</c> + the two-space separator) and shows the rest. If that
        /// offset ever stopped being exact, every line in the F10 console would start with a stray
        /// space or lose a letter of its tag.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void The_overlay_can_drop_the_stamp_with_a_fixed_offset(
            string name, ConsoleRecord record, string expected)
        {
            const int stampAndSeparator = 14;

            Assert.True(expected.Length > stampAndSeparator, name);
            Assert.StartsWith(record.Tag + "  ", expected.Substring(stampAndSeparator));
        }

        [Fact]
        public void The_corpus_covers_at_least_forty_lines_and_every_console_tag()
        {
            var lines = new List<string>();
            var tags = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (_, record, expected) in Corpus())
            {
                lines.Add(expected);
                tags.Add(record.Tag);
            }

            Assert.True(lines.Count >= 40, $"the corpus has {lines.Count} lines; the spec asks for at least 40");

            foreach (var tag in new[]
                     {
                         ConsoleTags.Speech, ConsoleTags.Say, ConsoleTags.State, ConsoleTags.Face,
                         ConsoleTags.Motion, ConsoleTags.Look, ConsoleTags.Restore, ConsoleTags.Subtitle,
                         ConsoleTags.Music, ConsoleTags.Pomodoro, ConsoleTags.Scenario, ConsoleTags.Scene,
                         ConsoleTags.Window, ConsoleTags.Language, ConsoleTags.Ready,
                         ConsoleTags.Button, ConsoleTags.Typed,
                         ConsoleTags.Click, ConsoleTags.Health, ConsoleTags.Unity, ConsoleTags.Data,
                         ConsoleTags.Voice, ConsoleTags.Texture, ConsoleTags.Packs, ConsoleTags.MasterData,
                         ConsoleTags.Keys, ConsoleTags.Smoke, ConsoleTags.Console, ConsoleTags.Setup,
                         ConsoleTags.Trace, ConsoleTags.Warn, ConsoleTags.Error,
                         ConsoleTags.Mods, ConsoleTags.Mod,
                     })
            {
                Assert.Contains(tag, tags);
            }
        }

        /// <summary>
        /// The corpus file <c>tools/mac/tests/console-golden.txt</c>, which <c>build.sh</c> runs
        /// <c>tools/mac/watch-log.py --strict --no-follow</c> over, must equal the corpus above -
        /// that is what keeps the Python parser and the C# grammar from drifting apart.
        /// <para>
        /// C5: the test compares and never writes, unless <c>CWY_UPDATE_GOLDEN=1</c> is set; then it
        /// regenerates the file (only when the content differs, so the file's mtime does not move on
        /// every run). A stale file fails with the command that fixes it.
        /// </para>
        /// </summary>
        [Fact]
        public void The_corpus_file_for_the_viewer_parity_check_is_current()
        {
            var root = RepoRoot();
            Assert.NotNull(root);

            var path = Path.Combine(root!, "tools", "mac", "tests", GoldenFileName);
            var expected = CorpusFileLines();

            var current = File.Exists(path) ? ReadLines(path) : null;
            if (current != null && Same(current, expected))
            {
                return;
            }

            if (UpdateRequested())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, string.Join("\n", expected) + "\n");
                return;
            }

            Assert.Fail(
                (current is null ? GoldenFileName + " is missing" : GoldenFileName + " is stale")
                + " (" + path + "). Regenerate it with: " + UpdateEnvVar + "=1 dotnet test src/CwyLs.ModLoader.Tests"
                + " - then check the diff and run build.sh, whose watch-log.py --strict pass must still parse it.");
        }

        [Fact]
        public void The_update_switch_is_off_unless_explicitly_one()
        {
            Assert.True(IsUpdateValue("1"));
            Assert.False(IsUpdateValue(null));
            Assert.False(IsUpdateValue(string.Empty));
            Assert.False(IsUpdateValue("0"));
            Assert.False(IsUpdateValue("true"));
        }

        /// <summary>Name of the corpus file under <c>tools/mac/tests</c>.</summary>
        private const string GoldenFileName = "console-golden.txt";

        /// <summary>Environment variable that lets the test regenerate the corpus file.</summary>
        private const string UpdateEnvVar = "CWY_UPDATE_GOLDEN";

        /// <summary>The file's lines: a three-line header, then one golden line per case.</summary>
        private static List<string> CorpusFileLines()
        {
            var lines = new List<string>
            {
                "# Golden console corpus, generated by LoggingConsoleGoldenTests.",
                "# One line per event kind, in the grammar tools/mac/watch-log.py must parse.",
                "# Regenerate: " + UpdateEnvVar + "=1 DOTNET_ROOT=... dotnet test src/CwyLs.ModLoader.Tests",
            };

            foreach (var (_, _, expected) in Corpus())
            {
                lines.Add(expected);
            }

            return lines;
        }

        /// <summary>Reads the file's lines, whatever the line ending, ignoring a trailing newline.</summary>
        private static List<string> ReadLines(string path)
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            var lines = new List<string>(text.Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        private static bool Same(IReadOnlyList<string> a, IReadOnlyList<string> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (var i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool UpdateRequested() => IsUpdateValue(Environment.GetEnvironmentVariable(UpdateEnvVar));

        private static bool IsUpdateValue(string? value) => string.Equals(value, "1", StringComparison.Ordinal);

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>Maps a bus event through the real routing table and pins its stamp.</summary>
        private static ConsoleRecord Bus(string sys, string name, IReadOnlyDictionary<string, object?> payload)
        {
            Assert.True(
                ConsoleEventMap.TryMap(new BusEvent(sys, name, payload, At), out var record),
                sys + "/" + name + " should have a console line");

            return record!;
        }

        /// <summary>
        /// Runs a producer call, catches the one event it publishes, maps it, and pins the stamp so
        /// the golden string is stable. Exercises the real producer helper, the real routing table
        /// and the real formatter.
        /// </summary>
        private static ConsoleRecord Emitted(Action publish)
        {
            ConsoleRecord? seen = null;
            var count = 0;

            TraceBus.Reset();
            using (TraceBus.Subscribe(evt =>
                   {
                       count++;
                       if (ConsoleEventMap.TryMap(evt, out var record))
                       {
                           seen = record;
                       }
                   }))
            {
                publish();
            }

            TraceBus.Reset();

            Assert.Equal(1, count);
            Assert.NotNull(seen);
            return Record(seen!);
        }

        private static ConsoleRecord Record(ConsoleRecord source) =>
            new ConsoleRecord(At, source.Tag, source.Primary, source.Fields, source.Level);

        private static Dictionary<string, object?> Speech(
            string? text = "I made you some tea.",
            string? textJa = "お茶を淹れたよ。",
            string? voice = "Voice_Tutorial_1_001",
            string? id = "Tutorial_1_001",
            string? source = "Tutorial",
            object? duration = null,
            string attribution = "row") =>
            new Dictionary<string, object?>
            {
                ["voice"] = voice,
                ["text"] = text,
                ["textJa"] = textJa,
                ["localizationId"] = id,
                ["noTextInMaster"] = id is null,
                ["language"] = "English",
                ["isStory"] = false,
                ["scenarioType"] = "Tutorial",
                ["scenarioGroupId"] = "tutorial_1",
                ["source"] = source,
                ["moveMouth"] = true,
                ["durationSec"] = duration ?? 2.25d,
                ["attribution"] = attribution,
            };

        /// <summary>The repository root (<see cref="TestRepo"/>).</summary>
        private static string? RepoRoot() => TestRepo.Root;
    }
}
