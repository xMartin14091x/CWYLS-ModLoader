using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Json;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    public sealed class MakerM5bTests
    {
        private const string Line = "/v1/maker/studio/packs/sample.pack/languages/th/lines/one";
        private static MakerStudioEditor Editor(MakerProjectStore store) => new MakerStudioEditor(
            (_, _) => MakerResult<MakerProjectStore>.Success(store), (_, _, _, _, w) => new MakerSubtitleLiveResult { Saved = new MakerSaved { Path = w.Path } });
        private sealed class V1 : IMakerEditor
        {
            private readonly MakerStudioEditor _editor;
            public V1(MakerStudioEditor editor) => _editor = editor;
            public Task<MakerResult<MakerSaved>> SaveSubtitleAsync(string p, string l, string id, string? t, CancellationToken c) => _editor.SaveV1SubtitleAsync(p,l,id,t,c);
            public Task<MakerResult<MakerSaved>> SaveVoiceAsync(string p, string l, string id, byte[]? t, CancellationToken c) => throw new NotSupportedException();
            public Task<MakerResult<MakerSaved>> PlayAsync(string id, string v, string l, CancellationToken c) => throw new NotSupportedException();
        }

        [Theory]
        [InlineData("corrupt")]
        [InlineData("case")]
        [InlineData("unrecoverable")]
        public void V1_routes_ignore_broken_studio_transactions_and_do_not_create_capture_intents(string damage)
        {
            using var fx = MakerRecoveryTests.Blocked();
            if (damage == "corrupt") fx.Write(MakerStudioPaths.Pending(fx.Operation(10)), "broken");
            if (damage == "case") Directory.Move(Path.Combine(fx.Root,".maker/transactions"), Path.Combine(fx.Root,".maker/Transactions"));
            var rig = new MakerRig(editor: new V1(Editor(fx.Store)));
            var before = Directory.GetFiles(fx.Root,"*.pending.json",SearchOption.AllDirectories).Length;
            for (var i=0;i<2;i++)
            {
                var reply = rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/sample.pack/subtitles/th/one",rig.Token,"{\"text\":\"v1 " + i + "\"}"));
                Assert.Equal(200,reply.Status);
                Assert.Equal(new[] {"live","path","durationSec","note"}, ApiTestKit.Json(reply).Keys);
                Assert.Equal("v1 " + i,fx.Store.CurrentSubtitle("th","one"));
            }
            Assert.Equal(before,Directory.GetFiles(fx.Root,"*.pending.json",SearchOption.AllDirectories).Length);
        }

        [Fact]
        public async Task Invalid_editor_inputs_fail_before_opening_missing_or_readonly_packs()
        {
            var opened=0;
            var editor=new MakerStudioEditor((_,_)=> { opened++; return MakerResult<MakerProjectStore>.Fail("not_found","missing"); },(_,_,_,_,_)=>throw new Exception());
            Assert.Equal("bad_request",(await editor.SaveV1SubtitleAsync("missing","en","one","x",default)).Error);
            Assert.Equal("bad_request",(await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest { PackId="missing",Lang="en",Id="one",Text="x" },default)).Error);
            Assert.Equal("bad_request",(await editor.HistoryAsync("missing","th","nul",0,20,default)).Error);
            Assert.Equal(0,opened);
        }

        [Theory]
        [InlineData(false,"manifest.json","subtitle")]
        [InlineData(true,"manifest.json","subtitle")]
        [InlineData(false,"data/subtitles/th.json","workflow")]
        [InlineData(false,".maker/glossary/th.json","subtitle")]
        [InlineData(false,".maker/lines/th/one.json","unknown")]
        public void Disk_authored_unconfined_kind_target_is_refused_through_read_route(bool readOnly,string target,string kind)
        {
            using var fx = new MakerProjectFixture();
            fx.Write(target,"original");
            var op=fx.Operation(5); var stage=MakerStudioPaths.Stage(op,0);
            var bytes=Encoding.UTF8.GetBytes("replaced"); fx.Store.Files.WriteAtomic(stage,bytes);
            fx.Write(MakerStudioPaths.Pending(op),ApiJson.Write(new JsonObject {
                ["schema"]=1,["operationId"]=op,["kind"]=kind,
                ["files"]=new object?[] {new JsonObject {["path"]=target,["before"]=fx.Store.FingerprintOf(target),["after"]=MakerPackFiles.Fingerprint(bytes),["stage"]=stage}}
            }));
            var store = new MakerProjectStore(fx.Root, recoveryWritable:!readOnly);
            var editor=Editor(store); var rig=new MakerRig(studio:()=>new MakerStudioBackends(subtitles:editor));
            var response=rig.Send(MakerHttp.Get(Line+"/history",rig.Token));
            Assert.Equal(503,response.Status); Assert.Contains("recovery_required",response.Body);
            Assert.Contains(op,response.Body); Assert.Equal("original",fx.Read(target));
            Assert.True(fx.Store.Files.Exists(stage));
        }

        [Fact]
        public void Readonly_route_preserves_valid_pending_targets_and_orphan_swaps()
        {
            using var fx = MakerRecoveryTests.Blocked();
            fx.Write("data/subtitles/th.json",MakerSubtitleFile.Apply(null,"one","before").Text!);
            var journal=JsonReader.ParseObject(fx.Read(MakerStudioPaths.Pending(fx.Operation(10))));
            var first=Assert.IsType<JsonObject>(Assert.IsAssignableFrom<System.Collections.Generic.IList<object?>>(journal["files"])[0]);
            Assert.Equal(first["before"],fx.Store.FingerprintOf("data/subtitles/th.json")); // A writable recovery could roll forward.
            var orphan=MakerStudioPaths.Transactions+"/"+fx.Operation(99)+".0.swap"; fx.Write(orphan,"preserve");
            var before=Directory.GetFiles(fx.Root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllText);
            var editor=Editor(new MakerProjectStore(fx.Root,recoveryWritable:false));
            var rig=new MakerRig(studio:()=>new MakerStudioBackends(subtitles:editor));
            Assert.Equal(503,rig.Send(MakerHttp.Get(Line+"/history",rig.Token)).Status);
            Assert.All(before,p=>Assert.Equal(p.Value,File.ReadAllText(p.Key)));
            Assert.Equal(before.Count,Directory.GetFiles(fx.Root,"*",SearchOption.AllDirectories).Length);
        }

        [Fact]
        public async Task Two_packs_cannot_apply_a_stale_effective_layer_last()
        {
            using var low=new MakerProjectFixture();using var high=new MakerProjectFixture();
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var opened=new ManualResetEventSlim();
            string? visible=null;
            var editor=new MakerStudioEditor((pack,_)=> {
                if(pack=="high")opened.Set();return MakerResult<MakerProjectStore>.Success(pack=="low"?low.Store:high.Store);
            },(pack,_,_,_,_)=> {
                var effective=high.Store.CurrentSubtitle("th","one")??low.Store.CurrentSubtitle("th","one");
                var winner=high.Store.CurrentSubtitle("th","one")!=null?"high":"low";
                if(pack=="low") { entered.Set();Assert.True(release.Wait(TimeSpan.FromSeconds(15))); }
                visible=effective;return new MakerSubtitleLiveResult {EffectivePackId=winner,Saved=new MakerSaved {Live=true}};
            });
            MakerStudioSubtitleRequest Request(string pack,MakerProjectFixture fx)=>new MakerStudioSubtitleRequest {
                PackId=pack,Lang="th",Id="one",Text=pack,OperationId=fx.Operation(1),ExpectedRevision=fx.Store.ReadLine("th","one").Value!.Revision};
            var firstRequest=Request("low",low);var secondRequest=Request("high",high);
            var first=Task.Factory.StartNew(()=>editor.SaveSubtitleAsync(firstRequest,default),default,TaskCreationOptions.LongRunning,TaskScheduler.Default).Unwrap();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
            var second=Task.Factory.StartNew(()=>editor.SaveSubtitleAsync(secondRequest,default),default,TaskCreationOptions.LongRunning,TaskScheduler.Default).Unwrap();
            try { Assert.True(opened.Wait(TimeSpan.FromSeconds(15)));Assert.NotSame(second,await Task.WhenAny(second,Task.Delay(150))); }
            finally {release.Set();}
            Assert.True((await first).Ok);Assert.True((await second).Ok);Assert.Equal("high",visible);
            Assert.Equal("high",(await editor.SaveSubtitleAsync(secondRequest,default)).Value!.EffectivePackId);
        }

        [Fact]
        public async Task A_studio_request_recovers_once_even_when_stores_and_ledger_read_again()
        {
            using var fx=new MakerProjectFixture(); var editor=Editor(fx.Store);
            var revision=fx.Store.ReadLine("th","one").Value!.Revision; var count=fx.Store.RecoveryPasses;
            var result=await editor.SaveSubtitleAsync(new MakerStudioSubtitleRequest { PackId="sample.pack",Lang="th",Id="one",Text="saved",OperationId=fx.Operation(1),ExpectedRevision=revision },default);
            Assert.True(result.Ok,result.Message); Assert.Equal(count+1,fx.Store.RecoveryPasses);
        }

        [Fact]
        public void Verified_own_swap_can_resume_a_replacement()
        {
            using var fx=new MakerProjectFixture(); const string path=".maker/lines/th/one.json";
            fx.Write(path,"old"); var swap=MakerStudioPaths.Transactions+"/"+fx.Operation(1)+".0.swap"; fx.Write(swap,"old");
            Assert.True(fx.Store.Files.WriteChecked(path,Encoding.UTF8.GetBytes("new"),fx.Store.FingerprintOf(path),swap));
            Assert.Equal("new",fx.Read(path)); Assert.Equal("old",fx.Read(swap));
        }

        [Theory]
        [InlineData(4096,4096,true)]
        [InlineData(4096,1024,true)]
        [InlineData(4096,4096,false)]
        [InlineData(4096,1024,false)]
        public void Quota_matches_real_peak_for_equal_and_shrinking_targets(int oldSize,int newSize,bool inside)
        {
            var path=inside?".maker/lines/th/one.json":"data/subtitles/th.json";
            long journal;
            using(var probe=new MakerProjectFixture()) {
                probe.Write(path,new string('a',oldSize));
                var commit=new MakerCommit(probe.Store.Files,"subtitle",boundary:p=>{if(p=="after-intent")throw new InvalidOperationException();});
                Assert.Throws<InvalidOperationException>(()=>commit.Execute(probe.Operation(1),new[]{probe.Store.Target(path,new byte[newSize])}));
                journal=new FileInfo(probe.Store.Files.Resolve(MakerStudioPaths.Pending(probe.Operation(1)))).Length;
            }
            var peak=journal+(inside?oldSize+2L*newSize:oldSize+newSize);
            // A commit that shrinks .maker is never refused (REVIEW-FIXES-M5c item 3); every other one needs its peak.
            var shrinks=inside&&newSize<oldSize;
            foreach(var delta in new[]{-1,1}) {
                using var fx=new MakerProjectFixture(quota:peak+delta); fx.Write(path,new string('a',oldSize));
                var result=fx.Store.Commit(fx.Operation(1),new[]{fx.Store.Target(path,new byte[newSize])},"subtitle");
                Assert.Equal(delta>0||shrinks,result.Ok); if(delta<0&&!shrinks)Assert.Equal("storage_limit",result.Error);
            }
        }

        [Fact]
        public void Parenthesized_ids_save_through_v1_and_v2_routes()
        {
            using var fx=new MakerProjectFixture(); var editor=Editor(fx.Store);
            var rig=new MakerRig(editor:new V1(editor),studio:()=>new MakerStudioBackends(subtitles:editor));
            Assert.Equal(200,rig.Send(MakerHttp.Json("PUT","/v1/maker/packs/sample.pack/subtitles/th/line(5)",rig.Token,"{\"text\":\"first\"}")).Status);
            var revision=fx.Store.ReadLine("th","line(5)").Value!.Revision;
            var body=ApiJson.Write(new JsonObject {["text"]="second",["operationId"]=fx.Operation(1),["expectedRevision"]=revision});
            Assert.Equal(200,rig.Send(MakerHttp.Json("PUT",Line.Replace("/one","/line(5)")+"/subtitle",rig.Token,body)).Status);
            Assert.Equal("second",fx.Store.CurrentSubtitle("th","line(5)"));
        }

        [Theory]
        [InlineData("\\uD800")]
        [InlineData("\\uDC00")]
        public void Unpaired_surrogate_json_is_refused_before_persistence(string escape)
        {
            using var fx=new MakerProjectFixture(); var editor=Editor(fx.Store);
            var rig=new MakerRig(editor:new V1(editor),studio:()=>new MakerStudioBackends(subtitles:editor));
            Assert.Equal(400,rig.Send(MakerHttp.Json("PUT","/v1/maker/packs/sample.pack/subtitles/th/one",rig.Token,"{\"text\":\""+escape+"\"}")).Status);
            var body="{\"text\":\""+escape+"\",\"operationId\":\""+fx.Operation(1)+"\",\"expectedRevision\":\"initial\"}";
            Assert.Equal(400,rig.Send(MakerHttp.Json("PUT",Line+"/subtitle",rig.Token,body)).Status);
            Assert.Empty(Directory.GetFiles(fx.Root,"*",SearchOption.AllDirectories));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" \r\n\t")]
        public async Task Blank_subtitle_table_is_empty_for_studio_reads_and_writes(string blank)
        {
            using var fx=new MakerProjectFixture();fx.Write("data/subtitles/th.json",blank);
            var read=fx.Store.ReadLine("th","one");Assert.True(read.Ok,read.Message);
            Assert.True((await Editor(fx.Store).SaveSubtitleAsync(new MakerStudioSubtitleRequest {PackId="sample.pack",Lang="th",Id="one",Text="text",ExpectedRevision=read.Value!.Revision,OperationId=fx.Operation(1)},default)).Ok);
        }

        [Fact]
        public void Workflow_creation_records_baseline_before_a_later_external_edit()
        {
            using var fx=new MakerProjectFixture();fx.Write("data/subtitles/th.json","{\"one\":\"original\"}");
            var result=new MakerWorkflowStore(fx.Store).Update(new MakerStudioWorkflowRequest {PackId="sample.pack",Lang="th",Id="one",Status="draft",ExpectedRevision=fx.Store.ReadLine("th","one").Value!.Revision,OperationId=fx.Operation(1)});
            Assert.True(result.Ok,result.Message);fx.Write("data/subtitles/th.json","{\"one\":\"external\"}");
            var history=new MakerHistoryStore(fx.Store).Read("th","one",0,20).Value!;
            Assert.Contains(history.Items,h=>h.Source=="baseline"&&h.Text=="original");
            Assert.Contains(history.Items,h=>h.Source=="external"&&h.Text=="external");
        }

        [Fact]
        public void Commit_requires_explicit_kind_with_no_default()
        {
            using var fx = new MakerProjectFixture();
            var target = fx.Store.Target(".maker/lines/th/one.json", Encoding.UTF8.GetBytes("{}"));
            Assert.Throws<InvalidOperationException>(() => new MakerCommit(fx.Store.Files).Execute(fx.Operation(1), new[] { target }));
            Assert.Throws<ArgumentException>(() => new MakerCommit(fx.Store.Files, ""));
            foreach (var p in typeof(MakerProjectStore).GetMethod(nameof(MakerProjectStore.Commit))!.GetParameters())
            {
                if (p.Name == "kind") Assert.False(p.HasDefaultValue);
            }
        }

        [Fact]
        public void Legacy_journal_without_kind_derives_kind_from_staged_receipt_installed_receipt_or_metadata()
        {
            // 1. Staged receipt derives kind="glossary"
            using (var fx = new MakerProjectFixture())
            {
                var op = fx.Operation(1);
                var ledger = new MakerOperationLedger(fx.Store);
                var receiptTarget = ledger.ReceiptTarget(MakerOperationLedger.Receipt(op, "fp", "glossary", "rev", true, fx.Clock.Now, new JsonObject()));
                var glossaryBytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"revision\":\"rev\",\"entries\":[]}");
                fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(op, 0), glossaryBytes);
                fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(op, 1), receiptTarget.Bytes);
                fx.Write(MakerStudioPaths.Pending(op), ApiJson.Write(new JsonObject
                {
                    ["schema"] = 1,
                    ["operationId"] = op,
                    ["files"] = new object?[]
                    {
                        new JsonObject { ["path"] = ".maker/glossary/th.json", ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(glossaryBytes), ["stage"] = MakerStudioPaths.Stage(op, 0) },
                        new JsonObject { ["path"] = receiptTarget.Path, ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(receiptTarget.Bytes), ["stage"] = MakerStudioPaths.Stage(op, 1) },
                    },
                }));
                Assert.True(fx.Store.RecoverPending().Ok);
                Assert.True(fx.Store.Files.Exists(".maker/glossary/th.json"));
            }

            // 2. Installed receipt (after stage 1 was already moved/cleaned) derives kind="subtitle"
            using (var fx = new MakerProjectFixture())
            {
                var op = fx.Operation(2);
                var ledger = new MakerOperationLedger(fx.Store);
                var receiptTarget = ledger.ReceiptTarget(MakerOperationLedger.Receipt(op, "fp", "subtitle", "rev", true, fx.Clock.Now, new JsonObject()));
                var subBytes = Encoding.UTF8.GetBytes("{\"one\":\"hello\"}");
                fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(op, 0), subBytes);
                fx.Store.Files.WriteAtomic(receiptTarget.Path, receiptTarget.Bytes);
                fx.Write(MakerStudioPaths.Pending(op), ApiJson.Write(new JsonObject
                {
                    ["schema"] = 1,
                    ["operationId"] = op,
                    ["files"] = new object?[]
                    {
                        new JsonObject { ["path"] = "data/subtitles/th.json", ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(subBytes), ["stage"] = MakerStudioPaths.Stage(op, 0) },
                        new JsonObject { ["path"] = receiptTarget.Path, ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(receiptTarget.Bytes), ["stage"] = MakerStudioPaths.Stage(op, 1) },
                    },
                }));
                Assert.True(fx.Store.RecoverPending().Ok);
                Assert.Equal("hello", fx.Store.CurrentSubtitle("th", "one"));
            }

            // 3. Receiptless legacy journal derives kind="metadata" (allows .maker/lines, refuses data/subtitles)
            using (var fx = new MakerProjectFixture())
            {
                var op = fx.Operation(3);
                var lineBytes = Encoding.UTF8.GetBytes("{\"schema\":1,\"revision\":\"rev\",\"subtitleFingerprint\":\"x\",\"workflow\":{\"status\":\"draft\",\"starred\":false},\"history\":[],\"historyGap\":false}");
                fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(op, 0), lineBytes);
                fx.Write(MakerStudioPaths.Pending(op), ApiJson.Write(new JsonObject
                {
                    ["schema"] = 1,
                    ["operationId"] = op,
                    ["files"] = new object?[]
                    {
                        new JsonObject { ["path"] = ".maker/lines/th/one.json", ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(lineBytes), ["stage"] = MakerStudioPaths.Stage(op, 0) },
                    },
                }));
                Assert.True(fx.Store.RecoverPending().Ok);
                Assert.True(fx.Store.Files.Exists(".maker/lines/th/one.json"));

                var badOp = fx.Operation(4);
                var subBytes = Encoding.UTF8.GetBytes("{\"one\":\"bad\"}");
                fx.Store.Files.WriteAtomic(MakerStudioPaths.Stage(badOp, 0), subBytes);
                fx.Write(MakerStudioPaths.Pending(badOp), ApiJson.Write(new JsonObject
                {
                    ["schema"] = 1,
                    ["operationId"] = badOp,
                    ["files"] = new object?[]
                    {
                        new JsonObject { ["path"] = "data/subtitles/th.json", ["before"] = null, ["after"] = MakerPackFiles.Fingerprint(subBytes), ["stage"] = MakerStudioPaths.Stage(badOp, 0) },
                    },
                }));
                var refused = fx.Store.RecoverPending();
                Assert.False(refused.Ok);
                Assert.Equal("recovery_required", refused.Error);
            }
        }

        [Theory]
        [InlineData("con")]
        [InlineData("aux")]
        [InlineData("line.")]
        public void V1_subtitle_save_accepts_contract_valid_ids_that_cannot_name_sidecars_and_skips_optional_capture(string id)
        {
            using var fx = new MakerProjectFixture();
            var editor = Editor(fx.Store);
            var rig = new MakerRig(editor: new V1(editor), studio: () => new MakerStudioBackends(subtitles: editor));
            var v1Reply = rig.Send(MakerHttp.Json("PUT", "/v1/maker/packs/sample.pack/subtitles/th/" + id, rig.Token, "{\"text\":\"v1 allowed\"}"));
            Assert.Equal(200, v1Reply.Status);
            Assert.Equal("v1 allowed", fx.Store.CurrentSubtitle("th", id));
            Assert.False(Directory.Exists(Path.Combine(fx.Root, ".maker", "lines")));

            var v2Body = "{\"text\":\"v2 refused\",\"operationId\":\"" + fx.Operation(1) + "\",\"expectedRevision\":\"initial\"}";
            var v2Reply = rig.Send(MakerHttp.Json("PUT", "/v1/maker/studio/packs/sample.pack/languages/th/lines/" + id + "/subtitle", rig.Token, v2Body));
            Assert.Equal(400, v2Reply.Status);
        }

        [Fact]
        public async Task Glossary_validation_counts_unicode_code_points_refuses_unpaired_surrogates_and_runs_before_opening_or_recovering()
        {
            var emoji2000 = string.Concat(Enumerable.Repeat("\U0001F600", 2000));
            var emoji2001 = emoji2000 + "a";
            Assert.Equal(4000, emoji2000.Length);
            using var fx = new MakerProjectFixture();
            var glossary = new MakerGlossaryStore(fx.Store);
            var initialRev = glossary.Read("th").Value!.Revision;
            var saved1 = glossary.Save(new MakerStudioGlossaryRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                ExpectedRevision = initialRev,
                OperationId = fx.Operation(10),
                Entries = new[] { new MakerStudioGlossaryEntry { Id = "ok", Target = emoji2000, SourceJa = emoji2000 } },
            });
            Assert.True(saved1.Ok, saved1.Message);
            var saved2 = glossary.Save(new MakerStudioGlossaryRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                ExpectedRevision = saved1.Value!.Revision,
                OperationId = fx.Operation(11),
                Entries = new[] { new MakerStudioGlossaryEntry { Id = "ok", Target = "ok", SourceEn = emoji2000, Note = emoji2000 } },
            });
            Assert.True(saved2.Ok, saved2.Message);
            foreach (var bad in new[]
            {
                new MakerStudioGlossaryEntry { Id = "long_target", Target = emoji2001 },
                new MakerStudioGlossaryEntry { Id = "long_ja", Target = "ok", SourceJa = emoji2001 },
                new MakerStudioGlossaryEntry { Id = "surrogate_target", Target = "a\uD800b" },
                new MakerStudioGlossaryEntry { Id = "surrogate_note", Target = "ok", Note = "a\uDC00b" },
            })
            {
                var rejected = glossary.Save(new MakerStudioGlossaryRequest
                {
                    PackId = "sample.pack",
                    Lang = "th",
                    ExpectedRevision = saved2.Value!.Revision,
                    OperationId = fx.Operation(12),
                    Entries = new[] { bad },
                });
                Assert.False(rejected.Ok);
                Assert.Equal("bad_request", rejected.Error);
            }

            var opened = 0;
            var editor = new MakerStudioEditor((_, _) => { opened++; return MakerResult<MakerProjectStore>.Fail("not_found", "missing"); },
                (_, _, _, _, _) => throw new Exception());
            var badEditorResult = await editor.SaveGlossaryAsync(new MakerStudioGlossaryRequest
            {
                PackId = "missing",
                Lang = "th",
                ExpectedRevision = "initial",
                OperationId = new string('a', 32),
                Entries = new[] { new MakerStudioGlossaryEntry { Id = "bad", Target = emoji2001 } },
            }, default);
            Assert.Equal("bad_request", badEditorResult.Error);
            Assert.Equal(0, opened);

            using var blockedFx = MakerRecoveryTests.Blocked();
            var passesBefore = blockedFx.Store.RecoveryPasses;
            var badStoreResult = new MakerGlossaryStore(blockedFx.Store).Save(new MakerStudioGlossaryRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                ExpectedRevision = "initial",
                OperationId = blockedFx.Operation(99),
                Entries = new[] { new MakerStudioGlossaryEntry { Id = "bad", Target = emoji2001 } },
            });
            Assert.Equal("bad_request", badStoreResult.Error);
            Assert.Equal(passesBefore, blockedFx.Store.RecoveryPasses);
        }

        [Fact]
        public async Task Glossary_save_with_null_entries_is_an_empty_list_not_a_null_dereference()
        {
            using var fx = new MakerProjectFixture();
            var editor = Editor(fx.Store);
            var saved = await editor.SaveGlossaryAsync(new MakerStudioGlossaryRequest
            {
                PackId = "sample.pack",
                Lang = "th",
                ExpectedRevision = new MakerGlossaryStore(fx.Store).Read("th").Value!.Revision,
                OperationId = fx.Operation(1),
                Entries = null!, // a direct backend caller; the router always builds a list
            }, default);
            Assert.True(saved.Ok, saved.Message);
            Assert.Empty(saved.Value!.Entries);
        }

        [Fact]
        public void Real_loader_validates_studio_bodies_before_refusing_a_read_only_pack()
        {
            using var fx = new MakerProjectFixture();
            var editor = new MakerStudioEditor((pack, editing) => pack == "reference" && editing
                    ? MakerResult<MakerProjectStore>.Fail("read_only_pack", "Reference pack")
                    : MakerResult<MakerProjectStore>.Success(fx.Store),
                (_, _, _, _, _) => new MakerSubtitleLiveResult());
            var rig = new MakerRig(studio: () => new MakerStudioBackends(workflow: editor, subtitles: editor, projects: editor));
            var mutation = "\"expectedRevision\":\"initial\",\"operationId\":\"" + new string('a', 32) + "\",";
            var tooLong = new string('x', 2001);
            const string pack = "/v1/maker/studio/packs/reference";
            const string line = pack + "/languages/th/lines/one";

            // The same parity the loopback fake pins in test_mutation_bodies_are_validated_before_the_read_only_pack_check.
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", pack + "/glossary/th", rig.Token,
                "{" + mutation + "\"entries\":[{\"id\":\"word\",\"target\":\"" + tooLong + "\"}]}")).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Json("PUT", pack + "/glossary/th", rig.Token,
                "{" + mutation + "\"entries\":[{\"id\":\"word\",\"target\":\"ok\"}]}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PUT", line + "/subtitle", rig.Token, "{" + mutation + "\"text\":\"" + tooLong + "\"}")).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Json("PUT", line + "/subtitle", rig.Token, "{" + mutation + "\"text\":\"ok\"}")).Status);
            Assert.Equal(400, rig.Send(MakerHttp.Json("PATCH", line + "/workflow", rig.Token, "{" + mutation + "\"status\":\"retake\"}")).Status);
            Assert.Equal(403, rig.Send(MakerHttp.Json("PATCH", line + "/workflow", rig.Token, "{" + mutation + "\"status\":\"done\"}")).Status);
        }

        [Fact]
        public void Timed_out_request_body_answers_408_and_records_408_request_timeout_in_audit_log()
        {
            using var fx = new MakerProjectFixture();
            var editor = Editor(fx.Store);
            var rig = new MakerRig(editor: new V1(editor));
            var headers = new ApiHeaders();
            headers.Add("Host", "127.0.0.1:7870");
            headers.Add("Authorization", ApiTestKit.AuthValue(rig.Token));
            headers.Add("Content-Type", "application/json; charset=utf-8");
            headers.Add("Content-Length", "100");
            var req = new ApiRequest("http", "PUT", "/v1/maker/packs/sample.pack/subtitles/th/one", headers, 100, false,
                _ => ApiBody.TimedOut("the request body did not arrive within 5 s"));
            var resp = rig.Send(req);
            Assert.Equal(408, resp.Status);
            Assert.Equal("request_timeout", ApiTestKit.ErrorCode(resp));
            lock (rig.Records)
            {
                var record = Assert.Single(rig.Records).Record;
                Assert.Equal(408, record.Status);
                Assert.Equal("request_timeout", record.Error);
            }
        }
    }
}
