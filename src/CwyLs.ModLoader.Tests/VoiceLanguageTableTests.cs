using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>Language voice sets: the runtime state of the language sets - lookup, load order, rescans.</summary>
    public class VoiceLanguageTableTests
    {
        private static LanguageVoicePlan PlanOf(params (string Pack, string[] Paths)[] packs) =>
            LanguageVoicePlan.Build(packs.Select(p =>
                new KeyValuePair<string, PackContentIndex>(p.Pack, PackContentIndex.Build(p.Paths))));

        private static LanguageVoiceTable ThaiTable(params string[] names) =>
            new LanguageVoiceTable(PlanOf(("my.thai", names.Select(n => "audio/voice/th/" + n + ".ogg").ToArray())));

        /// <summary>Takes one named step, loads it with <paramref name="clip"/>, and returns whether it was kept.</summary>
        private static bool Load(LanguageVoiceTable table, string name, object clip)
        {
            Assert.True(table.Prioritize("th", name));
            var step = Assert.Single(table.TakeNext("th", 1, includeBackground: false));
            Assert.Equal(name, step.Name);
            return table.Complete("th", step, clip, out _);
        }

        [Fact]
        public void Lookup_says_what_the_set_of_the_language_in_force_holds()
        {
            var table = ThaiTable("Voice_A", "Voice_B");
            var clip = new object();

            Assert.Equal(LanguageVoiceOutcome.NoSet, table.Lookup("en", "Voice_A").Outcome);
            Assert.Equal(LanguageVoiceOutcome.NoSet, table.Lookup("", "Voice_A").Outcome);
            Assert.Equal(LanguageVoiceOutcome.NoSet, table.Lookup(null, "Voice_A").Outcome);
            Assert.Equal(LanguageVoiceOutcome.NotInSet, table.Lookup("th", "Voice_Z").Outcome);
            Assert.Equal(LanguageVoiceOutcome.NotInSet, table.Lookup("th", "voice_a").Outcome); // case-sensitive, like the game's cache

            var waiting = table.Lookup("th", "Voice_A");
            Assert.Equal(LanguageVoiceOutcome.NotLoaded, waiting.Outcome);
            Assert.True(waiting.IsFallback);
            Assert.False(waiting.HasClip);
            Assert.Equal("my.thai", waiting.Step!.PackId);

            Assert.True(Load(table, "Voice_A", clip));

            var loaded = table.Lookup("TH", "Voice_A");
            Assert.Equal(LanguageVoiceOutcome.Loaded, loaded.Outcome);
            Assert.True(loaded.HasClip);
            Assert.Same(clip, loaded.Clip);
            Assert.Equal("th", loaded.Code);

            // The same name under a language without a set is the game's business.
            Assert.Equal(LanguageVoiceOutcome.NoSet, table.Lookup("ja", "Voice_A").Outcome);
        }

        [Fact]
        public void A_failed_file_is_reported_and_not_retried_by_the_background()
        {
            var table = ThaiTable("Voice_A");
            var step = Assert.Single(table.TakeNext("th", 5, includeBackground: true));

            Assert.True(table.Fail("th", step, "file not found"));

            var lookup = table.Lookup("th", "Voice_A");
            Assert.Equal(LanguageVoiceOutcome.Failed, lookup.Outcome);
            Assert.Equal("file not found", lookup.Failure);
            Assert.True(lookup.IsFallback);
            Assert.Empty(table.TakeNext("th", 5, includeBackground: true));
            Assert.False(table.Prioritize("th", "Voice_A"));
            Assert.Equal(new[] { new KeyValuePair<string, string>("Voice_A", "file not found") }, table.Failures("th", 5));
            Assert.True(table.CountsFor("th").Settled);
        }

        [Fact]
        public void A_null_clip_counts_as_a_failure()
        {
            var table = ThaiTable("Voice_A");
            var step = table.TakeNext("th", 1, includeBackground: true).Single();

            Assert.False(table.Complete("th", step, null, out var reason));
            Assert.Equal("the loader returned no clip", reason);
            Assert.Equal(LanguageVoiceOutcome.Failed, table.Lookup("th", "Voice_A").Outcome);
        }

        [Fact]
        public void Background_loading_follows_plan_order_and_only_when_allowed()
        {
            var table = ThaiTable("Voice_A", "Voice_B", "Voice_C");

            // Before the room is ready only asked-for clips load.
            Assert.False(table.HasWaiting("th", includeBackground: false));
            Assert.Empty(table.TakeNext("th", 3, includeBackground: false));
            Assert.True(table.HasWaiting("th", includeBackground: true));

            Assert.Equal(new[] { "Voice_A", "Voice_B" }, table.TakeNext("th", 2, includeBackground: true).Select(s => s.Name));
            Assert.Equal(new[] { "Voice_C" }, table.TakeNext("th", 2, includeBackground: true).Select(s => s.Name));
            Assert.Empty(table.TakeNext("th", 2, includeBackground: true));
            Assert.False(table.HasWaiting("th", includeBackground: true));

            var counts = table.CountsFor("th");
            Assert.Equal(3, counts.Loading);
            Assert.False(counts.Settled);
        }

        [Fact]
        public void A_line_that_fell_back_jumps_the_queue()
        {
            var table = ThaiTable("Voice_A", "Voice_B", "Voice_C", "Voice_D");

            Assert.True(table.Prioritize("th", "Voice_C"));
            Assert.True(table.HasWaiting("th", includeBackground: false));

            Assert.Equal(new[] { "Voice_C", "Voice_A" }, table.TakeNext("th", 2, includeBackground: true).Select(s => s.Name));
            Assert.Equal(new[] { "Voice_B", "Voice_D" }, table.TakeNext("th", 5, includeBackground: true).Select(s => s.Name));
        }

        [Fact]
        public void A_scene_prefetch_goes_first_as_a_block_in_reading_order()
        {
            var table = ThaiTable("Voice_A", "Voice_B", "Voice_C", "Voice_D", "Voice_E");

            Assert.True(table.Prioritize("th", "Voice_E"));
            Assert.Equal(2, table.Prioritize("th", new[] { "Voice_D", "Voice_Unknown", null, "Voice_B", "Voice_D" }));

            Assert.Equal(
                new[] { "Voice_D", "Voice_B", "Voice_E" },
                table.TakeNext("th", 3, includeBackground: false).Select(s => s.Name));
        }

        [Fact]
        public void Only_the_asked_language_loads()
        {
            var table = new LanguageVoiceTable(PlanOf(("p", new[] { "audio/voice/th/Voice_A.ogg", "audio/voice/ja/Voice_A.ogg" })));

            var thai = table.TakeNext("th", 10, includeBackground: true);
            Assert.Equal("audio/voice/th/Voice_A.ogg", Assert.Single(thai).RelativePath);
            Assert.Equal(LanguageVoiceState.Pending, table.StateOf("ja", "Voice_A"));
            Assert.Empty(table.TakeNext("en", 10, includeBackground: true));
            Assert.Empty(table.TakeNext("", 10, includeBackground: true));
        }

        [Fact]
        public void A_rescan_keeps_unchanged_clips_and_refuses_stale_loads()
        {
            var table = ThaiTable("Voice_A", "Voice_B", "Voice_C");
            var clipA = new object();
            Assert.True(Load(table, "Voice_A", clipA));
            var inFlightB = table.TakeNext("th", 1, includeBackground: true).Single();
            Assert.Equal("Voice_B", inFlightB.Name);

            // The new scan: A unchanged, B now comes from a fix pack, C is gone, D is new.
            var rebuild = table.Rebuild(PlanOf(
                ("my.thai", new[] { "audio/voice/th/Voice_A.ogg", "audio/voice/th/Voice_D.ogg" }),
                ("fix", new[] { "audio/voice/th/Voice_B.ogg" })));

            Assert.Equal(1, rebuild.Kept);
            Assert.Equal(2, rebuild.Added);
            Assert.Equal(2, rebuild.Dropped);

            Assert.Same(clipA, table.Lookup("th", "Voice_A").Clip);
            Assert.Equal(LanguageVoiceOutcome.NotInSet, table.Lookup("th", "Voice_C").Outcome);

            // The load started before the rescan is for a file the set no longer uses.
            Assert.False(table.Complete("th", inFlightB, new object(), out var reason));
            Assert.Contains("packs rescanned", reason);
            Assert.Equal(LanguageVoiceOutcome.NotLoaded, table.Lookup("th", "Voice_B").Outcome);
            Assert.Equal("fix", table.Lookup("th", "Voice_B").Step!.PackId);
        }

        [Fact]
        public void A_kept_loading_clip_still_lands_after_a_rescan()
        {
            var table = ThaiTable("Voice_A");
            var step = table.TakeNext("th", 1, includeBackground: true).Single();

            table.Rebuild(table.Plan);

            Assert.True(table.Complete("th", step, new object(), out _));
            Assert.Equal(LanguageVoiceOutcome.Loaded, table.Lookup("th", "Voice_A").Outcome);
        }

        [Fact]
        public void A_completion_for_a_clip_that_was_not_loading_is_refused()
        {
            var table = ThaiTable("Voice_A");
            var step = table.Plan.Sets.Single().Steps.Single();

            Assert.False(table.Complete("th", step, new object(), out var reason));
            Assert.Contains("not loading", reason);
        }

        [Fact]
        public void Reset_sends_a_loaded_clip_back_to_waiting()
        {
            var table = ThaiTable("Voice_A");
            Assert.True(Load(table, "Voice_A", new object()));

            Assert.True(table.Reset("th", "Voice_A"));
            Assert.Equal(LanguageVoiceOutcome.NotLoaded, table.Lookup("th", "Voice_A").Outcome);
            Assert.False(table.Reset("th", "Voice_A"));
            Assert.True(table.Prioritize("th", "Voice_A"));
        }

        [Fact]
        public void Counts_and_samples_follow_the_states()
        {
            var table = ThaiTable("Voice_A", "Voice_B", "Voice_C", "Voice_D");
            var a = new object();
            var c = new object();
            Assert.True(Load(table, "Voice_A", a));
            Assert.True(Load(table, "Voice_C", c));
            var b = table.TakeNext("th", 1, includeBackground: true).Single();
            Assert.Equal("Voice_B", b.Name);

            var counts = table.CountsFor("th");
            Assert.Equal(4, counts.Total);
            Assert.Equal(2, counts.Loaded);
            Assert.Equal(1, counts.Loading);
            Assert.Equal(1, counts.Pending);
            Assert.Equal(0, counts.Failed);
            Assert.Equal("th: 2/4 loaded, 1 loading, 1 waiting, 0 failed", counts.ToString());

            var sample = table.LoadedClips("th", 5);
            Assert.Equal(new[] { "Voice_A", "Voice_C" }, sample.Select(p => p.Key));
            Assert.Same(a, sample[0].Value);
            Assert.Single(table.LoadedClips("th", 1));
            Assert.Empty(table.LoadedClips("en", 5));
            Assert.Equal(0, table.CountsFor("en").Total);
            Assert.False(table.CountsFor("en").Settled);
        }
    }
}
