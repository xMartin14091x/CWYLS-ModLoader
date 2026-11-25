using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Voice;
using Xunit;

namespace CwyLs.ModLoader.Tests;

public sealed class MakerDlcEntryPointTests
{
    private const string PackId = "sample.pack";
    private const string Line = "locked_line";
    private const string Clip = "locked_clip";
    private const string Group = "locked_group";
    private const string SubtitlePath = "data/subtitles/th.json";
    private static readonly MakerDlcHidden Hidden = new(new[] { Line }, new[] { Group }, new[] { Clip }, 0, "test verdict");

    [Theory]
    [InlineData("data/novel.json", """[{"ID":"new_scene","ScenarioGroupID":"free_group","Command":"Text","LocalizationID":"locked_line"}]""")]
    [InlineData("DATA/Novel.JSON", """[{"ID":"new_scene","ScenarioGroupID":"locked_group","Command":"Text"}]""")]
    [InlineData("data/scenario_groups.json", """[{"ID":"locked_group","Scenario":"ShortConversation"}]""")]
    [InlineData("data/scenario_groups.json", """[{"ID":"new_group","Scenario":"ShortConversation","TitleLocalizationID":"locked_line"}]""")]
    [InlineData("DATA/SCENARIO_GROUPS.JSON", """[{"ID":"new_group","Scenario":"ShortConversation","SubtitleLocalizationID":"locked_line"}]""")]
    [InlineData("DATA/Localization.JSON", """[{"ID":"locked_line","Ja":"fabricated"}]""")]
    [InlineData("DATA/Localization.JSON", """[{"ID":"new_line","Voice":"folder/locked_clip.ogg"}]""")]
    [InlineData("Audio/Voice/locked_clip.WAV", "fake audio")]
    [InlineData("Audio/Voice/TH/locked_clip.OGG", "fake audio")]
    public async Task Export_refuses_each_runtime_recognized_paid_reference(string path, string content)
    {
        using var fixture = NewPack();
        fixture.Write(path, content);
        // A hidden group may exist with no remaining hidden lines or voices.
        var verdict = content.Contains(Group, StringComparison.Ordinal)
            ? new MakerDlcHidden(null, new[] { Group }, null, 0, "group-only verdict")
            : Hidden;
        var exporter = Exporter(fixture, _ => Known(verdict));

        var check = await exporter.CheckAsync(PackId, default);
        Assert.True(check.Ok, check.Message);
        var paid = Assert.Single(check.Value!.Checks, check => check.Id == "paid");
        Assert.Equal(MakerExportSeverity.Error, paid.Severity);
        Assert.Equal(path, Assert.Single(paid.Paths));

        var result = await exporter.ExportAsync(Request(fixture, 1), default);
        Assert.Equal(MakerStudioErrorCodes.ExportBlocked, result.Error);
        Assert.False(Directory.Exists(Exports(fixture)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upgrade_rechecks_an_old_archive_even_after_the_source_is_clean(bool legacyFolder)
    {
        using var fixture = NewPack();
        fixture.Write(SubtitlePath, """{"locked_line":"fabricated subtitle"}""");
        var request = Request(fixture, 1);
        var oldExporter = Exporter(fixture, null);
        var oldExport = await oldExporter.ExportAsync(request, default);
        Assert.True(oldExport.Ok, oldExport.Message);
        var name = oldExport.Value!.FileName;
        if (legacyFolder)
            File.Move(Path.Combine(Exports(fixture), PackId, name), Path.Combine(Exports(fixture), name));

        fixture.Write(SubtitlePath, """{"free_line":"fabricated subtitle"}""");
        var exporter = Exporter(fixture, _ => Known(Hidden));
        var preview = await exporter.CheckAsync(PackId, default);
        Assert.True(preview.Value!.Ok);
        var download = await exporter.ReadExportAsync(PackId, name, default);
        Assert.Equal(MakerStudioErrorCodes.ExportBlocked, download.Error);
        Assert.Null(download.Value);
        Assert.Equal(MakerStudioErrorCodes.ExportBlocked, (await exporter.ExportAsync(request, default)).Error);

        var fresh = await exporter.ExportAsync(Request(fixture, 2), default);
        Assert.True(fresh.Ok, fresh.Message);
        Assert.True((await exporter.ReadExportAsync(PackId, fresh.Value!.FileName, default)).Ok);
        var unknown = Exporter(fixture, _ => Known(MakerDlcHidden.Unknown("unclassified")));
        Assert.Equal(MakerStudioErrorCodes.ExportBlocked,
            (await unknown.ReadExportAsync(PackId, fresh.Value.FileName, default)).Error);
    }

    [Fact]
    public void Manual_voice_requests_refuse_a_hidden_clip_and_game_lookup_reaches_the_original()
    {
        using var adapter = new Adapter();
        var paid = adapter.Type("Maker.PaidDlcContent");
        var console = adapter.Type("Console.BulbulConsoleCommands");
        var voice = adapter.Type("VoiceFeature");
        var seam = adapter.Type("VoiceSeam");
        var patches = adapter.Type("VoicePatches");
        var manager = new FakeVoiceManager();
        const string path = "cache/locked_clip.ogg";
        SetField(paid, null, "_hidden", Hidden);
        SetField(seam, null, "_resolved", true);
        SetField(seam, null, "_voiceManagerType", typeof(FakeVoiceManager));
        SetField(seam, null, "_getAudioClip", typeof(FakeVoiceManager).GetMethod(nameof(FakeVoiceManager.GetAudioClip)));

        foreach (var command in new[] { "play", "satone" })
        {
            var answer = (string)Call(console, null, "VoiceCommand", (object)new[] { command, path })!;
            Assert.Contains("belongs to paid DLC content", answer);
        }
        Assert.Equal(false, Call(voice, null, "PlayAsSatone", path, true));

        SetField(patches, null, "_lastChoice", VoiceChoice.Runtime);
        object?[] args = { manager, path, null };
        var useOriginal = (bool)Call(patches, null, "GetAudioClipPrefix", args)!;
        Assert.True(useOriginal);
        Assert.Equal(VoiceChoice.Game, patches.GetField("_lastChoice", All)!.GetValue(null));
        Assert.Null(args[2]);
        Assert.Same(manager.Original, manager.GetAudioClip(path));
        Assert.Equal(1, manager.OriginalRequests);
    }

    [Theory]
    [InlineData("unclassified")]
    [InlineData("delayed")]
    [InlineData("superseded")]
    public async Task Invalidated_catalog_refuses_play_voice_save_and_export(string capture)
    {
        using var fixture = NewPack();
        using var adapter = new Adapter();
        var type = adapter.Type("Maker.MakerCatalog");
        var catalog = type.GetProperty("Instance", All)!.GetValue(null);
        var editor = Assert.IsAssignableFrom<IMakerEditor>(Activator.CreateInstance(adapter.Type("Maker.MakerEditor"), nonPublic: true));
        var game = new MakerGameData(new[] { new MakerGameRow(Line, Clip, Group, "fabricated", null) }, new System.Collections.Generic.Dictionary<string, double>(), null);
        var snapshot = type.GetNestedType("GameSnap", BindingFlags.NonPublic)!;
        SetField(type, catalog, "_snap", Activator.CreateInstance(snapshot, All, null, new object[] { game, 0, 0d }, null));
        Task<MakerResult<MakerDlcHidden>> Verdict(CancellationToken cancel) =>
            (Task<MakerResult<MakerDlcHidden>>)Call(type, catalog, "HiddenAsync", cancel)!;
        Assert.True((await Verdict(default)).Ok);

        Call(type, null, "InvalidateGame", "test master-data change");
        // Keep the shared capture in flight until all protected entry points have asked for it.
        var outcome = type.GetNestedType("CaptureOutcome", BindingFlags.NonPublic)!;
        var completionType = typeof(TaskCompletionSource<>).MakeGenericType(outcome);
        var completion = Activator.CreateInstance(completionType, TaskCreationOptions.RunContinuationsAsynchronously);
        SetField(type, catalog, "_capture", completionType.GetProperty("Task")!.GetValue(completion));

        var play = editor.PlayAsync(Line, "original", "th", default);
        var save = editor.SaveVoiceAsync(PackId, "th", Clip, MakerAudioFixtures.Wav(48000, 1, 4800), default);
        var exact = ((IMakerCatalog)catalog!).LineAsync(Line, "th", default);
        var exportAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exporter = Exporter(fixture, cancel =>
        {
            var requested = Verdict(cancel);
            exportAsked.TrySetResult();
            return requested;
        });
        var export = Task.Run(() => exporter.ExportAsync(Request(fixture, 1), default));
        await exportAsked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (capture != "delayed")
        {
            var classified = capture == "superseded";
            var landed = Call(type, catalog, "Land", Task.FromResult(classified
                ? game : new MakerGameData(null, null, null, null, null, "test classification failure")), classified ? 0 : 1, true, null);
            Call(completionType, completion, "SetResult", landed);
        }

        var expectedError = capture == "delayed" ? MakerErrorCodes.Unavailable : MakerErrorCodes.NotReady;
        Assert.Equal(expectedError, (await play).Error);
        Assert.Equal(expectedError, (await save).Error);
        Assert.Equal(expectedError, (await exact).Error);
        Assert.Equal(MakerStudioErrorCodes.ExportBlocked, (await export).Error);
        Assert.False(Directory.Exists(Exports(fixture)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "audio")));
    }

    private static MakerProjectFixture NewPack()
    {
        var fixture = new MakerProjectFixture();
        fixture.Write("manifest.json", """{"id":"sample.pack","name":"Sample Pack","version":"0.1.0"}""");
        return fixture;
    }

    private static MakerExportRequest Request(MakerProjectFixture fixture, int operation) =>
        new() { PackId = PackId, ExpectedVersion = "0.1.0", Bump = "none", OperationId = fixture.Operation(operation) };

    private static string Exports(MakerProjectFixture fixture) =>
        Path.Combine(Path.GetDirectoryName(fixture.Root)!, MakerPackExporter.ExportsFolderName);

    private static MakerPackExporter Exporter(MakerProjectFixture fixture, Func<CancellationToken, Task<MakerResult<MakerDlcHidden>>>? paid) =>
        new((_, _) => MakerResult<MakerProjectStore>.Success(fixture.Store), _ => null, () => Exports(fixture),
            publish: (_, _) => { }, clock: fixture.Clock, paid: paid);

    private static Task<MakerResult<MakerDlcHidden>> Known(MakerDlcHidden verdict) =>
        Task.FromResult(MakerResult<MakerDlcHidden>.Success(verdict));

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object? Call(Type type, object? instance, string method, params object?[] args) =>
        type.GetMethod(method, All)!.Invoke(instance, args);
    private static void SetField(Type type, object? instance, string name, object? value) =>
        type.GetField(name, All)!.SetValue(instance, value);

    private sealed class FakeVoiceManager
    {
        internal object Original { get; } = new();
        internal int OriginalRequests { get; private set; }
        public object GetAudioClip(string _) { OriginalRequests++; return Original; }
    }

    // Execute only the adapter's managed entry points. Each test gets isolated adapter statics.
    // Game and Unity dependencies are resolved from their local install paths for type resolution.
    private sealed class Adapter : AssemblyLoadContext, IDisposable
    {
        private const string Bulbul = "CwyLs.ModLoader.Bulbul";
        private readonly Assembly _assembly;
        private readonly string[] _dependencies;

        internal Adapter() : base(isCollectible: true)
        {
            var tests = typeof(Adapter).Assembly;
            var configuration = tests.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var root = TestRepo.Root!;
            string Output(string project) => Path.Combine(root, "src", project, "bin", configuration, "netstandard2.1");
            var metadata = tests.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(entry => entry.Key, entry => entry.Value!);
            _dependencies = new[] { Output(Bulbul), Output("CwyLs.ModLoader"), metadata["FreezeGameManagedPath"], metadata["FreezeBepInExCorePath"] };
            _assembly = LoadFromAssemblyPath(Path.Combine(_dependencies[0], Bulbul + ".dll"));
        }

        internal Type Type(string name) => _assembly.GetType(Bulbul + "." + name, throwOnError: true)!;
        protected override Assembly? Load(AssemblyName name)
        {
            try { return Default.LoadFromAssemblyName(name); }
            catch (FileNotFoundException)
            {
                foreach (var directory in _dependencies)
                {
                    var file = Path.Combine(directory, name.Name + ".dll");
                    if (File.Exists(file)) return LoadFromAssemblyPath(file);
                }
                return null;
            }
        }
        public void Dispose() => Unload();
    }
}
