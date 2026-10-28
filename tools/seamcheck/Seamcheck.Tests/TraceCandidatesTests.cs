using System.Reflection;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Trace;
using CWY.Seamcheck.Tests.Fixtures;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>
    /// The Trace plugin's candidate rules (src/CwyLs.ModLoader.Trace/TraceCandidates.cs), moved out of
    /// TracePatcher unchanged; checked on runtime types and on the same types read metadata-only,
    /// because seamcheck's preflight relies on both giving the same answer.
    /// </summary>
    public class TraceCandidatesTests
    {
        private static MethodInfo RuntimeMethod(string name) =>
            typeof(TraceShapes).GetMethod(name, TraceCandidates.DeclaredMethods)!;

        [Theory]
        [InlineData(nameof(TraceShapes.Normal), true)]
        [InlineData(nameof(TraceShapes.Abstract), false)]
        [InlineData(nameof(TraceShapes.getpid), false)]
        public void HasBody_is_false_for_abstract_and_extern_methods(string method, bool expected)
        {
            Assert.Equal(expected, TraceCandidates.HasBody(RuntimeMethod(method)));
        }

        [Theory]
        [InlineData(nameof(TraceShapes.Normal), false)]
        [InlineData(nameof(TraceShapes.Pointer), true)]
        [InlineData(nameof(TraceShapes.RefPointer), true)]
        public void Pointer_signatures_are_unpatchable(string method, bool expected)
        {
            Assert.Equal(expected, TraceCandidates.HasUnpatchableSignature(RuntimeMethod(method)));
        }

        [Fact]
        public void Only_closed_classes_are_traceable()
        {
            Assert.True(TraceCandidates.IsTraceableType(typeof(Sample)));
            Assert.False(TraceCandidates.IsTraceableType(typeof(Sample.Mode)));
            Assert.False(TraceCandidates.IsTraceableType(typeof(Holder<>)));
            Assert.True(TraceCandidates.IsTraceableType(typeof(Holder<string>)));
        }

        [Fact]
        public void Describe_gives_the_same_candidate_from_runtime_and_metadata_reflection()
        {
            using var assemblies = TestAssemblies.OpenSelf();
            var offlineType = assemblies.TryLoad(TestAssemblies.Self)!.GetType(typeof(TraceShapes).FullName!)!;
            var offlineMethods = offlineType.GetMethods(TraceCandidates.DeclaredMethods);
            var runtimeMethods = typeof(TraceShapes).GetMethods(TraceCandidates.DeclaredMethods);

            Assert.Equal(runtimeMethods.Length, offlineMethods.Length);
            foreach (var runtime in runtimeMethods)
            {
                var offline = System.Array.Find(offlineMethods, m => m.Name == runtime.Name)!;
                Assert.Equal(
                    Key(TraceCandidates.Describe(runtime, typeof(TraceShapes), false)),
                    Key(TraceCandidates.Describe(offline, offlineType, false)));
                Assert.Equal(TraceCandidates.HasUnpatchableSignature(runtime), TraceCandidates.HasUnpatchableSignature(offline));
            }
        }

        [Fact]
        public void The_traced_assemblies_are_the_game_and_its_audio_manager()
        {
            Assert.Equal(new[] { "Assembly-CSharp", "AudioManager" }, TraceCandidates.TracedAssemblies);
        }

        private static string Key(MethodCandidate c) =>
            string.Join("|", c.NamespaceName, c.ClassName, c.MethodName, c.IsPublic, c.IsStatic, c.IsAbstract,
                c.IsGenericDefinition, c.HasBody, c.IsDeclaringTypeInterface, c.IsInteractionClass);
    }
}
