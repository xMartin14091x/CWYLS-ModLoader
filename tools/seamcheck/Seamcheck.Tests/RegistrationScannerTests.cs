using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using CWY.Seamcheck.Tests.Fixtures;
using Xunit;

namespace CWY.Seamcheck.Tests
{
    /// <summary>
    /// Registration discovery: the IL scan finds every <c>PatchTargetRegistry.Register</c> call,
    /// classifies how its table is passed, and the loader reads exactly the static tables.
    /// </summary>
    public class RegistrationScannerTests
    {
        private const string Shapes = "CWY.Seamcheck.Tests.Fixtures.RegistrationShapes";

        private static RegistrationSite Site(string method) =>
            RegistrationScanner.Scan(TestAssemblies.SelfPath).Single(s => s.Caller == Shapes + "." + method);

        [Fact]
        public void A_property_table_is_found_with_its_owner_literal()
        {
            var site = Site(nameof(RegistrationShapes.ByProperty));

            Assert.Equal(TableSourceKind.StaticProperty, site.Kind);
            Assert.Equal(RegistrationShapes.PropertyOwner, site.Owner);
            Assert.Equal(Shapes + ".PropertyTable", site.TableName);
        }

        [Theory]
        [InlineData(nameof(RegistrationShapes.ByField), nameof(TableSourceKind.StaticField))]
        [InlineData(nameof(RegistrationShapes.ByMethod), nameof(TableSourceKind.StaticMethod))]
        [InlineData(nameof(RegistrationShapes.Forward), nameof(TableSourceKind.Forwarded))]
        [InlineData(nameof(RegistrationShapes.Inline), nameof(TableSourceKind.Unenumerable))]
        [InlineData(nameof(RegistrationShapes.ByThrowing), nameof(TableSourceKind.StaticProperty))]
        public void Each_way_of_passing_a_table_is_classified(string method, string expected)
        {
            Assert.Equal(expected, Site(method).Kind.ToString());
        }

        [Fact]
        public void Static_tables_load_in_table_order()
        {
            Assert.Equal(
                new[] { "fixture.property", "fixture.field" },
                TableLoader.Load(Site(nameof(RegistrationShapes.ByProperty))).Select(t => t.Id));
            Assert.Equal("fixture.field", Assert.Single(TableLoader.Load(Site(nameof(RegistrationShapes.ByField)))).Id);
            Assert.Equal("fixture.method", Assert.Single(TableLoader.Load(Site(nameof(RegistrationShapes.ByMethod)))).Id);
        }

        [Fact]
        public void A_table_that_throws_is_a_tool_error_naming_the_table_and_the_call()
        {
            var error = Assert.Throws<SeamcheckException>(() => TableLoader.Load(Site(nameof(RegistrationShapes.ByThrowing))));

            Assert.Contains(Shapes + ".get_Throwing", error.Message);
            Assert.Contains("InvalidOperationException: fixture table is broken", error.Message);
            Assert.Contains(Shapes + ".ByThrowing", error.Message);
        }

        [Fact]
        public void A_forwarder_or_inline_table_cannot_be_loaded()
        {
            Assert.Throws<SeamcheckException>(() => TableLoader.Load(Site(nameof(RegistrationShapes.Forward))));
            Assert.Throws<SeamcheckException>(() => TableLoader.Load(Site(nameof(RegistrationShapes.Inline))));
        }

        [Fact]
        public void The_summary_reports_errors_forwarders_and_duplicates_like_the_game()
        {
            var summary = SeamcheckRunner.LoadRegistrations(new[] { TestAssemblies.SelfPath }, ToolLog.Null);

            Assert.Single(summary.Forwarders);
            Assert.Contains(summary.Errors, e => e.Contains(Shapes + ".Inline") && e.Contains("cannot be read offline"));
            Assert.Contains(summary.Errors, e => e.Contains("fixture table is broken"));

            // Both the field and the property table declare "fixture.field": one survives, one is reported.
            Assert.Single(summary.Targets, t => t.Id == "fixture.field");
            Assert.Contains(summary.Warnings, w => w.Contains("'fixture.field' is already registered"));
        }

        [Fact]
        public void The_real_plugins_register_their_tables_through_static_members_only()
        {
            var summary = SeamcheckRunner.LoadRegistrations(TestAssemblies.PluginAssemblies(), ToolLog.Null);

            Assert.Empty(summary.Errors);
            Assert.Empty(summary.Warnings);
            Assert.Contains(summary.Loaded, r => r.Site.TableName == "CwyLs.ModLoader.Bulbul.BulbulPatchTargets.All");
            Assert.Contains(summary.Loaded, r => r.Site.TableName == "CwyLs.ModLoader.Bulbul.Events.BulbulEventTargets.All");
            Assert.Contains(summary.Loaded, r => r.Site.TableName == "CwyLs.ModLoader.Bulbul.Data.DataPatchTargets.All");
            Assert.True(summary.Targets.Count >= 132, "expected at least the 132 targets registered after C5, got " + summary.Targets.Count);
            Assert.Equal(summary.Targets.Count, summary.Targets.Select(t => t.Id).Distinct().Count());
        }

        [Fact]
        public void The_decoder_walks_a_switch_to_the_end_of_the_body()
        {
            using var stream = System.IO.File.OpenRead(TestAssemblies.SelfPath);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var names = new MetadataNames(reader);
            var handle = reader.TypeDefinitions.Single(t => names.TypeDefinitionName(t) == typeof(Switches).FullName);
            var method = reader.GetTypeDefinition(handle).GetMethods()
                .Select(reader.GetMethodDefinition)
                .Single(m => reader.GetString(m.Name) == nameof(Switches.Pick));

            var instructions = IlDecoder.Decode(pe.GetMethodBody(method.RelativeVirtualAddress));

            Assert.Contains(instructions, i => i.OpCode == ILOpCode.Switch);
            Assert.Equal(ILOpCode.Ret, instructions[^1].OpCode);
            Assert.True(instructions.Zip(instructions.Skip(1)).All(p => p.First.Offset < p.Second.Offset));
        }
    }
}
