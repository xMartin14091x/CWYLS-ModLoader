using System;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// B2: the <c>language.json</c> schema, error message by error message.
    /// </summary>
    public partial class LanguageDeclarationParserTests
    {
        private const string Minimal = "{\"code\":\"th\",\"caption\":\"ไทย\",\"englishName\":\"Thai\"}";

        [Fact]
        public void Parses_a_minimal_declaration_with_defaults()
        {
            var declaration = LanguageDeclarationParser.Parse(Minimal, "my.thai").Single();

            Assert.Equal("my.thai", declaration.PackId);
            Assert.Equal("th", declaration.Code);
            Assert.Equal("ไทย", declaration.Caption);
            Assert.Equal("Thai", declaration.EnglishName);
            Assert.Equal(string.Empty, declaration.Script);
            Assert.Equal(new[] { "en" }, declaration.Fallback);
            Assert.Empty(declaration.FontAssets);
            Assert.Null(declaration.MarksPath);
            Assert.Equal(LineBreakHelpers.Auto, declaration.LineBreaks);
            Assert.Equal(LanguageDeclarationParser.FileName, declaration.SourcePath);
        }

        [Fact]
        public void Parses_a_full_declaration()
        {
            var json = "{\"schema\":1,\"code\":\"TH\",\"caption\":\" ไทย \",\"englishName\":\"Thai\",\"script\":\"Thai\","
                + "\"fallback\":[\"en\",\"ja\"],\"font\":{\"gameAssets\":[\"Base_TH\",\"Dynamic_TH\"]},"
                + "\"marks\":\"./fonts\\\\thai-marks.json\",\"lineBreaks\":\"THAI\",\"x-note\":\"ignored\"}";

            var declaration = LanguageDeclarationParser.Parse(json, "my.thai").Single();

            Assert.Equal("th", declaration.Code);
            Assert.Equal("ไทย", declaration.Caption);
            Assert.Equal("thai", declaration.Script);
            Assert.Equal(new[] { "en", "ja" }, declaration.Fallback);
            Assert.Equal(new[] { "Base_TH", "Dynamic_TH" }, declaration.FontAssets);
            Assert.Equal("fonts/thai-marks.json", declaration.MarksPath);
            Assert.Equal("thai", declaration.LineBreaks);
        }

        [Fact]
        public void A_single_fallback_and_a_single_font_may_be_plain_strings()
        {
            var json = "{\"code\":\"vi\",\"caption\":\"Tiếng Việt\",\"englishName\":\"Vietnamese\","
                + "\"fallback\":\"ja\",\"font\":{\"gameAssets\":\"Base_JP\"}}";

            var declaration = LanguageDeclarationParser.Parse(json, "fake.vi").Single();

            Assert.Equal(new[] { "ja" }, declaration.Fallback);
            Assert.Equal(new[] { "Base_JP" }, declaration.FontAssets);
        }

        [Fact]
        public void An_array_declares_several_languages()
        {
            var json = "[" + Minimal + ",{\"code\":\"vi\",\"caption\":\"Tiếng Việt\",\"englishName\":\"Vietnamese\"}]";

            var declarations = LanguageDeclarationParser.Parse(json, "multi.pack");

            Assert.Equal(new[] { "th", "vi" }, declarations.Select(d => d.Code));
        }

        [Theory]
        [InlineData("", "the file is empty")]
        [InlineData("   ", "the file is empty")]
        [InlineData("{", "invalid JSON")]
        [InlineData("42", "must be an object or an array of objects")]
        [InlineData("[]", "declares no language")]
        [InlineData("[1]", "language.json[0] must be an object")]
        [InlineData("{\"caption\":\"x\",\"englishName\":\"x\"}", "\"code\" is required")]
        [InlineData("{\"code\":\"th\",\"englishName\":\"x\"}", "\"caption\" is required")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\"}", "\"englishName\" is required")]
        [InlineData("{\"code\":7,\"caption\":\"x\",\"englishName\":\"x\"}", "\"code\" must be a string")]
        [InlineData("{\"code\":\"thai language\",\"caption\":\"x\",\"englishName\":\"x\"}", "is not a language code")]
        [InlineData("{\"code\":\"en\",\"caption\":\"x\",\"englishName\":\"x\"}", "one of the game's own languages")]
        [InlineData("{\"code\":\"JP\",\"caption\":\"x\",\"englishName\":\"x\"}", "one of the game's own languages")]
        [InlineData("{\"schema\":2,\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\"}", "\"schema\" must be 1")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"fallbak\":[\"en\"]}", "unknown member \"fallbak\"")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"script\":\"Thaiscript\"}", "4-letter ISO 15924")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"lineBreaks\":\"dictionary\"}", "is not one of")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"fallback\":[\"th\"]}", "cannot fall back to itself")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"fallback\":[\"en\",\"EN\"]}", "listed twice")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"fallback\":[\"\"]}", "non-blank language code")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"fallback\":{}}", "must be a code or an array")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"font\":\"Base_TH\"}", "\"font\" must be an object")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"font\":{\"file\":\"a.otf\"}}", "not supported yet")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"font\":{\"asset\":\"x\"}}", "unknown member \"asset\"")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"font\":{\"gameAssets\":[\"A\",\"A\"]}}", "listed twice")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"marks\":\"../outside.json\"}", "must be a .json path inside the pack")]
        [InlineData("{\"code\":\"th\",\"caption\":\"x\",\"englishName\":\"x\",\"marks\":\"fonts/marks.txt\"}", "must be a .json path inside the pack")]
        public void Refuses_a_bad_declaration_with_a_message_naming_the_problem(string json, string expected)
        {
            var ex = Assert.Throws<LanguageDeclarationException>(() => LanguageDeclarationParser.Parse(json, "bad.pack"));

            Assert.Contains(expected, ex.Message);
            Assert.StartsWith("language.json", ex.Message);
        }

        [Fact]
        public void The_same_code_twice_in_one_file_is_refused()
        {
            var ex = Assert.Throws<LanguageDeclarationException>(
                () => LanguageDeclarationParser.Parse("[" + Minimal + "," + Minimal + "]", "twice.pack"));

            Assert.Contains("\"th\" is declared twice", ex.Message);
        }

        [Fact]
        public void TryParse_reports_instead_of_throwing()
        {
            Assert.False(LanguageDeclarationParser.TryParse("{}", "x.pack", out var none, out var error, "x.pack/language.json"));
            Assert.Empty(none);
            Assert.StartsWith("x.pack/language.json", error);

            Assert.True(LanguageDeclarationParser.TryParse(Minimal, "x.pack", out var one, out var noError));
            Assert.Single(one);
            Assert.Null(noError);
        }

        [Fact]
        public void Language_json_is_its_own_pack_content_kind()
        {
            var index = PackContentIndex.Build(new[] { "manifest.json", "language.json", "fonts/thai-marks.json", "data/subtitles/th.json" });

            Assert.Equal(PackContentKind.Language, PackContentIndex.Classify("language.json"));
            Assert.Equal("language.json", index.Language!.Path);
            Assert.Equal(PackContentKind.Other, PackContentIndex.Classify("data/language.json"));
        }

        [Fact]
        public void The_example_pack_declares_no_language()
        {
            Assert.False(File.Exists(Path.Combine(PackFolder("cwy.example"), "data", "subtitles", "th.json")));
            Assert.False(File.Exists(Path.Combine(PackFolder("cwy.example"), "language.json")));
        }

        /// <summary>The repo's <c>packs/&lt;id&gt;</c> folder (<see cref="TestRepo"/>).</summary>
        internal static string PackFolder(string id)
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);
            var folder = Path.Combine(root!, "packs", id);
            Assert.True(Directory.Exists(folder), folder);
            return folder;
        }
    }
}
