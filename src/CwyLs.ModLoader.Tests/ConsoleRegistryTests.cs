using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class ConsoleRegistryTests
    {
        [Fact]
        public void A_registered_command_runs_with_the_arguments_after_its_name()
        {
            var registry = new CommandRegistry();
            IReadOnlyList<string>? seen = null;
            registry.Register("packs", "Lists packs.", "packs list|rescan", "framework", args =>
            {
                seen = args;
                return "ran";
            });

            var output = registry.Execute("packs list cwy.example");

            Assert.Equal("ran", output);
            Assert.Equal(new[] { "list", "cwy.example" }, seen!);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void A_name_is_matched_case_insensitively()
        {
            var registry = new CommandRegistry();
            registry.Register("Keys", null, null, null, _ => "keys");

            Assert.Equal("keys", registry.Execute("KEYS"));
            Assert.True(registry.TryGet("kEyS", out var found));
            Assert.Equal("Keys", found!.Name);
        }

        [Fact]
        public void An_unknown_name_is_reported_rather_than_thrown()
        {
            var registry = new CommandRegistry();

            var output = registry.Execute("nope");

            Assert.StartsWith(CommandRegistry.UnknownPrefix, output);
            Assert.Contains("help", output);
        }

        [Fact]
        public void A_handler_that_throws_becomes_an_error_line_and_reaches_the_error_sink()
        {
            var registry = new CommandRegistry();
            var logged = new List<string>();
            registry.ErrorSink = logged.Add;
            registry.Register("boom", null, null, null, _ => throw new InvalidOperationException("no runner"));

            var output = registry.Execute("boom");

            Assert.Contains("boom failed", output);
            Assert.Contains("no runner", output);
            Assert.Single(logged);
            Assert.Equal(output, logged[0]);
        }

        [Fact]
        public void Re_registering_a_name_replaces_it_in_place()
        {
            var registry = new CommandRegistry();
            registry.Register("a", null, null, null, _ => "first");
            registry.Register("b", null, null, null, _ => "b");
            registry.Register("a", null, null, null, _ => "second");

            Assert.Equal(2, registry.Count);
            Assert.Equal("second", registry.Execute("a"));
            Assert.Equal(new[] { "a", "b" }, new[] { registry.All[0].Name, registry.All[1].Name });
        }

        [Fact]
        public void Help_lists_every_command_grouped_and_one_command_in_full()
        {
            var registry = new CommandRegistry();
            registry.Register("smoke", "Runs the smoke checks.", "smoke run", "framework", _ => string.Empty);
            registry.Register("voice", "Plays a voice line.", "voice play <name>", "game", _ => string.Empty);

            var all = registry.Help();
            var one = registry.Help("voice");

            Assert.Contains("[framework]", all);
            Assert.Contains("[game]", all);
            Assert.Contains("smoke run - Runs the smoke checks.", all);
            Assert.Contains("voice play <name>", one);
            Assert.Contains("group: game", one);
            Assert.StartsWith(CommandRegistry.UnknownPrefix, registry.Help("missing"));
        }

        [Fact]
        public void A_blank_line_runs_nothing()
        {
            var registry = new CommandRegistry();
            registry.Register("a", null, null, null, _ => "ran");

            Assert.Equal(string.Empty, registry.Execute("   "));
            Assert.Equal(string.Empty, registry.Execute((string?)null));
        }

        [Fact]
        public void Unregister_removes_a_command_and_reports_whether_it_did()
        {
            var registry = new CommandRegistry();
            registry.Register("a", null, null, null, _ => "ran");

            Assert.True(registry.Unregister("A"));
            Assert.False(registry.Unregister("A"));
            Assert.Equal(0, registry.Count);
        }

        [Fact]
        public void A_command_name_cannot_contain_whitespace()
        {
            var registry = new CommandRegistry();

            Assert.Throws<ArgumentException>(() =>
                registry.Register("packs list", null, null, null, _ => string.Empty));
            Assert.Throws<ArgumentException>(() =>
                registry.Register(" ", null, null, null, _ => string.Empty));
        }

        [Fact]
        public void A_command_without_a_usage_or_group_gets_readable_defaults()
        {
            var command = new ConsoleCommand("clear", "Empties the output.", null, null, _ => null);

            Assert.Equal("clear", command.Usage);
            Assert.Equal(ConsoleCommand.DefaultGroup, command.Group);
            Assert.Equal("clear - Empties the output.", command.Describe());
        }
    }
}
