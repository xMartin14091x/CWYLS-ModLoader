using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Grading;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// D-SCENERY: the Lightroom-style control table - ids, aliases, ranges and clamping - and the
    /// time-of-day names the game uses.
    /// </summary>
    public class GradingControlsTests
    {
        [Theory]
        [InlineData("basic.exposure", -5, 5, 0)]
        [InlineData("basic.contrast", -100, 100, 0)]
        [InlineData("whiteBalance.temperature", -100, 100, 0)]
        [InlineData("presence.vibrance", -100, 100, 0)]
        [InlineData("hsl.blues.luminance", -100, 100, 0)]
        [InlineData("splitToning.shadowHue", 0, 360, 0)]
        [InlineData("splitToning.highlightSaturation", 0, 100, 0)]
        [InlineData("colorGrading.midtones.luminance", -100, 100, 0)]
        [InlineData("effects.vignette.midpoint", 0, 100, 50)]
        [InlineData("effects.grain.size", 0, 100, 25)]
        [InlineData("effects.bloom", -100, 100, 0)]
        [InlineData("lut.amount", 0, 100, 100)]
        [InlineData("blend", 0, 1, 1)]
        public void Ranges_follow_lightroom_where_the_mapping_allows(string id, double min, double max, double neutral)
        {
            var control = GradingControls.Get(id);

            Assert.Equal(min, control.Min);
            Assert.Equal(max, control.Max);
            Assert.Equal(neutral, control.Neutral);
        }

        [Fact]
        public void Every_hsl_band_and_wheel_has_its_three_controls()
        {
            foreach (var band in GradingControls.HslBands)
            {
                foreach (var property in GradingControls.HslProperties)
                {
                    Assert.True(GradingControls.TryGetById(GradingControls.HslId(band, property), out _), band + "." + property);
                }
            }

            foreach (var wheel in GradingControls.Wheels)
            {
                foreach (var property in new[] { "hue", "saturation", "luminance" })
                {
                    Assert.True(GradingControls.TryGetById(GradingControls.WheelId(wheel, property), out _), wheel + "." + property);
                }
            }

            Assert.Equal(8, GradingControls.HslBands.Count);
        }

        [Theory]
        [InlineData("exposure", "basic.exposure")]
        [InlineData("EXPOSURE", "basic.exposure")]
        [InlineData("temp", "whiteBalance.temperature")]
        [InlineData("blues.lum", "hsl.blues.luminance")]
        [InlineData("vignette", "effects.vignette.amount")]
        [InlineData("grain", "effects.grain.amount")]
        [InlineData("split.highlightHue", "splitToning.highlightHue")]
        [InlineData("shadows.hue", "colorGrading.shadows.hue")]
        [InlineData("basic.Exposure", "basic.exposure")]
        public void Aliases_and_ids_resolve_case_insensitively(string typed, string id)
        {
            Assert.True(GradingControls.TryFind(typed, out var control));
            Assert.Equal(id, control!.Id);
        }

        [Fact]
        public void Aliases_are_not_accepted_where_only_ids_are()
        {
            Assert.False(GradingControls.TryGetById("exposure", out _));
            Assert.False(GradingControls.TryFind("nonsense", out _));
            Assert.False(GradingControls.TryFind("  ", out _));
        }

        [Fact]
        public void Ids_and_aliases_are_unique()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var control in GradingControls.All)
            {
                Assert.True(seen.Add(control.Id), "duplicate id " + control.Id);
                foreach (var alias in control.Aliases)
                {
                    Assert.True(seen.Add(alias), "alias collides: " + alias);
                }
            }
        }

        [Theory]
        [InlineData("basic.exposure", 7.5, 5, true)]
        [InlineData("basic.exposure", -9, -5, true)]
        [InlineData("basic.exposure", 1.25, 1.25, false)]
        [InlineData("basic.contrast", 150, 100, true)]
        [InlineData("splitToning.shadowHue", 370, 10, true)]
        [InlineData("splitToning.shadowHue", -30, 330, true)]
        [InlineData("splitToning.shadowHue", 359, 359, false)]
        [InlineData("blend", 1.5, 1, true)]
        public void Values_are_clamped_and_angles_wrapped(string id, double given, double expected, bool changed)
        {
            var result = GradingControls.Get(id).Clamp(given, out var moved);

            Assert.Equal(expected, result, 9);
            Assert.Equal(changed, moved);
        }

        [Fact]
        public void A_non_finite_value_is_refused()
        {
            var control = GradingControls.Get("basic.exposure");
            Assert.Throws<ArgumentOutOfRangeException>(() => control.Clamp(double.NaN, out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.Clamp(double.PositiveInfinity, out _));
        }

        [Fact]
        public void Group_paths_cover_every_nesting_level()
        {
            Assert.Contains("basic", GradingControls.GroupPaths);
            Assert.Contains("hsl.reds", GradingControls.GroupPaths);
            Assert.Contains("effects.vignette", GradingControls.GroupPaths);
            Assert.Contains("colorGrading.global", GradingControls.GroupPaths);
            Assert.DoesNotContain("blend", GradingControls.GroupPaths);
        }

        [Theory]
        [InlineData("Day", GradingTimeOfDay.Day)]
        [InlineData("sunset", GradingTimeOfDay.Sunset)]
        [InlineData(" NIGHT ", GradingTimeOfDay.Night)]
        [InlineData("Cloudy", GradingTimeOfDay.Cloudy)]
        [InlineData("Sakura", GradingTimeOfDay.Unknown)]
        [InlineData("LightRain", GradingTimeOfDay.Unknown)]
        [InlineData(null, GradingTimeOfDay.Unknown)]
        public void Time_of_day_reads_the_game_names(string? name, GradingTimeOfDay expected)
        {
            Assert.Equal(expected, GradingTimesOfDay.FromName(name));
        }

        [Fact]
        public void Block_names_are_lower_case_and_unknown_has_none()
        {
            Assert.Equal("day", GradingTimesOfDay.BlockName(GradingTimeOfDay.Day));
            Assert.Equal("cloudy", GradingTimesOfDay.BlockName(GradingTimeOfDay.Cloudy));
            Assert.Equal(string.Empty, GradingTimesOfDay.BlockName(GradingTimeOfDay.Unknown));
        }

        [Theory]
        [InlineData("warm-film", null)]
        [InlineData("my.look_2", null)]
        [InlineData("", "required")]
        [InlineData("-dash", "starts with")]
        [InlineData("has space", "may only use")]
        [InlineData("con", "device")]
        [InlineData("a/b", "may only use")]
        public void Save_names_are_checked(string name, string? problem)
        {
            var actual = GradingFormat.NameProblem(name);
            if (problem is null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Contains(problem, actual!, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Theory]
        [InlineData("luts/film.cube", null)]
        [InlineData("film.png", null)]
        [InlineData("../evil.cube", "inside")]
        [InlineData("/abs/film.cube", "relative")]
        [InlineData("C:/film.cube", "relative")]
        [InlineData("film.jpg", ".cube")]
        public void Lut_paths_stay_inside_the_preset_folder(string path, string? problem)
        {
            var actual = GradingFormat.LutPathProblem(path);
            if (problem is null)
            {
                Assert.Null(actual);
            }
            else
            {
                Assert.NotNull(actual);
                Assert.Contains(problem, actual!, StringComparison.Ordinal);
            }
        }
    }
}
