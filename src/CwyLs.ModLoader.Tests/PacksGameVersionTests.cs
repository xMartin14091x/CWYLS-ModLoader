using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// PACK-VERSION (2026-09-29): a pack's <c>gameVersion</c> means "made and tested with" and only
    /// warns; <c>minGameVersion</c>/<c>maxGameVersion</c> are inclusive SemVer bounds that skip;
    /// an unknown or unparseable running version never skips. Found in game when 1.17.2 -> 1.17.3
    /// skipped every pack while all patch targets still resolved.
    /// </summary>
    public partial class PacksGameVersionTests
    {
        private const string Running = "1.17.3";

        private static readonly SemVer CoreVersion = SemVer.Parse("0.1.0");

        // ---------- gameVersion: made and tested with ------------------------------------

        [Fact]
        public void The_same_version_loads_silently()
        {
            var resolution = Resolve(Running, Pack("same.pack", gameVersion: Running));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Empty(resolution.Warnings);
            Assert.Equal(PackGameVersionStatus.Match, pack.GameVersionCheck!.Status);
            Assert.Equal(GameVersionDifference.Same, pack.GameVersionCheck.Difference);
            Assert.False(pack.GameVersionCheck.Warns);
        }

        [Fact]
        public void A_patch_difference_loads_with_one_warning_naming_both_versions()
        {
            // The defect seen in game: 1.17.2 packs on the auto-updated 1.17.3.
            var resolution = Resolve(Running, Pack("old.pack", gameVersion: "1.17.2"));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            var warning = Assert.Single(resolution.Warnings);
            Assert.Equal("old.pack", warning.Id);
            Assert.Equal("old.pack", warning.FolderName);
            Assert.Equal(
                "made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway",
                warning.Message);
            Assert.Equal("old.pack: " + warning.Message, warning.ToString());
            Assert.Equal(PackGameVersionStatus.Differs, pack.GameVersionCheck!.Status);
            Assert.Equal(GameVersionDifference.Patch, pack.GameVersionCheck.Difference);
            Assert.Equal("PACKS: 1 loaded (old.pack@1.0.0); 0 skipped", resolution.Summary);
        }

        [Theory]
        [InlineData("1.17.3", "1.18.0", GameVersionDifference.Minor, "newer minor version")]
        [InlineData("1.17.3", "2.0.0", GameVersionDifference.Major, "newer major version")]
        [InlineData("1.18.0", "1.17.3", GameVersionDifference.Minor, "older minor version")]
        [InlineData("1.17.3", "1.17.2", GameVersionDifference.Patch, "older patch version")]
        [InlineData("1.18.0-beta.1", "1.18.0", GameVersionDifference.Prerelease, "newer build (pre-release tag differs)")]
        public void Minor_major_and_older_differences_load_with_a_warning(
            string madeFor, string running, GameVersionDifference difference, string phrase)
        {
            var resolution = Resolve(running, Pack("any.pack", gameVersion: madeFor));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            var warning = Assert.Single(resolution.Warnings);
            Assert.Equal(
                "made for game " + madeFor + ", running " + running + " (" + phrase + "); loaded anyway",
                warning.Message);
            Assert.Equal(difference, pack.GameVersionCheck!.Difference);
        }

        [Fact]
        public void Build_metadata_alone_is_the_same_version()
        {
            var resolution = Resolve("1.17.3+steam.25498460", Pack("meta.pack", gameVersion: "1.17.3"));

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Warnings);
        }

        [Fact]
        public void A_gameVersion_that_is_not_a_version_number_warns_without_a_comparison()
        {
            var resolution = Resolve(Running, Pack("odd.pack", gameVersion: "latest"));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Equal(
                "made for game latest, running 1.17.3; loaded anyway",
                Assert.Single(resolution.Warnings).Message);
            Assert.Equal(GameVersionDifference.Unknown, pack.GameVersionCheck!.Difference);
        }

        // ---------- minGameVersion / maxGameVersion: inclusive bounds that skip ----------

        [Fact]
        public void Below_minGameVersion_is_GameVersionTooOld()
        {
            var resolution = Resolve(Running, Pack("future.pack", minGameVersion: "1.18.0"));

            Assert.Empty(resolution.Loaded);
            Assert.Empty(resolution.Warnings);
            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.GameVersionTooOld, skip.Reason);
            Assert.Equal("pack needs game >= 1.18.0, running 1.17.3", skip.Detail);
            Assert.Equal("PACKS: 0 loaded; 1 skipped (future.pack: GameVersionTooOld)", resolution.Summary);
        }

        [Fact]
        public void Above_maxGameVersion_is_GameVersionTooNew()
        {
            var resolution = Resolve(Running, Pack("past.pack", maxGameVersion: "1.17.2"));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.GameVersionTooNew, skip.Reason);
            Assert.Equal("pack needs game <= 1.17.2, running 1.17.3", skip.Detail);
        }

        [Fact]
        public void Outside_both_bounds_names_the_whole_range()
        {
            var resolution = Resolve(Running, Pack("band.pack", minGameVersion: "1.16.0", maxGameVersion: "1.17.2"));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.GameVersionTooNew, skip.Reason);
            Assert.Equal("pack needs game 1.16.0 to 1.17.2, running 1.17.3", skip.Detail);
        }

        [Theory]
        [InlineData("1.17.3", null)]
        [InlineData(null, "1.17.3")]
        [InlineData("1.17.3", "1.17.3")]
        [InlineData("1.17.0", "1.17.99")]
        public void The_bounds_are_inclusive(string? min, string? max)
        {
            var resolution = Resolve(Running, Pack("edge.pack", gameVersion: Running, minGameVersion: min, maxGameVersion: max));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Empty(resolution.Warnings);
            Assert.Equal(PackGameVersionStatus.Match, pack.GameVersionCheck!.Status);
        }

        [Fact]
        public void Inside_the_range_but_not_the_made_for_version_still_warns_once()
        {
            var resolution = Resolve(
                Running, Pack("range.pack", gameVersion: "1.17.2", minGameVersion: "1.17.0", maxGameVersion: "1.17.99"));

            Assert.Single(resolution.Loaded);
            Assert.Equal(
                "made for game 1.17.2, running 1.17.3 (newer patch version); loaded anyway",
                Assert.Single(resolution.Warnings).Message);
        }

        [Fact]
        public void A_two_part_running_version_is_compared_with_the_missing_part_as_zero()
        {
            var resolution = Resolve("1.18", Pack("old.pack", maxGameVersion: "1.17.99"));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.GameVersionTooNew, skip.Reason);
            Assert.Equal("pack needs game <= 1.17.99, running 1.18", skip.Detail);
        }

        // ---------- unknown or unparseable running version: never skips ------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_unknown_running_version_never_skips_it_warns(string? running)
        {
            var resolution = Resolve(
                running, Pack("strict.pack", gameVersion: "1.17.3", minGameVersion: "1.17.0", maxGameVersion: "1.17.99"));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Equal(
                "made for game 1.17.3, running game version unknown; game 1.17.0 to 1.17.99 not checked; loaded anyway",
                Assert.Single(resolution.Warnings).Message);
            Assert.Equal(PackGameVersionStatus.Unverified, pack.GameVersionCheck!.Status);
            Assert.Equal(GameVersions.Unknown, pack.GameVersionCheck.Running);
        }

        [Fact]
        public void An_unknown_running_version_with_only_bounds_says_why_they_were_not_checked()
        {
            var resolution = Resolve(null, Pack("bounded.pack", minGameVersion: "1.17.0"));

            Assert.Single(resolution.Loaded);
            Assert.Equal(
                "running game version unknown, so game >= 1.17.0 not checked; loaded anyway",
                Assert.Single(resolution.Warnings).Message);
        }

        [Fact]
        public void An_unparseable_running_version_never_skips_it_warns()
        {
            var resolution = Resolve("1.17.3b", Pack("bounded.pack", minGameVersion: "1.18.0"));

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Equal(
                "running 1.17.3b (not a version number), so game >= 1.18.0 not checked; loaded anyway",
                Assert.Single(resolution.Warnings).Message);
            Assert.Equal(PackGameVersionStatus.Unverified, pack.GameVersionCheck!.Status);
        }

        [Fact]
        public void An_unparseable_running_version_equal_to_gameVersion_is_a_match()
        {
            var resolution = Resolve("1.17.3b", Pack("text.pack", gameVersion: "1.17.3b"));

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Warnings);
        }

        [Fact]
        public void A_pack_that_declares_nothing_loads_silently_on_any_build()
        {
            foreach (var running in new[] { null, "abc", "0.0.1", "9.9.9" })
            {
                var resolution = Resolve(running, Pack("free.pack"));

                var pack = Assert.Single(resolution.Loaded);
                Assert.Empty(resolution.Warnings);
                Assert.Equal(PackGameVersionStatus.NotDeclared, pack.GameVersionCheck!.Status);
            }
        }

        // ---------- IgnoreGameVersion ----------------------------------------------------

        [Fact]
        public void IgnoreGameVersion_loads_a_pack_above_its_range_with_a_single_combined_warning()
        {
            var resolution = PackResolver.Resolve(
                Sources(Pack("past.pack", gameVersion: "1.17.1", maxGameVersion: "1.17.2")),
                Running,
                CoreVersion,
                ignoreGameVersion: true);

            var pack = Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Skipped);
            Assert.Equal(
                "made for game 1.17.1, running 1.17.3 (newer patch version); " +
                "pack needs game <= 1.17.2, running 1.17.3; loaded anyway because IgnoreGameVersion is on",
                Assert.Single(resolution.Warnings).Message);
            Assert.Equal(PackGameVersionStatus.TooNew, pack.GameVersionCheck!.Status);
            Assert.False(pack.GameVersionCheck.Skips);
        }

        [Fact]
        public void IgnoreGameVersion_loads_a_pack_below_its_range()
        {
            var resolution = PackResolver.Resolve(
                Sources(Pack("future.pack", minGameVersion: "1.18.0")), Running, CoreVersion, ignoreGameVersion: true);

            Assert.Single(resolution.Loaded);
            Assert.Equal(
                "pack needs game >= 1.18.0, running 1.17.3; loaded anyway because IgnoreGameVersion is on",
                Assert.Single(resolution.Warnings).Message);
        }

        [Fact]
        public void IgnoreGameVersion_changes_nothing_for_a_matching_pack()
        {
            var resolution = PackResolver.Resolve(
                Sources(Pack("same.pack", gameVersion: Running, minGameVersion: "1.17.0")),
                Running,
                CoreVersion,
                ignoreGameVersion: true);

            Assert.Single(resolution.Loaded);
            Assert.Empty(resolution.Warnings);
        }

        // ---------- warnings are per loaded pack -----------------------------------------

        [Fact]
        public void A_pack_skipped_for_another_reason_gets_no_version_warning()
        {
            var resolution = Resolve(Running, Pack("needy.pack", gameVersion: "1.17.2", dependsOn: "absent.pack"));

            Assert.Empty(resolution.Loaded);
            Assert.Equal(PackSkipReason.MissingDependency, Assert.Single(resolution.Skipped).Reason);
            Assert.Empty(resolution.Warnings);
        }

        [Fact]
        public void Each_warned_pack_gets_exactly_one_warning_in_load_order()
        {
            var resolution = Resolve(
                Running,
                Pack("b.pack", gameVersion: "1.17.2", dependsOn: "a.pack"),
                Pack("a.pack", gameVersion: "1.16.0"),
                Pack("c.pack", gameVersion: Running));

            // b waits for a; c has no constraint, so it is placed in the same pass as a.
            Assert.Equal(new[] { "a.pack", "c.pack", "b.pack" }, resolution.Loaded.Select(p => p.Id).ToArray());
            Assert.Equal(new[] { "a.pack", "b.pack" }, resolution.Warnings.Select(w => w.Id).ToArray());
        }

        [Fact]
        public void A_hand_built_resolution_has_no_warnings()
        {
            var manifest = PackManifestParser.Parse(Pack("hand.pack", gameVersion: "1.0.0").ManifestText);
            var resolved = new ResolvedPack(Pack("hand.pack"), manifest, PackContentIndex.Build(new string[0]));

            var resolution = new PackResolution(new[] { resolved }, null);

            Assert.Null(resolved.GameVersionCheck);
            Assert.Empty(resolution.Warnings);
            Assert.Empty(PackResolution.Empty.Warnings);
        }

        // ---------- the check on its own -------------------------------------------------

        [Fact]
        public void Evaluate_refuses_a_null_manifest() =>
            Assert.Throws<ArgumentNullException>(() => PackGameVersionCheck.Evaluate(null!, Running));

        [Fact]
        public void A_skip_carries_no_warning_and_a_load_carries_no_skip_reason()
        {
            var tooOld = PackGameVersionCheck.Evaluate(Parse(minGameVersion: "1.18.0"), Running);
            Assert.True(tooOld.Skips);
            Assert.False(tooOld.Warns);
            Assert.Equal(PackSkipReason.GameVersionTooOld, tooOld.SkipReason);
            Assert.Equal(PackGameVersionStatus.TooOld, tooOld.Status);

            var differs = PackGameVersionCheck.Evaluate(Parse(gameVersion: "1.17.2"), Running);
            Assert.False(differs.Skips);
            Assert.Null(differs.SkipReason);
            Assert.Equal(differs.Warning, differs.Detail);

            var match = PackGameVersionCheck.Evaluate(Parse(gameVersion: Running, minGameVersion: "1.17.0"), Running);
            Assert.Equal("running 1.17.3, made for game 1.17.3, inside game >= 1.17.0", match.Detail);
        }

        [Theory]
        [InlineData(null, null, "any game")]
        [InlineData("1.17.0", null, "game >= 1.17.0")]
        [InlineData(null, "1.17.9", "game <= 1.17.9")]
        [InlineData("1.17.0", "1.17.9", "game 1.17.0 to 1.17.9")]
        public void DescribeRange_reads_like_the_manifest(string? min, string? max, string expected) =>
            Assert.Equal(
                expected,
                PackGameVersionCheck.DescribeRange(
                    min is null ? null : SemVer.Parse(min), max is null ? null : SemVer.Parse(max)));

        // ---------- manifest members -----------------------------------------------------

        [Fact]
        public void The_parser_reads_minGameVersion_and_maxGameVersion()
        {
            var manifest = Parse(gameVersion: "1.17.3", minGameVersion: "1.17.0", maxGameVersion: "1.17.99");

            Assert.Equal("1.17.3", manifest.GameVersion);
            Assert.Equal(SemVer.Parse("1.17.0"), manifest.MinGameVersion);
            Assert.Equal(SemVer.Parse("1.17.99"), manifest.MaxGameVersion);
        }

        [Fact]
        public void Both_bounds_are_optional()
        {
            var manifest = Parse(gameVersion: "1.17.3");

            Assert.Null(manifest.MinGameVersion);
            Assert.Null(manifest.MaxGameVersion);
        }

        [Fact]
        public void An_equal_min_and_max_pins_exactly_one_build()
        {
            var manifest = Parse(minGameVersion: "1.17.3", maxGameVersion: "1.17.3");

            Assert.Equal(PackGameVersionStatus.Match, PackGameVersionCheck.Evaluate(manifest, "1.17.3").Status);
            Assert.Equal(PackSkipReason.GameVersionTooNew, PackGameVersionCheck.Evaluate(manifest, "1.17.4").SkipReason);
            Assert.Equal(PackSkipReason.GameVersionTooOld, PackGameVersionCheck.Evaluate(manifest, "1.17.2").SkipReason);
        }

        [Theory]
        [InlineData("\"minGameVersion\":\"1.18\"", "\"minGameVersion\":")]
        [InlineData("\"maxGameVersion\":\"latest\"", "\"maxGameVersion\":")]
        [InlineData("\"minGameVersion\":1", "\"minGameVersion\" must be a string")]
        [InlineData("\"maxGameVersion\":[\"1.17.3\"]", "\"maxGameVersion\" must be a string")]
        [InlineData("\"minGameVersion\":\"1.18.0\",\"maxGameVersion\":\"1.17.3\"", "\"minGameVersion\": 1.18.0 is above \"maxGameVersion\" 1.17.3")]
        [InlineData("\"mingameVersion\":\"1.17.0\"", "unknown member \"mingameVersion\"")]
        public void The_parser_rejects_bad_bounds(string members, string expectedFragment)
        {
            var json = "{\"id\":\"cwy.example\",\"name\":\"Example\",\"version\":\"0.1.0\"," + members + "}";

            var ex = Assert.Throws<PackManifestException>(() => PackManifestParser.Parse(json));

            Assert.Contains(expectedFragment, ex.Message);
        }

        [Fact]
        public void A_bad_bound_makes_the_pack_InvalidManifest()
        {
            var source = new FakePackSource(
                "bad.pack",
                "{\"id\":\"bad.pack\",\"name\":\"Bad\",\"version\":\"1.0.0\",\"minGameVersion\":\"soon\"}");

            var skip = Assert.Single(PackResolver.Resolve(Sources(source), Running, CoreVersion).Skipped);

            Assert.Equal(PackSkipReason.InvalidManifest, skip.Reason);
            Assert.Contains("minGameVersion", skip.Detail);
        }

        [Fact]
        public void The_constructor_refuses_min_above_max()
        {
            var ex = Assert.Throws<ArgumentException>(() => new PackManifest(
                "cwy.example", "Example", SemVer.Parse("0.1.0"),
                minGameVersion: SemVer.Parse("1.18.0"), maxGameVersion: SemVer.Parse("1.17.0")));

            Assert.Contains("above", ex.Message);
        }

        /// <summary>The repo packs this checks.</summary>
        public static IEnumerable<object[]> RepoPackIds() =>
            new[] { "cwy.example" }.Select(id => new object[] { id });

        [Theory]
        [MemberData(nameof(RepoPackIds))]
        public void The_repo_manifests_parse_and_load_on_their_own_game_version(string id)
        {
            var text = File.ReadAllText(Path.Combine(LanguageDeclarationParserTests.PackFolder(id), "manifest.json"));
            var manifest = PackManifestParser.Parse(text);

            Assert.NotNull(manifest.GameVersion);
            Assert.True(GameVersions.TryParse(manifest.GameVersion, out _), manifest.GameVersion);
            Assert.Equal(PackGameVersionStatus.Match, PackGameVersionCheck.Evaluate(manifest, manifest.GameVersion).Status);
        }

        // ---------- GameVersions ---------------------------------------------------------

        [Theory]
        [InlineData("1.17.3", "1.17.3")]
        [InlineData(" 1.17.3 ", "1.17.3")]
        [InlineData("1.17.3+steam.1", "1.17.3+steam.1")]
        [InlineData("1.18.0-beta.1", "1.18.0-beta.1")]
        [InlineData("1.18", "1.18.0")]
        [InlineData("2", "2.0.0")]
        public void GameVersions_parse_leniently(string text, string expected)
        {
            Assert.True(GameVersions.TryParse(text, out var version));
            Assert.Equal(expected, version!.ToString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("unknown")]
        [InlineData("1.17.3.1")]
        [InlineData("1.17.3b")]
        [InlineData("1..2")]
        [InlineData("01.2")]
        [InlineData("1.")]
        [InlineData("v1.17.3")]
        public void GameVersions_refuse_what_is_not_a_version(string? text)
        {
            Assert.False(GameVersions.TryParse(text, out var version));
            Assert.Null(version);
        }

        [Theory]
        [InlineData("1.17.2", "1.17.2", GameVersionDifference.Same)]
        [InlineData("1.17.2", "1.17.2+build", GameVersionDifference.Same)]
        [InlineData("1.17", "1.17.0", GameVersionDifference.Same)]
        [InlineData("1.17.2", "1.17.3", GameVersionDifference.Patch)]
        [InlineData("1.17.2", "1.18.0", GameVersionDifference.Minor)]
        [InlineData("1.17.2", "2.0.0", GameVersionDifference.Major)]
        [InlineData("1.18.0-rc.1", "1.18.0", GameVersionDifference.Prerelease)]
        [InlineData("abc", "abc", GameVersionDifference.Same)]
        [InlineData("abc", "abd", GameVersionDifference.Unknown)]
        [InlineData("1.17.2", "abc", GameVersionDifference.Unknown)]
        [InlineData(null, "1.17.2", GameVersionDifference.Unknown)]
        [InlineData("1.17.2", null, GameVersionDifference.Unknown)]
        public void GameVersions_classify_the_difference(string? from, string? to, GameVersionDifference expected) =>
            Assert.Equal(expected, GameVersions.Difference(from, to));

        [Theory]
        [InlineData("1.17.2", "1.17.2")]
        [InlineData("1.17.2", "abc")]
        [InlineData(null, "1.17.2")]
        public void GameVersions_describe_nothing_when_there_is_nothing_to_say(string? reference, string? actual) =>
            Assert.Null(GameVersions.Describe(reference, actual));

        // ---------- GameVersionGate (the framework's own target-drift explanation) --------

        [Fact]
        public void The_gate_treats_build_metadata_as_the_same_version()
        {
            var check = new GameVersionGate("1.17.2").Check("1.17.2+steam.25498460");

            Assert.True(check.IsOk);
            Assert.Equal(GameVersionDifference.Same, check.Difference);
            Assert.Equal(
                "game version 1.17.2+steam.25498460 matches 1.17.2, the build the patch targets were verified against",
                check.Message);
        }

        [Fact]
        public void The_gate_names_a_patch_release_as_such()
        {
            var check = new GameVersionGate("1.17.2").Check("1.17.3");

            Assert.False(check.IsOk);
            Assert.Equal(GameVersionDifference.Patch, check.Difference);
            Assert.Equal(
                "game version mismatch: running 1.17.3, patch targets verified against 1.17.2 (newer patch version)",
                check.Message);
        }

        [Fact]
        public void The_gate_keeps_an_exact_match_message_unchanged()
        {
            var check = new GameVersionGate("1.17.2").Check("1.17.2");

            Assert.Equal(GameVersionDifference.Same, check.Difference);
            Assert.Equal("game version 1.17.2 matches the build the patch targets were verified against", check.Message);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("1.17.2b")]
        public void The_gate_cannot_measure_an_unknown_or_unparseable_version(string? actual)
        {
            var check = new GameVersionGate("1.17.2").Check(actual);

            Assert.False(check.IsOk);
            Assert.Equal(GameVersionDifference.Unknown, check.Difference);
            Assert.DoesNotContain("(", check.Message);
        }

        // ---------- helpers ---------------------------------------------------------------

        private static PackResolution Resolve(string? running, params IPackSource[] sources) =>
            PackResolver.Resolve(sources, running, CoreVersion);

        private static IPackSource[] Sources(params IPackSource[] sources) => sources;

        private static PackManifest Parse(
            string? gameVersion = null,
            string? minGameVersion = null,
            string? maxGameVersion = null) =>
            PackManifestParser.Parse(
                Pack("cwy.example", gameVersion, minGameVersion, maxGameVersion).ManifestText);

        private static FakePackSource Pack(
            string id,
            string? gameVersion = null,
            string? minGameVersion = null,
            string? maxGameVersion = null,
            string? dependsOn = null)
        {
            var json = "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\"";
            var optional = new List<(string Key, string? Value)>
            {
                ("gameVersion", gameVersion),
                ("minGameVersion", minGameVersion),
                ("maxGameVersion", maxGameVersion),
            };

            foreach (var (key, value) in optional)
            {
                if (value != null)
                {
                    json += ",\"" + key + "\":\"" + value + "\"";
                }
            }

            if (dependsOn != null)
            {
                json += ",\"dependsOn\":[\"" + dependsOn + "\"]";
            }

            return new FakePackSource(id, json + "}");
        }

        /// <summary>An in-memory pack folder: everything the resolver reads, and nothing else.</summary>
        private sealed class FakePackSource : IPackSource
        {
            internal FakePackSource(string folderName, string manifestText)
            {
                Id = folderName;
                FolderName = folderName;
                ManifestText = manifestText;
                Files = new[] { "manifest.json" };
            }

            public string Id { get; }

            public string FolderName { get; }

            public string ManifestText { get; }

            public IReadOnlyList<string> Files { get; }
        }
    }
}
