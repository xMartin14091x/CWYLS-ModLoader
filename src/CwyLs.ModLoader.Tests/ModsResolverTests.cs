using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: code mods and content packs share one dependency graph. These cases cover the
    /// new gates (entryDll present, contentPackFor names a code mod) and optional dependencies.
    /// </summary>
    public class ModsResolverTests
    {
        private const string GameVersion = "1.17.3";

        [Fact]
        public void A_code_mod_with_its_dll_loads_and_keeps_its_content_index()
        {
            var resolution = Resolve(Mod("my.mod", "MyMod.dll", files: new[] { "MyMod.dll", "textures/Body/_MainTex.png" }));

            var pack = Assert.Single(resolution.Loaded);
            Assert.True(pack.Manifest.IsCodeMod);
            Assert.Single(pack.Index.Textures);
        }

        [Fact]
        public void A_code_mod_whose_dll_is_missing_is_EntryDllMissing_and_takes_its_dependents_down()
        {
            var resolution = Resolve(
                Mod("my.mod", "MyMod.dll", files: new[] { "README.md" }),
                Pack("needs.mod", dependsOn: "\"my.mod\""));

            Assert.Empty(resolution.Loaded);
            var skip = resolution.FindSkipped("my.mod")!;
            Assert.Equal(PackSkipReason.EntryDllMissing, skip.Reason);
            Assert.Contains("MyMod.dll", skip.Detail);
            var cascade = resolution.FindSkipped("needs.mod")!;
            Assert.Equal(PackSkipReason.MissingDependency, cascade.Reason);
            Assert.Contains("EntryDllMissing", cascade.Detail);
        }

        [Fact]
        public void The_entry_dll_may_live_in_a_subfolder_and_match_case_insensitively()
        {
            var resolution = Resolve(Mod("my.mod", "bin/mymod.dll", files: new[] { "bin\\MyMod.dll" }));

            Assert.Single(resolution.Loaded);
        }

        [Fact]
        public void A_mod_needing_a_newer_framework_says_mod_not_pack()
        {
            var resolution = PackResolver.Resolve(
                new[] { Mod("my.mod", "MyMod.dll", loaderVersion: "1.2.0") }, GameVersion, CwyApi.Version);

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.CoreVersionTooOld, skip.Reason);
            Assert.StartsWith("mod needs CwyLs.ModLoader >= 1.2.0, running " + CwyApi.VersionText, skip.Detail);
        }

        [Fact]
        public void A_mod_asking_for_this_api_version_loads()
        {
            var resolution = PackResolver.Resolve(
                new[] { Mod("my.mod", "MyMod.dll", loaderVersion: CwyApi.VersionText) }, GameVersion, CwyApi.Version);

            Assert.Single(resolution.Loaded);
        }

        [Fact]
        public void A_content_pack_for_a_code_mod_loads_after_it()
        {
            var resolution = Resolve(
                Pack("data.pack", contentPackFor: "\"host.mod\""),
                Mod("host.mod", "Host.dll"));

            Assert.Equal(new[] { "host.mod", "data.pack" }, Ids(resolution));
        }

        [Fact]
        public void A_content_pack_whose_code_mod_is_absent_is_MissingDependency()
        {
            var resolution = Resolve(Pack("data.pack", contentPackFor: "\"host.mod\""));

            var skip = Assert.Single(resolution.Skipped);
            Assert.Equal(PackSkipReason.MissingDependency, skip.Reason);
            Assert.Contains("host.mod", skip.Detail);
        }

        [Fact]
        public void A_content_pack_for_a_plain_content_pack_is_refused()
        {
            var resolution = Resolve(
                Pack("host.pack"),
                Pack("data.pack", contentPackFor: "\"host.pack\""));

            Assert.Equal(new[] { "host.pack" }, Ids(resolution));
            var skip = resolution.FindSkipped("data.pack")!;
            Assert.Equal(PackSkipReason.MissingDependency, skip.Reason);
            Assert.Contains("not a code mod", skip.Detail);
        }

        [Fact]
        public void A_content_pack_for_an_old_code_mod_is_DependencyVersionTooLow()
        {
            var resolution = Resolve(
                Mod("host.mod", "Host.dll", version: "1.0.0"),
                Pack("data.pack", contentPackFor: "{\"id\":\"host.mod\",\"minVersion\":\"2.0.0\"}"));

            Assert.Equal(PackSkipReason.DependencyVersionTooLow, resolution.FindSkipped("data.pack")!.Reason);
        }

        [Fact]
        public void An_absent_optional_dependency_is_ignored()
        {
            var resolution = Resolve(Pack("a.pack", dependsOn: Optional("absent.pack")));

            Assert.Equal(new[] { "a.pack" }, Ids(resolution));
            Assert.Empty(resolution.Skipped);
        }

        [Fact]
        public void A_present_optional_dependency_loads_first()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: Optional("b.pack")),
                Pack("b.pack"));

            Assert.Equal(new[] { "b.pack", "a.pack" }, Ids(resolution));
        }

        [Fact]
        public void An_optional_dependency_that_was_skipped_is_ignored()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: Optional("b.pack")),
                Pack("b.pack", minGameVersion: "9.0.0"));

            Assert.Equal(new[] { "a.pack" }, Ids(resolution));
            Assert.Equal(PackSkipReason.GameVersionTooOld, Assert.Single(resolution.Skipped).Reason);
        }

        [Fact]
        public void An_optional_dependency_that_is_installed_but_too_old_refuses_the_dependent()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: "{\"id\":\"b.pack\",\"minVersion\":\"2.0.0\",\"optional\":true}"),
                Pack("b.pack", version: "1.0.0"));

            var skip = resolution.FindSkipped("a.pack")!;
            Assert.Equal(PackSkipReason.DependencyVersionTooLow, skip.Reason);
            Assert.Contains("optional", skip.Detail);
        }

        [Fact]
        public void An_optional_dependency_whose_provider_goes_later_in_the_fixed_point_is_ignored()
        {
            // The survival pass walks from the last candidate: a.pack is checked first and finds
            // b.pack present; then c.pack goes (missing base) and takes b.pack with it. The fixed
            // point must re-check a.pack, see its optional provider gone, and keep it.
            var resolution = Resolve(
                Pack("b.pack", dependsOn: "\"c.pack\""),
                Pack("c.pack", dependsOn: "\"absent.pack\""),
                Pack("a.pack", dependsOn: Optional("b.pack")));

            Assert.Equal(new[] { "a.pack" }, Ids(resolution));
            Assert.Equal(2, resolution.Skipped.Count);
        }

        [Fact]
        public void A_cycle_through_an_optional_edge_is_broken_and_both_load()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: "\"b.pack\""),
                Pack("b.pack", dependsOn: Optional("a.pack")));

            Assert.Empty(resolution.Skipped);
            Assert.Equal(new[] { "b.pack", "a.pack" }, Ids(resolution));
        }

        [Fact]
        public void P1_an_installed_optional_dependency_off_the_cycle_still_orders_first()
        {
            // Review probe P1: once one optional cycle was broken, every optional edge was ignored
            // for the rest of the run, so d.pack loaded before its present optional dependency c.pack.
            var resolution = Resolve(
                Pack("d.pack", dependsOn: Optional("c.pack")),
                Pack("c.pack", dependsOn: "\"a.pack\""),
                Pack("a.pack", dependsOn: Optional("b.pack")),
                Pack("b.pack", dependsOn: Optional("a.pack")));

            var ids = Ids(resolution);
            Assert.Empty(resolution.Skipped);
            Assert.True(Array.IndexOf(ids, "c.pack") < Array.IndexOf(ids, "d.pack"), string.Join(", ", ids));
            Assert.True(Array.IndexOf(ids, "a.pack") < Array.IndexOf(ids, "c.pack"), string.Join(", ", ids));
        }

        [Fact]
        public void Only_the_optional_edges_on_a_cycle_are_ignored()
        {
            var resolution = Resolve(
                Pack("c.pack", dependsOn: Optional("a.pack")),
                Pack("a.pack", dependsOn: Optional("b.pack")),
                Pack("b.pack", dependsOn: Optional("a.pack")));

            var ids = Ids(resolution);
            Assert.Equal(3, ids.Length);
            Assert.True(Array.IndexOf(ids, "a.pack") < Array.IndexOf(ids, "c.pack"), string.Join(", ", ids));
        }

        [Fact]
        public void An_optional_edge_off_a_mixed_cycle_keeps_its_order()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: "\"b.pack\""),
                Pack("b.pack", dependsOn: Optional("a.pack")),
                Pack("c.pack", dependsOn: Optional("b.pack")));

            Assert.Empty(resolution.Skipped);
            Assert.Equal(new[] { "b.pack", "c.pack", "a.pack" }, Ids(resolution));
        }

        [Fact]
        public void Behind_a_required_cycle_the_optional_chain_still_orders()
        {
            var resolution = Resolve(
                Pack("d.pack", dependsOn: Optional("c.pack")),
                Pack("a.pack", dependsOn: "\"b.pack\""),
                Pack("b.pack", dependsOn: "\"a.pack\""),
                Pack("c.pack", dependsOn: Optional("a.pack")));

            Assert.Equal(new[] { "c.pack", "d.pack" }, Ids(resolution));
            Assert.Equal(PackSkipReason.DependencyCycle, resolution.FindSkipped("a.pack")!.Reason);
            Assert.Equal(PackSkipReason.DependencyCycle, resolution.FindSkipped("b.pack")!.Reason);
        }

        [Fact]
        public void A_required_cycle_still_skips_but_an_optional_dependent_of_it_loads()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: "\"b.pack\""),
                Pack("b.pack", dependsOn: "\"a.pack\""),
                Pack("c.pack", dependsOn: Optional("a.pack")));

            Assert.Equal(new[] { "c.pack" }, Ids(resolution));
            Assert.All(resolution.Skipped, skip => Assert.Equal(PackSkipReason.DependencyCycle, skip.Reason));
            Assert.Contains("a.pack -> b.pack -> a.pack", resolution.FindSkipped("a.pack")!.Detail);
        }

        [Fact]
        public void A_required_dependent_of_a_cycle_is_still_reported_behind_the_cycle()
        {
            var resolution = Resolve(
                Pack("a.pack", dependsOn: "\"b.pack\""),
                Pack("b.pack", dependsOn: "\"a.pack\""),
                Pack("c.pack", dependsOn: "\"a.pack\""));

            Assert.Empty(resolution.Loaded);
            Assert.Equal(PackSkipReason.DependencyCycle, resolution.FindSkipped("c.pack")!.Reason);
        }

        [Fact]
        public void Code_mods_and_packs_share_one_order()
        {
            var resolution = Resolve(
                Mod("ui.mod", "Ui.dll", dependsOn: "\"theme.pack\""),
                Pack("theme.pack", dependsOn: "\"core.mod\""),
                Mod("core.mod", "Core.dll"));

            Assert.Equal(new[] { "core.mod", "theme.pack", "ui.mod" }, Ids(resolution));
        }

        [Fact]
        public void GameVersionMismatch_is_retired_and_its_number_stays_unused()
        {
            Assert.False(Enum.IsDefined(typeof(PackSkipReason), 3));
            Assert.DoesNotContain("GameVersionMismatch", Enum.GetNames(typeof(PackSkipReason)));
            Assert.Equal(10, (int)PackSkipReason.EntryDllMissing);
            Assert.Equal(4, (int)PackSkipReason.CoreVersionTooOld);
        }

        private static PackResolution Resolve(params IPackSource[] sources) =>
            PackResolver.Resolve(sources, GameVersion, CwyApi.Version);

        private static string[] Ids(PackResolution resolution) =>
            resolution.Loaded.Select(pack => pack.Manifest.Id).ToArray();

        private static string Optional(string id) => "{\"id\":\"" + id + "\",\"optional\":true}";

        private static FakeSource Pack(
            string id,
            string version = "1.0.0",
            string? dependsOn = null,
            string? contentPackFor = null,
            string? minGameVersion = null) =>
            new FakeSource(id, Json(id, version, null, dependsOn, contentPackFor, null, minGameVersion), "manifest.json");

        private static FakeSource Mod(
            string id,
            string entryDll,
            string version = "1.0.0",
            string? dependsOn = null,
            string? loaderVersion = null,
            string[]? files = null)
        {
            var listing = new List<string> { "manifest.json" };
            listing.AddRange(files ?? new[] { entryDll });
            return new FakeSource(id, Json(id, version, entryDll, dependsOn, null, loaderVersion, null), listing.ToArray());
        }

        private static string Json(
            string id, string version, string? entryDll, string? dependsOn, string? contentPackFor,
            string? loaderVersion, string? minGameVersion)
        {
            var json = "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"" + version + "\"";
            if (entryDll != null) { json += ",\"entryDll\":\"" + entryDll + "\""; }
            if (dependsOn != null) { json += ",\"dependsOn\":[" + dependsOn + "]"; }
            if (contentPackFor != null) { json += ",\"contentPackFor\":" + contentPackFor; }
            if (loaderVersion != null) { json += ",\"loaderVersion\":\"" + loaderVersion + "\""; }
            if (minGameVersion != null) { json += ",\"minGameVersion\":\"" + minGameVersion + "\""; }
            return json + "}";
        }

        /// <summary>An in-memory pack folder.</summary>
        private sealed class FakeSource : IPackSource
        {
            internal FakeSource(string folderName, string? manifestText, params string[] files)
            {
                Id = folderName;
                FolderName = folderName;
                ManifestText = manifestText;
                Files = files;
            }

            public string Id { get; }

            public string FolderName { get; }

            public string? ManifestText { get; }

            public IReadOnlyList<string> Files { get; }
        }
    }
}
