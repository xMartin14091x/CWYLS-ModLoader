using System;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: the default tier gating (ActionGate's table) with every switch at its default and flipped.</summary>
    public class ActionsGateTests
    {
        private static readonly ActionDescriptor Read = new ActionDescriptor("music.current", ActionTier.Read, "The current track.");
        private static readonly ActionDescriptor Act = new ActionDescriptor("music.play", ActionTier.Act, "Plays a track.");
        private static readonly ActionDescriptor Write = new ActionDescriptor("todo.add", ActionTier.Write, "Adds a todo.");
        private static readonly ActionDescriptor Delete = new ActionDescriptor("todo.delete", ActionTier.Write, "Deletes a todo.", isDestructive: true);
        private static readonly ActionDescriptor ActDestructive = new ActionDescriptor("music.forget", ActionTier.Act, "Removes an imported track.", isDestructive: true);

        private static readonly ActionGateSettings WritesOn = new ActionGateSettings { AllowWrites = true };
        private static readonly ActionGateSettings AllOn = new ActionGateSettings { AllowWrites = true, AllowApiActions = true };

        [Fact]
        public void The_defaults_are_everything_off()
        {
            var defaults = ActionGateSettings.Defaults;

            Assert.False(defaults.AllowWrites);
            Assert.False(defaults.AllowApiActions);
            Assert.NotSame(ActionGateSettings.Defaults, ActionGateSettings.Defaults);
        }

        [Theory]
        [InlineData(ActionCaller.Mod)]
        [InlineData(ActionCaller.Console)]
        [InlineData(ActionCaller.Script)]
        [InlineData(ActionCaller.Api)]
        public void Reads_are_always_allowed(ActionCaller caller) =>
            Assert.Equal(ActionGateVerdict.Allow, ActionGate.Decide(Read, caller).Verdict);

        [Theory]
        [InlineData(ActionCaller.Mod, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Console, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Script, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Api, ActionGateVerdict.Deny)]
        public void By_default_acting_is_allowed_inside_the_game_only(ActionCaller caller, ActionGateVerdict expected) =>
            Assert.Equal(expected, ActionGate.Decide(Act, caller).Verdict);

        [Theory]
        [InlineData(ActionCaller.Mod)]
        [InlineData(ActionCaller.Console)]
        [InlineData(ActionCaller.Script)]
        [InlineData(ActionCaller.Api)]
        public void By_default_every_write_is_denied(ActionCaller caller)
        {
            var decision = ActionGate.Decide(Write, caller);

            Assert.Equal(ActionGateVerdict.Deny, decision.Verdict);
        }

        [Fact]
        public void The_deny_reasons_name_the_switch()
        {
            Assert.Contains(ActionGateSettings.AllowWritesKey, ActionGate.Decide(Write, ActionCaller.Console).Reason);
            Assert.Contains(ActionGateSettings.AllowApiActionsKey, ActionGate.Decide(Act, ActionCaller.Api).Reason);
        }

        [Theory]
        [InlineData(ActionCaller.Mod, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Console, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Script, ActionGateVerdict.Allow)]
        [InlineData(ActionCaller.Api, ActionGateVerdict.Deny)]
        public void With_writes_on_the_api_still_needs_its_own_switch(ActionCaller caller, ActionGateVerdict expected) =>
            Assert.Equal(expected, ActionGate.Decide(Write, caller, WritesOn).Verdict);

        [Fact]
        public void With_both_switches_on_an_api_write_is_confirmed_in_game()
        {
            Assert.Equal(ActionGateVerdict.Confirm, ActionGate.Decide(Write, ActionCaller.Api, AllOn).Verdict);
            Assert.Equal(ActionGateVerdict.Allow, ActionGate.Decide(Act, ActionCaller.Api, AllOn).Verdict);
        }

        [Theory]
        [InlineData(ActionCaller.Mod)]
        [InlineData(ActionCaller.Console)]
        [InlineData(ActionCaller.Script)]
        [InlineData(ActionCaller.Api)]
        public void A_delete_is_always_confirmed_once_allowed(ActionCaller caller)
        {
            Assert.Equal(ActionGateVerdict.Confirm, ActionGate.Decide(Delete, caller, AllOn).Verdict);
            Assert.Equal(ActionGateVerdict.Confirm, ActionGate.Decide(ActDestructive, caller, AllOn).Verdict);
        }

        [Fact]
        public void A_delete_with_writes_off_is_denied_not_asked()
        {
            Assert.Equal(ActionGateVerdict.Deny, ActionGate.Decide(Delete, ActionCaller.Console).Verdict);
            Assert.Equal(ActionGateVerdict.Confirm, ActionGate.Decide(ActDestructive, ActionCaller.Console).Verdict);
        }

        [Fact]
        public void A_missing_descriptor_is_a_programming_error()
        {
            Assert.Throws<ArgumentNullException>(() => ActionGate.Decide(null!, ActionCaller.Mod));
            Assert.StartsWith("Allow", ActionGate.Decide(Read, ActionCaller.Api).ToString());
        }
    }
}
