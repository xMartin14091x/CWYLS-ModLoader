using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.PackLines;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Pack lines against real files: the shipped packs load exactly as before the <c>Languages</c>/<c>Pool</c>
    /// members existed, and every worked example in <c>src/CwyLs.ModLoader.Bulbul/PackLines/README.md</c>
    /// is accepted by the screen as written.
    /// </summary>
    public partial class PackLinesPacksTests
    {
        private const string ReadmePath = "src/CwyLs.ModLoader.Bulbul/PackLines/README.md";

        private static readonly string[] ExampleFiles = { "scenario_groups.json", "novel.json", "localization.json", "th.json" };

        [Fact]
        public void The_example_pack_parses_to_the_same_rows_and_passes_the_screen_untouched()
        {
            var input = Read("cwy.example");
            var merged = MasterDataMerge.Merge(new[] { input });

            Assert.Equal(0, merged.Report.RejectedCount);
            var group = Assert.Single(merged.ScenarioGroups);
            Assert.Equal("cwy_example_01", group.Id);
            Assert.Equal(22, group.Scenario);
            Assert.Equal(900f, group.EpisodeNumber);
            Assert.Equal("None", group.UnlockStoryEpisode);
            Assert.Null(group.Languages);
            Assert.Equal(string.Empty, group.Pool);
            Assert.Equal(2, merged.Novel.Count);
            Assert.Equal("Voice_CWY_Test", Assert.Single(merged.Localization).Voice);

            Assert.Same(merged, PackLineScreen.Screen(merged, MasterDataBaseline.Empty, admit: true).Set);
            Assert.Same(merged, PackLineScreen.Screen(merged, MasterDataBaseline.Empty, admit: false).Set);
        }

        [Fact]
        public void Every_worked_example_in_the_readme_is_accepted_as_written()
        {
            var examples = Examples();
            Assert.True(examples.Count >= 6, "expected at least six worked examples, found " + examples.Count);

            foreach (var example in examples)
            {
                AssertAccepted(new[] { example.ToInput("my.lines") }, example.Title);
            }

            // All of them in one pack: their ids and episodes do not clash either.
            var all = new MasterDataPackInput("my.lines")
            {
                ScenarioGroupsJson = JoinArrays(examples.Select(e => e.ScenarioGroups)),
                NovelJson = JoinArrays(examples.Select(e => e.Novel)),
                LocalizationJson = JoinArrays(examples.Select(e => e.Localization)),
            };
            all.AddSubtitles("data/subtitles/th.json", JoinObjects(examples.Select(e => e.Thai)));
            AssertAccepted(new[] { all }, "all examples together");
        }

        private static void AssertAccepted(IEnumerable<MasterDataPackInput> inputs, string title)
        {
            var merged = MasterDataMerge.Merge(inputs);
            Assert.True(merged.Report.RejectedCount == 0, title + ": " + string.Join(" | ", merged.Report.AllRejections));

            var exclusive = merged.ScenarioGroups.Count(g => g.Languages != null);
            Assert.True(exclusive > 0, title + ": no group declares Languages");

            var screen = PackLineScreen.Screen(merged, MasterDataBaseline.Empty, admit: true);
            Assert.True(screen.Rejected.Count == 0, title + ": " + string.Join(" | ", screen.Rejected));
            Assert.Equal(exclusive, screen.Lines.Count);

            foreach (var row in merged.Novel.Where(r => r.LocalizationId != "0" && r.LocalizationId.Length > 0))
            {
                Assert.True(merged.Subtitles.TryGet(row.LocalizationId, "th", out _), title + ": no Thai text for " + row.LocalizationId);
            }

            foreach (var line in screen.Lines)
            {
                Assert.True(line.IsOfferedIn("th"), title + ": " + line);
                Assert.False(line.IsOfferedIn("en"), title + ": " + line);
                Assert.NotEmpty(line.Voices);
            }
        }

        private static MasterDataPackInput Read(string packId)
        {
            var folder = LanguageDeclarationParserTests.PackFolder(packId);
            var input = new MasterDataPackInput(packId)
            {
                LocalizationJson = ReadIfPresent(Path.Combine(folder, "data", "localization.json")),
                NovelJson = ReadIfPresent(Path.Combine(folder, "data", "novel.json")),
                ScenarioGroupsJson = ReadIfPresent(Path.Combine(folder, "data", "scenario_groups.json")),
            };

            var subtitles = Path.Combine(folder, "data", "subtitles");
            if (Directory.Exists(subtitles))
            {
                foreach (var file in Directory.GetFiles(subtitles, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    input.AddSubtitles("data/subtitles/" + Path.GetFileName(file), File.ReadAllText(file));
                }
            }

            return input;
        }

        private static string? ReadIfPresent(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

        private static List<Example> Examples()
        {
            var root = TestRepo.Root;
            Assert.NotNull(root);
            var lines = File.ReadAllLines(Path.Combine(root!, ReadmePath));

            var examples = new List<Example>();
            Example? current = null;
            var title = string.Empty;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("### ", StringComparison.Ordinal))
                {
                    title = lines[i].Substring(4).Trim();
                }

                if (!lines[i].StartsWith("```json ", StringComparison.Ordinal))
                {
                    continue;
                }

                var file = lines[i].Substring("```json ".Length).Trim();
                var index = Array.IndexOf(ExampleFiles, file);
                if (index < 0)
                {
                    continue;
                }

                var body = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
                {
                    body.Add(lines[i]);
                }

                var text = string.Join("\n", body);
                if (index == 0)
                {
                    current = new Example(title);
                    examples.Add(current);
                }

                Assert.True(current != null, "README: a " + file + " block before any scenario_groups.json block");
                current!.Set(index, text);
            }

            foreach (var example in examples)
            {
                Assert.True(example.IsComplete, "README example '" + example.Title + "' lacks one of " + string.Join(", ", ExampleFiles));
            }

            return examples;
        }

        private static string JoinArrays(IEnumerable<string> arrays) =>
            "[" + string.Join(",", arrays.Select(a => a.Trim().TrimStart('[').TrimEnd(']'))) + "]";

        private static string JoinObjects(IEnumerable<string> objects) =>
            "{" + string.Join(",", objects.Select(o => o.Trim().TrimStart('{').TrimEnd('}'))) + "}";

        private sealed class Example
        {
            private readonly string?[] _blocks = new string?[4];

            internal Example(string title)
            {
                Title = title;
            }

            internal string Title { get; }

            internal string ScenarioGroups => _blocks[0]!;

            internal string Novel => _blocks[1]!;

            internal string Localization => _blocks[2]!;

            internal string Thai => _blocks[3]!;

            internal bool IsComplete => _blocks.All(b => b != null);

            internal void Set(int index, string text) => _blocks[index] = text;

            internal MasterDataPackInput ToInput(string packId)
            {
                var input = new MasterDataPackInput(packId)
                {
                    ScenarioGroupsJson = ScenarioGroups,
                    NovelJson = Novel,
                    LocalizationJson = Localization,
                };
                input.AddSubtitles("data/subtitles/th.json", Thai);
                return input;
            }
        }
    }
}
