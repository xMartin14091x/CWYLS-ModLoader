using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceFilterTests
    {
        private static TraceFilter Filter(TraceLevel level = TraceLevel.Trace, string? include = null, string? exclude = null) =>
            new TraceFilter(new TraceConfig { Level = level, IncludeRegex = include, ExcludeRegex = exclude });

        [Theory]
        // Suffix rule, in each included namespace.
        [InlineData("Bulbul", "HeroineService", "PlayVoice", true)]
        [InlineData("Bulbul", "ScenarioReader", "StartReady", true)]
        [InlineData("Bulbul.Mobile", "SettingController", "Apply", true)]
        [InlineData("Bulbul.Web", "WebApiHandler", "Send", true)]
        [InlineData("NestopiSystem", "SaveLoader", "Load", true)]
        [InlineData("NestopiSystem.Steam", "SteamManager", "Init", true)]
        [InlineData("KanKikuchi.AudioManager", "VoiceManager", "Play", true)]
        [InlineData("", "MusicService", "PlayNextMusic", true)]
        // Music surface added by the 13:35 addendum.
        [InlineData("Bulbul", "MusicPlayListTabUI", "Refresh", true)]
        [InlineData("Bulbul", "GameAudioInfo", "ImportLocalFiles", true)]
        [InlineData("Bulbul", "MusicImportButton", "OnClick", true)]
        // Namespace not on the list.
        [InlineData("Bulbul.MasterData", "NovelService", "Load", false)]
        [InlineData("UnityEngine", "AudioManager", "Play", false)]
        // Class name carries no system suffix and is not allow-listed.
        [InlineData("Bulbul", "NovelData", "Read", false)]
        public void Namespace_and_class_rules(string ns, string cls, string method, bool expected)
        {
            Assert.Equal(expected, Filter().ShouldPatch(ns, cls, method, isPublic: true, isStatic: false));
        }

        [Theory]
        [InlineData("Update")]
        [InlineData("LateUpdate")]
        [InlineData("FixedUpdate")]
        [InlineData("OnGUI")]
        [InlineData("Tick")]
        [InlineData("OnAnimatorIK")]
        [InlineData("OnAnimatorMove")]
        [InlineData("OnRenderObject")]
        [InlineData("OnWillRenderObject")]
        public void Per_frame_methods_are_never_patched(string method)
        {
            // A prefix on a per-frame method is the one thing that would actually cost frame time.
            Assert.False(Filter().ShouldPatch("Bulbul", "HeroineService", method, isPublic: true, isStatic: false));
        }

        [Fact]
        public void The_web_api_surface_is_allow_listed_by_name()
        {
            var filter = Filter();

            // Everything in Bulbul.Web is a DTO; the two classes that actually make the calls
            // carry no system suffix, so they are named explicitly.
            Assert.True(filter.ShouldPatch("Bulbul", "WebApiPulser", "Pulse", true, false));
            Assert.True(filter.ShouldPatch("Bulbul", "WebApiErrorBehavior", "Handle", true, false));
        }

        [Theory]
        // Suffixes added for the interaction surface.
        [InlineData("TodoTaskListItemView", "OnClick")]
        [InlineData("CommonDialog", "Show")]
        [InlineData("LongPressButton", "Press")]
        [InlineData("MusicPlayListButtons", "Refresh")]
        [InlineData("PurchasePopover", "Open")]
        [InlineData("ToggleSwitch", "Apply")]
        [InlineData("SwipeHelper", "Begin")]
        [InlineData("NoteContentInputFieldsView", "Commit")]
        [InlineData("ScrollHandle", "Move")]
        [InlineData("InputForwarder", "Send")]
        [InlineData("ClickTrigger", "Fire")]
        [InlineData("BirthdayField", "Set")]
        public void Interaction_class_name_suffixes_are_traced(string cls, string method)
        {
            Assert.True(Filter().ShouldPatch("Bulbul", cls, method, isPublic: true, isStatic: false));
        }

        [Theory]
        [InlineData("FacilityClickHeroine", "OnClickHeroine")]
        [InlineData("FacilityCalendar", "Open")]
        public void Facility_classes_are_traced_by_prefix(string cls, string method)
        {
            // FacilityClickHeroine is the click-on-Satone handler, the one the whole mod is about.
            Assert.True(Filter().ShouldPatch("", cls, method, isPublic: true, isStatic: false));
        }

        [Fact]
        public void A_class_flagged_as_an_interaction_handler_is_traced_whatever_it_is_called()
        {
            var filter = Filter();

            // The plugin sets the flag after seeing IPointerClickHandler or an OnPointer* method;
            // without it the same class has no traceable name at all.
            Assert.False(filter.ShouldPatch(
                MethodCandidate.Ordinary("Bulbul", "HeroineTapArea", "React", isInteractionClass: false)));
            Assert.True(filter.ShouldPatch(
                MethodCandidate.Ordinary("Bulbul", "HeroineTapArea", "React", isInteractionClass: true)));
        }

        [Theory]
        [InlineData("OnPointerClick", true)]
        [InlineData("OnPointerDown", true)]
        [InlineData("OnClick", true)]
        [InlineData("OnClickButton", true)]
        [InlineData("OnSubmit", true)]
        [InlineData("OnValueChanged", true)]
        [InlineData("OnEndEdit", true)]
        [InlineData("OnBeginDrag", true)]
        [InlineData("OnDrag", true)]
        [InlineData("OnEndDrag", true)]
        [InlineData("OnSelect", true)]
        [InlineData("OnDeselect", true)]
        [InlineData("OnDestroy", false)]
        [InlineData("Update", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Interaction_method_names_are_recognised(string? method, bool expected)
        {
            Assert.Equal(expected, TraceFilter.IsInteractionMethodName(method));
        }

        [Theory]
        [InlineData("IPointerClickHandler", true)]
        [InlineData("IDragHandler", true)]
        [InlineData("IScrollHandler", true)]
        [InlineData("IDeselectHandler", true)]
        [InlineData("IDisposable", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Interaction_interface_names_are_recognised(string? contract, bool expected)
        {
            Assert.Equal(expected, TraceFilter.IsInteractionInterfaceName(contract));
        }

        [Fact]
        public void The_interaction_interface_list_is_published_for_the_plugin()
        {
            // Core stays Unity-free: it publishes the names and the plugin does the reflection.
            Assert.Equal(12, TraceFilter.InteractionInterfaces.Count);
            Assert.Contains("IPointerClickHandler", TraceFilter.InteractionInterfaces);
        }

        [Fact]
        public void Clicking_Satone_is_a_curated_event()
        {
            Assert.True(CuratedEvents.IsCurated("FacilityClickHeroine", "OnClickHeroine"));
            Assert.True(CuratedEvents.IsCurated("HeroineAI", "StartHeroineClickReaction"));
            Assert.True(CuratedEvents.IsCurated("HeroineAI", "FinishHeroineClickReaction"));
            Assert.True(CuratedEvents.IsCurated("Input", "click"));
        }

        [Fact]
        public void Deconstruct_is_treated_as_a_language_protocol_method()
        {
            // Its out parameters defeat Harmony's argument capture, and it carries no game meaning.
            Assert.False(Filter().ShouldPatch("Bulbul", "ViewModel", "Deconstruct", true, false));
        }

        [Theory]
        [InlineData("Equals")]
        [InlineData("GetHashCode")]
        [InlineData("ToString")]
        [InlineData("GetType")]
        [InlineData("Finalize")]
        [InlineData("MemberwiseClone")]
        public void Object_protocol_methods_are_never_patched(string method)
        {
            Assert.False(Filter().ShouldPatch("Bulbul", "HeroineService", method, isPublic: true, isStatic: false));
        }

        [Theory]
        [InlineData("get_PlayingMusic")]
        [InlineData("set_PlayingMusic")]
        [InlineData("add_OnChangeMusic")]
        [InlineData("remove_OnChangeMusic")]
        [InlineData("op_Equality")]
        [InlineData(".ctor")]
        [InlineData(".cctor")]
        public void Accessors_operators_and_constructors_are_never_patched(string method)
        {
            Assert.False(Filter().ShouldPatch("", "MusicService", method, isPublic: true, isStatic: false));
        }

        [Theory]
        [InlineData("MusicService", "<PlayNextMusic>b__12_0")]
        [InlineData("<PlayNextMusic>d__27", "MoveNext")]
        [InlineData("PlayNextMusicd__27", "MoveNext")]
        public void Compiler_generated_members_are_never_patched(string cls, string method)
        {
            Assert.False(Filter().ShouldPatch("", cls, method, isPublic: true, isStatic: false));
        }

        [Fact]
        public void Private_methods_need_the_allow_list()
        {
            var filter = Filter();

            Assert.False(filter.ShouldPatch("", "HeroineAI", "DecideNextAction", isPublic: false, isStatic: false));
            Assert.True(filter.ShouldPatch("", "HeroineAI", "ChangeState", isPublic: false, isStatic: false));
            Assert.True(filter.ShouldPatch("Bulbul", "ScenarioReader", "CommandVoiceText", isPublic: false, isStatic: false));
            Assert.True(filter.ShouldPatch("Bulbul", "ScenarioReader", "CommandPomodoroVoiceText", isPublic: false, isStatic: false));
            Assert.True(filter.ShouldPatch("KanKikuchi.AudioManager", "AudioManager", "GetAudioClip", isPublic: false, isStatic: false));
        }

        [Fact]
        public void Public_static_methods_are_patched()
        {
            Assert.True(Filter().ShouldPatch("Bulbul", "GameAudioInfo", "ImportLocalFiles", isPublic: true, isStatic: true));
        }

        [Theory]
        [InlineData(true, false, true, false, false)]   // abstract
        [InlineData(false, true, true, false, false)]   // open generic
        [InlineData(false, false, false, false, false)] // no body (extern / InternalCall)
        [InlineData(false, false, true, true, false)]   // declared on an interface
        [InlineData(false, false, true, false, true)]   // ordinary: patched
        public void Unpatchable_shapes_are_refused(bool isAbstract, bool isGeneric, bool hasBody, bool isInterface, bool expected)
        {
            var candidate = new MethodCandidate(
                "Bulbul", "HeroineService", "PlayVoice",
                isPublic: true, isStatic: false,
                isAbstract: isAbstract, isGenericDefinition: isGeneric, hasBody: hasBody, isDeclaringTypeInterface: isInterface);

            Assert.Equal(expected, Filter().ShouldPatch(candidate));
        }

        [Fact]
        public void Level_Off_patches_nothing()
        {
            Assert.False(Filter(TraceLevel.Off).ShouldPatch("Bulbul", "HeroineService", "PlayVoice", true, false));
        }

        [Fact]
        public void Level_Events_patches_only_curated_calls()
        {
            var filter = Filter(TraceLevel.Events);

            Assert.True(filter.ShouldPatch("Bulbul", "HeroineService", "PlayVoice", true, false));
            Assert.True(filter.ShouldPatch("Bulbul", "SaveDataManager", "SaveAll", true, false));
            Assert.True(filter.ShouldPatch("", "MusicService", "PlayNextMusic", true, false));
            Assert.False(filter.ShouldPatch("Bulbul", "HeroineService", "ChangeHeroineFacialAnimation", true, false));
            Assert.False(filter.ShouldPatch("Bulbul", "SaveDataManager", "DeleteAll", true, false));
        }

        [Fact]
        public void User_include_regex_is_applied_last_on_Class_Method()
        {
            var filter = Filter(include: "^Music");

            Assert.True(filter.ShouldPatch("", "MusicService", "PlayNextMusic", true, false));
            Assert.False(filter.ShouldPatch("Bulbul", "HeroineService", "PlayVoice", true, false));
            // Even an allow-listed private method loses to the user's include regex.
            Assert.False(filter.ShouldPatch("", "HeroineAI", "ChangeState", isPublic: false, isStatic: false));
        }

        [Fact]
        public void User_exclude_regex_is_applied_last_on_Class_Method()
        {
            var filter = Filter(exclude: @"\.Play");

            Assert.False(filter.ShouldPatch("Bulbul", "HeroineService", "PlayVoice", true, false));
            Assert.True(filter.ShouldPatch("Bulbul", "HeroineService", "ChangeAnimation", true, false));
        }

        [Fact]
        public void An_invalid_user_regex_is_ignored_rather_than_fatal()
        {
            // A typo in BepInEx.cfg must not stop the trace from starting.
            var filter = Filter(include: "([unclosed");

            Assert.True(filter.ShouldPatch("Bulbul", "HeroineService", "PlayVoice", true, false));
        }

        [Fact]
        public void Curated_list_matches_the_events_the_ticket_asks_for()
        {
            Assert.True(CuratedEvents.IsCurated("HeroineAI", "ChangeState"));
            Assert.True(CuratedEvents.IsCurated("ScenarioReader", "StartReady"));
            Assert.True(CuratedEvents.IsCurated("ScenarioReader", "EndStory"));
            Assert.True(CuratedEvents.IsCurated("HeroineService", "PlayVoice"));
            Assert.True(CuratedEvents.IsCurated("PomodoroService", "CompletePomodoroTimer"));
            Assert.True(CuratedEvents.IsCurated("SaveDataManager", "SavePomodoroData"));
            Assert.True(CuratedEvents.IsCurated("SaveDataManager", "LoadPlayerData"));
            Assert.True(CuratedEvents.IsCurated("WindowViewService", "ActivateWindow"));
            Assert.True(CuratedEvents.IsCurated("VoiceManager", "Play"));
            Assert.True(CuratedEvents.IsCurated("MusicManager", "Play"));
            Assert.True(CuratedEvents.IsCurated("MusicService", "SkipCurrentMusic"));
            Assert.True(CuratedEvents.IsCurated("ScenarioMusicManager", "Play"));
            Assert.True(CuratedEvents.IsCurated("AmbientBGMManager", "Play"));
            Assert.True(CuratedEvents.IsCurated("BGMManager", "Play"));
            Assert.True(CuratedEvents.IsCurated("Scene", "sceneLoaded"));

            Assert.False(CuratedEvents.IsCurated("SaveDataManager", "DeleteAll"));
            Assert.False(CuratedEvents.IsCurated("HeroineService", "ChangeAnimation"));
            Assert.False(CuratedEvents.IsCurated(null, "Play"));
        }
    }
}
