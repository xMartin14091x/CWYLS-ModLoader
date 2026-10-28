using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Packs;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// C1-FRONTDOOR: the pure half of the code-mod loader - which mods load in which order, the
    /// runtime dependency gate, the one-entry-class rule, the shared-assembly rule, data keys,
    /// failure rate-limiting and the console wording. The Unity half only maps reflection onto these.
    /// </summary>
    public class ModsLoaderModelTests
    {
        // ---- planning ------------------------------------------------------------------------

        [Fact]
        public void The_plan_holds_only_code_mods_in_resolver_order_with_their_code_mod_dependencies()
        {
            var resolution = Resolve(
                Mod("ui.mod", deps: "\"theme.pack\",\"core.mod\",{\"id\":\"extra.mod\",\"optional\":true},{\"id\":\"absent.mod\",\"optional\":true}"),
                Pack("theme.pack"),
                Mod("core.mod"),
                Mod("extra.mod"));

            var plan = ModLoadPlanner.Plan(resolution);

            Assert.Equal(new[] { "core.mod", "extra.mod", "ui.mod" }, plan.Select(c => c.Id).ToArray());
            var ui = plan.Single(c => c.Id == "ui.mod");
            Assert.Equal(new[] { "core.mod" }, ui.RequiredCodeMods.ToArray());
            Assert.Equal(new[] { "extra.mod" }, ui.OptionalCodeMods.ToArray());
            Assert.Equal("UiMod.dll", ui.EntryDll);
        }

        [Fact]
        public void Nothing_to_plan_without_code_mods_or_a_resolution()
        {
            Assert.Empty(ModLoadPlanner.Plan(null));
            Assert.Empty(ModLoadPlanner.Plan(Resolve(Pack("only.pack"))));
        }

        [Fact]
        public void A_candidate_must_be_a_code_mod()
        {
            var pack = Resolve(Pack("only.pack")).Loaded[0];
            Assert.Throws<ArgumentException>(() => new ModCandidate(pack, null, null));
        }

        // ---- runtime gate ----------------------------------------------------------------------

        [Fact]
        public void The_gate_opens_when_every_required_code_mod_loaded()
        {
            var candidate = Candidate("ui.mod", "\"core.mod\"", Mod("core.mod"));
            var statuses = new Dictionary<string, ModStatus>
            {
                ["core.mod"] = new ModStatus("core.mod", SemVer.Parse("1.0.0"), ModState.Loaded),
            };

            Assert.True(ModLoadGate.CanLoad(candidate, statuses, out var detail));
            Assert.Equal(string.Empty, detail);
        }

        [Fact]
        public void The_gate_names_a_failed_dependency_and_where_it_failed()
        {
            var candidate = Candidate("ui.mod", "\"core.mod\"", Mod("core.mod"));
            var statuses = new Dictionary<string, ModStatus>
            {
                ["core.mod"] = new ModStatus("core.mod", null, ModState.Failed, stage: ModStage.Entry, detail: "boom"),
            };

            Assert.False(ModLoadGate.CanLoad(candidate, statuses, out var detail));
            Assert.Equal("requires core.mod, which failed in Entry", detail);
        }

        [Fact]
        public void The_gate_names_a_skipped_dependency_and_why()
        {
            var candidate = Candidate("ui.mod", "\"core.mod\"", Mod("core.mod"));
            var statuses = new Dictionary<string, ModStatus>
            {
                ["core.mod"] = new ModStatus("core.mod", null, ModState.Skipped, ModSkipReason.NoEntryType),
            };

            Assert.False(ModLoadGate.CanLoad(candidate, statuses, out var detail));
            Assert.Equal("requires core.mod, which was skipped (NoEntryType)", detail);
        }

        [Fact]
        public void The_gate_lists_every_missing_dependency()
        {
            var candidate = Candidate("ui.mod", "\"a.mod\",\"b.mod\"", Mod("a.mod"), Mod("b.mod"));

            Assert.False(ModLoadGate.CanLoad(candidate, new Dictionary<string, ModStatus>(), out var detail));
            Assert.Equal("requires a.mod, which has not loaded; b.mod, which has not loaded", detail);
        }

        [Fact]
        public void The_gate_ignores_a_failed_optional_dependency()
        {
            var candidate = Candidate("ui.mod", "{\"id\":\"core.mod\",\"optional\":true}", Mod("core.mod"));
            var statuses = new Dictionary<string, ModStatus>
            {
                ["core.mod"] = new ModStatus("core.mod", null, ModState.Failed, stage: ModStage.Entry),
            };

            Assert.True(ModLoadGate.CanLoad(candidate, statuses, out _));
        }

        [Fact]
        public void A_failure_cascades_down_a_chain_of_required_mods()
        {
            // The loader feeds each outcome back into the dictionary before the next candidate.
            var plan = ModLoadPlanner.Plan(Resolve(
                Mod("a.mod"),
                Mod("b.mod", deps: "\"a.mod\""),
                Mod("c.mod", deps: "\"b.mod\""),
                Mod("free.mod")));
            var statuses = new Dictionary<string, ModStatus>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in plan)
            {
                var status = candidate.Id == "a.mod"
                    ? new ModStatus(candidate.Id, null, ModState.Failed, stage: ModStage.Entry)
                    : ModLoadGate.CanLoad(candidate, statuses, out var why)
                        ? new ModStatus(candidate.Id, null, ModState.Loaded)
                        : new ModStatus(candidate.Id, null, ModState.Skipped, ModSkipReason.DependencyFailed, detail: why);
                statuses[candidate.Id] = status;
            }

            Assert.Equal(ModState.Skipped, statuses["b.mod"].State);
            Assert.Equal("requires a.mod, which failed in Entry", statuses["b.mod"].Detail);
            Assert.Equal("requires b.mod, which was skipped (DependencyFailed)", statuses["c.mod"].Detail);
            Assert.Equal(ModState.Loaded, statuses["free.mod"].State);
        }

        // ---- status and summary ----------------------------------------------------------------

        [Fact]
        public void A_status_keeps_only_the_reason_that_matches_its_state()
        {
            var loaded = new ModStatus("a.mod", null, ModState.Loaded, ModSkipReason.NoEntryType, ModStage.Entry);
            Assert.Null(loaded.SkipReason);
            Assert.Null(loaded.Stage);
            Assert.Equal("Loaded", loaded.ReasonText);

            var failed = new ModStatus("a.mod", SemVer.Parse("1.0.0"), ModState.Failed, ModSkipReason.NoEntryType, ModStage.Construct, "Boom: x");
            Assert.Null(failed.SkipReason);
            Assert.Equal("Construct", failed.ReasonText);
            Assert.Equal("a.mod@1.0.0: Failed (Construct) - Boom: x", failed.ToString());

            Assert.Throws<ArgumentException>(() => new ModStatus(" ", null, ModState.Loaded));
        }

        [Fact]
        public void The_stages_keep_their_numbers_and_new_ones_are_appended()
        {
            Assert.Equal(10, (int)ModStage.Shutdown);
            Assert.Equal(11, (int)ModStage.Action);
            Assert.Equal(12, (int)ModStage.Script);
            Assert.Equal(13, (int)ModStage.Token);
            Assert.Equal(14, Enum.GetValues(typeof(ModStage)).Length);
        }

        [Fact]
        public void The_summary_lists_loaded_skipped_and_failed_mods()
        {
            var summary = ModConsoleLines.Summary(new[]
            {
                new ModStatus("cwy.sample.hello", SemVer.Parse("0.1.0"), ModState.Loaded),
                new ModStatus("x.mod", null, ModState.Skipped, ModSkipReason.NoEntryType),
                new ModStatus("y.mod", null, ModState.Failed, stage: ModStage.Entry),
            });

            Assert.Equal("1 loaded (cwy.sample.hello@0.1.0); 1 skipped (x.mod: NoEntryType); 1 failed (y.mod: Entry)", summary);
            Assert.Equal("0 loaded; 0 skipped; 0 failed", ModConsoleLines.Summary(null));
        }

        [Fact]
        public void A_failure_line_is_one_line_and_counts_the_swallowed_repeats()
        {
            var line = ModConsoleLines.Failure("my.mod", ModStage.Event, "RoomReady", "NullReferenceException", "first\nsecond", 3);

            Assert.Equal("my.mod failed in Event 'RoomReady': NullReferenceException: first second (+3 earlier)", line);
            Assert.DoesNotContain("\n", line);
            Assert.Equal("my.mod failed in Entry: X", ModConsoleLines.Failure("my.mod", ModStage.Entry, null, "X", null, 0));
        }

        [Theory]
        [InlineData(ModConsoleLines.ModsTag)]
        [InlineData(ModConsoleLines.ModTag)]
        public void The_mod_tags_survive_the_console_grammar(string tag)
        {
            var record = new ConsoleRecord(
                new DateTimeOffset(2026, 9, 29, 10, 30, 0, TimeSpan.FromHours(7)),
                tag,
                "cwy.sample.hello@0.1.0 loaded",
                new[] { new ConsoleField("type", "CwySample.Hello.ModEntry"), new ConsoleField("ms", "2.1") },
                ConsoleLevel.Info);

            var line = ConsoleRecordFormatter.Format(record);
            Assert.True(ConsoleRecordParser.TryParse(line, record.Time, out var parsed));
            Assert.Equal(tag, parsed!.Tag);
            Assert.Equal("cwy.sample.hello@0.1.0 loaded", parsed.Primary);
        }

        // ---- entry type rule -------------------------------------------------------------------

        [Fact]
        public void Exactly_one_concrete_mod_class_is_the_entry()
        {
            var types = new[]
            {
                new ModTypeInfo("My.Helper", false, false, false, true),
                new ModTypeInfo("My.ModBase", true, true, false, false),
                new ModTypeInfo("My.ModEntry", true, false, false, true),
            };

            var selection = ModEntryTypeSelector.Select(types, "MyMod.dll");

            Assert.True(selection.Found);
            Assert.Equal(2, selection.Index);
            Assert.Equal("My.ModEntry", selection.Detail);
        }

        [Fact]
        public void No_mod_class_is_NoEntryType()
        {
            var selection = ModEntryTypeSelector.Select(new[] { new ModTypeInfo("My.Helper", false, false, false, true) }, "MyMod.dll");

            Assert.False(selection.Found);
            Assert.Equal(ModSkipReason.NoEntryType, selection.Reason);
            Assert.Equal(-1, selection.Index);
            Assert.Equal("MyMod.dll has no concrete class deriving from CwyMod", selection.Detail);
        }

        [Fact]
        public void Only_abstract_or_generic_mod_classes_are_named_in_the_refusal()
        {
            var selection = ModEntryTypeSelector.Select(
                new[]
                {
                    new ModTypeInfo("My.ModBase", true, true, false, false),
                    new ModTypeInfo("My.GenericMod`1", true, false, true, true),
                },
                "MyMod.dll");

            Assert.Equal(ModSkipReason.NoEntryType, selection.Reason);
            Assert.Contains("My.ModBase (abstract)", selection.Detail);
            Assert.Contains("My.GenericMod`1 (open generic)", selection.Detail);
        }

        [Fact]
        public void Two_mod_classes_are_MultipleEntryTypes_naming_both()
        {
            var selection = ModEntryTypeSelector.Select(
                new[]
                {
                    new ModTypeInfo("My.First", true, false, false, true),
                    new ModTypeInfo("My.Second", true, false, false, true),
                },
                "MyMod.dll");

            Assert.Equal(ModSkipReason.MultipleEntryTypes, selection.Reason);
            Assert.Equal("MyMod.dll has 2 classes deriving from CwyMod (My.First, My.Second); a mod has exactly one entry class", selection.Detail);
        }

        [Fact]
        public void The_entry_needs_a_parameterless_constructor()
        {
            var selection = ModEntryTypeSelector.Select(new[] { new ModTypeInfo("My.ModEntry", true, false, false, false) }, "MyMod.dll");

            Assert.Equal(ModSkipReason.NoEntryType, selection.Reason);
            Assert.Contains("needs a parameterless constructor", selection.Detail);
        }

        [Fact]
        public void A_null_type_list_is_NoEntryType()
        {
            Assert.Equal(ModSkipReason.NoEntryType, ModEntryTypeSelector.Select(null, "").Reason);
        }

        // ---- assembly identity -----------------------------------------------------------------

        [Fact]
        public void Bundled_copies_of_loaded_assemblies_are_found_in_any_folder()
        {
            var files = new[]
            {
                "manifest.json", "MyMod.dll", "lib/0Harmony.dll", "CwyLs.ModLoader.DLL", "lib/MyHelper.dll", "notes.txt",
            };
            var loaded = new List<string> { "CwyLs.ModLoader", "0Harmony", "UnityEngine.CoreModule", "mscorlib" };

            var bundled = ModAssemblyRules.FindBundledShared(files, "MyMod.dll", loaded);

            Assert.Equal(new[] { "CwyLs.ModLoader.DLL", "lib/0Harmony.dll" }, bundled.ToArray());
        }

        [Fact]
        public void The_entry_dll_itself_is_not_a_bundled_copy()
        {
            var bundled = ModAssemblyRules.FindBundledShared(new[] { "MyMod.dll" }, "mymod.dll", new List<string> { "MyMod" });

            Assert.Empty(bundled);
        }

        [Fact]
        public void Nothing_is_bundled_without_files_or_loaded_names()
        {
            Assert.Empty(ModAssemblyRules.FindBundledShared(null, "MyMod.dll", new List<string> { "x" }));
            Assert.Empty(ModAssemblyRules.FindBundledShared(new[] { "x.dll" }, "MyMod.dll", null));
            Assert.Empty(ModAssemblyRules.FindBundledShared(new[] { "x.dll" }, "MyMod.dll", new List<string>()));
        }

        [Theory]
        [InlineData("CwyLs.ModLoader", true)]
        [InlineData("CwyLs.ModLoader.Bulbul.Data", true)]
        [InlineData("cwyls.modloader.core", true)]
        [InlineData("Assembly-CSharp", true)]
        [InlineData("Assembly-CSharp-firstpass", true)]
        [InlineData("UnityEngine.CoreModule", true)]
        [InlineData("Unity.TextMeshPro", true)]
        [InlineData("BepInEx", true)]
        [InlineData("0Harmony", true)]
        [InlineData("MonoMod.Utils", true)]
        [InlineData("Mono.Cecil.Rocks", true)]
        [InlineData("System.Runtime", false)]
        [InlineData("Microsoft.Extensions.Logging", false)]
        [InlineData("mscorlib", true)]
        [InlineData("netstandard", true)]
        [InlineData("CwyLs.ModLoaderTools", false)]
        [InlineData("UnityHelpers", false)]
        [InlineData("Systematic", false)]
        [InlineData("MyMod.Helpers", false)]
        [InlineData("", false)]
        public void Shared_names_are_whole_families_not_prefixes(string name, bool shared) =>
            Assert.Equal(shared, ModAssemblyRules.IsShared(name, null));

        [Fact]
        public void Names_the_game_ships_are_shared_through_the_known_list()
        {
            var known = new List<string> { "UniTask", "R3", "Newtonsoft.Json" };

            Assert.True(ModAssemblyRules.IsShared("newtonsoft.json", known));
            Assert.False(ModAssemblyRules.IsShared("Newtonsoft.Json", null));
        }

        [Fact]
        public void A_bundled_framework_dll_is_found_even_when_it_is_not_loaded_yet()
        {
            var bundled = ModAssemblyRules.FindBundledShared(
                new[] { "MyMod.dll", "CwyLs.ModLoader.Bulbul.Data.dll", "R3.dll", "MyLib.dll" },
                "MyMod.dll",
                new List<string> { "R3" });

            Assert.Equal(new[] { "CwyLs.ModLoader.Bulbul.Data.dll", "R3.dll" }, bundled.ToArray());
        }

        [Fact]
        public void The_same_build_under_another_name_is_found_by_mvid()
        {
            var mvid = Guid.NewGuid();
            var loaded = new[]
            {
                new KeyValuePair<string, Guid>("CwyLs.ModLoader", Guid.NewGuid()),
                new KeyValuePair<string, Guid>("MyModOld", mvid),
            };

            Assert.Equal("MyModOld", ModAssemblyRules.FindSameBuild(mvid, loaded));
            Assert.Null(ModAssemblyRules.FindSameBuild(Guid.NewGuid(), loaded));
            Assert.Null(ModAssemblyRules.FindSameBuild(Guid.Empty, loaded));
            Assert.Null(ModAssemblyRules.FindSameBuild(mvid, null));
        }

        // ---- resolver order (C1-FIX review #1, #2, #15) --------------------------------------------

        [Fact]
        public void The_resolver_prefers_an_already_loaded_assembly_over_any_folder()
        {
            var decision = ModAssemblyResolver.Decide(
                "MyLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                ModAssemblyRequester.Mod,
                "/mods/a",
                Env(new[] { "/mods/a" }, _ => true, loaded: new[] { "MyLib" }));

            Assert.Equal(ModAssemblySource.Loaded, decision.Source);
            Assert.Equal("MyLib", decision.Name);
            Assert.Null(decision.Path);
        }

        [Fact]
        public void A_loaded_assembly_is_served_to_any_requester()
        {
            foreach (var requester in new[] { ModAssemblyRequester.Mod, ModAssemblyRequester.NotAMod, ModAssemblyRequester.Unknown })
            {
                var decision = ModAssemblyResolver.Decide("MyLib", requester, null, Env(new[] { "/mods/a" }, _ => true, loaded: new[] { "mylib" }));
                Assert.Equal(ModAssemblySource.Loaded, decision.Source);
            }
        }

        [Fact]
        public void The_resolver_tries_the_requesting_mods_folder_before_the_others()
        {
            var existing = Files("/mods/a", "/mods/b");

            var decision = ModAssemblyResolver.Decide(
                "MyLib", ModAssemblyRequester.Mod, "/mods/b", Env(new[] { "/mods/a", "/mods/b" }, existing.Contains));

            Assert.Equal(ModAssemblySource.ModFolder, decision.Source);
            Assert.Equal(Path.Combine("/mods/b", "MyLib.dll"), decision.Path);
        }

        [Fact]
        public void A_mod_requester_falls_back_to_the_other_mod_folders_in_load_order()
        {
            var existing = Files("/mods/b", "/mods/c");

            var decision = ModAssemblyResolver.Decide(
                "MyLib", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a", "/mods/b", "/mods/c" }, existing.Contains));

            Assert.Equal(Path.Combine("/mods/b", "MyLib.dll"), decision.Path);
            Assert.Empty(decision.AmbiguousCandidates);
        }

        [Fact]
        public void A_requester_that_is_not_a_mod_is_never_served_from_a_mod_folder()
        {
            var decision = ModAssemblyResolver.Decide(
                "MyLib", ModAssemblyRequester.NotAMod, null, Env(new[] { "/mods/a" }, _ => true));

            Assert.Equal(ModAssemblySource.NotFound, decision.Source);
            Assert.Null(decision.Path);
            Assert.Empty(decision.IgnoredSharedCopies);
        }

        [Fact]
        public void An_unknown_requester_is_served_only_when_exactly_one_mod_folder_has_the_name()
        {
            var one = ModAssemblyResolver.Decide(
                "MyLib", ModAssemblyRequester.Unknown, null, Env(new[] { "/mods/a", "/mods/b" }, Files("/mods/b").Contains));
            Assert.Equal(ModAssemblySource.ModFolder, one.Source);
            Assert.Equal(Path.Combine("/mods/b", "MyLib.dll"), one.Path);

            var two = ModAssemblyResolver.Decide(
                "MyLib", ModAssemblyRequester.Unknown, "/mods/a", Env(new[] { "/mods/a", "/mods/b", "/mods/c" }, Files("/mods/b", "/mods/c").Contains));
            Assert.Equal(ModAssemblySource.NotFound, two.Source);
            Assert.Equal(new[] { Path.Combine("/mods/b", "MyLib.dll"), Path.Combine("/mods/c", "MyLib.dll") }, two.AmbiguousCandidates.ToArray());

            var none = ModAssemblyResolver.Decide("MyLib", ModAssemblyRequester.Unknown, null, Env(new[] { "/mods/a" }, _ => false));
            Assert.Equal(ModAssemblySource.NotFound, none.Source);
            Assert.Empty(none.AmbiguousCandidates);
        }

        [Fact]
        public void The_resolver_never_serves_a_shared_name_from_a_mod_folder()
        {
            var decision = ModAssemblyResolver.Decide(
                "CwyLs.ModLoader.Core, Version=0.0.0.0",
                ModAssemblyRequester.Mod,
                "/mods/a",
                Env(new[] { "/mods/a", "/mods/b" }, _ => true));

            Assert.Equal(ModAssemblySource.NotFound, decision.Source);
            Assert.Equal(new[] { Path.Combine("/mods/a", "CwyLs.ModLoader.Core.dll"), Path.Combine("/mods/b", "CwyLs.ModLoader.Core.dll") },
                decision.IgnoredSharedCopies.ToArray());
        }

        [Fact]
        public void A_game_library_listed_as_shared_is_not_served_from_a_mod_folder_either()
        {
            var decision = ModAssemblyResolver.Decide(
                "R3", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a" }, _ => true, known: new[] { "R3" }));

            Assert.Equal(ModAssemblySource.NotFound, decision.Source);
            Assert.Single(decision.IgnoredSharedCopies);
        }

        [Theory]
        [InlineData("System.ClientModel, Version=1.1.0.0, Culture=neutral, PublicKeyToken=92742159e12e44c8")]
        [InlineData("Microsoft.Extensions.Logging.Abstractions, Version=8.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60")]
        [InlineData("System.Reactive, Version=6.0.0.0, Culture=neutral, PublicKeyToken=94bc3704cddfc263")]
        [InlineData("Microsoft.CSharp")]
        [InlineData("System.Collections.Immutable")]
        public void A_provider_sdk_dependency_the_game_does_not_ship_is_served_from_the_mod_folder(string requested)
        {
            // Review probe P2: System.* and Microsoft.* were whole shared families, so these never loaded.
            var decision = ModAssemblyResolver.Decide(
                requested,
                ModAssemblyRequester.Mod,
                "/mods/my.mod",
                Env(new[] { "/mods/my.mod" }, _ => true, loaded: new[] { "mscorlib", "System", "CwyLs.ModLoader" },
                    known: new[] { "System.Text.Json", "Microsoft.Bcl.AsyncInterfaces" }));

            Assert.Equal(ModAssemblySource.ModFolder, decision.Source);
            Assert.Empty(decision.IgnoredSharedCopies);
        }

        [Theory]
        [InlineData("System.Text.Json")]
        [InlineData("Microsoft.Bcl.AsyncInterfaces")]
        public void A_system_library_the_game_ships_stays_shared(string requested)
        {
            var decision = ModAssemblyResolver.Decide(
                requested,
                ModAssemblyRequester.Mod,
                "/mods/my.mod",
                Env(new[] { "/mods/my.mod" }, _ => true, known: new[] { "System.Text.Json", "Microsoft.Bcl.AsyncInterfaces" }));

            Assert.Equal(ModAssemblySource.NotFound, decision.Source);
            Assert.Single(decision.IgnoredSharedCopies);
        }

        [Fact]
        public void Another_mods_entry_name_is_only_served_from_that_mods_folder()
        {
            // Review #15: a.mod bundles b.mod's entry DLL; the copy in /mods/a is never loaded.
            var reserved = new Dictionary<string, string> { ["BMod"] = "/mods/b" };
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine("/mods/a", "BMod.dll"), Path.Combine("/mods/b", "BMod.dll") };

            var beforeB = ModAssemblyResolver.Decide(
                "BMod", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a" }, existing.Contains, reserved: reserved));
            Assert.Equal(ModAssemblySource.NotFound, beforeB.Source);
            Assert.Equal(new[] { Path.Combine("/mods/a", "BMod.dll") }, beforeB.IgnoredReservedCopies.ToArray());

            var afterB = ModAssemblyResolver.Decide(
                "bmod", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a", "/mods/b/" }, existing.Contains, reserved: reserved));
            Assert.Equal(ModAssemblySource.ModFolder, afterB.Source);
            Assert.Equal(Path.Combine("/mods/b/", "bmod.dll"), afterB.Path);

            var unknown = ModAssemblyResolver.Decide(
                "BMod", ModAssemblyRequester.Unknown, null, Env(new[] { "/mods/a", "/mods/b" }, existing.Contains, reserved: reserved));
            Assert.Equal(ModAssemblySource.ModFolder, unknown.Source);
            Assert.Equal(Path.Combine("/mods/b", "BMod.dll"), unknown.Path);
        }

        [Fact]
        public void A_bundled_copy_of_another_mods_entry_dll_is_found_and_named()
        {
            var owners = new Dictionary<string, string> { ["BMod"] = "b.mod", ["AMod"] = "a.mod" };

            var found = ModAssemblyRules.FindBundledReserved(
                new[] { "AMod.dll", "lib/bmod.dll", "lib/Helper.dll", "readme.txt" }, "AMod.dll", "a.mod", owners);

            var copy = Assert.Single(found);
            Assert.Equal("lib/bmod.dll", copy.Key);
            Assert.Equal("b.mod", copy.Value);
            Assert.Empty(ModAssemblyRules.FindBundledReserved(new[] { "x/AMod.dll" }, "AMod.dll", "a.mod", owners));
            Assert.Empty(ModAssemblyRules.FindBundledReserved(new[] { "BMod.dll" }, "AMod.dll", "a.mod", null));
        }

        [Fact]
        public void The_resolver_leaves_resources_and_unknown_names_alone()
        {
            Assert.Equal(ModAssemblySource.NotFound,
                ModAssemblyResolver.Decide("MyMod.resources, Culture=th", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a" }, _ => true)).Source);
            Assert.Equal(ModAssemblySource.NotFound,
                ModAssemblyResolver.Decide("Absent", ModAssemblyRequester.Mod, "/mods/a", Env(new[] { "/mods/a" }, _ => false)).Source);
            Assert.Equal(ModAssemblySource.NotFound,
                ModAssemblyResolver.Decide(" ", ModAssemblyRequester.Unknown, null, Env(null, _ => true)).Source);
            Assert.Throws<ArgumentNullException>(() => ModAssemblyResolver.Decide("x", ModAssemblyRequester.Mod, null, null!));
            Assert.Throws<ArgumentNullException>(() => new ModAssemblyEnvironment(null, null, null!));
        }

        [Theory]
        [InlineData("/mods/a", "/mods/a/", true)]
        [InlineData("/Mods/A", "/mods/a", true)]
        [InlineData("/mods/a", "/mods/ab", false)]
        [InlineData(null, "", true)]
        public void Folders_compare_without_case_or_trailing_separators(string? a, string? b, bool same) =>
            Assert.Equal(same, ModAssemblyResolver.SameFolder(a, b));

        [Theory]
        [InlineData("MyLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", "MyLib")]
        [InlineData("  MyLib  ", "MyLib")]
        [InlineData("My.Lib", "My.Lib")]
        [InlineData(null, "")]
        public void The_simple_name_is_the_part_before_the_first_comma(string? full, string expected) =>
            Assert.Equal(expected, ModAssemblyResolver.SimpleName(full));

        [Fact]
        public void An_already_loaded_simple_name_is_detected_case_insensitively()
        {
            var loaded = new[] { "CwyLs.ModLoader", "CwySampleHello" };

            Assert.True(ModAssemblyRules.IsAlreadyLoaded("cwysamplehello", loaded));
            Assert.False(ModAssemblyRules.IsAlreadyLoaded("Other", loaded));
            Assert.False(ModAssemblyRules.IsAlreadyLoaded(null, loaded));
            Assert.False(ModAssemblyRules.IsAlreadyLoaded("x", null));
        }

        // ---- data keys -------------------------------------------------------------------------

        [Theory]
        [InlineData("state.json", true)]
        [InlineData("greeted_2026-09-29.txt", true)]
        [InlineData("A", true)]
        [InlineData("-dash-first", true)]
        [InlineData(".hidden", false)]
        [InlineData("a..b", false)]
        [InlineData("sub/file.json", false)]
        [InlineData("sub\\file.json", false)]
        [InlineData("with space", false)]
        [InlineData("ไทย.json", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Data_keys_are_plain_file_names(string? key, bool valid) =>
            Assert.Equal(valid, ModDataKey.IsValid(key));

        [Fact]
        public void A_data_key_is_at_most_64_characters()
        {
            Assert.True(ModDataKey.IsValid(new string('a', 64)));
            Assert.False(ModDataKey.IsValid(new string('a', 65)));
        }

        [Fact]
        public void The_temporary_file_of_a_write_can_never_be_a_key()
        {
            Assert.Equal("state.json.tmp~", ModDataKey.TempName("state.json"));
            Assert.False(ModDataKey.IsValid(ModDataKey.TempName("state.json")));
        }

        [Fact]
        public void Require_names_the_rule()
        {
            var error = Assert.Throws<ArgumentException>(() => ModDataKey.Require("../escape"));
            Assert.Contains("letters, digits", error.Message);
            ModDataKey.Require("fine.json");
        }

        // ---- failure rate limit ----------------------------------------------------------------

        [Fact]
        public void The_first_failure_logs_and_repeats_in_the_window_are_counted()
        {
            var limiter = new ModFailureLimiter(10);

            Assert.True(limiter.ShouldLog("m|Tick|x", 0, out var first));
            Assert.Equal(0, first);
            Assert.False(limiter.ShouldLog("m|Tick|x", 1, out var s1));
            Assert.Equal(1, s1);
            Assert.False(limiter.ShouldLog("m|Tick|x", 9.9, out var s2));
            Assert.Equal(2, s2);
            Assert.Equal(2, limiter.PendingSuppressed);

            Assert.True(limiter.ShouldLog("m|Tick|x", 10, out var carried));
            Assert.Equal(2, carried);
            Assert.Equal(0, limiter.PendingSuppressed);
        }

        [Fact]
        public void Sites_are_limited_independently()
        {
            var limiter = new ModFailureLimiter(10);

            Assert.True(limiter.ShouldLog("a", 0, out _));
            Assert.True(limiter.ShouldLog("b", 0, out _));
            Assert.False(limiter.ShouldLog("a", 0.5, out _));
            Assert.True(limiter.ShouldLog(null!, 0, out _));
        }

        [Fact]
        public void A_zero_window_logs_every_failure()
        {
            var limiter = new ModFailureLimiter(0);

            Assert.True(limiter.ShouldLog("a", 0, out _));
            Assert.True(limiter.ShouldLog("a", 0, out var suppressed));
            Assert.Equal(0, suppressed);
            Assert.Equal(0, new ModFailureLimiter(-5).WindowSeconds);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static PackResolution Resolve(params IPackSource[] sources) =>
            PackResolver.Resolve(sources, "1.17.3", CwyApi.Version);

        private static ModAssemblyEnvironment Env(
            string[]? folders,
            Func<string, bool> exists,
            string[]? loaded = null,
            string[]? known = null,
            IReadOnlyDictionary<string, string>? reserved = null) =>
            new ModAssemblyEnvironment(loaded ?? new string[0], folders, exists, known, reserved);

        private static HashSet<string> Files(params string[] folders)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
            {
                files.Add(Path.Combine(folder, "MyLib.dll"));
            }

            return files;
        }

        private static ModCandidate Candidate(string id, string deps, params IPackSource[] others)
        {
            var sources = new List<IPackSource>(others) { Mod(id, deps) };
            return ModLoadPlanner.Plan(Resolve(sources.ToArray())).Single(c => c.Id == id);
        }

        private static Source Pack(string id) =>
            new Source(id, "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\"}", "manifest.json");

        private static Source Mod(string id, string? deps = null)
        {
            var dll = string.Concat(id.Split('.').Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1))) + ".dll";
            var json = "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"version\":\"1.0.0\",\"entryDll\":\"" + dll + "\"";
            if (deps != null)
            {
                json += ",\"dependsOn\":[" + deps + "]";
            }

            return new Source(id, json + "}", "manifest.json", dll);
        }

        private sealed class Source : IPackSource
        {
            internal Source(string folder, string manifest, params string[] files)
            {
                Id = folder;
                FolderName = folder;
                ManifestText = manifest;
                Files = files;
            }

            public string Id { get; }

            public string FolderName { get; }

            public string? ManifestText { get; }

            public IReadOnlyList<string> Files { get; }
        }
    }
}
