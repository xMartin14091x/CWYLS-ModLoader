using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceArgFormatterTests
    {
        private enum Mood
        {
            WorkTyping,
        }

        /// <summary>Stands in for a game row: identity on fields, like Bulbul's master-data types.</summary>
        private sealed class FakeAudioInfo
        {
            public string Title = "Lo-Fi Night";
            public string Credit = "unused";
        }

        /// <summary>Stands in for a scenario row: identity on properties, several of them.</summary>
        private sealed class FakeScenarioRow
        {
            public string ID { get; set; } = "SG_0007";

            public string Name { get; set; } = "morning";

            public string ScenarioGroupID { get; set; } = "grp_01";

            public int Unrelated { get; set; } = 42;
        }

        private sealed class FakeOpaque
        {
            public int Irrelevant = 1;
        }

        private sealed class FakeThrowing
        {
            public string Name => throw new InvalidOperationException("half-built");
        }

        private sealed class FakeNested
        {
            public FakeAudioInfo Name { get; } = new FakeAudioInfo();
        }

        [Fact]
        public void Null_and_strings()
        {
            var formatter = new ArgFormatter(maxStringLength: 10);

            Assert.Equal("null", formatter.Format(null));
            Assert.Equal("short", formatter.Format("short"));
            Assert.Equal("0123456789", formatter.Format("0123456789"));
            Assert.Equal("0123456789" + ArgFormatter.Ellipsis, formatter.Format("0123456789A"));
        }

        [Fact]
        public void Primitives_and_enums()
        {
            var formatter = new ArgFormatter();

            Assert.Equal("true", formatter.Format(true));
            Assert.Equal("7", formatter.Format(7));
            Assert.Equal("1.5", formatter.Format(1.5f));
            Assert.Equal("WorkTyping", formatter.Format(Mood.WorkTyping));
        }

        [Fact]
        public void Numbers_do_not_follow_the_machine_locale()
        {
            // A German locale would otherwise write 1,5 and break the JSON consumer.
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("1.5", new ArgFormatter().Format(1.5d));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Arrays_and_collections_report_their_length()
        {
            var formatter = new ArgFormatter();

            Assert.Equal("String[3]", formatter.Format(new[] { "a", "b", "c" }));
            Assert.Equal("Int32[0]", formatter.Format(Array.Empty<int>()));
            Assert.Equal("List[2]", formatter.Format(new List<string> { "a", "b" }));
        }

        [Fact]
        public void Unknown_objects_print_their_identity_members()
        {
            var formatter = new ArgFormatter();

            Assert.Equal("FakeAudioInfo{Title=Lo-Fi Night}", formatter.Format(new FakeAudioInfo()));
            Assert.Equal(
                "FakeScenarioRow{ID=SG_0007, Name=morning, ScenarioGroupID=grp_01}",
                formatter.Format(new FakeScenarioRow()));
        }

        [Fact]
        public void An_object_with_no_identity_member_is_just_its_type()
        {
            Assert.Equal("FakeOpaque", new ArgFormatter().Format(new FakeOpaque()));
        }

        [Fact]
        public void Identity_values_obey_the_string_limit()
        {
            var formatter = new ArgFormatter(maxStringLength: 4);

            Assert.Equal("FakeAudioInfo{Title=Lo-F" + ArgFormatter.Ellipsis + "}", formatter.Format(new FakeAudioInfo()));
        }

        [Fact]
        public void Depth_is_capped_so_one_argument_stays_one_line()
        {
            // Depth 1: the nested object is named by its type, never expanded again.
            Assert.Equal("FakeNested{Name=FakeAudioInfo}", new ArgFormatter(argDepth: 1).Format(new FakeNested()));

            // Depth 0: not even the top-level object is expanded.
            Assert.Equal("FakeNested", new ArgFormatter(argDepth: 0).Format(new FakeNested()));
        }

        [Fact]
        public void A_getter_that_throws_is_reported_not_propagated()
        {
            // The game must never fail because the trace asked it a question.
            Assert.Equal("FakeThrowing{Name=<error>}", new ArgFormatter().Format(new FakeThrowing()));
        }

        [Fact]
        public void The_object_hook_wins_over_the_generic_case()
        {
            var formatter = new ArgFormatter { ObjectHook = value => value is FakeOpaque ? "GameObject(Room)" : null };

            Assert.Equal("GameObject(Room)", formatter.Format(new FakeOpaque()));
            Assert.Equal("FakeAudioInfo{Title=Lo-Fi Night}", formatter.Format(new FakeAudioInfo()));
        }

        [Fact]
        public void A_hook_that_throws_falls_back_instead_of_failing()
        {
            var formatter = new ArgFormatter { ObjectHook = _ => throw new InvalidOperationException("boom") };

            Assert.Equal("FakeOpaque", formatter.Format(new FakeOpaque()));
        }

        [Fact]
        public void FormatAll_handles_the_empty_and_null_argument_lists()
        {
            var formatter = new ArgFormatter();

            Assert.Empty(formatter.FormatAll(null));
            Assert.Empty(formatter.FormatAll(Array.Empty<object?>()));
            Assert.Equal(new[] { "a", "null", "3" }, formatter.FormatAll(new object?[] { "a", null, 3 }));
        }
    }
}
