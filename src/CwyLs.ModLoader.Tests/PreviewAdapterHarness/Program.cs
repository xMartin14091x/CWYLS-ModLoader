using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Bulbul.Maker;
using CwyLs.ModLoader.Core.Api.Maker;
internal static class Program
{
    internal static string PackRoot = Path.Combine(Path.GetTempPath(), "cwy-preview-adapter-" + Guid.NewGuid().ToString("N"));
    static int SubtitleScenarios()
    {
        int failures = 0;
        Directory.CreateDirectory(Path.Combine(PackRoot, "data/subtitles"));
        foreach (var scenario in new[] { "selected", "fallback", "game-text", "busy", "unknown" })
        {
            File.WriteAllText(Path.Combine(PackRoot, "data/subtitles/vi.json"), scenario == "selected" ? "{\"line\":\"Selected subtitle\"}" : "{}");
            Rig.Scenario = "sub"; Rig.Busy = scenario == "busy"; Rig.Cancel = new CancellationTokenSource();
            Rig.Queue = new CwyLs.ModLoader.Core.Threading.MainThreadQueue(() => 0); Rig.Queue.CaptureMainThread();
            Rig.Playback = new TaskCompletionSource<bool>(); Rig.Starts = Rig.Decodes = Rig.Focuses = 0;
            Rig.Said = null; Rig.PinnedClip = new UnityEngine.AudioClip();
            Rig.FallbackSubtitle = scenario == "selected" || scenario == "fallback" ? "Effective subtitle" : null;
            Rig.OriginalClip = new UnityEngine.AudioClip();
            var task = Task.Run(() => new MakerEditor().PlayStudioAsync(new MakerStudioPlayRequest
                { Id = scenario == "unknown" ? "missing" : "line", Variant = "sub", Lang = "vi", PackId = "lab.selected" }, Rig.Cancel.Token));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Rig.Queue.Pump(1); Thread.Sleep(1); }
            var result = task.IsCompleted ? task.GetAwaiter().GetResult() : null;
            var expected = scenario == "selected" ? "Selected subtitle" : scenario == "fallback" ? "Effective subtitle" : "line";
            var pass = scenario == "unknown"
                ? result?.Error == MakerErrorCodes.NotFound && Rig.Starts == 0 && Rig.Focuses == 0
                : scenario == "busy"
                ? result?.Error == MakerErrorCodes.NotReady && Rig.Starts == 0 && Rig.Focuses == 0
                : result?.Ok == true && Rig.Said?.Text == expected && Rig.Said?.VoiceName == "voice"
                    && Rig.PinnedClip == null && Rig.Decodes == 0 && Rig.Focuses == 1 && result.Value!.Saved.DurationSec == 1d
                    && (scenario == "game-text" || (Rig.Said.TextJa == expected && Rig.Said.Texts!["vi"] == expected));
            Console.WriteLine((pass ? "PASS " : "FAIL ") + "subtitle-" + scenario + " text=" + Rig.Said?.Text + " decodes=" + Rig.Decodes);
            if (!pass) failures++;
            Rig.Playback.TrySetResult(true);
        }
        Rig.FallbackSubtitle = null; Rig.OriginalClip = null; Rig.Busy = false;
        return failures;
    }

    static int Main()
    {
        Directory.CreateDirectory(Path.Combine(PackRoot,"audio/voice/vi"));
        File.WriteAllText(Path.Combine(PackRoot,"audio/voice/vi/voice.wav"), "synthetic placeholder, never decoded by Unity");
        int failures=0;
        foreach(var scenario in new[]{"cancel","validation","rejected","success","late","borrowed","fallback","busy","timeout","queuefull"})
        foreach(var winning in new[]{false,true})
        {
            Rig.Scenario=scenario; Rig.Winning=winning; Rig.Busy=false; Rig.Cancel=new CancellationTokenSource();
            Rig.Queue=new CwyLs.ModLoader.Core.Threading.MainThreadQueue(()=>0,capacity: scenario=="queuefull" ? 1 : 2048); Rig.Queue.CaptureMainThread();
            Rig.DestroyThreads.Clear(); Rig.Playback=new TaskCompletionSource<bool>(); Rig.Starts=0; Rig.LateDecode=null;
            Rig.Borrowed=scenario=="borrowed" ? new UnityEngine.AudioClip() : null;
            var task=Task.Run(async () =>
            {
                if(scenario=="borrowed" || scenario=="fallback") return (await new MakerEditor().PlayAsync("line","dub","vi",Rig.Cancel.Token)).Ok;
                return (await new MakerEditor().PlayStudioAsync(new MakerStudioPlayRequest { Id="line", Variant="dub", Lang="vi", PackId="lab.selected" },Rig.Cancel.Token)).Ok;
            });
            var until=DateTime.UtcNow.AddSeconds(3);
            while(!task.IsCompleted && DateTime.UtcNow<until) { Rig.Queue.Pump(1); Thread.Sleep(1); }
            if(!task.IsCompleted) { Console.WriteLine("FAIL hung "+scenario); failures++; continue; }
            var result=task.GetAwaiter().GetResult();
            if(scenario=="late") Rig.LateDecode?.Invoke(Rig.Decode());
            var beforeCompletion=Rig.DestroyThreads.Count;
            Rig.Playback.TrySetResult(true);
            until=DateTime.UtcNow.AddMilliseconds(80);
            while(DateTime.UtcNow<until) { Rig.Queue.Pump(1); Thread.Sleep(1); }
            var expected=scenario=="borrowed" ? 0 : 1;
            var pass=Rig.DestroyThreads.Count==expected && Rig.DestroyThreads.TrueForAll(id=>id==Thread.CurrentThread.ManagedThreadId)
                && ((scenario!="success" && scenario!="fallback") || beforeCompletion==0)
                && (scenario!="cancel" || Rig.Starts==0);
            Console.WriteLine((pass?"PASS ":"FAIL ")+scenario+" winning="+winning+" destroyed="+Rig.DestroyThreads.Count+" expected="+expected+" ok="+result+" starts="+Rig.Starts);
            if(!pass) failures++;
        }
        failures += SubtitleScenarios();
        Console.WriteLine("Adapter scenarios: 25; failures: "+failures);
        Directory.Delete(PackRoot, recursive: true);
        return failures==0?0:1;
    }
}
