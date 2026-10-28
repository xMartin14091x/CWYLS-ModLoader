using System;
using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: the manifest fields a code mod adds - <c>entryDll</c>, <c>contentPackFor</c>,
    /// <c>dependsOn[].optional</c> - and the <c>x-</c> extras a mod can read back.
    /// </summary>
    public class ModsManifestTests
    {
        private const string Head = "{\"id\":\"my.mod\",\"name\":\"My Mod\",\"version\":\"1.0.0\"";

        [Fact]
        public void A_manifest_without_entryDll_is_a_content_pack()
        {
            var manifest = PackManifestParser.Parse(Head + "}");

            Assert.False(manifest.IsCodeMod);
            Assert.Null(manifest.EntryDll);
            Assert.Null(manifest.ContentPackFor);
            Assert.Empty(manifest.AllDependencies);
            Assert.Empty(manifest.Extensions);
        }

        [Theory]
        [InlineData("MyMod.dll", "MyMod.dll")]
        [InlineData("./MyMod.dll", "MyMod.dll")]
        [InlineData("bin\\\\MyMod.dll", "bin/MyMod.dll")]
        [InlineData("bin/Release/MyMod.DLL", "bin/Release/MyMod.DLL")]
        [InlineData("  MyMod.dll  ", "MyMod.dll")]
        public void EntryDll_is_normalised_and_makes_a_code_mod(string raw, string expected)
        {
            var manifest = PackManifestParser.Parse(Head + ",\"entryDll\":\"" + raw + "\"}");

            Assert.True(manifest.IsCodeMod);
            Assert.Equal(expected, manifest.EntryDll);
        }

        [Theory]
        [InlineData("\"entryDll\":\"\"", "\"entryDll\"")]
        [InlineData("\"entryDll\":\"MyMod.txt\"", "must name a .dll file")]
        [InlineData("\"entryDll\":\".dll\"", "must name a .dll file")]
        [InlineData("\"entryDll\":\"/abs/MyMod.dll\"", "not an absolute path")]
        [InlineData("\"entryDll\":\"C:/Mods/MyMod.dll\"", "not an absolute path")]
        [InlineData("\"entryDll\":\"../other/MyMod.dll\"", "not a path inside the pack folder")]
        [InlineData("\"entryDll\":\"bin/../MyMod.dll\"", "not a path inside the pack folder")]
        [InlineData("\"entryDll\":\"bin//MyMod.dll\"", "segments")]
        [InlineData("\"entryDll\":\"bin/./MyMod.dll\"", "segments")]
        [InlineData("\"entryDll\":\"folder/\"", "not a path inside the pack folder")]
        [InlineData("\"entryDll\":3", "must be a string")]
        public void Unusable_entryDll_values_are_refused_with_the_member_named(string member, string expected)
        {
            var ok = PackManifestParser.TryParse(Head + "," + member + "}", out var manifest, out var error);

            Assert.False(ok);
            Assert.Null(manifest);
            Assert.Contains("entryDll", error);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void An_explicit_null_entryDll_is_a_content_pack()
        {
            Assert.False(PackManifestParser.Parse(Head + ",\"entryDll\":null}").IsCodeMod);
        }

        [Fact]
        public void ContentPackFor_accepts_the_id_shorthand_as_a_required_dependency()
        {
            var manifest = PackManifestParser.Parse(Head + ",\"contentPackFor\":\"host.mod\"}");

            Assert.NotNull(manifest.ContentPackFor);
            Assert.Equal("host.mod", manifest.ContentPackFor!.Id);
            Assert.False(manifest.ContentPackFor.IsOptional);
            Assert.Null(manifest.ContentPackFor.MinVersion);
            Assert.False(manifest.IsCodeMod);
            var dependency = Assert.Single(manifest.AllDependencies);
            Assert.Equal("host.mod", dependency.Id);
            Assert.Empty(manifest.DependsOn);
        }

        [Fact]
        public void ContentPackFor_accepts_an_object_with_minVersion()
        {
            var manifest = PackManifestParser.Parse(
                Head + ",\"contentPackFor\":{\"id\":\"host.mod\",\"minVersion\":\"2.1.0\"}}");

            Assert.Equal(SemVer.Parse("2.1.0"), manifest.ContentPackFor!.MinVersion);
            Assert.Equal("host.mod >= 2.1.0", manifest.ContentPackFor.ToString());
        }

        [Theory]
        [InlineData("\"contentPackFor\":{\"id\":\"host.mod\",\"optional\":true}", "unknown member \"optional\"")]
        [InlineData("\"contentPackFor\":\"my.mod\"", "content pack for itself")]
        [InlineData("\"contentPackFor\":\"Bad Id\"", "must match")]
        [InlineData("\"contentPackFor\":7", "must be an object or a pack id string")]
        [InlineData("\"contentPackFor\":\"host.mod\",\"entryDll\":\"MyMod.dll\"", "cannot both be set")]
        [InlineData("\"contentPackFor\":\"host.mod\",\"dependsOn\":[\"host.mod\"]", "remove it from \"dependsOn\"")]
        public void Bad_contentPackFor_values_are_refused(string members, string expected)
        {
            var ok = PackManifestParser.TryParse(Head + "," + members + "}", out _, out var error);

            Assert.False(ok);
            Assert.Contains(expected, error);
        }

        [Fact]
        public void A_dependency_can_be_optional()
        {
            var manifest = PackManifestParser.Parse(
                Head + ",\"dependsOn\":[{\"id\":\"nice.to.have\",\"optional\":true},{\"id\":\"must.have\",\"optional\":false},\"also.must\"]}");

            Assert.True(manifest.DependsOn[0].IsOptional);
            Assert.False(manifest.DependsOn[1].IsOptional);
            Assert.False(manifest.DependsOn[2].IsOptional);
            Assert.Equal("nice.to.have (optional)", manifest.DependsOn[0].ToString());
        }

        [Theory]
        [InlineData("\"yes\"")]
        [InlineData("1")]
        [InlineData("[]")]
        public void A_non_boolean_optional_is_refused(string value)
        {
            var ok = PackManifestParser.TryParse(
                Head + ",\"dependsOn\":[{\"id\":\"x.pack\",\"optional\":" + value + "}]}", out _, out var error);

            Assert.False(ok);
            Assert.Contains("dependsOn[0].optional", error);
            Assert.Contains("true or false", error);
        }

        [Fact]
        public void An_unknown_dependency_member_names_optional_among_the_expected_ones()
        {
            var ok = PackManifestParser.TryParse(
                Head + ",\"dependsOn\":[{\"id\":\"x.pack\",\"soft\":true}]}", out _, out var error);

            Assert.False(ok);
            Assert.Contains("unknown member \"soft\"", error);
            Assert.Contains("\"optional\"", error);
        }

        [Fact]
        public void X_members_are_kept_as_extensions_in_document_order()
        {
            var manifest = PackManifestParser.Parse(
                Head + ",\"x-homepage\":\"https://example.invalid\",\"x-settings\":{\"volume\":3,\"tags\":[\"a\"]},\"x-flag\":true}");

            Assert.Equal(new[] { "x-homepage", "x-settings", "x-flag" }, manifest.Extensions.Keys.ToArray());
            Assert.Equal("https://example.invalid", manifest.Extensions["x-homepage"]);
            var settings = Assert.IsType<JsonObject>(manifest.Extensions["x-settings"]);
            Assert.Equal(3L, settings["volume"]);
            Assert.Equal(true, manifest.Extensions["x-flag"]);
        }

        [Fact]
        public void A_full_code_mod_manifest_parses()
        {
            var manifest = PackManifestParser.Parse(
                "{\"id\":\"cwy.sample.hello\",\"name\":\"Hello\",\"version\":\"0.1.0\",\"author\":\"CWY:LS\"," +
                "\"gameVersion\":\"1.17.3\",\"loaderVersion\":\"0.9.0\",\"entryDll\":\"CwySampleHello.dll\"," +
                "\"dependsOn\":[{\"id\":\"my.thai\",\"optional\":true}]}");

            Assert.True(manifest.IsCodeMod);
            Assert.Equal("CwySampleHello.dll", manifest.EntryDll);
            Assert.Equal(SemVer.Parse("0.9.0"), manifest.LoaderVersion);
            Assert.True(Assert.Single(manifest.AllDependencies).IsOptional);
        }

        [Fact]
        public void The_old_field_name_still_works_and_the_new_one_wins()
        {
            // RP-3: contentPatcherVersion is the field's name from before the rename to CWY:LS Mod Loader.
            var old = PackManifestParser.Parse(Head + ",\"contentPatcherVersion\":\"0.8.0\"}");
            Assert.Equal(SemVer.Parse("0.8.0"), old.LoaderVersion);

            var both = PackManifestParser.Parse(Head + ",\"loaderVersion\":\"0.9.0\",\"contentPatcherVersion\":\"0.8.0\"}");
            Assert.Equal(SemVer.Parse("0.9.0"), both.LoaderVersion);
        }

        [Fact]
        public void The_constructor_enforces_the_same_rules_as_the_parser()
        {
            var version = SemVer.Parse("1.0.0");

            Assert.Throws<ArgumentException>(() => new PackManifest("my.mod", "M", version, entryDll: "../x.dll"));
            Assert.Throws<ArgumentException>(() => new PackManifest(
                "my.mod", "M", version, entryDll: "x.dll", contentPackFor: new PackDependency("host.mod")));
            Assert.Throws<ArgumentException>(() => new PackManifest(
                "my.mod", "M", version, contentPackFor: new PackDependency("host.mod", optional: true)));
            Assert.Throws<ArgumentException>(() => new PackManifest(
                "my.mod", "M", version, contentPackFor: new PackDependency("my.mod")));
        }

        [Fact]
        public void The_constructor_merges_a_contentPackFor_that_dependsOn_also_names()
        {
            var manifest = new PackManifest(
                "my.pack", "P", SemVer.Parse("1.0.0"),
                dependsOn: new[] { new PackDependency("host.mod", SemVer.Parse("1.0.0"), optional: true) },
                contentPackFor: new PackDependency("host.mod", SemVer.Parse("2.0.0")));

            var dependency = Assert.Single(manifest.AllDependencies);
            Assert.False(dependency.IsOptional);
            Assert.Equal(SemVer.Parse("2.0.0"), dependency.MinVersion);
        }

        [Fact]
        public void The_parser_accepts_contentPackFor_with_an_optional_dependsOn_of_the_same_mod()
        {
            // Review #18: this was refused as "already a required dependency". contentPackFor makes it
            // required, and the stricter minVersion wins.
            var manifest = PackManifestParser.Parse(
                Head + ",\"dependsOn\":[{\"id\":\"host.mod\",\"minVersion\":\"3.0.0\",\"optional\":true}]," +
                "\"contentPackFor\":{\"id\":\"host.mod\",\"minVersion\":\"2.0.0\"}}");

            var dependency = Assert.Single(manifest.AllDependencies);
            Assert.Equal("host.mod", dependency.Id);
            Assert.False(dependency.IsOptional);
            Assert.Equal(SemVer.Parse("3.0.0"), dependency.MinVersion);
            Assert.True(Assert.Single(manifest.DependsOn).IsOptional);
        }

        [Fact]
        public void A_required_dependsOn_repeating_contentPackFor_is_still_refused_with_the_right_words()
        {
            var ok = PackManifestParser.TryParse(Head + ",\"dependsOn\":[\"host.mod\"],\"contentPackFor\":\"host.mod\"}", out _, out var error);

            Assert.False(ok);
            Assert.Contains("already makes \"host.mod\" a required dependency", error);
        }

        [Fact]
        public void The_manifest_collections_are_read_only_copies()
        {
            // Review probe P13: DependsOn was the parser's List, so a cast gave write access.
            var manifest = PackManifestParser.Parse(Head + ",\"dependsOn\":[\"b.pack\"],\"x-meta\":{\"k\":1}}");

            var list = Assert.IsAssignableFrom<IList<PackDependency>>(manifest.DependsOn);
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
            var all = Assert.IsAssignableFrom<IList<PackDependency>>(manifest.AllDependencies);
            Assert.True(all.IsReadOnly);
            var extensions = Assert.IsAssignableFrom<IDictionary<string, object?>>(manifest.Extensions);
            Assert.True(extensions.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => extensions.Clear());
            Assert.Single(manifest.DependsOn);
            Assert.Single(manifest.Extensions);
        }

        [Fact]
        public void The_constructor_copies_what_it_is_given()
        {
            var dependsOn = new List<PackDependency> { new PackDependency("a.pack") };
            var extensions = new Dictionary<string, object?> { ["x-one"] = 1L, ["x-two"] = "2" };
            var manifest = new PackManifest("my.pack", "P", SemVer.Parse("1.0.0"), dependsOn: dependsOn, extensions: extensions);

            dependsOn.Add(new PackDependency("b.pack"));
            extensions.Remove("x-one");

            Assert.Single(manifest.DependsOn);
            Assert.Equal(new[] { "x-one", "x-two" }, manifest.Extensions.Keys.ToArray());
            var empty = new PackManifest("my.pack", "P", SemVer.Parse("1.0.0"));
            Assert.True(Assert.IsAssignableFrom<IList<PackDependency>>(empty.DependsOn).IsReadOnly);
            Assert.True(Assert.IsAssignableFrom<IDictionary<string, object?>>(empty.Extensions).IsReadOnly);
        }

        [Fact]
        public void AsRequired_keeps_the_id_and_version()
        {
            var optional = new PackDependency("a.pack", SemVer.Parse("1.2.0"), optional: true);
            var required = optional.AsRequired();

            Assert.False(required.IsOptional);
            Assert.Equal("a.pack", required.Id);
            Assert.Equal(SemVer.Parse("1.2.0"), required.MinVersion);
            var already = new PackDependency("b.pack");
            Assert.Same(already, already.AsRequired());
        }

        [Fact]
        public void EntryDllPath_matches_listings_case_insensitively_in_any_separator_style()
        {
            var files = new List<string> { "manifest.json", "bin\\MyMod.dll", "README.md" };

            Assert.True(EntryDllPath.IsListed(files, "bin/mymod.dll"));
            Assert.False(EntryDllPath.IsListed(files, "MyMod.dll"));
            Assert.False(EntryDllPath.IsListed(null, "MyMod.dll"));
            Assert.False(EntryDllPath.IsListed(files, null));
        }

        [Theory]
        [InlineData("MyMod.dll", "MyMod")]
        [InlineData("bin/Release/My.Mod.DLL", "My.Mod")]
        [InlineData("noext", "noext")]
        public void EntryDllPath_gives_the_simple_name(string path, string expected) =>
            Assert.Equal(expected, EntryDllPath.SimpleName(path));
    }
}
