using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Scenario;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W0-CONTRACTS: script-command numbers stay in 5000-5999; names, owners and duplicates.</summary>
    public class ScenarioCommandRegistryTests
    {
        private static readonly ScenarioCommandHandler Nothing = _ => { };

        private static ScenarioCommandDescriptor Command(string name) => new ScenarioCommandDescriptor(name, "Does " + name + ".");

        [Fact]
        public void The_range_is_5000_to_5999_and_outside_every_game_command()
        {
            Assert.True(ScenarioCommandIds.IsInRange(5000));
            Assert.True(ScenarioCommandIds.IsInRange(5999));
            Assert.False(ScenarioCommandIds.IsInRange(4999));
            Assert.False(ScenarioCommandIds.IsInRange(6000));
            Assert.False(ScenarioCommandIds.IsInRange(1000));
            foreach (var name in CwyLs.ModLoader.Core.MasterData.MasterDataEnums.Command.Names)
            {
                CwyLs.ModLoader.Core.MasterData.MasterDataEnums.Command.TryParseName(name, out var value);
                Assert.False(ScenarioCommandIds.IsInRange(value), name + " = " + value);
            }
        }

        [Fact]
        public void Built_ins_have_fixed_distinct_numbers_in_the_framework_block()
        {
            var ids = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, id) in ScenarioCommandIds.BuiltIns)
            {
                Assert.True(ScenarioCommandIds.IsFrameworkId(id), name);
                Assert.True(ids.Add(id), "number " + id + " repeats");
                Assert.True(names.Add(name), name + " repeats");
                Assert.StartsWith("cwy.", name);
                Assert.True(ScenarioCommandDescriptor.IsValidName(name), name);
            }

            Assert.True(ScenarioCommandIds.TryGetBuiltIn("CWY.DO", out var doId));
            Assert.Equal(5000, doId);
            Assert.True(ScenarioCommandIds.TryGetBuiltInName(5020, out var stream));
            Assert.Equal("cwy.say.stream", stream);
            Assert.False(ScenarioCommandIds.TryGetBuiltInName(5099, out _));
        }

        [Fact]
        public void A_fresh_registry_resolves_built_ins_without_handing_out_numbers()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(registry.TryResolve("cwy.defer", out var defer));
            Assert.Equal(ScenarioCommandIds.Defer, defer);
            Assert.True(registry.IsKnownId(ScenarioCommandIds.If));
            Assert.Equal(0, registry.DynamicCount);
            Assert.False(registry.RuntimeAvailable);
            Assert.Equal(ScenarioCommandRegistry.NoRuntimeDetail, registry.RuntimeDetail);
        }

        [Fact]
        public void Other_names_get_the_next_number_from_5100_once_and_keep_it()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(registry.TryResolve("my.mod.wave", out var wave));
            Assert.True(registry.TryResolve("My.Mod.Wave", out var again));
            Assert.True(registry.TryResolve("cwy.later.command", out var later));

            Assert.Equal(ScenarioCommandIds.DynamicFirst, wave);
            Assert.Equal(wave, again);
            Assert.Equal(wave + 1, later);
            Assert.Equal(2, registry.DynamicCount);
            Assert.True(registry.TryGetName(wave, out var name));
            Assert.Equal("my.mod.wave", name);
        }

        [Theory]
        [InlineData("wave")]
        [InlineData("5000")]
        [InlineData("VoiceText")]
        [InlineData("my mod.wave")]
        [InlineData("")]
        [InlineData(null)]
        public void Unusable_names_get_no_number(string? name)
        {
            var registry = new ScenarioCommandRegistry();

            Assert.False(registry.TryResolve(name, out var id, out var problem));
            Assert.Equal(0, id);
            Assert.NotEmpty(problem);
            Assert.Equal(0, registry.DynamicCount);
        }

        [Fact]
        public void The_dynamic_block_runs_out_at_5999_and_never_goes_past_it()
        {
            var registry = new ScenarioCommandRegistry();
            var capacity = ScenarioCommandIds.Last - ScenarioCommandIds.DynamicFirst + 1;

            for (var i = 0; i < capacity; i++)
            {
                Assert.True(registry.TryResolve("my.mod.c" + i, out var id));
                Assert.InRange(id, ScenarioCommandIds.DynamicFirst, ScenarioCommandIds.Last);
            }

            Assert.False(registry.TryResolve("my.mod.onemore", out var none, out var problem));
            Assert.Equal(0, none);
            Assert.Contains("in use", problem);
            Assert.True(registry.TryResolve("cwy.do", out var builtIn));
            Assert.Equal(5000, builtIn);
            Assert.False(registry.TryGetName(6000, out _));
            Assert.False(registry.IsKnownId(4999));
        }

        [Fact]
        public void A_number_has_one_handler_and_a_duplicate_is_refused()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(registry.TryRegister("my.mod", Command("my.mod.wave"), Nothing, out var first, out _));
            Assert.False(registry.TryRegister("my.mod", Command("my.mod.wave"), Nothing, out var second, out var problem));
            Assert.Null(second);
            Assert.Contains("already has a handler from my.mod", problem);
            Assert.Throws<InvalidOperationException>(() => registry.Register("my.mod", Command("my.mod.wave"), Nothing));

            Assert.True(registry.TryGetBinding("MY.MOD.WAVE", out var binding));
            Assert.Equal(ScenarioCommandIds.DynamicFirst, binding!.Id);

            first!.Dispose();
            Assert.False(registry.TryGetBinding(binding.Id, out _));
            Assert.True(registry.IsKnownId(binding.Id));
            Assert.True(registry.TryRegister("my.mod", Command("my.mod.wave"), Nothing, out _, out _));
            Assert.True(registry.TryGetBinding(binding.Id, out var rebound));
            Assert.Equal(binding.Id, rebound!.Id);
        }

        [Fact]
        public void A_row_parsed_before_the_mod_loaded_finds_its_handler_later()
        {
            var registry = new ScenarioCommandRegistry();
            Assert.True(registry.TryResolve("my.mod.wave", out var parsedEarly));

            registry.Register("my.mod", Command("my.mod.wave"), Nothing);

            Assert.True(registry.TryGetBinding(parsedEarly, out var binding));
            Assert.Equal("my.mod.wave", binding!.Descriptor.Name);
        }

        [Fact]
        public void Owners_are_kept_in_their_own_names()
        {
            var registry = new ScenarioCommandRegistry();

            Assert.True(registry.TryRegister("cwy", Command("cwy.do"), Nothing, out _, out _));
            Assert.True(registry.TryGetBinding(ScenarioCommandIds.Do, out var builtIn));
            Assert.Equal("cwy", builtIn!.Owner);

            Assert.False(registry.TryRegister("cwy", Command("my.mod.wave"), Nothing, out _, out var frameworkForeign));
            Assert.Contains("cwy.<name>", frameworkForeign);
            Assert.False(registry.TryRegister("my.mod", Command("other.mod.wave"), Nothing, out _, out var modForeign));
            Assert.Contains("my.mod.<name>", modForeign);
            Assert.False(registry.TryRegister("cwy.camera", Command("cwy.camera.preset"), Nothing, out _, out var stolen));
            Assert.Contains("framework command", stolen);
            Assert.False(registry.TryRegister("Bad Owner", Command("bad.owner.x"), Nothing, out _, out _));
            Assert.False(registry.TryRegister("my.mod", null!, Nothing, out _, out _));
        }

        [Fact]
        public void The_runtime_flag_is_set_by_the_seam()
        {
            var registry = new ScenarioCommandRegistry();

            registry.SetRuntime(true, "ReadSentence prefix");
            Assert.True(registry.RuntimeAvailable);
            Assert.Equal("ReadSentence prefix", registry.RuntimeDetail);

            registry.SetRuntime(false, " ");
            Assert.False(registry.RuntimeAvailable);
            Assert.Equal(ScenarioCommandRegistry.NoRuntimeDetail, registry.RuntimeDetail);
        }

        [Fact]
        public void Descriptors_and_rows_check_their_shape()
        {
            Assert.Throws<ArgumentException>(() => new ScenarioCommandDescriptor("Wave", "d"));
            Assert.Throws<ArgumentException>(() => new ScenarioCommandDescriptor("my.mod.wave", " "));
            Assert.Throws<ArgumentException>(() => new ScenarioCommandArg(5, "x", "d"));
            Assert.Throws<ArgumentException>(() => new ScenarioCommandDescriptor(
                "my.mod.wave", "d", new[] { new ScenarioCommandArg(1, "a", "d"), new ScenarioCommandArg(1, "b", "d") }));

            var descriptor = new ScenarioCommandDescriptor(
                "my.mod.wave", "Waves.", new[] { new ScenarioCommandArg(3, "hand", "Which hand."), new ScenarioCommandArg(1, "times", "How often.", required: true) });
            Assert.Equal(1, descriptor.Args[0].Slot);
            Assert.Equal("Arg1 times (required)", descriptor.Args[0].ToString());

            var row = new ScenarioCommandRow("r1", "g1", 12, 5100, "my.mod.wave", null, 2f, "left", null, "0");
            Assert.Equal(string.Empty, row.Arg1);
            Assert.Equal("r1 in g1 (#12 my.mod.wave)", row.ToString());
        }
    }
}
