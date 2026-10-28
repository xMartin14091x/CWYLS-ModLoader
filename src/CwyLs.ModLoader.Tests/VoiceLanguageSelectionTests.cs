using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Language voice sets: who answers a voice lookup (runtime, language set, pack, game), how a language switch
    /// changes the set in force, and the length comparison with the game's clips.
    /// </summary>
    public class VoiceLanguageSelectionTests
    {
        private static readonly VoiceEntry PackEntry = new VoiceEntry("Voice_A", "cwy.example", VoicePriority.Pack, clip: "pack-clip");
        private static readonly VoiceEntry RuntimeEntry = new VoiceEntry("Voice_A", VoiceRegistry.RuntimeSource, VoicePriority.Runtime, clip: "mod-clip");

        private static LanguageVoiceLookup Loaded() =>
            new LanguageVoiceLookup(LanguageVoiceOutcome.Loaded, "th", "Voice_A", null, "thai-clip", null);

        private static LanguageVoiceLookup Of(LanguageVoiceOutcome outcome) =>
            new LanguageVoiceLookup(outcome, "th", "Voice_A", null, null, null);

        [Fact]
        public void A_mod_beats_the_language_set_which_beats_a_pack_which_beats_the_game()
        {
            Assert.Equal(VoiceChoice.Runtime, LanguageVoiceRules.Choose(RuntimeEntry, Loaded()));
            Assert.Equal(VoiceChoice.Language, LanguageVoiceRules.Choose(PackEntry, Loaded()));
            Assert.Equal(VoiceChoice.Language, LanguageVoiceRules.Choose(null, Loaded()));
            Assert.Equal(VoiceChoice.Pack, LanguageVoiceRules.Choose(PackEntry, Of(LanguageVoiceOutcome.NoSet)));
            Assert.Equal(VoiceChoice.Game, LanguageVoiceRules.Choose(null, Of(LanguageVoiceOutcome.NoSet)));
            Assert.Equal(VoiceChoice.Game, LanguageVoiceRules.Choose(null, default));
        }

        [Theory]
        [InlineData(LanguageVoiceOutcome.NotLoaded)]
        [InlineData(LanguageVoiceOutcome.Failed)]
        [InlineData(LanguageVoiceOutcome.NotInSet)]
        public void A_set_clip_that_is_not_ready_never_makes_a_silent_line(LanguageVoiceOutcome outcome)
        {
            // The next clip in line plays for that one time: the pack's, else the game's own.
            Assert.Equal(VoiceChoice.Pack, LanguageVoiceRules.Choose(PackEntry, Of(outcome)));
            Assert.Equal(VoiceChoice.Game, LanguageVoiceRules.Choose(null, Of(outcome)));
        }

        [Fact]
        public void The_set_is_not_asked_when_a_mod_already_answers()
        {
            Assert.False(LanguageVoiceRules.AsksLanguageSet(RuntimeEntry));
            Assert.True(LanguageVoiceRules.AsksLanguageSet(PackEntry));
            Assert.True(LanguageVoiceRules.AsksLanguageSet(null));
        }

        [Fact]
        public void Default_lookup_reads_as_no_set_with_empty_strings()
        {
            LanguageVoiceLookup none = default;
            Assert.Equal(LanguageVoiceOutcome.NoSet, none.Outcome);
            Assert.Equal(string.Empty, none.Code);
            Assert.Equal(string.Empty, none.Name);
            Assert.False(none.HasClip);
            Assert.False(none.IsFallback);
        }

        [Fact]
        public void Thai_selected_resolves_the_set_and_any_other_language_resolves_the_game()
        {
            // The quiet smoke check proves the same through the real lookup; this is the rule itself.
            var names = new[] { "Voice_Tutorial_1_001", "Voice_Tutorial_1_002", "Voice_ClickHeroine_Word_Normal_012" };
            var table = new LanguageVoiceTable(LanguageVoicePlan.Build(new[]
            {
                new KeyValuePair<string, PackContentIndex>(
                    "my.thai", PackContentIndex.Build(names.Select(n => "audio/voice/th/" + n + ".ogg"))),
            }));

            var clips = new Dictionary<string, object>();
            foreach (var step in table.TakeNext("th", 10, includeBackground: true))
            {
                clips[step.Name] = "th:" + step.Name;
                Assert.True(table.Complete("th", step, clips[step.Name], out _));
            }

            foreach (var name in names)
            {
                var underThai = table.Lookup("th", name);
                Assert.Equal(VoiceChoice.Language, LanguageVoiceRules.Choose(null, underThai));
                Assert.Same(clips[name], underThai.Clip);

                foreach (var other in new[] { "en", "ja", "zh-hans", "ko", "" })
                {
                    Assert.Equal(VoiceChoice.Game, LanguageVoiceRules.Choose(null, table.Lookup(other, name)));
                }
            }
        }

        [Fact]
        public void Switching_language_turns_the_set_on_and_off_and_keeps_its_clips()
        {
            var table = new LanguageVoiceTable(LanguageVoicePlan.Build(new[]
            {
                new KeyValuePair<string, PackContentIndex>("my.thai", PackContentIndex.Build(new[] { "audio/voice/th/Voice_A.ogg" })),
            }));
            var clip = new object();
            var step = table.TakeNext("th", 1, includeBackground: true).Single();
            Assert.True(table.Complete("th", step, clip, out _));

            var voiceSwitch = new LanguageVoiceSwitch(code => table.Has(code));

            Assert.False(voiceSwitch.Observe("", out _));                // the save has not loaded yet
            Assert.False(voiceSwitch.Observe("ja", out _));              // no set: nothing to switch
            Assert.Equal(string.Empty, voiceSwitch.Active);

            Assert.True(voiceSwitch.Observe("th", out var previous));    // enter th
            Assert.Equal("th", voiceSwitch.Active);
            Assert.Equal(string.Empty, previous);
            Assert.False(voiceSwitch.Observe("TH", out _));              // same language, any spelling: no event

            Assert.True(voiceSwitch.Observe("en", out previous));        // leave th
            Assert.Equal(string.Empty, voiceSwitch.Active);
            Assert.Equal("th", previous);
            Assert.Equal("en", voiceSwitch.LastObserved);
            Assert.False(voiceSwitch.Observe("ja", out _));              // en -> ja: still no set

            // Leaving did not touch the table: back in th, the clip is still there, ready.
            Assert.True(voiceSwitch.Observe("thai", out _));
            Assert.Same(clip, table.Lookup(voiceSwitch.Active, "Voice_A").Clip);
            Assert.Equal(3, voiceSwitch.Changes);
        }

        [Fact]
        public void A_rescan_that_removes_the_set_turns_it_off_on_refresh()
        {
            var table = new LanguageVoiceTable(LanguageVoicePlan.Build(new[]
            {
                new KeyValuePair<string, PackContentIndex>("my.thai", PackContentIndex.Build(new[] { "audio/voice/th/Voice_A.ogg" })),
            }));
            var voiceSwitch = new LanguageVoiceSwitch(code => table.Has(code));
            Assert.True(voiceSwitch.Observe("th", out _));

            Assert.False(voiceSwitch.Refresh(out _));                    // nothing changed

            table.Rebuild(LanguageVoicePlan.Empty);
            Assert.True(voiceSwitch.Refresh(out var previous));
            Assert.Equal("th", previous);
            Assert.Equal(string.Empty, voiceSwitch.Active);
        }

        [Fact]
        public void Lengths_are_compared_with_the_game_and_the_overruns_ranked()
        {
            var lengths = new LanguageVoiceLengths();
            lengths.Add("Voice_Same", 3.0, 3.0);
            lengths.Add("Voice_Long", 5.0, 3.0);     // 1.67x, +2.0 s
            lengths.Add("Voice_Bit", 3.5, 3.0);      // 1.17x, +0.5 s
            lengths.Add("Voice_Short", 2.0, 3.0);    // 0.67x
            lengths.Add("Voice_New", 2.0, 0.0);      // a pack-only line: no game clip
            lengths.Add("Voice_Broken", double.NaN, 3.0);

            Assert.Equal(4, lengths.Compared);
            Assert.Equal(2, lengths.Unmatched);
            Assert.Equal(1, lengths.Longer);
            Assert.Equal(1, lengths.Shorter);
            Assert.Equal(13.5, lengths.LanguageSeconds, 6);
            Assert.Equal(12.0, lengths.GameSeconds, 6);
            Assert.Equal(2.0, lengths.MaxOverrunSeconds, 6);
            Assert.Equal("Voice_Long", lengths.MaxOverrunName);
            Assert.Equal(new[] { "Voice_Long", "Voice_Bit" }, lengths.TopOverruns.Select(p => p.Key));
            Assert.Equal(
                "4 compared: 1 longer than 1.25x, 1 shorter than 0.8x, total 13.5 s vs 12.0 s, max +2.0 s (Voice_Long)",
                lengths.Summarize());
        }

        [Fact]
        public void Only_the_largest_overruns_are_kept()
        {
            var lengths = new LanguageVoiceLengths();
            for (var i = 1; i <= LanguageVoiceLengths.KeptOverruns + 5; i++)
            {
                lengths.Add("Voice_" + i, 1.0 + i, 1.0);
            }

            Assert.Equal(LanguageVoiceLengths.KeptOverruns, lengths.TopOverruns.Count);
            Assert.Equal("Voice_" + (LanguageVoiceLengths.KeptOverruns + 5), lengths.TopOverruns[0].Key);
            Assert.Equal("no clips compared", new LanguageVoiceLengths().Summarize());
        }
    }
}
