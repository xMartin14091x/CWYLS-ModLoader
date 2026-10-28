using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Trace;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// The pure half of the sys:"ui" layer: the hierarchy-path rule and the line shape. Walking
    /// Transform.parent needs Unity and is covered in game instead.
    /// </summary>
    public class TraceUiTests
    {
        /// <summary>A hand-cranked clock, so the tests never depend on wall time.</summary>
        private sealed class FakeClock
        {
            public long Now;

            public long Read() => Now;
        }

        [Fact]
        public void A_short_path_is_joined_whole()
        {
            Assert.Equal(
                "Canvas/RoomUI/SettingButton",
                UiPath.Build(new[] { "Canvas", "RoomUI", "SettingButton" }));
        }

        [Fact]
        public void A_single_segment_has_no_separator()
        {
            Assert.Equal("Canvas", UiPath.Build(new[] { "Canvas" }));
        }

        [Fact]
        public void Nothing_to_join_is_an_empty_path()
        {
            Assert.Equal(string.Empty, UiPath.Build(null));
            Assert.Equal(string.Empty, UiPath.Build(Array.Empty<string?>()));
        }

        [Fact]
        public void A_deep_hierarchy_keeps_the_segments_nearest_the_control()
        {
            var names = new List<string?>();
            for (var i = 0; i < 20; i++)
            {
                names.Add("n" + i);
            }

            var path = UiPath.Build(names);

            // 20 segments, cap 12: the eight nearest the root are dropped, and the cut is marked.
            Assert.StartsWith(UiPath.Elision + "/n8/n9/", path);
            Assert.EndsWith("/n19", path);
            Assert.Equal(UiPath.MaxSegments + 1, path.Split('/').Length);
        }

        [Fact]
        public void Exactly_the_cap_is_not_elided()
        {
            var names = new List<string?>();
            for (var i = 0; i < UiPath.MaxSegments; i++)
            {
                names.Add("n" + i);
            }

            Assert.DoesNotContain(UiPath.Elision, UiPath.Build(names));
        }

        [Fact]
        public void A_nameless_object_becomes_a_question_mark()
        {
            Assert.Equal("Canvas/?/Button", UiPath.Build(new[] { "Canvas", null, "Button" }));
        }

        [Fact]
        public void A_ui_line_carries_path_label_and_value()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Ui(
                new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9)),
                frame: 1200,
                realtime: 42.5,
                gameClock: "13:45:01",
                controlType: "Button",
                eventName: "press",
                path: "Canvas/RoomUI/SettingButton",
                label: "Settings",
                value: null,
                threadId: 1);

            Assert.Equal(
                "{\"t\":\"2026-09-22T13:45:01.123+09:00\",\"rt\":42.5,\"gt\":\"13:45:01\",\"f\":1200," +
                "\"sys\":\"ui\",\"cls\":\"Button\",\"m\":\"press\",\"path\":\"Canvas/RoomUI/SettingButton\"," +
                "\"label\":\"Settings\",\"value\":null,\"th\":1}",
                line);
        }

        [Fact]
        public void A_value_change_right_after_a_pointer_event_is_the_user()
        {
            var clock = new FakeClock();
            var intent = new InteractionSource(clock.Read);

            intent.NoteUserIntent("Canvas/Settings/VolumeSlider");
            clock.Now += 1900;

            Assert.Equal(InteractionSource.User, intent.Classify("Canvas/Settings/VolumeSlider", 2d));
            Assert.True(intent.WasUserIntent("Canvas/Settings/VolumeSlider", 2d));
        }

        [Fact]
        public void A_value_change_long_after_the_pointer_event_is_the_game()
        {
            var clock = new FakeClock();
            var intent = new InteractionSource(clock.Read);

            intent.NoteUserIntent("Canvas/Settings/VolumeSlider");
            clock.Now += 2001;

            // This is the case that mattered: UI_FacilityMusic/ProgressBar/Slider is driven every
            // frame by the music player and must never read as the user dragging it.
            Assert.Equal(InteractionSource.Code, intent.Classify("Canvas/Settings/VolumeSlider", 2d));
        }

        [Fact]
        public void The_boundary_of_the_window_still_counts_as_the_user()
        {
            var clock = new FakeClock();
            var intent = new InteractionSource(clock.Read);

            intent.NoteUserIntent("slider");
            clock.Now += 2000;

            Assert.Equal(InteractionSource.User, intent.Classify("slider", 2d));
        }

        [Fact]
        public void A_control_never_touched_is_the_game()
        {
            var intent = new InteractionSource(new FakeClock().Read);

            Assert.Equal(InteractionSource.Code, intent.Classify("UI_FacilityMusic/ProgressBar/Slider", 2d));
            Assert.Equal(InteractionSource.Code, intent.Classify(null, 2d));
            Assert.Equal(InteractionSource.Code, intent.Classify(string.Empty, 2d));
            Assert.Equal(0, intent.TrackedKeys);
        }

        [Fact]
        public void Controls_are_classified_independently()
        {
            var clock = new FakeClock();
            var intent = new InteractionSource(clock.Read);

            intent.NoteUserIntent("toggleA");
            clock.Now += 500;
            intent.NoteUserIntent("toggleB");
            clock.Now += 800;

            // toggleA is 1300 ms old, toggleB only 800: at a one-second window they differ.
            Assert.Equal(InteractionSource.Code, intent.Classify("toggleA", 1d));
            Assert.Equal(InteractionSource.User, intent.Classify("toggleB", 1d));
            Assert.Equal(2, intent.TrackedKeys);
        }

        [Fact]
        public void A_zero_window_never_attributes_anything_to_the_user()
        {
            var intent = new InteractionSource(new FakeClock().Read);

            intent.NoteUserIntent("button");

            Assert.Equal(InteractionSource.Code, intent.Classify("button", 0d));
        }

        [Fact]
        public void A_ui_line_carries_its_source_and_aggregate_count()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Ui(
                new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9)),
                frame: 1200, realtime: 42.5, gameClock: "13:45:01",
                controlType: "Slider", eventName: "valueChanged",
                path: "UI_FacilityMusic/ProgressBar/Slider", label: null, value: "0.42",
                threadId: 1, source: InteractionSource.Code, aggregateCount: 60);

            Assert.EndsWith("\"value\":\"0.42\",\"th\":1,\"src\":\"code\",\"agg\":true,\"count\":60}", line);
        }

        [Fact]
        public void A_ui_line_escapes_what_the_user_typed()
        {
            var builder = new TraceLineBuilder();

            var line = builder.Ui(
                DateTimeOffset.Now, 1, 0d, null, "TMP_InputField", "endEdit",
                "Canvas/Note/Input", null, "買い物 \"list\"\nline 2", 1);

            Assert.Contains("\"value\":\"買い物 \\\"list\\\"\\nline 2\"", line);
            Assert.DoesNotContain("\n", line);
        }
    }
}
