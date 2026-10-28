using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Actions;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>E-ACT-CORE: the gate switches on <see cref="ActionServices"/> and the confirmation request's arguments.</summary>
    public class ActionsServicesTests
    {
        [Fact]
        public void Switches_nobody_installed_are_off()
        {
            var services = new ActionServices();

            var settings = services.GateSettings;
            Assert.False(settings.AllowWrites);
            Assert.False(settings.AllowApiActions);
            Assert.False(services.HasWritesSwitch);
            Assert.False(services.HasApiActionsSwitch);
        }

        [Fact]
        public void Installed_switches_are_read_on_every_call()
        {
            var services = new ActionServices();
            var writes = false;
            services.UseWritesSwitch(() => writes);
            services.UseApiActionsSwitch(() => true);

            Assert.True(services.HasWritesSwitch);
            Assert.False(services.GateSettings.AllowWrites);
            Assert.True(services.GateSettings.AllowApiActions);

            writes = true;
            Assert.True(services.GateSettings.AllowWrites);
            Assert.NotSame(services.GateSettings, services.GateSettings);
        }

        [Fact]
        public void A_throwing_switch_reads_as_off_and_is_reported()
        {
            var services = new ActionServices();
            var errors = new List<string>();
            services.ErrorSink = errors.Add;
            services.UseWritesSwitch(() => throw new InvalidOperationException("config gone"));

            Assert.False(services.GateSettings.AllowWrites);
            Assert.Contains(errors, e => e.Contains(ActionGateSettings.AllowWritesKey) && e.Contains("config gone"));
            Assert.Throws<ArgumentNullException>(() => services.UseWritesSwitch(null!));
            Assert.Throws<ArgumentNullException>(() => services.UseApiActionsSwitch(null!));
        }

        [Fact]
        public void A_confirmation_request_carries_the_arguments_for_the_dialog()
        {
            var plain = new ConfirmationRequest("todo.delete", "Delete?", "Deletes a todo.", ActionCaller.Api, "cwy-mcp", isDestructive: true);
            Assert.Same(ActionArgs.Empty, plain.Arguments);

            var args = ActionArgs.Of(("id", 5));
            var withArgs = new ConfirmationRequest("todo.delete", "Delete?", "Deletes a todo.", ActionCaller.Api, "cwy-mcp", true, arguments: args);
            Assert.Same(args, withArgs.Arguments);
            Assert.Equal(ConfirmationRequest.DefaultTimeout, withArgs.Timeout);
        }
    }
}
