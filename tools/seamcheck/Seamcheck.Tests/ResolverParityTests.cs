using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>
    /// The game's <see cref="ReflectionMemberResolver"/> (runtime reflection, in this process) and
    /// seamcheck's <see cref="MetadataMemberResolver"/> (metadata only) must give the same answer and
    /// the same wording for the same target. Each case below is one shape the real tables use, or one
    /// way a target can be missing.
    /// </summary>
    public class ResolverParityTests
    {
        private const string SampleType = "CWY.Seamcheck.Tests.Fixtures.Sample";

        private static PatchTarget Method(string id, string member, string[] parameters, bool isStatic = false, string? signature = null, string type = SampleType) =>
            new PatchTarget(id, TestAssemblies.Self, type, MemberKind.Method, member, parameters, isStatic, signature);

        private static PatchTarget Field(string id, string member, bool isStatic = false, string? signature = null, string type = SampleType) =>
            new PatchTarget(id, TestAssemblies.Self, type, MemberKind.Field, member, null, isStatic, signature);

        private static PatchTarget Property(string id, string member, bool isStatic = false, string? signature = null) =>
            new PatchTarget(id, TestAssemblies.Self, SampleType, MemberKind.Property, member, null, isStatic, signature);

        /// <summary>Case ids, for the theory (strings, so xunit lists every case separately).</summary>
        public static IEnumerable<object[]> CaseIds() => Table.Select(c => new object[] { c.Target.Id });

        private static PatchTarget TargetFor(string id) => Table.Single(c => c.Target.Id == id).Target;

        private static bool FoundFor(string id) => Table.Single(c => c.Target.Id == id).Found;

        /// <summary>Every case: the target, and whether it must be found.</summary>
        private static readonly (PatchTarget Target, bool Found)[] Table = BuildTable();

        private static (PatchTarget Target, bool Found)[] BuildTable()
        {
            return new (PatchTarget Target, bool Found)[]
            {
                (Method("method.ok", "Play", new[] { "System.String", "System.Boolean", "System.Boolean" }, signature: "void Play(string, bool, bool)"), true),
                (Method("method.signature-changed", "Play", new[] { "System.String", "System.Boolean", "System.Boolean" }, signature: "int Play(string, bool, bool)"), false),
                (Method("method.missing", "Vanished", new string[0]), false),
                (Method("method.static-mismatch", "Play", new[] { "System.String", "System.Boolean", "System.Boolean" }, isStatic: true), false),
                (Method("method.no-overload", "Overloaded", new[] { "System.Double" }), false),
                (Method("method.overload", "Overloaded", new[] { "System.String" }, signature: "void Overloaded(string)"), true),
                (Method("method.ambiguous", "Ambiguous", new[] { "System.Int32" }), false),
                (Method("method.task", "Load", new string[0], signature: "Task<int> Load()"), true),
                (Method("method.nullable", "SetWhen", new[] { "System.Nullable<System.DateTime>" }, signature: "void SetWhen(Nullable<DateTime>)"), true),
                (Method("method.byref", "TryGet", new[] { "System.UInt64", SampleType + "+Inner&" }, signature: "bool TryGet(ulong, Sample.Inner&)"), true),
                (Method("method.generic-byref", "TryResolve", new[] { "T&" }, isStatic: true, signature: "static bool TryResolve(T&)"), true),
                (Method("method.nested-generic", "Map", new string[0], signature: "Dictionary<string, List<int>> Map()"), true),
                (Method("method.private-nested-enum", "Hidden", new[] { SampleType + "+Mode" }, signature: "int Hidden(Sample.Mode)"), true),
                (Field("field.ok", "Name", signature: "string Name"), true),
                (Field("field.static-private", "s_count", isStatic: true, signature: "static int s_count"), true),
                (Field("field.missing", "Nope"), false),
                (Field("field.enum-constant", "B", isStatic: true, signature: "static Sample.Mode B", type: SampleType + "+Mode"), true),
                (Property("property.ok", "Count", signature: "int Count { get; set; }"), true),
                (Property("property.static", "Flag", isStatic: true, signature: "static bool Flag { get; }"), true),
                (Property("property.signature-changed", "Count", signature: "int Count { get; }"), false),
                (Property("property.missing", "Nope"), false),
                (Field("generic.definition", "Value", signature: "T Value", type: "CWY.Seamcheck.Tests.Fixtures.Holder`1"), true),
                (Method("generic.closed", "Get", new[] { "System.String" }, signature: "string Get(string)",
                    type: "CWY.Seamcheck.Tests.Fixtures.Holder`1[[System.String, System.Private.CoreLib]]"), true),
                (Method("type.missing", "Play", new string[0], type: "CWY.Seamcheck.Tests.Fixtures.Nope"), false),
                (new PatchTarget("type.moved", TestAssemblies.Self, "CwyLs.ModLoader.Core.PatchTarget", MemberKind.Method, "Describe"), false),
                (new PatchTarget("assembly.missing", "NoSuchAssembly.Seamcheck", SampleType, MemberKind.Method, "Play"), false),
            };
        }

        [Theory]
        [MemberData(nameof(CaseIds))]
        public void Both_resolvers_agree_on_found_and_wording(string id)
        {
            var target = TargetFor(id);
            using var assemblies = TestAssemblies.OpenSelf();
            var offline = new MetadataMemberResolver(assemblies, ToolLog.Null).Resolve(target);
            var inGame = new ReflectionMemberResolver().Resolve(target);

            Assert.Equal(FoundFor(id), inGame.Found);
            Assert.Equal(inGame.Found, offline.Found);
            Assert.True(inGame.Detail == offline.Detail, id + ": game said '" + inGame.Detail + "', seamcheck said '" + offline.Detail + "'");
        }

        [Fact]
        public void The_moved_type_is_named_where_it_went()
        {
            using var assemblies = TestAssemblies.OpenSelf();
            var target = new PatchTarget("type.moved", TestAssemblies.Self, "CwyLs.ModLoader.Core.PatchTarget", MemberKind.Method, "Describe");

            var result = new MetadataMemberResolver(assemblies, ToolLog.Null).Resolve(target);

            Assert.Equal("type 'CwyLs.ModLoader.Core.PatchTarget' is not in Seamcheck.Tests any more; found in CwyLs.ModLoader.Core", result.Detail);
        }

        [Fact]
        public void A_whole_table_gives_the_game_summary_line()
        {
            using var assemblies = TestAssemblies.OpenSelf();
            var targets = Table.Select(c => c.Target).ToList();

            var offline = PatchTargetChecker.Check(targets, new MetadataMemberResolver(assemblies, ToolLog.Null));
            var inGame = PatchTargetChecker.Check(targets, new ReflectionMemberResolver());

            Assert.Equal(inGame.Summarize(), offline.Summarize());
            Assert.Equal(inGame.MissingLines(), offline.MissingLines());
            Assert.StartsWith("targets: ", offline.Summarize());
        }
    }
}
