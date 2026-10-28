using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="ConsolePanels"/> is process-wide state (mods in separate assemblies register
    /// into it), so its tests must not run beside another class that also touches it.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class ConsolePanelsCollection
    {
        public const string Name = "ConsolePanels";
    }

    [Collection(ConsolePanelsCollection.Name)]
    public class ConsolePanelsTests : IDisposable
    {
        public ConsolePanelsTests()
        {
            ConsolePanels.Clear();
            ConsolePanels.ErrorSink = null;
        }

        public void Dispose()
        {
            ConsolePanels.Clear();
            ConsolePanels.ErrorSink = null;
        }

        [Fact]
        public void A_registered_panel_renders_and_keeps_its_tab_order()
        {
            ConsolePanels.Register("Stream", () => "two events");
            ConsolePanels.Register("State", () => "ready");

            Assert.Equal(new[] { "Stream", "State" }, ConsolePanels.Names);
            Assert.True(ConsolePanels.TryRender("stream", out var text));
            Assert.Equal("two events", text);
            Assert.Equal(2, ConsolePanels.Count);
        }

        [Fact]
        public void Re_registering_a_panel_replaces_its_renderer_in_place()
        {
            ConsolePanels.Register("Stream", () => "old");
            ConsolePanels.Register("State", () => "state");
            ConsolePanels.Register("Stream", () => "new");

            Assert.Equal(new[] { "Stream", "State" }, ConsolePanels.Names);
            Assert.True(ConsolePanels.TryRender("Stream", out var text));
            Assert.Equal("new", text);
        }

        [Fact]
        public void A_panel_that_throws_shows_the_failure_and_reports_it()
        {
            var logged = new List<string>();
            ConsolePanels.ErrorSink = logged.Add;
            ConsolePanels.Register("Bad", () => throw new InvalidOperationException("no scene"));

            var ok = ConsolePanels.TryRender("Bad", out var text);

            Assert.False(ok);
            Assert.Contains("no scene", text);
            Assert.Single(logged);
        }

        [Fact]
        public void An_unknown_panel_says_so_instead_of_throwing()
        {
            Assert.False(ConsolePanels.TryRender("Nope", out var text));
            Assert.Contains("Nope", text);
            Assert.False(ConsolePanels.Has("Nope"));
        }

        [Fact]
        public void Unregister_removes_the_tab()
        {
            ConsolePanels.Register("Stream", () => "x");

            Assert.True(ConsolePanels.Unregister("stream"));
            Assert.False(ConsolePanels.Unregister("stream"));
            Assert.Empty(ConsolePanels.Names);
        }

        [Fact]
        public void A_panel_needs_a_name_and_a_renderer()
        {
            Assert.Throws<ArgumentException>(() => ConsolePanels.Register(" ", () => "x"));
            Assert.Throws<ArgumentNullException>(() => ConsolePanels.Register("Stream", null!));
        }
    }
}
