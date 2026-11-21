using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Core.Threading;
using UnityEngine;

internal static class Rig
{
    internal static MainThreadQueue Queue = new MainThreadQueue(() => 0);
    internal static string Scenario = "";
    internal static CancellationTokenSource Cancel = new CancellationTokenSource();
    internal static List<int> DestroyThreads = new List<int>();
    internal static TaskCompletionSource<bool> Playback = new TaskCompletionSource<bool>();
    internal static int Starts;
    internal static int Decodes;
    internal static int Focuses;
    internal static AudioClip? PinnedClip;
    internal static AudioClip? OriginalClip;
    internal static SayRequest? Said;
    internal static string? FallbackSubtitle;
    internal static bool Winning;
    internal static bool Busy;
    internal static AudioClip? Borrowed;
    internal static Action<AudioClip?>? LateDecode;
    internal static AudioClip Decode() { Decodes++; return new AudioClip(); }
}
namespace UnityEngine
{
    public class Object { public static void Destroy(object value) { Rig.DestroyThreads.Add(Thread.CurrentThread.ManagedThreadId); } }
    public class AudioClip : Object { public float length => 1; }
}
namespace Bulbul
{
    public class ScenarioReader { public bool IsPlayingScenario() => false; }
}
namespace CwyLs.ModLoader.Packs
{
    public class LoadedPack
    {
        public string Id => "lab.selected";
        public string RootPath => Program.PackRoot;
        public string FullPath(string value) => System.IO.Path.Combine(RootPath, value);
    }
    public static class PackManager { public static LoadedPack? Find(string id) => new LoadedPack(); }
}
namespace CwyLs.ModLoader.Loaders
{
    public static class AudioLoader
    {
        public static void LoadAsync(string path, Action<AudioClip?> callback)
        {
            if (Rig.Scenario == "late") Rig.LateDecode = callback;
            else callback(Rig.Decode());
        }
    }
}
namespace CwyLs.ModLoader.Threading
{
    public static class MainThread
    {
        public static bool Post(string label, Action action, string owner) => Rig.Queue.Post(owner, label, action);
        public static bool RunOrPost(string label, Action action, string owner) => Rig.Queue.RunOrPost(owner, label, action);
        public static IDisposable AddTicker(string label, Action action, string owner) => Rig.Queue.AddTicker(owner, label, action);
    }
}
namespace CwyLs.ModLoader.Bulbul.Events { public static class EventHost { public static bool RoomReadyRaised => true; } }
namespace CwyLs.ModLoader.Bulbul.MasterData
{
    public sealed class Row { public string Voice => "voice"; public string Ja => Rig.Scenario == "validation" ? "" : "line"; public string En => Ja; }
    public sealed class Loader { public Dictionary<string,Row> LocalizationList => new Dictionary<string,Row> { ["line"] = new Row() }; }
    public static class MasterDataFeature { public static Loader ResolveLoader() => new Loader(); }
    public static class MasterDataSubtitleSource { public static bool TryGetCurrent(string id,string code,out string? subtitle) { subtitle=Rig.FallbackSubtitle; return subtitle != null; } }
}
namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    public class Heroine { public bool IsFinishedVoice => true; public bool IsPossibleTalk() => true; }
    public static class SatoneSeam { public static bool TryHeroine(out Heroine h) { h=new Heroine(); return true; } }
}
namespace CwyLs.ModLoader.Bulbul.Subtitles { public class Placeholder {} }
namespace CwyLs.ModLoader.Bulbul
{
    public static class GameScopes { public static bool IsRoomLoaded => true; public static bool TryRoom<T>(out T? value) where T:new() { value=new T(); return true; } }
    public static class VoiceFeature { public static bool Enabled => true; }
    public static class VoiceSeam { public static object? TryGetInstance(out string? error) { error=null; return null; } public static object? TryGetGameClip(object? value,string voice) => Rig.OriginalClip; }
    public sealed class Table { public bool HasClip => Rig.Borrowed != null; public object? Clip => Rig.Borrowed; public Step? Step => null; }
    public sealed class Step { public string PackId => "lab.selected"; }
    public static class LanguageVoices
    {
        public static bool Enabled => true; public static string DisabledReason => "";
        public static bool TryLive(string code,string voice,out AudioClip? clip) { clip=null; return false; }
        public static Table PeekTable(string code,string voice) => new Table();
        public static int Pin(string voice,AudioClip? clip,string pack,string code,double duration) { Rig.PinnedClip=clip; return 1; }
        public static void Unpin(int token) {}
    }
    public sealed class FakeHandle { public bool IsCompleted => Rig.Scenario == "rejected"; public SayOutcome Outcome => SayOutcome.Rejected; public string Detail => "rejected"; public Task Completion => Rig.Playback.Task; public FakeLine Line { get; } = new FakeLine(); }
    public sealed class FakeLine { public string LocalizationId => "cwy_say_1_001"; }
    public static class SatoneSayFeature
    {
        public static bool Initialized => true; public static bool Enabled => true; public static bool IsSpeaking => Rig.Busy; public static int WaitingCount => 0;
        public static FakeHandle Say(SayRequest request) { Rig.Said=request; Rig.Starts++; return new FakeHandle(); }
    }
}
namespace CwyLs.ModLoader.Bulbul.Maker
{
    public static class MakerGameFocus { public static void Bring() { Rig.Focuses++; } }
    // The DLC gate (v1 Play): the harness line is never paid DLC content.
    public sealed class MakerCatalog
    {
        public static MakerCatalog Instance { get; } = new MakerCatalog();
        internal MakerPlayedLines Played { get; } = new MakerPlayedLines();
        internal Task<MakerResult<bool>?> DlcRefusalAsync(string? lineId, string? clip, CancellationToken cancel) => Task.FromResult<MakerResult<bool>?>(null);
    }
    public sealed partial class MakerEditor
    {
        private const string Owner="maker"; private const string TraceSys="maker"; private const string LogPrefix="maker";
        private static readonly TimeSpan LoadTimeout=TimeSpan.FromMilliseconds(10);
        private static MakerLayer<string> EffectiveVoice(string code,string voice) => new MakerLayer<string>(Rig.Winning ? "lab.selected" : "lab.winner", "audio/voice/vi/voice.wav");
        private static MakerResult<MakerSaved> Failed(string what, Exception ex) => MakerResult<MakerSaved>.Fail(MakerErrorCodes.Unavailable,ex.Message);
        private static Task<T> OnMain<T>(string label,Func<T> work,CancellationToken cancel)
        {
            if (label == "maker.play.start")
            {
                if (Rig.Scenario == "cancel") Rig.Cancel.Cancel();
                if (Rig.Scenario == "busy") Rig.Busy=true;
                if (Rig.Scenario == "timeout") return Task.FromException<T>(new TimeoutException("synthetic start timeout"));
                if (Rig.Scenario == "queuefull")
                {
                    Rig.Queue.Post("maker", "fill", () => {});
                    return Task.FromException<T>(new TimeoutException("synthetic start timeout with full cleanup queue"));
                }
            }
            return Rig.Queue.InvokeAsync(Owner,label,work,TimeSpan.FromSeconds(1),cancel);
        }
        private static async Task<AudioClip?> LoadFile(string path,CancellationToken cancel)
        {
            var done = new TaskCompletionSource<AudioClip?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await OnMain("maker.voice.load", () => { CwyLs.ModLoader.Loaders.AudioLoader.LoadAsync(path, loaded => done.TrySetResult(loaded)); return true; }, cancel).ConfigureAwait(false);
            var finished = await Task.WhenAny(done.Task, Task.Delay(LoadTimeout, cancel)).ConfigureAwait(false);
            return finished == done.Task ? done.Task.Result : null;
        }
    }
}
