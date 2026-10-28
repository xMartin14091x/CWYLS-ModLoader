using System;
using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// <see cref="SmokeRegistry"/> is process-wide state, so its tests must not run beside
    /// another class that also touches it.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class SmokeRegistryCollection
    {
        public const string Name = "SmokeRegistry";
    }

    [Collection(SmokeRegistryCollection.Name)]
    public class SmokeRegistryTests : IDisposable
    {
        public SmokeRegistryTests() => SmokeRegistry.Clear();

        public void Dispose() => SmokeRegistry.Clear();

        [Fact]
        public void Registered_checks_come_back_in_registration_order()
        {
            SmokeRegistry.Register(Check("b", "P0"));
            SmokeRegistry.Register(Check("a", "P0"));

            var snapshot = SmokeRegistry.Snapshot();

            Assert.Equal(2, SmokeRegistry.Count);
            Assert.Equal(new[] { "b", "a" }, new[] { snapshot[0].Id, snapshot[1].Id });
        }

        [Fact]
        public void A_snapshot_is_isolated_from_later_registrations()
        {
            SmokeRegistry.Register(Check("a", "P0"));
            var snapshot = SmokeRegistry.Snapshot();

            SmokeRegistry.Register(Check("b", "P0"));

            Assert.Single(snapshot);
            Assert.Equal(2, SmokeRegistry.Snapshot().Count);
        }

        [Fact]
        public void Re_registering_an_id_replaces_it_in_place()
        {
            // A plugin whose Awake runs twice must not double-report the same check.
            SmokeRegistry.Register(Check("a", "P0"));
            SmokeRegistry.Register(Check("b", "P0"));
            SmokeRegistry.Register(new SmokeCheck("a", "P1", () => SmokeCheckResult.Pass("a", "second")));

            var snapshot = SmokeRegistry.Snapshot();

            Assert.Equal(2, snapshot.Count);
            Assert.Equal("a", snapshot[0].Id);
            Assert.Equal("P1", snapshot[0].Phase);
            Assert.Equal("second", snapshot[0].Run().Detail);
        }

        [Fact]
        public void Snapshot_can_filter_by_phase()
        {
            SmokeRegistry.Register(Check("a", "P0"));
            SmokeRegistry.Register(Check("b", "P1"));

            Assert.Single(SmokeRegistry.Snapshot("P0"));
            Assert.Equal("b", Assert.Single(SmokeRegistry.Snapshot("p1")).Id);
            Assert.Empty(SmokeRegistry.Snapshot("P9"));
        }

        [Fact]
        public void Register_rejects_null()
        {
            Assert.Throws<ArgumentNullException>(() => SmokeRegistry.Register(null!));
            Assert.Equal(0, SmokeRegistry.Count);
        }

        private static SmokeCheck Check(string id, string phase) =>
            new SmokeCheck(id, phase, () => SmokeCheckResult.Pass(id));
    }
}
