using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B2: the registry that turns pack declarations into runtime language values, and the text
    /// resolution that serves a pack language through the game's text seam. Two languages coexist
    /// throughout: Thai from one pack and Vietnamese from a second pack.
    /// </summary>
    public class LanguageRegistryTests
    {
        private static LanguageDeclaration Thai(string pack = "my.thai") =>
            new LanguageDeclaration(pack, "th", "ไทย", "Thai", "thai", new[] { "en" }, new[] { "Base_TH", "Dynamic_TH" }, "fonts/thai-marks.json");

        private static LanguageDeclaration Vietnamese(params string[] fallback) =>
            new LanguageDeclaration("fake.vi", "vi", "Tiếng Việt", "Vietnamese", fallback: fallback.Length == 0 ? null : fallback);

        private static LanguageRegistry Both(params string[] vietnameseFallback)
        {
            var registry = new LanguageRegistry();
            Assert.True(registry.Register(Thai(), out var first), first);
            Assert.True(registry.Register(Vietnamese(vietnameseFallback), out var second), second);
            return registry;
        }

        [Fact]
        public void Two_languages_get_consecutive_values_in_pack_order()
        {
            var registry = Both();

            Assert.Equal(2, registry.Count);
            Assert.Equal(new[] { 100, 101 }, registry.Languages.Select(l => l.Value));
            Assert.Equal(new[] { "th", "vi" }, registry.Languages.Select(l => l.Code));
            Assert.True(registry.IsPackLanguage(100));
            Assert.True(registry.IsPackLanguage(101));
            Assert.False(registry.IsPackLanguage(102));
            Assert.False(registry.IsPackLanguage(GameLanguages.English));
        }

        [Fact]
        public void Lookups_work_by_value_and_by_any_spelling_of_the_code()
        {
            var registry = Both();

            Assert.True(registry.TryGet(101, out var byValue));
            Assert.Equal("vi", byValue!.Code);
            Assert.True(registry.TryGet("THAI", out var byCode));
            Assert.Equal(100, byCode!.Value);
            Assert.True(registry.TryFromCode("en", out var english));
            Assert.Equal(GameLanguages.English, english);
            Assert.True(registry.TryFromCode("vi", out var vietnamese));
            Assert.Equal(101, vietnamese);
            Assert.False(registry.TryFromCode("xx", out _));
        }

        [Fact]
        public void Names_and_codes_cover_pack_and_game_values()
        {
            var registry = Both();

            Assert.Equal("th", registry.ToCode(100));
            Assert.Equal("ja", registry.ToCode(GameLanguages.Japanese));
            Assert.Equal(string.Empty, registry.ToCode(150));
            Assert.Equal("Vietnamese", registry.ToName(101));
            Assert.Equal("Korean", registry.ToName(GameLanguages.Korean));
        }

        [Fact]
        public void The_first_pack_to_declare_a_code_keeps_it()
        {
            var registry = new LanguageRegistry();
            Assert.True(registry.Register(Thai("my.thai"), out _));

            Assert.False(registry.Register(Thai("other.thai"), out var reason));
            Assert.Contains("already declared by my.thai", reason);
            Assert.Single(registry.Rejected);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void A_game_language_cannot_be_declared_again()
        {
            var registry = new LanguageRegistry();

            Assert.False(registry.Register(new LanguageDeclaration("x.pack", "en", "English", "English"), out var reason));
            Assert.Contains("one of the game's own languages", reason);
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void A_file_that_did_not_parse_is_recorded_as_rejected()
        {
            var registry = new LanguageRegistry();
            registry.Reject("bad.pack/language.json: the file is empty");

            Assert.Equal(new[] { "bad.pack/language.json: the file is empty" }, registry.Rejected);
        }

        [Fact]
        public void Each_language_s_base_is_the_first_game_language_of_its_chain()
        {
            var registry = Both("ko", "en");

            registry.TryGet("th", out var thai);
            registry.TryGet("vi", out var vietnamese);

            Assert.Equal(GameLanguages.English, thai!.BaseValue);
            Assert.Equal(GameLanguages.Korean, vietnamese!.BaseValue);
        }

        [Fact]
        public void A_chain_through_another_pack_language_is_expanded_and_ends_in_a_game_language()
        {
            // vi -> th -> en: a Vietnamese row missing falls back to the Thai table, then English.
            var registry = Both("th");
            registry.TryGet("vi", out var vietnamese);

            Assert.Equal(new[] { "vi", "th", "en" }, vietnamese!.Chain.Select(s => s.Code));
            Assert.Equal(new[] { false, false, true }, vietnamese.Chain.Select(s => s.IsBuiltIn));
            Assert.Equal(GameLanguages.English, vietnamese.BaseValue);
        }

        [Fact]
        public void A_chain_that_names_no_game_language_ends_in_english()
        {
            var registry = new LanguageRegistry();
            registry.Register(new LanguageDeclaration("a.pack", "aa", "A", "A", fallback: new[] { "bb" }), out _);
            registry.Register(new LanguageDeclaration("b.pack", "bb", "B", "B", fallback: new[] { "aa" }), out _);
            registry.TryGet("aa", out var a);

            // aa -> bb -> (aa already visited) -> English appended; no infinite loop.
            Assert.Equal(new[] { "aa", "bb", "en" }, a!.Chain.Select(s => s.Code));
            Assert.Equal(GameLanguages.English, a.BaseValue);
        }

        [Fact]
        public void A_fallback_code_nobody_declared_is_skipped()
        {
            var registry = new LanguageRegistry();
            registry.Register(new LanguageDeclaration("a.pack", "aa", "A", "A", fallback: new[] { "zz", "ja" }), out _);
            registry.TryGet("aa", out var a);

            Assert.Equal(new[] { "aa", "ja" }, a!.Chain.Select(s => s.Code));
        }

        [Theory]
        [InlineData(100, GameLanguages.English)]
        [InlineData(101, GameLanguages.Japanese)]
        [InlineData(177, GameLanguages.English)]
        [InlineData(GameLanguages.Korean, GameLanguages.Korean)]
        [InlineData(GameLanguages.None, GameLanguages.None)]
        public void The_save_value_is_never_a_pack_value(int inMemory, int saved)
        {
            var registry = Both("ja");

            Assert.Equal(saved, registry.SaveValueFor(inMemory));
            Assert.False(GameLanguages.IsPackRange(registry.SaveValueFor(inMemory)));
        }

        [Fact]
        public void The_line_break_helper_follows_the_declared_script()
        {
            var registry = Both();

            Assert.Equal(LineBreakHelpers.Thai, registry.Languages[0].LineBreakHelper);
            Assert.Equal(string.Empty, registry.Languages[1].LineBreakHelper);
        }

        [Fact]
        public void Text_comes_from_the_language_s_own_table_first()
        {
            var registry = Both();
            var tables = new Table().Add("line_1", "th", "สวัสดี").Add("line_1", "vi", "Xin chào");

            var thai = LanguageText.Resolve(registry.Languages[0], "line_1", tables, Column);
            var vietnamese = LanguageText.Resolve(registry.Languages[1], "line_1", tables, Column);

            Assert.Equal("สวัสดี", thai.Text);
            Assert.True(thai.FromPack);
            Assert.Equal("th", thai.Code);
            Assert.Equal("Xin chào", vietnamese.Text);
            Assert.Equal("pack:vi", vietnamese.ToString());
        }

        [Fact]
        public void A_missing_row_falls_back_to_the_base_language_column_not_japanese()
        {
            var registry = Both();

            var result = LanguageText.Resolve(registry.Languages[0], "line_2", new Table(), Column);

            Assert.Equal("en column", result.Text);
            Assert.False(result.FromPack);
            Assert.Equal("game:en", result.ToString());
        }

        [Fact]
        public void A_missing_row_walks_the_chain_through_another_pack_language()
        {
            var registry = Both("th", "ja");
            var tables = new Table().Add("line_3", "th", "บรรทัดสาม");

            var result = LanguageText.Resolve(registry.Languages[1], "line_3", tables, Column);

            Assert.Equal("บรรทัดสาม", result.Text);
            Assert.Equal("th", result.Code);
        }

        [Fact]
        public void A_blank_game_column_moves_on_to_the_next_step()
        {
            var registry = Both("ko", "en");
            string? Blanks(int value) => value == GameLanguages.Korean ? string.Empty : Column(value);

            var result = LanguageText.Resolve(registry.Languages[1], "line_4", new Table(), Blanks);

            Assert.Equal("en column", result.Text);
        }

        [Fact]
        public void Nothing_found_leaves_the_game_s_answer()
        {
            var registry = Both();

            var result = LanguageText.Resolve(registry.Languages[0], "line_5", null, _ => null);

            Assert.False(result.Found);
            Assert.Equal("none", result.ToString());
            Assert.False(LanguageText.Resolve(registry.Languages[0], null, new Table(), Column).Found);
        }

        /// <summary>The game's column for a built-in value, as the adapter reads it.</summary>
        private static string? Column(int value) => LanguageCodes.ToCode(value) + " column";

        private sealed class Table : ISubtitleSource
        {
            private readonly Dictionary<(string, string), string> _rows = new Dictionary<(string, string), string>();

            public Table Add(string id, string lang, string text)
            {
                _rows[(id, lang)] = text;
                return this;
            }

            public bool TryGet(string localizationId, string lang, out string text)
            {
                if (_rows.TryGetValue((localizationId, lang), out var found))
                {
                    text = found;
                    return true;
                }

                text = string.Empty;
                return false;
            }
        }
    }
}
