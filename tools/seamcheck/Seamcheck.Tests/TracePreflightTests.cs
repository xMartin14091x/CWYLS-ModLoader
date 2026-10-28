using System.Linq;
using CwyLs.ModLoader.Trace;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>The static-constructor hazard check behind the Windows ScreenSystem exclusion.</summary>
    public class TracePreflightTests
    {
        private const string Fixtures = "CWY.Seamcheck.Tests.Fixtures.";

        [Theory]
        [InlineData("NestopiSystem.DIContainers.ProjectLifetimeScope", "get_GameObject", true)]
        [InlineData("Bulbul.RoomLifetimeScope", "Resolve", true)]
        [InlineData("Bulbul.SaveDataManager", "get_Instance", true)]
        [InlineData("UnityEngine.Object", "FindObjectOfType", true)]
        [InlineData("UnityEngine.GameObject", "Find", true)]
        [InlineData("R3.ReactiveProperty`1", ".ctor", false)]
        [InlineData("UnityEngine.GameObject", "get_transform", false)]
        [InlineData("Bulbul.SaveDataManager", "SaveSetting", false)]
        public void Game_state_calls_are_recognised(string type, string member, bool expected)
        {
            Assert.Equal(expected, TracePreflight.ReachesGameState(new MemberRefName("A", type, member, 0)));
        }

        [Fact]
        public void A_precise_constructor_that_reaches_a_scope_is_flagged()
        {
            var constructors = TracePreflight.StaticConstructorCalls(TestAssemblies.SelfPath);

            var hazard = constructors[Fixtures + "HazardousStatics"];
            Assert.Equal("precise", hazard.Init);
            Assert.Equal(new[] { "FakeLifetimeScope.get_GameObject" }, hazard.Calls);
        }

        [Fact]
        public void A_field_initializer_reaching_a_singleton_is_flagged_as_beforefieldinit()
        {
            var singleton = TracePreflight.StaticConstructorCalls(TestAssemblies.SelfPath)[Fixtures + "SingletonStatics"];

            Assert.Equal("beforefieldinit", singleton.Init);
            Assert.Equal(new[] { "FakeSingleton.get_Instance" }, singleton.Calls);
        }

        [Fact]
        public void A_constructor_that_only_builds_objects_is_not_flagged()
        {
            Assert.Empty(TracePreflight.StaticConstructorCalls(TestAssemblies.SelfPath)[Fixtures + "HarmlessStatics"].Calls);
        }

        [Fact]
        public void An_exclusion_verdict_says_whether_it_is_still_needed()
        {
            Assert.Equal("needed", new ExclusionCheck("T", "Windows", true, true, 3, true).Verdict);
            Assert.Equal("not-applicable", new ExclusionCheck("T", "Windows", false, true, 3, false).Verdict);
            Assert.Equal("NOT-APPLIED-BUT-HAZARD", new ExclusionCheck("T", "Windows", false, true, 3, true).Verdict);
            Assert.Equal("stale-type-missing", new ExclusionCheck("T", "Windows", true, false, 0, false).Verdict);
            Assert.Equal("stale-no-hazard", new ExclusionCheck("T", "Windows", true, true, 3, false).Verdict);
        }

        [ManagedFolderFact("win")]
        public void Windows_ScreenSystem_is_the_one_hazard_and_is_excluded()
        {
            using var assemblies = ManagedAssemblySet.FromManagedDirectory(TestAssemblies.WinManaged, ToolLog.Null);

            var result = TracePreflight.Run(assemblies, TracePlatforms.Windows, TraceExclusions.Declared, ToolLog.Null);

            var hazard = Assert.Single(result.Hazards);
            Assert.Equal("NestopiSystem.ScreenSystem", hazard.Type);
            Assert.Equal("precise", hazard.Init);
            Assert.Contains("ProjectLifetimeScope.get_GameObject", hazard.Calls);
            Assert.True(hazard.ExcludedHere);
            Assert.Equal(3, hazard.PatchedMethods);
            Assert.Equal("needed", Assert.Single(result.Exclusions).Verdict);
        }

        [ManagedFolderFact("win")]
        public void Without_the_exclusion_Windows_would_patch_ScreenSystem()
        {
            using var assemblies = ManagedAssemblySet.FromManagedDirectory(TestAssemblies.WinManaged, ToolLog.Null);

            var result = TracePreflight.Run(assemblies, TracePlatforms.Windows, System.Array.Empty<TraceExclusion>(), ToolLog.Null);

            Assert.False(Assert.Single(result.Hazards).ExcludedHere);
        }

        [ManagedFolderFact("mac")]
        public void macOS_has_no_hazard_and_keeps_tracing_ScreenSystem()
        {
            using var assemblies = ManagedAssemblySet.FromManagedDirectory(TestAssemblies.MacManaged, ToolLog.Null);

            var result = TracePreflight.Run(assemblies, TracePlatforms.MacOS, TraceExclusions.Declared, ToolLog.Null);

            Assert.Empty(result.Hazards);
            var check = Assert.Single(result.Exclusions);
            Assert.Equal("not-applicable", check.Verdict);
            Assert.Equal(3, check.WouldPatch);
            Assert.True(result.Methods > 2000, "the trace filter accepted only " + result.Methods + " methods");
        }
    }
}
