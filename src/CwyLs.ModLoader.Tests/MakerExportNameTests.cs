using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack Maker export zip names with unusual authors (HN-009, commit 15584b2). The rule is
    /// <c>&lt;packId&gt;-v&lt;version&gt;.zip</c>, or <c>&lt;packId&gt;-v&lt;version&gt;-by-&lt;authorSlug&gt;.zip</c> when the
    /// author slug (lowercase, [a-z0-9] only) is not already in the pack id. Pure function tests; no files touched.
    /// </summary>
    public sealed class MakerExportNameTests
    {
        private const string PackId = "sample.studio";

        [Theory]
        [InlineData("Ploy Sriwan", "sample.studio-v1.0.0-beta.1-by-ploysriwan.zip")]
        [InlineData("  Lab-2 ", "sample.studio-v1.0.0-beta.1-by-lab2.zip")]
        [InlineData("ภาษาไทย", "sample.studio-v1.0.0-beta.1.zip")]
        [InlineData("", "sample.studio-v1.0.0-beta.1.zip")]
        [InlineData(null, "sample.studio-v1.0.0-beta.1.zip")]
        public void The_author_slug_keeps_only_lowercase_latin_letters_and_digits(string? author, string expected) =>
            Assert.Equal(expected, MakerPackExporter.ExportFileName(PackId, "1.0.0-beta.1", author));

        [Fact]
        public void A_thai_only_author_has_no_slug_so_the_name_is_the_plain_one()
        {
            var name = MakerPackExporter.ExportFileName(PackId, "0.1.1", "สมชาย");

            Assert.Equal("sample.studio-v0.1.1.zip", name);
        }

        [Fact]
        public void Path_and_shell_characters_and_dot_dot_never_reach_the_file_name()
        {
            const string author = "Ev/il\\..\\Na:me*?\"<x>|y..z";

            var name = MakerPackExporter.ExportFileName(PackId, "0.1.1", author);

            Assert.Equal("sample.studio-v0.1.1-by-evilnamexyz.zip", name);
            var afterPackId = name.Substring(PackId.Length);
            Assert.DoesNotContain("..", afterPackId);
            Assert.DoesNotContain(name, c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' || c == ':');
        }

        [Fact]
        public void A_200_character_author_is_cut_so_the_version_segment_stays_within_64_characters()
        {
            var author = new string('a', 200);

            var name = MakerPackExporter.ExportFileName(PackId, "0.1.1", author);

            Assert.Equal("sample.studio-v0.1.1-by-" + new string('a', 54) + ".zip", name);
            var versionSegment = name.Substring(PackId.Length + 1, name.Length - PackId.Length - 1 - ".zip".Length);
            Assert.True(versionSegment.Length <= 64, versionSegment);
        }

        [Theory]
        [InlineData("lab.export", "Lab", "lab.export-v0.1.1.zip")]
        [InlineData("sample.lab", "LAB", "sample.lab-v0.1.1.zip")]
        public void An_id_that_already_contains_the_author_slug_keeps_the_plain_name(string packId, string author, string expected) =>
            Assert.Equal(expected, MakerPackExporter.ExportFileName(packId, "0.1.1", author));

        [Fact]
        public void An_id_without_the_author_slug_gets_the_by_suffix() =>
            Assert.Equal("sample.studio-v0.1.1-by-lab.zip", MakerPackExporter.ExportFileName("sample.studio", "0.1.1", "Lab"));

        [Fact]
        public void An_empty_author_keeps_the_plain_name()
        {
            Assert.Equal("sample.studio-v0.1.1.zip", MakerPackExporter.ExportFileName(PackId, "0.1.1", string.Empty));
        }
    }
}
