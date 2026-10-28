using System.Collections.Generic;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Subtitles;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// P2-2: the request, the resolved line and the id generator - everything a
    /// <c>Satone.Say</c> call decides before it touches the game.
    /// </summary>
    public class SatoneSayModelTests
    {
        private const string English = "Hello from the Mod Loader.";
        private const string Thai = "สวัสดีจาก Mod Loader";
        private const string Japanese = "モッドローダーからこんにちは。";

        /// <summary>
        /// A pack-declared language's code. The framework has no Thai constant (languages come
        /// from packs), so the test names the code it expects the table key to normalise to.
        /// </summary>
        private const string ThaiCode = "th";

        [Fact]
        public void A_blank_line_is_refused_with_a_reason()
        {
            Assert.False(SayRequest.For("   ").Validate(out var problem));
            Assert.Contains("needs text", problem);

            Assert.True(SayRequest.For(English).Validate(out var none));
            Assert.Equal(string.Empty, none);
        }

        [Fact]
        public void A_voice_name_the_game_could_never_look_up_is_refused()
        {
            var request = new SayRequest { Text = English, VoiceName = "Voice CWY Test" };

            Assert.False(request.Validate(out var problem));
            Assert.Contains("Voice CWY Test", problem);

            var ok = new SayRequest { Text = English, VoiceName = "Voice_CWY_Test" };
            Assert.True(ok.Validate(out _));
            Assert.True(ok.HasVoiceName);
        }

        [Fact]
        public void An_out_of_range_look_and_an_oversized_text_are_refused()
        {
            var look = new SayRequest { Text = English, LookScale = 1.5f };
            Assert.False(look.Validate(out var lookProblem));
            Assert.Contains("LookScale", lookProblem);

            var long_ = new SayRequest { Text = new string('a', SayRequest.MaxTextLength + 1) };
            Assert.False(long_.Validate(out var lengthProblem));
            Assert.Contains(SayRequest.MaxTextLength.ToString(), lengthProblem);
        }

        [Fact]
        public void The_text_table_carries_the_line_its_japanese_column_and_every_translation()
        {
            var request = new SayRequest
            {
                Text = English,
                TextJa = Japanese,
                Texts = new Dictionary<string, string> { ["TH"] = Thai }
            };

            var texts = request.BuildTexts();

            Assert.Equal(English, texts[SubtitleLanguages.English]);
            Assert.Equal(Japanese, texts[SubtitleLanguages.Japanese]);
            Assert.Equal(Thai, texts[ThaiCode]);
            Assert.Equal(3, texts.Count);
        }

        [Fact]
        public void Without_a_japanese_text_the_japanese_column_repeats_the_line()
        {
            var texts = SayRequest.For(English).BuildTexts();

            Assert.Equal(English, texts[SubtitleLanguages.Japanese]);
            Assert.Equal(English, texts[SubtitleLanguages.English]);
        }

        [Fact]
        public void An_explicit_translation_outranks_the_default_for_the_same_language()
        {
            var request = new SayRequest
            {
                Text = "was written in Japanese",
                Language = "ja-JP",
                Texts = new Dictionary<string, string> { ["ja"] = Japanese }
            };

            Assert.Equal(SubtitleLanguages.Japanese, request.NormalizedLanguage);
            Assert.Equal(Japanese, request.BuildTexts()[SubtitleLanguages.Japanese]);
        }

        [Fact]
        public void Ids_are_monotonic_and_spell_the_group_row_and_voice_names()
        {
            var ids = new SayIdGenerator();

            Assert.Equal(1, ids.Next());
            Assert.Equal(2, ids.Next());
            Assert.Equal(2, ids.Last);

            Assert.Equal("cwy_say_2", SayIdGenerator.GroupId(2));
            Assert.Equal("cwy_say_2_001", SayIdGenerator.LocalizationId(2));
            Assert.Equal("CWY_Say_2", SayIdGenerator.VoiceName(2));
            Assert.True(SayIdGenerator.IsSayGroup("cwy_say_2"));
            Assert.False(SayIdGenerator.IsSayGroup("motion_scenario_001"));
        }

        [Fact]
        public void Episode_numbers_start_far_above_the_games_own_motion_scenarios()
        {
            // The game's MotionScenario groups run 1..29 in v1.17.2 and it finds a group by
            // (ScenarioType, EpisodeNumber), so a collision would play the wrong line.
            Assert.Equal(9001f, SayIdGenerator.EpisodeFor(1));
            Assert.Equal(9500f, SayIdGenerator.EpisodeFor(500));
            Assert.True(SayIdGenerator.EpisodeFor(1) > 29f);
        }

        [Fact]
        public void A_resolved_line_carries_the_ids_the_rows_will_use()
        {
            var request = new SayRequest
            {
                Text = English,
                Texts = new Dictionary<string, string> { ["th"] = Thai },
                Facial = "smile",
                LookScale = 1f
            };

            var line = new SayLine(7, request, "Voice_CWY_Test", facialId: 4, motionId: 51, queuedAt: 12d);

            Assert.Equal(7, line.Id);
            Assert.Equal("cwy_say_7", line.GroupId);
            Assert.Equal("cwy_say_7_001", line.LocalizationId);
            Assert.Equal(9007f, line.EpisodeNumber);
            Assert.True(line.HasVoice);
            Assert.Equal(4, line.FacialId);
            Assert.Equal(51, line.MotionId);
            Assert.Equal(1f, line.LookScale);
            Assert.True(line.TryText("th", out var thai));
            Assert.Equal(Thai, thai);
            Assert.False(line.TryText("ko", out var korean));
            Assert.Equal(string.Empty, korean);
        }

        [Fact]
        public void A_line_without_a_clip_is_text_only_and_waits_from_when_it_was_queued()
        {
            var line = new SayLine(1, SayRequest.For(English), voiceName: null, facialId: -1, motionId: -1, queuedAt: 5d);

            Assert.False(line.HasVoice);
            Assert.Equal(string.Empty, line.VoiceName);
            Assert.Equal(SayLine.NoMotion, line.FacialId);
            Assert.Null(line.LookScale);
            Assert.Equal(3d, line.WaitedSeconds(8d));
            Assert.Equal(0d, line.WaitedSeconds(4d));
        }

        [Fact]
        public void Long_text_is_shortened_for_the_log_line_only()
        {
            var line = new SayLine(1, SayRequest.For(new string('x', 200)), null, -1, -1, 0d);

            Assert.Equal(20, line.ShortText(20).Length);
            Assert.EndsWith("…", line.ShortText(20));
            Assert.Equal(200, line.Text.Length);
            Assert.Contains("#1", line.ToString());
            Assert.Contains("voice=none", line.ToString());
        }

        [Fact]
        public async Task A_handle_completes_once_and_reports_how()
        {
            var line = new SayLine(3, SayRequest.For(English), "Voice_CWY_Test", -1, -1, 0d);
            var handle = new SayHandle(line);

            Assert.False(handle.IsCompleted);
            Assert.Equal(SayOutcome.Pending, handle.Outcome);

            Assert.True(handle.Complete(SayOutcome.Spoken, "ScenarioEnded cwy_say_3"));
            Assert.False(handle.Complete(SayOutcome.TimedOut, "too late"));

            Assert.True(handle.IsCompleted);
            Assert.Equal(SayOutcome.Spoken, handle.Outcome);
            Assert.Equal("ScenarioEnded cwy_say_3", handle.Detail);
            Assert.Equal(SayOutcome.Spoken, await handle.Completion);
        }

        [Fact]
        public void A_rejected_handle_is_born_finished_and_cannot_be_cancelled()
        {
            var line = new SayLine(4, SayRequest.For(English), null, -1, -1, 0d);

            var handle = SayHandle.Rejected(line, "the queue is full");

            Assert.True(handle.IsCompleted);
            Assert.Equal(SayOutcome.Rejected, handle.Outcome);
            Assert.False(handle.Cancel());
        }
    }
}
