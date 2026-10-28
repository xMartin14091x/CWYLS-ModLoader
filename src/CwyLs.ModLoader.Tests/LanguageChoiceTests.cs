using CwyLs.ModLoader.Core.Language;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B2 (was F5): the selected/applied/persisted state machine behind "a pack language is a
    /// language you can pick". The one rule every case is really about: the save file never holds a
    /// pack value, because a launch without this framework would then throw in every localized
    /// label's <c>Awake</c>.
    /// </summary>
    public class LanguageChoiceTests
    {
        private const int Thai = GameLanguages.FirstPackValue;
        private const int Vietnamese = GameLanguages.FirstPackValue + 1;

        /// <summary>Thai (base English) and a fake Vietnamese declared with a Japanese base.</summary>
        private static LanguageRegistry TwoLanguages()
        {
            var registry = new LanguageRegistry();
            Assert.True(registry.Register(new LanguageDeclaration("my.thai", "th", "ไทย", "Thai", "thai", new[] { "en" }), out _));
            Assert.True(registry.Register(new LanguageDeclaration("fake.vi", "vi", "Tiếng Việt", "Vietnamese", fallback: new[] { "ja" }), out _));
            return registry;
        }

        [Fact]
        public void A_fresh_choice_follows_the_game()
        {
            var choice = new LanguageChoice(TwoLanguages());

            Assert.Equal(string.Empty, choice.Selected);
            Assert.False(choice.IsPackSelected);
            Assert.True(choice.Active);
        }

        [Theory]
        [InlineData("th", Thai)]
        [InlineData("TH", Thai)]
        [InlineData("tha", Thai)]
        [InlineData(" Thai ", Thai)]
        [InlineData("vi", Vietnamese)]
        public void A_declared_code_in_the_config_selects_that_language(string configured, int value)
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: configured);

            Assert.True(choice.IsPackSelected);
            Assert.Equal(value, choice.SelectedLanguage!.Value);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("en")]
        [InlineData("ja-JP")]
        public void Blank_or_a_game_language_means_follow_the_game(string? configured)
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: configured);

            Assert.False(choice.IsPackSelected);
            Assert.Equal(string.Empty, choice.Selected);
        }

        [Fact]
        public void A_code_no_loaded_pack_declares_is_kept_but_not_applied()
        {
            // The pack failed to load this session: the player's choice must survive it.
            var choice = new LanguageChoice(TwoLanguages(), selected: "klingon");

            Assert.Equal("klingon", choice.Selected);
            Assert.False(choice.IsPackSelected);
            Assert.Equal(GameLanguages.English, choice.Adopt(GameLanguages.English));
        }

        [Fact]
        public void The_master_switch_stops_a_pack_language_being_applied()
        {
            var choice = new LanguageChoice(TwoLanguages(), enabled: false, selected: "th");

            Assert.False(choice.Active);
            Assert.False(choice.IsPackSelected);
            Assert.Contains("(disabled)", choice.ToString());
        }

        [Fact]
        public void A_missing_patch_target_stops_a_pack_language_being_applied()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "th", targetsVerified: false);

            Assert.False(choice.IsPackSelected);
            Assert.Contains("(patch target missing)", choice.ToString());
        }

        [Fact]
        public void Adopt_applies_the_selected_language_over_whatever_the_save_held()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "vi");

            Assert.Equal(Vietnamese, choice.Adopt(GameLanguages.English));
            Assert.Equal(Vietnamese, choice.Applied);
        }

        [Fact]
        public void Adopt_leaves_the_saved_language_alone_when_nothing_is_selected()
        {
            Assert.Equal(GameLanguages.Korean, new LanguageChoice(TwoLanguages()).Adopt(GameLanguages.Korean));
        }

        [Theory]
        [InlineData(Thai, GameLanguages.English)]
        [InlineData(Vietnamese, GameLanguages.Japanese)]
        [InlineData(150, GameLanguages.English)]
        public void Adopt_rescues_a_save_that_somehow_holds_a_pack_value(int saved, int rescued)
        {
            // Belt and braces for the one state that breaks a vanilla launch: if a pack value ever
            // reached the file, a run that will not apply it must not load it either.
            var choice = new LanguageChoice(TwoLanguages(), enabled: false);

            Assert.Equal(rescued, choice.Adopt(saved));
        }

        [Theory]
        [InlineData(Thai, GameLanguages.English)]
        [InlineData(Vietnamese, GameLanguages.Japanese)]
        public void ForSave_writes_each_language_s_own_base(int inMemory, int onDisk)
        {
            Assert.Equal(onDisk, new LanguageChoice(TwoLanguages()).ForSave(inMemory));
        }

        [Theory]
        [InlineData(GameLanguages.None)]
        [InlineData(GameLanguages.Japanese)]
        [InlineData(GameLanguages.English)]
        [InlineData(GameLanguages.Russian)]
        public void ForSave_never_touches_a_language_the_game_has(int value)
        {
            Assert.Equal(value, new LanguageChoice(TwoLanguages()).ForSave(value));
        }

        [Fact]
        public void Persisted_is_the_base_language_while_a_pack_language_is_applied()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "th");
            choice.Adopt(GameLanguages.English);

            Assert.Equal(Thai, choice.Applied);
            Assert.Equal(GameLanguages.English, choice.Persisted);
        }

        [Fact]
        public void Observing_a_pack_language_records_its_code()
        {
            var choice = new LanguageChoice(TwoLanguages());

            Assert.True(choice.Observe(Vietnamese));
            Assert.Equal("vi", choice.Selected);
            Assert.True(choice.IsPackSelected);
        }

        [Fact]
        public void Observing_the_same_language_twice_does_not_ask_for_a_second_write()
        {
            var choice = new LanguageChoice(TwoLanguages());

            Assert.True(choice.Observe(Thai));
            Assert.False(choice.Observe(Thai));
        }

        [Fact]
        public void Switching_between_two_pack_languages_rewrites_the_choice()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "th");

            Assert.True(choice.Observe(Vietnamese));
            Assert.Equal("vi", choice.Selected);
            Assert.Equal(GameLanguages.Japanese, choice.Persisted);
        }

        [Fact]
        public void Picking_a_game_language_again_clears_the_choice()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "th");

            Assert.True(choice.Observe(GameLanguages.Japanese));
            Assert.Equal(string.Empty, choice.Selected);
            Assert.False(choice.IsPackSelected);
            Assert.Equal(GameLanguages.Japanese, choice.Persisted);
        }

        [Fact]
        public void ToString_says_selected_applied_and_persisted()
        {
            var choice = new LanguageChoice(TwoLanguages(), selected: "th");
            choice.Adopt(GameLanguages.English);

            Assert.Equal("selected=th applied=Thai persisted=English", choice.ToString());
        }
    }
}
