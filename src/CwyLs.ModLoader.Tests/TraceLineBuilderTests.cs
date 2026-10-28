using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class TraceLineBuilderTests
    {
        private static readonly DateTimeOffset When =
            new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9));

        private readonly TraceLineBuilder _builder = new TraceLineBuilder();

        [Fact]
        public void Header_is_the_startup_self_check()
        {
            Assert.Equal(
                "{\"header\":true,\"t\":\"2026-09-22T13:45:01.123+09:00\",\"level\":\"Trace\",\"patched\":1696,\"failed\":3,\"classes\":238,\"ms\":4210.5}",
                _builder.Header(When, TraceLevel.Trace, patched: 1696, failed: 3, classes: 238, milliseconds: 4210.47));
        }

        [Fact]
        public void Call_has_the_documented_shape()
        {
            var line = _builder.Call(
                When,
                frame: 1200,
                realtime: 42.3456,
                gameClock: "13:45:01",
                systemName: "Bulbul",
                className: "HeroineService",
                methodName: "PlayVoice",
                args: new[] { "Voice_Tutorial_1_001", "true" },
                threadId: 1,
                instance: "HeroineService(RoomScene)");

            Assert.Equal(
                "{\"t\":\"2026-09-22T13:45:01.123+09:00\",\"rt\":42.346,\"gt\":\"13:45:01\",\"f\":1200," +
                "\"sys\":\"Bulbul\",\"cls\":\"HeroineService\",\"m\":\"PlayVoice\"," +
                "\"args\":[\"Voice_Tutorial_1_001\",\"true\"],\"th\":1,\"inst\":\"HeroineService(RoomScene)\"}",
                line);
        }

        [Fact]
        public void An_empty_namespace_is_written_as_root_and_a_static_call_has_a_null_instance()
        {
            var line = _builder.Call(When, 1, 0d, null, null, "MusicService", "Stop", null, 7, null);

            Assert.Contains("\"sys\":\"root\"", line);
            Assert.Contains("\"gt\":null", line);
            Assert.Contains("\"args\":[]", line);
            Assert.Contains("\"inst\":null", line);
            Assert.Contains("\"th\":7", line);
        }

        [Fact]
        public void Return_value_and_note_are_only_written_when_present()
        {
            var plain = _builder.Call(When, 1, 0d, null, "Bulbul", "MusicService", "Stop", null, 1, null);
            Assert.DoesNotContain("\"ret\"", plain);
            Assert.DoesNotContain("\"note\"", plain);

            var rich = _builder.Call(
                When, 1, 0d, null, "Bulbul", "MusicService", "AddMusicItem", null, 1, null,
                returnValue: "true", note: "suppressed 14 calls");

            Assert.EndsWith("\"ret\":\"true\",\"note\":\"suppressed 14 calls\"}", rich);
        }

        [Fact]
        public void Argument_text_is_json_escaped()
        {
            // Game text is Japanese with quotes in it; an unescaped line would break every consumer.
            var line = _builder.Call(
                When, 1, 0d, null, "Bulbul", "ScenarioReader", "CommandVoiceText",
                new[] { "say \"hi\"\n\tさとね", "back\\slash" }, 1, null);

            Assert.Contains("\"args\":[\"say \\\"hi\\\"\\n\\tさとね\",\"back\\\\slash\"]", line);
            Assert.DoesNotContain("\n", line);
        }

        [Fact]
        public void Footer_closes_the_file()
        {
            Assert.Equal(
                "{\"footer\":true,\"t\":\"2026-09-22T13:45:01.123+09:00\",\"rt\":90.5,\"f\":5400,\"lines\":12345," +
                "\"suppressed\":67,\"pollingCount\":1,\"polling\":[\"AudioPlayer.Stop\"]," +
                "\"bus\":{\"emitted\":0,\"dropped\":0},\"unpatchedCount\":0,\"unpatched\":[]," +
                "\"hookNs\":null,\"queue\":null}",
                _builder.Footer(When, frame: 5400, realtime: 90.5, lines: 12345, suppressed: 67,
                    pollingMethods: new[] { "AudioPlayer.Stop" }));
        }

        [Fact]
        public void Realtime_is_rounded_and_never_NaN()
        {
            Assert.Contains("\"rt\":0", _builder.Call(When, 0, double.NaN, null, "x", "C", "M", null, 1, null));
            Assert.Contains("\"rt\":1.235", _builder.Call(When, 0, 1.23456, null, "x", "C", "M", null, 1, null));
        }

        [Fact]
        public void Long_arguments_survive_as_a_flat_list()
        {
            IReadOnlyList<string> args = new List<string> { "a", "b", "c" };
            Assert.Contains("\"args\":[\"a\",\"b\",\"c\"]", _builder.Call(When, 0, 0d, null, "x", "C", "M", args, 1, null));
        }
    }
}
