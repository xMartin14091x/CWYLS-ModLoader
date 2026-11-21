using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>W2b-05: the four Studio catalog GET routes through the router: parsing, strictness, shapes and error mapping.</summary>
    public sealed class MakerStudioCatalogRouterTests
    {
        private const string S = "/v1/maker/studio";
        private const string Lines = S + "/packs/sample.pack/languages/th/lines";

        private static MakerRig Rig(RecordingCatalog? catalog = null) =>
            new MakerRig(studio: () => new MakerStudioBackends(catalog: catalog ?? new RecordingCatalog()));

        [Fact]
        public void The_catalog_is_advertised_only_when_a_backend_is_installed()
        {
            var with = Rig();
            var without = new MakerRig(studio: () => new MakerStudioBackends());

            var present = ApiTestKit.Json(with.Send(MakerHttp.Get(S + "/capabilities", with.Token)));
            var absent = ApiTestKit.Json(without.Send(MakerHttp.Get(S + "/capabilities", without.Token)));

            Assert.Contains("catalog", ((IEnumerable<object?>)present["features"]!).Cast<string>());
            Assert.DoesNotContain("catalog", ((IEnumerable<object?>)absent["features"]!).Cast<string>());
        }

        [Fact]
        public void Packs_has_the_contract_shape_and_refuses_a_query_a_body_and_other_methods()
        {
            var rig = Rig();

            var response = rig.Send(MakerHttp.Get(S + "/packs", rig.Token));

            Assert.Equal(200, response.Status);
            var pack = Assert.IsType<JsonObject>(((IList<object?>)ApiTestKit.Json(response)["packs"]!)[0]);
            Assert.Equal(new[] { "id", "name", "version", "languages", "editable", "readOnlyReason", "loaded", "restartRequired",
                "studioEditable", "studioReadOnlyReason", "metadataState" }, pack.Keys.ToArray());
            Assert.Equal(400, rig.Send(MakerHttp.Get(S + "/packs?x=1", rig.Token)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Json("PUT", S + "/packs", rig.Token, "{}")).Status);
            Assert.Equal(401, rig.Send(MakerHttp.Get(S + "/packs", null)).Status);
        }

        [Fact]
        public void Lines_parses_every_query_member_into_the_catalog_query_with_the_documented_defaults()
        {
            var catalog = new RecordingCatalog();
            var rig = Rig(catalog);

            Assert.Equal(200, rig.Send(MakerHttp.Get(Lines, rig.Token)).Status);
            var defaults = catalog.LastQuery!;
            Assert.Equal("sample.pack", defaults.PackId);
            Assert.Equal("th", defaults.Lang);
            Assert.Equal("all", defaults.Filter);
            Assert.Equal(0, defaults.Offset);
            Assert.Equal(100, defaults.Limit);
            Assert.Null(defaults.Search);
            Assert.Null(defaults.Group);
            Assert.Null(defaults.CatalogRevision);

            var full = rig.Send(MakerHttp.Get(Lines + "?q=%E0%B8%94%E0%B8%B2%E0%B8%A7&group=focus&filter=needs-retake&offset=5&limit=7&catalogRevision=abc-123", rig.Token));
            Assert.Equal(200, full.Status);
            var query = catalog.LastQuery!;
            Assert.Equal("ดาว", query.Search);
            Assert.Equal("focus", query.Group);
            Assert.Equal("needs-retake", query.Filter);
            Assert.Equal(5, query.Offset);
            Assert.Equal(7, query.Limit);
            Assert.Equal("abc-123", query.CatalogRevision);
        }

        [Theory]
        [InlineData("?filter=warnings")]
        [InlineData("?filter=retake")]
        [InlineData("?limit=0")]
        [InlineData("?limit=501")]
        [InlineData("?limit=x")]
        [InlineData("?offset=-1")]
        [InlineData("?group=")]
        [InlineData("?catalogRevision=has%20space")]
        [InlineData("?lang=th")]
        [InlineData("?surprise=1")]
        public void Lines_refuses_a_bad_or_unknown_query_with_400_before_the_backend_is_called(string query)
        {
            var catalog = new RecordingCatalog();
            var rig = Rig(catalog);

            var response = rig.Send(MakerHttp.Get(Lines + query, rig.Token));

            Assert.Equal(400, response.Status);
            Assert.Equal(0, catalog.Calls);
        }

        [Fact]
        public void The_limit_message_names_the_range()
        {
            var rig = Rig();

            var response = rig.Send(MakerHttp.Get(Lines + "?limit=501", rig.Token));

            Assert.Contains("limit must be 1-500", response.Body);
        }

        [Fact]
        public void Lines_returns_the_page_shape_with_the_full_line_member_order()
        {
            var rig = Rig();

            var json = ApiTestKit.Json(rig.Send(MakerHttp.Get(Lines, rig.Token)));

            Assert.Equal(new[] { "total", "offset", "catalogRevision", "items" }, json.Keys.ToArray());
            var line = Assert.IsType<JsonObject>(((IList<object?>)json["items"]!)[0]);
            Assert.Equal(new[] { "id", "voice", "group", "durationSec", "ja", "en", "subtitle", "subtitlePack", "dubPack", "dubDurationSec",
                "ownedSubtitle", "ownedDub", "workflow", "takeCount", "revision", "clipRevision", "publishedTakeId", "publishedTakeRevision",
                "context", "readOnly" }, line.Keys.ToArray());
            Assert.Equal(new[] { "status", "starred" }, ((JsonObject)line["workflow"]!).Keys.ToArray());
            Assert.Equal(new[] { "occurrences", "selectedOccurrenceId", "mood", "pose", "source" }, ((JsonObject)line["context"]!).Keys.ToArray());
            var occurrence = (JsonObject)((IList<object?>)((JsonObject)line["context"]!)["occurrences"]!)[0]!;
            Assert.Equal(new[] { "occurrenceId", "groupId", "order", "previousId", "nextId", "talker", "bodyMotion", "facialMotion" }, occurrence.Keys.ToArray());
        }

        [Fact]
        public void Line_passes_an_occurrence_id_and_refuses_anything_else_in_the_query()
        {
            var catalog = new RecordingCatalog();
            var rig = Rig(catalog);
            var occurrence = new string('c', 32);

            Assert.Equal(200, rig.Send(MakerHttp.Get(Lines + "/line_1?occurrenceId=" + occurrence, rig.Token)).Status);
            Assert.Equal(occurrence, catalog.LastOccurrence);
            Assert.Equal(200, rig.Send(MakerHttp.Get(Lines + "/line_1", rig.Token)).Status);
            Assert.Null(catalog.LastOccurrence);
            var before = catalog.Calls;
            Assert.Equal(400, rig.Send(MakerHttp.Get(Lines + "/line_1?occurrenceId=not-hex", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(Lines + "/line_1?other=1", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(S + "/packs/BAD/languages/th/lines/line_1", rig.Token)).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Get(S + "/packs/sample.pack/languages/TH/lines/line_1", rig.Token)).Status);
            Assert.Equal(before, catalog.Calls);
        }

        [Fact]
        public void Groups_has_the_contract_shape_and_takes_no_query()
        {
            var rig = Rig();
            var path = S + "/packs/sample.pack/languages/th/groups";

            var json = ApiTestKit.Json(rig.Send(MakerHttp.Get(path, rig.Token)));

            Assert.Equal(new[] { "catalogRevision", "overall", "groups" }, json.Keys.ToArray());
            Assert.Equal(new[] { "totalLines", "dubbableLines", "subtitlePresent", "dubPresent", "todo", "draft", "done", "needsRetake", "starred" },
                ((JsonObject)json["overall"]!).Keys.ToArray());
            var group = (JsonObject)((IList<object?>)json["groups"]!)[0]!;
            Assert.Equal(new[] { "id", "labelKey", "title", "order", "progress" }, group.Keys.ToArray());
            Assert.Equal(400, rig.Send(MakerHttp.Get(path + "?x=1", rig.Token)).Status);
        }

        [Theory]
        [InlineData("restart_required", 409)]
        [InlineData("read_only_pack", 403)]
        [InlineData("not_found", 404)]
        [InlineData("conflict", 409)]
        [InlineData("metadata_unavailable", 503)]
        [InlineData("not_ready", 409)]
        public void A_catalog_error_keeps_its_contract_status_and_code_on_every_read_route(string code, int status)
        {
            var rig = Rig(new RecordingCatalog { Fail = code });

            foreach (var path in new[] { S + "/packs", Lines, Lines + "/line_1", S + "/packs/sample.pack/languages/th/groups" })
            {
                var response = rig.Send(MakerHttp.Get(path, rig.Token));
                Assert.Equal(status, response.Status);
                Assert.Equal(code, ApiTestKit.ErrorCode(response));
            }
        }

        [Fact]
        public void The_read_routes_are_503_without_a_catalog_and_404_for_other_methods()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends());
            var groups = S + "/packs/sample.pack/languages/th/groups";

            foreach (var path in new[] { S + "/packs", Lines, Lines + "/line_1", groups })
            {
                Assert.Equal(503, rig.Send(MakerHttp.Get(path, rig.Token)).Status);
            }

            var full = Rig();
            foreach (var path in new[] { Lines, Lines + "/line_1", groups })
            {
                Assert.Equal(404, full.Send(MakerHttp.Json("POST", path, full.Token, "{}")).Status);
                Assert.Equal(404, full.Send(MakerHttp.Json("DELETE", path, full.Token, "{}")).Status);
            }
        }

        [Fact]
        public void The_existing_text_work_routes_still_answer_after_the_catalog_routes_were_added()
        {
            var rig = new MakerRig(studio: () => new MakerStudioBackends(catalog: new RecordingCatalog()));

            Assert.Equal(503, rig.Send(MakerHttp.Get(Lines + "/line_1/history", rig.Token)).Status);
            Assert.Equal(503, rig.Send(MakerHttp.Get(S + "/packs/sample.pack/glossary/th", rig.Token)).Status);
            Assert.Equal(404, rig.Send(MakerHttp.Get(S + "/packs/sample.pack/languages/th", rig.Token)).Status);
        }

        private sealed class RecordingCatalog : IMakerStudioCatalog
        {
            internal int Calls;
            internal string? Fail;
            internal MakerStudioLineQuery? LastQuery;
            internal string? LastOccurrence;

            private MakerResult<T> Answer<T>(T value) where T : class
            {
                Interlocked.Increment(ref Calls);
                return Fail != null ? MakerResult<T>.Fail(Fail, "refused") : MakerResult<T>.Success(value);
            }

            private static MakerStudioLine SampleLine() => new MakerStudioLine
            {
                Id = "line_1", Voice = "Voice_1", Group = "focus", DurationSec = 1.5, Ja = "ja", En = "en", Revision = "r1", ClipRevision = "c1",
                Context = new MakerStudioContext
                {
                    Occurrences = new[] { new MakerStudioOccurrence { OccurrenceId = new string('a', 32), GroupId = "focus", Order = 0 } },
                    SelectedOccurrenceId = new string('a', 32), Source = "novel",
                },
            };

            public Task<MakerResult<IReadOnlyList<MakerStudioPack>>> PacksAsync(CancellationToken cancel) =>
                Task.FromResult(Answer<IReadOnlyList<MakerStudioPack>>(new[] { new MakerStudioPack { Id = "sample.pack", Name = "Sample", Version = "1.0.0", MetadataState = "ready" } }));

            public Task<MakerResult<MakerStudioLinePage>> LinesAsync(MakerStudioLineQuery query, CancellationToken cancel)
            {
                LastQuery = query;
                return Task.FromResult(Answer(new MakerStudioLinePage { Total = 1, CatalogRevision = "cat", Items = new[] { SampleLine() } }));
            }

            public Task<MakerResult<MakerStudioLine>> LineAsync(string packId, string lang, string id, string? occurrenceId, CancellationToken cancel)
            {
                LastOccurrence = occurrenceId;
                return Task.FromResult(Answer(SampleLine()));
            }

            public Task<MakerResult<MakerStudioGroups>> GroupsAsync(string packId, string lang, CancellationToken cancel) =>
                Task.FromResult(Answer(new MakerStudioGroups
                {
                    CatalogRevision = "cat",
                    Groups = new[] { new MakerStudioGroup { Id = "focus", Title = "Focus", Order = 0 } },
                }));
        }
    }
}
