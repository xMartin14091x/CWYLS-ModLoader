using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-UI: the rules a window spec must follow, the names derived from it, and the parts' heights.</summary>
    public class UiSpecRulesTests
    {
        private static CwyWindowSpec Valid()
        {
            var spec = new CwyWindowSpec("chat") { Title = "Satone" };
            spec.Parts.Add(new CwyChatSpec("log"));
            spec.Parts.Add(new CwyInputSpec("say") { Lines = 3 });
            var row = new CwyButtonsSpec("actions");
            row.Buttons.Add(new CwyButtonSpec("send", "Send"));
            spec.Parts.Add(row);
            return spec;
        }

        [Theory]
        [InlineData("chat", true)]
        [InlineData("my.window_2-b", true)]
        [InlineData("A", true)]
        [InlineData("", false)]
        [InlineData(".hidden", false)]
        [InlineData("has space", false)]
        [InlineData("slash/no", false)]
        [InlineData("ไทย", false)]
        public void Ids_follow_the_data_key_rule(string id, bool valid) =>
            Assert.Equal(valid, CwyWindowSpecRules.IsValidId(id));

        [Fact]
        public void An_id_is_at_most_48_characters()
        {
            Assert.True(CwyWindowSpecRules.IsValidId(new string('a', 48)));
            Assert.False(CwyWindowSpecRules.IsValidId(new string('a', 49)));
            Assert.False(CwyWindowSpecRules.IsValidId(null));
        }

        [Fact]
        public void Root_names_and_keys_carry_the_mod_and_the_window()
        {
            Assert.Equal("CWY.Mod.my.mod.chat", CwyWindowSpecRules.RootName("my.mod", "chat"));
            Assert.Equal("my.mod/chat", CwyWindowSpecRules.Key("my.mod", "chat"));
            Assert.StartsWith(CwyWindowSpecRules.RootPrefix, CwyWindowSpecRules.RootName("m", "w"));
        }

        [Fact]
        public void A_valid_spec_has_no_problems() => Assert.Empty(CwyWindowSpecRules.Validate(Valid()));

        [Fact]
        public void A_bad_window_id_is_reported()
        {
            var problems = CwyWindowSpecRules.Validate(new CwyWindowSpec("bad id"));
            Assert.Contains(problems, p => p.Contains("window id 'bad id'"));
        }

        [Fact]
        public void Duplicate_part_ids_are_refused_ignoring_case()
        {
            var spec = new CwyWindowSpec("w");
            spec.Parts.Add(new CwyTextSpec("Log"));
            spec.Parts.Add(new CwyChatSpec("log"));
            Assert.Contains(CwyWindowSpecRules.Validate(spec), p => p.Contains("another part already uses this id"));
        }

        [Fact]
        public void Negative_or_non_finite_sizes_are_refused()
        {
            var spec = new CwyWindowSpec("w") { Width = -1f, Height = float.NaN };
            spec.Parts.Add(new CwyTextSpec("t") { Height = float.PositiveInfinity });
            var problems = CwyWindowSpecRules.Validate(spec);
            Assert.Contains(problems, p => p.StartsWith("Width"));
            Assert.Contains(problems, p => p.StartsWith("Height"));
            Assert.Contains(problems, p => p.Contains("text part 't' Height"));
        }

        [Fact]
        public void Tab_rules()
        {
            var noStrip = new CwyWindowSpec("w");
            noStrip.Parts.Add(new CwyTextSpec("t") { Tab = 0 });
            Assert.Contains(CwyWindowSpecRules.Validate(noStrip), p => p.Contains("has no tab strip"));

            var twoStrips = new CwyWindowSpec("w");
            var a = new CwyTabsSpec("a");
            a.Labels.Add("one");
            var b = new CwyTabsSpec("b");
            b.Labels.Add("one");
            twoStrips.Parts.Add(a);
            twoStrips.Parts.Add(b);
            Assert.Contains(CwyWindowSpecRules.Validate(twoStrips), p => p.Contains("at most one tab strip"));

            var pastLast = new CwyWindowSpec("w");
            var strip = new CwyTabsSpec("tabs");
            strip.Labels.Add("one");
            strip.Labels.Add("two");
            pastLast.Parts.Add(strip);
            pastLast.Parts.Add(new CwyTextSpec("t") { Tab = 2 });
            Assert.Contains(CwyWindowSpecRules.Validate(pastLast), p => p.Contains("past the last tab (1)"));

            var stripOnTab = new CwyWindowSpec("w");
            var onTab = new CwyTabsSpec("tabs") { Tab = 0 };
            onTab.Labels.Add("one");
            stripOnTab.Parts.Add(onTab);
            Assert.Contains(CwyWindowSpecRules.Validate(stripOnTab), p => p.Contains("shows on every tab"));

            var empty = new CwyWindowSpec("w");
            empty.Parts.Add(new CwyTabsSpec("tabs"));
            Assert.Contains(CwyWindowSpecRules.Validate(empty), p => p.Contains("1-8 tabs"));
        }

        [Fact]
        public void Part_option_ranges()
        {
            var spec = new CwyWindowSpec("w");
            spec.Parts.Add(new CwyInputSpec("i") { Lines = 13, MaxLength = -1 });
            spec.Parts.Add(new CwyTextSpec("t") { MaxLines = 0 });
            spec.Parts.Add(new CwyChatSpec("c") { MaxBubbles = 1001 });
            spec.Parts.Add(new CwyButtonsSpec("b"));
            var problems = CwyWindowSpecRules.Validate(spec);
            Assert.Contains(problems, p => p.Contains("Lines must be 1-12"));
            Assert.Contains(problems, p => p.Contains("MaxLength"));
            Assert.Contains(problems, p => p.Contains("MaxLines"));
            Assert.Contains(problems, p => p.Contains("MaxBubbles"));
            Assert.Contains(problems, p => p.Contains("at least one button"));
        }

        [Fact]
        public void Button_ids_are_checked_and_unique()
        {
            var spec = new CwyWindowSpec("w");
            var row = new CwyButtonsSpec("b");
            row.Buttons.Add(new CwyButtonSpec("ok", "OK"));
            row.Buttons.Add(new CwyButtonSpec("OK", "again"));
            row.Buttons.Add(new CwyButtonSpec("not ok", "x"));
            spec.Parts.Add(row);
            var problems = CwyWindowSpecRules.Validate(spec);
            Assert.Contains(problems, p => p.Contains("two buttons use the id 'OK'"));
            Assert.Contains(problems, p => p.Contains("button id 'not ok'"));
        }

        [Fact]
        public void Fixed_heights_by_kind()
        {
            Assert.Equal(0f, CwyWindowSpecRules.FixedHeight(new CwyChatSpec("c")));
            Assert.Equal(0f, CwyWindowSpecRules.FixedHeight(new CwyTextSpec("t")));
            Assert.Equal(0f, CwyWindowSpecRules.FixedHeight(new CwyListSpec("l")));
            Assert.Equal(CwyWindowSpecRules.RowHeight, CwyWindowSpecRules.FixedHeight(new CwyInputSpec("i")));
            Assert.Equal(18f + (3 * 26f), CwyWindowSpecRules.FixedHeight(new CwyInputSpec("i") { Lines = 3 }));
            Assert.Equal(CwyWindowSpecRules.RowHeight, CwyWindowSpecRules.FixedHeight(new CwyButtonsSpec("b")));
            Assert.Equal(CwyWindowSpecRules.TabsHeight, CwyWindowSpecRules.FixedHeight(new CwyTabsSpec("t")));
            Assert.Equal(123f, CwyWindowSpecRules.FixedHeight(new CwyChatSpec("c") { Height = 123f }));
        }

        [Fact]
        public void The_minimum_height_fits_the_parts_and_never_drops_below_the_spec()
        {
            var spec = Valid();
            var expectedBody = 60f + 96f + 44f + (2 * StackLayout.Gap);
            Assert.Equal(System.Math.Max(spec.MinHeight, CwyWindowSpecRules.FrameHeight + expectedBody), CwyWindowSpecRules.MinimumHeight(spec));

            spec.MinHeight = 900f;
            Assert.Equal(900f, CwyWindowSpecRules.MinimumHeight(spec));

            var tiny = new CwyWindowSpec("w") { MinHeight = 10f, MinWidth = 10f };
            Assert.Equal(CwyWindowSpecRules.FloorMinHeight, CwyWindowSpecRules.MinimumHeight(tiny));
            Assert.Equal(CwyWindowSpecRules.FloorMinWidth, CwyWindowSpecRules.MinimumWidth(tiny));
        }

        [Fact]
        public void Spec_defaults_are_the_documented_ones()
        {
            var spec = new CwyWindowSpec("w");
            Assert.True(spec.RememberRect);
            Assert.True(spec.FollowHideUi);
            Assert.True(spec.CloseOnEscape);
            Assert.True(spec.HideOnClose);
            Assert.True(spec.Resizable);
            var input = new CwyInputSpec("i");
            Assert.True(input.SubmitOnEnter);
            Assert.True(input.ClearOnSubmit);
            Assert.Equal(1, input.Lines);
            Assert.Equal(-1, new CwyTextSpec("t").Tab);
            Assert.Equal(200, new CwyChatSpec("c").MaxBubbles);
            Assert.Single(CwyWindowSpecRules.Validate(new CwyWindowSpec(null!)), p => p.Contains("window id"));
        }
    }
}
