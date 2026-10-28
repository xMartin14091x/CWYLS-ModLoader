using System;
using CwyLs.ModLoader.Core.Smoke;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public class SmokeReportTests
    {
        private static readonly DateTimeOffset When =
            new DateTimeOffset(2026, 9, 22, 13, 45, 1, 123, TimeSpan.FromHours(9));

        [Fact]
        public void Ok_only_when_every_check_passed()
        {
            var report = Report(SmokeCheckResult.Pass("a"), SmokeCheckResult.Pass("b"));

            Assert.True(report.Ok);
            Assert.Equal(2, report.Passed);
            Assert.Equal(2, report.Total);
            Assert.Equal(string.Empty, report.Detail);
        }

        [Fact]
        public void One_failure_fails_the_run()
        {
            var report = Report(SmokeCheckResult.Pass("a"), SmokeCheckResult.Fail("b", "missing"));

            Assert.False(report.Ok);
            Assert.Equal(1, report.Passed);
            Assert.Equal(2, report.Total);
        }

        [Fact]
        public void No_checks_is_not_a_pass()
        {
            // A run that verified nothing must not look like a green launch.
            var report = Report();

            Assert.False(report.Ok);
            Assert.Equal(0, report.Total);
            Assert.Equal("no checks", report.Detail);
            Assert.Equal(SmokeReport.NoChecksDetail, report.Detail);
        }

        [Fact]
        public void Null_results_are_rejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => new SmokeReport("s", "P0-2", "u", "g", When, null!));
            Assert.Throws<ArgumentException>(
                () => new SmokeReport("s", "P0-2", "u", "g", When, new SmokeCheckResult[] { null! }));
        }

        [Fact]
        public void ToJson_writes_the_agreed_shape()
        {
            var report = new SmokeReport(
                "abc123",
                "P0-2",
                "2022.3.62f2",
                "1.17.2",
                When,
                new[] { new SmokeCheckResult("p0.paths", true, "both exist", 2) });

            Assert.Equal(
                "{\"session\":\"abc123\",\"ticket\":\"P0-2\",\"unity\":\"2022.3.62f2\",\"game\":\"1.17.2\"," +
                "\"time\":\"2026-09-22T13:45:01.123+09:00\",\"ok\":true,\"passed\":1,\"total\":1,\"detail\":\"\"," +
                "\"checks\":[{\"id\":\"p0.paths\",\"ok\":true,\"informational\":false," +
                "\"detail\":\"both exist\",\"elapsedMs\":2}]}",
                SmokeReportWriter.ToJson(report));
        }

        [Fact]
        public void ToJson_separates_checks_and_escapes_details()
        {
            var report = Report(
                SmokeCheckResult.Pass("a"),
                SmokeCheckResult.Fail("b", "say \"no\""));

            var json = SmokeReportWriter.ToJson(report);

            Assert.Contains("\"ok\":false,\"passed\":1,\"total\":2", json);
            Assert.Contains(
                "{\"id\":\"a\",\"ok\":true,\"informational\":false,\"detail\":\"\",\"elapsedMs\":0},{\"id\":\"b\"",
                json);
            Assert.Contains("\"detail\":\"say \\\"no\\\"\"", json);
        }

        [Fact]
        public void ToJson_of_an_empty_run_still_has_a_checks_array()
        {
            var json = SmokeReportWriter.ToJson(Report());

            Assert.Contains("\"ok\":false", json);
            Assert.Contains("\"detail\":\"no checks\"", json);
            Assert.EndsWith("\"checks\":[]}", json);
        }

        [Fact]
        public void ToJson_rejects_a_null_report()
        {
            Assert.Throws<ArgumentNullException>(() => SmokeReportWriter.ToJson(null!));
        }

        private static SmokeReport Report(params SmokeCheckResult[] results) =>
            new SmokeReport("abc123", "P0-2", "2022.3.62f2", "1.17.2", When, results);
    }
}
