using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.Bus;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// W0-CONTRACTS: the tag list is complete, every tag fits the one-word grammar, MODS/MOD moved
    /// into <see cref="ConsoleTags"/>, the terminal viewer has a colour for every tag, and a
    /// producer-named line with a new tag survives the formatter, the parser and the routing table.
    /// </summary>
    public class LoggingConsoleTagsTests
    {
        private static readonly Regex TagGrammar = new Regex(@"\A[A-Z][A-Z0-9]{1,11}\z");

        private static readonly DateTimeOffset At = new DateTimeOffset(2026, 9, 29, 12, 0, 0, 5, TimeSpan.FromHours(7));

        /// <summary>The Phases C-S tags W0-CONTRACTS added.</summary>
        public static IEnumerable<object[]> NewTags()
        {
            foreach (var tag in new[]
                     {
                         ConsoleTags.Mods, ConsoleTags.Mod, ConsoleTags.Action, ConsoleTags.Api, ConsoleTags.Script,
                         ConsoleTags.Trigger, ConsoleTags.Cond, ConsoleTags.Audio, ConsoleTags.UiSkin,
                         ConsoleTags.EditData, ConsoleTags.Mic, ConsoleTags.Camera,
                     })
            {
                yield return new object[] { tag };
            }
        }

        [Fact]
        public void All_lists_every_constant_exactly_once()
        {
            var constants = new List<string>();
            foreach (var field in typeof(ConsoleTags).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.IsLiteral && field.FieldType == typeof(string))
                {
                    constants.Add((string)field.GetRawConstantValue()!);
                }
            }

            Assert.Equal(constants.Count, ConsoleTags.All.Count);
            Assert.Equal(new HashSet<string>(constants), new HashSet<string>(ConsoleTags.All));
            Assert.Equal(ConsoleTags.All.Count, new HashSet<string>(ConsoleTags.All, StringComparer.Ordinal).Count);
        }

        [Fact]
        public void Every_tag_is_one_upper_case_word_the_viewer_can_parse()
        {
            foreach (var tag in ConsoleTags.All)
            {
                Assert.Matches(TagGrammar, tag);
            }
        }

        [Fact]
        public void The_mod_tags_live_in_ConsoleTags_and_the_old_names_are_aliases()
        {
            Assert.Equal("MODS", ConsoleTags.Mods);
            Assert.Equal("MOD", ConsoleTags.Mod);
            Assert.Equal(ConsoleTags.Mods, ModConsoleLines.ModsTag);
            Assert.Equal(ConsoleTags.Mod, ModConsoleLines.ModTag);
        }

        [Fact]
        public void Scene_keeps_its_meaning_and_script_lines_have_their_own_tag()
        {
            Assert.Equal("SCENE", ConsoleTags.Scene);
            Assert.Equal("SCRIPT", ConsoleTags.Script);
            Assert.NotEqual(ConsoleTags.Scene, ConsoleTags.Script);
        }

        [Fact]
        public void The_terminal_viewer_has_a_colour_for_every_tag()
        {
            var root = RepoRoot();
            Assert.NotNull(root);
            var viewer = File.ReadAllText(Path.Combine(root!, "tools", "mac", "watch-log.py"));

            var table = Regex.Match(viewer, @"TAGS\s*=\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline);
            Assert.True(table.Success, "watch-log.py has no TAGS table");

            var colours = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match entry in Regex.Matches(table.Groups["body"].Value, "\"(?<tag>[A-Z][A-Z0-9]{1,11})\"\\s*:\\s*\\("))
            {
                colours.Add(entry.Groups["tag"].Value);
            }

            foreach (var tag in ConsoleTags.All)
            {
                Assert.True(colours.Contains(tag), tag + " has no colour in tools/mac/watch-log.py TAGS");
            }
        }

        [Theory]
        [MemberData(nameof(NewTags))]
        public void A_producer_named_line_with_a_new_tag_formats_parses_and_routes(string tag)
        {
            var payload = new Dictionary<string, object?>
            {
                [ConsolePayload.TagKey] = tag,
                [ConsolePayload.PrimaryKey] = "something happened",
                [ConsolePayload.FieldsKey] = "who",
                ["who"] = "my.mod",
                ["fileOnly"] = 3,
            };

            Assert.True(ConsoleEventMap.TryMap(new BusEvent("w0", "test", payload, At), out var record));
            var line = ConsoleRecordFormatter.Format(record);

            Assert.Equal("12:00:00.005  " + tag + "  something happened  who=my.mod", line);
            Assert.True(ConsoleRecordParser.TryParse(line, At, out var parsed));
            Assert.Equal(tag, parsed!.Tag);
            Assert.Equal(line, ConsoleRecordFormatter.Format(parsed));
        }

        [Fact]
        public void A_ConditionsChanged_event_on_the_game_bus_stays_in_the_file()
        {
            var change = new ConditionsChanged(UpdatePoint.TimeChanged);

            Assert.False(ConsoleEventMap.TryMap(new BusEvent("event", nameof(ConditionsChanged), change.Fields(), At), out _));
        }

        /// <summary>The repository root (<see cref="TestRepo"/>).</summary>
        private static string? RepoRoot() => TestRepo.Root;
    }
}
