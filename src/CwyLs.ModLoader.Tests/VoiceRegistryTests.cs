using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>P1-4: who wins when two registrations claim the same voice name.</summary>
    public class VoiceRegistryTests
    {
        private static VoiceEntry Pack(string name, string packId, object? clip = null) =>
            new VoiceEntry(name, packId, VoicePriority.Pack, "audio/voice/" + name + ".ogg", clip);

        private static VoiceEntry Runtime(string name, object? clip = null, string source = VoiceRegistry.RuntimeSource) =>
            new VoiceEntry(name, source, VoicePriority.Runtime, null, clip);

        [Fact]
        public void Registers_a_free_name()
        {
            var registry = new VoiceRegistry();
            var clip = new object();

            var result = registry.Register(Pack("Voice_A", "cwy.example", clip));

            Assert.Equal(VoiceRegistrationOutcome.Added, result.Outcome);
            Assert.True(result.Accepted);
            Assert.Null(result.Previous);
            Assert.Same(clip, registry.Resolve("Voice_A")!.Clip);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void A_later_pack_overrides_an_earlier_one()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_A", "cwy.first"));

            var result = registry.Register(Pack("Voice_A", "cwy.second"));

            Assert.Equal(VoiceRegistrationOutcome.Replaced, result.Outcome);
            Assert.Equal("cwy.first", result.Previous!.Source);
            Assert.Equal("cwy.second", registry.Resolve("Voice_A")!.Source);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void A_runtime_registration_overrides_a_pack()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_A", "cwy.example"));

            var result = registry.Register(Runtime("Voice_A"));

            Assert.Equal(VoiceRegistrationOutcome.Replaced, result.Outcome);
            Assert.True(registry.Resolve("Voice_A")!.IsRuntime);
            Assert.Equal(1, registry.CountOf(VoicePriority.Runtime));
            Assert.Equal(0, registry.CountOf(VoicePriority.Pack));
        }

        [Fact]
        public void A_pack_never_overrides_a_runtime_registration()
        {
            var registry = new VoiceRegistry();
            registry.Register(Runtime("Voice_A", source: "my.mod"));

            var result = registry.Register(Pack("Voice_A", "cwy.example"));

            Assert.Equal(VoiceRegistrationOutcome.KeptExisting, result.Outcome);
            Assert.False(result.Accepted);
            Assert.Equal("my.mod", result.Previous!.Source);
            Assert.Equal("my.mod", registry.Resolve("Voice_A")!.Source);
        }

        [Fact]
        public void KeepPack_defers_to_a_pack_clip()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_A", "cwy.example"));

            var result = registry.Register(Runtime("Voice_A"), keepPack: true);

            Assert.Equal(VoiceRegistrationOutcome.KeptExisting, result.Outcome);
            Assert.Contains("keepPack", result.Reason);
            Assert.Equal("cwy.example", registry.Resolve("Voice_A")!.Source);
        }

        [Fact]
        public void KeepPack_still_takes_a_free_name_and_replaces_another_runtime_clip()
        {
            var registry = new VoiceRegistry();

            var first = registry.Register(Runtime("Voice_A", source: "mod.one"), keepPack: true);
            var second = registry.Register(Runtime("Voice_A", source: "mod.two"), keepPack: true);

            Assert.Equal(VoiceRegistrationOutcome.Added, first.Outcome);
            Assert.Equal(VoiceRegistrationOutcome.Replaced, second.Outcome);
            Assert.Equal("mod.two", registry.Resolve("Voice_A")!.Source);
        }

        [Fact]
        public void An_invalid_name_is_refused_and_changes_nothing()
        {
            var registry = new VoiceRegistry();

            var result = registry.Register(new VoiceEntry("Voice A", "cwy.example", VoicePriority.Pack));

            Assert.Equal(VoiceRegistrationOutcome.Invalid, result.Outcome);
            Assert.False(result.Accepted);
            Assert.Contains(VoiceNameRules.Pattern, result.Reason);
            Assert.Equal(0, registry.Count);
            Assert.Null(registry.Resolve("Voice A"));
        }

        [Fact]
        public void Names_are_case_sensitive_like_the_games_own_cache()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_A", "cwy.example"));

            registry.Register(Pack("voice_a", "cwy.example"));

            Assert.Equal(2, registry.Count);
            Assert.Equal(new[] { "Voice_A", "voice_a" }, registry.Names);
        }

        [Fact]
        public void Unregister_returns_what_it_removed()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_A", "cwy.example"));

            var removed = registry.Unregister("Voice_A");

            Assert.NotNull(removed);
            Assert.Equal("cwy.example", removed!.Source);
            Assert.Null(registry.Resolve("Voice_A"));
            Assert.Null(registry.Unregister("Voice_A"));
            Assert.Null(registry.Unregister(null));
        }

        [Fact]
        public void Snapshot_is_a_detached_ordinal_copy()
        {
            var registry = new VoiceRegistry();
            registry.Register(Pack("Voice_B", "cwy.example"));
            registry.Register(Pack("Voice_A", "cwy.example"));

            var snapshot = registry.Snapshot();
            registry.Clear();

            Assert.Equal(new[] { "Voice_A", "Voice_B" }, new[] { snapshot[0].Name, snapshot[1].Name });
            Assert.Equal(2, snapshot.Count);
            Assert.Equal(0, registry.Count);
            Assert.Empty(registry.Names);
        }
    }
}
