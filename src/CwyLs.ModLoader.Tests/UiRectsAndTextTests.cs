using System.Collections.Generic;
using CwyLs.ModLoader.Core.Console;
using CwyLs.ModLoader.Core.Ui;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>S-UI: remembered window places, first placement, rich-text escaping and the Thai mark check.</summary>
    public class UiRectsAndTextTests
    {
        // ---- remembered places -------------------------------------------------------------------

        [Fact]
        public void Places_round_trip_through_the_file()
        {
            var store = new WindowRectStore();
            Assert.True(store.Set("my.mod/chat", new WindowRect(1210f, 80.04f, 640f, 720f)));
            Assert.True(store.Set("a.mod/log", new WindowRect(10f, 20f, 300f, 200f)));
            Assert.True(store.IsDirty);

            var json = store.ToJson();
            Assert.False(store.IsDirty);
            Assert.True(json.IndexOf("a.mod/log") < json.IndexOf("my.mod/chat"));

            var back = WindowRectStore.Parse(json, out var problem);
            Assert.Equal(string.Empty, problem);
            Assert.Equal(2, back.Count);
            Assert.True(back.TryGet("my.mod/chat", out var rect));
            Assert.Equal(new WindowRect(1210f, 80f, 640f, 720f), rect);
        }

        [Fact]
        public void Setting_the_same_place_again_is_not_a_change()
        {
            var store = new WindowRectStore();
            store.Set("m/w", new WindowRect(1f, 2f, 300f, 200f));
            store.ToJson();
            Assert.True(store.Set("m/w", new WindowRect(1f, 2f, 300f, 200f)));
            Assert.False(store.IsDirty);
        }

        [Fact]
        public void Unusable_places_are_not_stored()
        {
            var store = new WindowRectStore();
            Assert.False(store.Set("m/w", new WindowRect(0f, 0f, 0f, 100f)));
            Assert.False(store.Set("m/w", new WindowRect(float.NaN, 0f, 100f, 100f)));
            Assert.False(store.Set(string.Empty, new WindowRect(0f, 0f, 100f, 100f)));
            Assert.False(store.Set("m/w", new WindowRect(0f, 0f, 1e9f, 100f)));
            Assert.Equal(0, store.Count);
        }

        [Fact]
        public void A_damaged_entry_is_skipped_and_named_the_rest_load()
        {
            const string json = "{\"schema\":1,\"windows\":{"
                + "\"good/one\":{\"left\":1,\"top\":2,\"width\":300,\"height\":200},"
                + "\"bad/size\":{\"left\":1,\"top\":2,\"width\":-5,\"height\":200},"
                + "\"bad/missing\":{\"left\":1},"
                + "\"bad/type\":\"nope\"}}";
            var store = WindowRectStore.Parse(json, out var problem);
            Assert.Equal(1, store.Count);
            Assert.Contains("bad/size", problem);
            Assert.Contains("bad/missing", problem);
            Assert.Contains("bad/type", problem);
        }

        [Theory]
        [InlineData(null, "")]
        [InlineData("   ", "")]
        [InlineData("not json", "not valid JSON")]
        [InlineData("[1,2]", "the file is not a JSON object")]
        [InlineData("{\"schema\":1}", "no \"windows\" object")]
        public void A_missing_or_broken_file_is_an_empty_store(string? json, string problemStart)
        {
            var store = WindowRectStore.Parse(json, out var problem);
            Assert.Equal(0, store.Count);
            Assert.StartsWith(problemStart, problem);
        }

        [Fact]
        public void Remove_and_clear_mark_the_store_dirty()
        {
            var store = new WindowRectStore();
            store.Set("m/a", new WindowRect(0f, 0f, 300f, 200f));
            store.Set("m/b", new WindowRect(0f, 0f, 300f, 200f));
            store.ToJson();
            Assert.True(store.Remove("m/a"));
            Assert.True(store.IsDirty);
            Assert.False(store.Remove("m/a"));
            Assert.Equal(1, store.Clear());
            Assert.Empty(store.Keys);
        }

        [Fact]
        public void A_remembered_place_is_kept_on_a_smaller_canvas()
        {
            var restored = WindowPlacement.Restore(new WindowRect(1700f, 900f, 640f, 720f), 1920f, 1080f, 420f, 280f);
            Assert.Equal(new WindowRect(1280f, 360f, 640f, 720f), restored);
        }

        [Fact]
        public void A_preferred_size_opens_centred_a_quarter_down()
        {
            var place = WindowPlacement.Default(1920f, 1080f, 640f, 720f, 420f, 280f);
            Assert.Equal(new WindowRect(640f, 90f, 640f, 720f), place);

            // No preference: exactly the console's default.
            Assert.Equal(
                WindowRectMath.Default(1920f, 1080f, 420f, 280f),
                WindowPlacement.Default(1920f, 1080f, 0f, 0f, 420f, 280f));

            // Too big for the canvas: shrunk to fit with the margin.
            var big = WindowPlacement.Default(1280f, 720f, 2000f, 2000f, 420f, 280f);
            Assert.Equal(1280f - 48f, big.Width);
            Assert.Equal(720f - 48f, big.Height);
        }

        // ---- rich text ---------------------------------------------------------------------------

        [Fact]
        public void Untrusted_text_is_shown_as_written()
        {
            Assert.Equal(string.Empty, CwyRichText.Escape(null));
            Assert.Equal("<noparse><b>hi</b></noparse>", CwyRichText.Escape("<b>hi</b>"));

            var sneaky = CwyRichText.Escape("a</noparse><color=red>b");
            Assert.StartsWith("<noparse>", sneaky);
            Assert.EndsWith("</noparse>", sneaky);
            Assert.Equal(1, Count(sneaky, "</noparse>"));
        }

        // ---- Thai marks --------------------------------------------------------------------------

        [Fact]
        public void The_sample_has_six_tone_marks_over_upper_vowels()
        {
            var marks = 0;
            for (var i = 1; i < ThaiMarkCheck.Sample.Length; i++)
            {
                if (ThaiMarkCheck.IsToneMark(ThaiMarkCheck.Sample[i]) && ThaiMarkCheck.IsUpperVowel(ThaiMarkCheck.Sample[i - 1]))
                {
                    marks++;
                }
            }

            Assert.Equal(26, ThaiMarkCheck.Sample.Length);
            Assert.Equal(6, marks);
        }

        [Fact]
        public void Stacked_marks_pass()
        {
            var report = ThaiMarkCheck.Check(Line(markMinY: 30f));
            Assert.True(report.Ok, report.Describe());
            Assert.Equal(1, report.MarksChecked);
            Assert.Equal(3, report.ThaiCharacters);
            Assert.Equal(3, report.Fonts["Base_TH"]);
        }

        [Fact]
        public void A_mark_sitting_on_the_vowel_fails()
        {
            var report = ThaiMarkCheck.Check(Line(markMinY: 20f));
            Assert.False(report.Ok);
            Assert.Contains(report.Problems, p => p.Contains("U+0E48") && p.Contains("overlaps"));
        }

        [Fact]
        public void A_mark_beside_its_consonant_fails()
        {
            var report = ThaiMarkCheck.Check(Line(markMinY: 30f, markX: 40f));
            Assert.Contains(report.Problems, p => p.Contains("not over its consonant"));
        }

        [Fact]
        public void A_missing_glyph_fails_but_a_space_does_not_count()
        {
            var glyphs = new List<GlyphBox>(Line(markMinY: 30f))
            {
                new GlyphBox(' ', "Base_JP", true, 0f, 0f, 0f, 0f),
            };
            Assert.True(ThaiMarkCheck.Check(glyphs).Ok);

            glyphs.Add(new GlyphBox(0x0E01, "Base_TH", true, 0f, 0f, 0f, 0f));
            var report = ThaiMarkCheck.Check(glyphs);
            Assert.False(report.Ok);
            Assert.Equal(1, report.Missing);
        }

        [Fact]
        public void No_Thai_is_not_ok()
        {
            Assert.False(ThaiMarkCheck.Check(new[] { new GlyphBox('a', "Base_JP", false, 0f, 5f, 0f, 10f) }).Ok);
        }

        /// <summary>ท + ี + ่ : the consonant at x 0-10, the vowel's top at 25, the tone mark above it.</summary>
        private static GlyphBox[] Line(float markMinY, float markX = 5f) => new[]
        {
            new GlyphBox(0x0E17, "Base_TH", false, 0f, 10f, 0f, 15f),
            new GlyphBox(0x0E35, "Base_TH", false, 1f, 9f, 18f, 25f),
            new GlyphBox(0x0E48, "Base_TH", false, markX - 2f, markX + 2f, markMinY, markMinY + 5f),
        };

        private static int Count(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, System.StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }
}
