using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// Pack Maker's read side (<see cref="IMakerCatalog"/>, ticket DS-02): the game's lines with
    /// their subtitle and dub state per language, the packs Pack Maker may write to, and the game's
    /// status. Installed in <c>BulbulPlugin.Awake</c> with <c>MakerBackends.UseCatalog</c>; the
    /// maker routes call it on the API's worker threads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Snapshot.</b> The game's lines and clips are read once on the main thread
    /// (<see cref="MakerCatalogGame.Capture"/>, one shared capture through the dispatcher, taken ahead when Pack
    /// Maker opens and after every change; a stale snapshot is served while the next one is taken) and every
    /// query is answered from that snapshot on the caller's thread. The packs' subtitle tables and
    /// voice files are read from disk on the caller's thread (<see cref="MakerPackScanner"/>); no
    /// Unity call is made off the main thread.
    /// </para>
    /// <para>
    /// <b>Refresh.</b> The game snapshot is taken again after a master-data injection, a pack
    /// rescan, a room load, and while the game's voice cache was still empty. The pack read is
    /// taken again after <see cref="Invalidate"/>, a pack rescan, or whenever the packs' folders
    /// fingerprint differently (a table or a voice folder changed on disk) - checked on every query,
    /// a few file-time reads per pack. Pack Maker's editor (DS-03) calls <see cref="Invalidate"/>
    /// after every save, so the next query sees it even inside one file-time tick.
    /// </para>
    /// <para>
    /// Every method answers with data or a <see cref="MakerResult{T}"/> error and logs an
    /// unexpected failure as one line; none throws.
    /// </para>
    /// </remarks>
    public sealed class MakerCatalog : IMakerCatalog
    {
        /// <summary>Prefix of this feature's log lines.</summary>
        public const string LogPrefix = "MAKER";

        /// <summary>The dispatcher owner of the catalog's main-thread work.</summary>
        internal const string Owner = "maker.catalog";

        /// <summary>The trace subsystem of the catalog's file-only events.</summary>
        internal const string TraceSys = "maker";

        /// <summary>How long a game snapshot taken while the voice cache was empty is trusted.</summary>
        private const double PartialSnapshotSeconds = 5d;

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly MakerDubLengths _dubLengths = new MakerDubLengths();

        /// <summary>How long a request waits for the first snapshot (under the router's 12 s catalog budget).</summary>
        private static readonly TimeSpan ColdCaptureWait = TimeSpan.FromSeconds(10);

        /// <summary>How long a request waits for a refresh before it answers from the previous snapshot.</summary>
        private static readonly TimeSpan StaleCaptureWait = TimeSpan.FromSeconds(1);

        /// <summary>The main-thread timeout of the shared capture: work not started by then is skipped.</summary>
        private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

        /// <summary>The whole warm-up when Pack Maker opens.</summary>
        private static readonly TimeSpan WarmUpBudget = TimeSpan.FromSeconds(60);

        private readonly object _captureLock = new object();
        private Task<CaptureOutcome>? _capture;
        private GameSnap? _snap;
        private int _servedStale;
        private double _lastCaptureWaitMs;
        private double _lastCaptureMs;
        private int _gameGeneration;
        private int _packGeneration;
        private MakerPackContent? _content;
        private int _contentBuiltFor = -1;
        private MakerCatalogView? _view;

        private MakerCatalog()
        {
        }

        /// <summary>The one catalog.</summary>
        public static MakerCatalog Instance { get; } = new MakerCatalog();

        /// <summary>The caller-side wait for the main thread: 2 s (the dispatcher's default).</summary>
        internal static TimeSpan MainThreadTimeout => MainThread.DefaultInvokeTimeout;

        /// <summary>What the catalog learns from spoken lines (clip lengths, groups); fed by <see cref="MakerEvents"/>.</summary>
        internal MakerSpokenCache Spoken { get; } = new MakerSpokenCache();

        /// <summary>The runtime rows Pack Maker Play queued and the line each one says; fed by Play, read by <see cref="MakerEvents"/>.</summary>
        internal MakerPlayedLines Played { get; } = new MakerPlayedLines();

        /// <summary>
        /// Tells the catalog that a pack's subtitle table or dub changed on disk, so the next query
        /// reads the packs again. Cheap, safe from any thread, never throws. Pack Maker's editor
        /// (DS-03) calls this after every successful save or removal, before it answers.
        /// </summary>
        /// <param name="reason">What changed, for the trace file, e.g. <c>subtitle th.json saved</c>.</param>
        public static void Invalidate(string? reason)
        {
            Interlocked.Increment(ref Instance._packGeneration);
            ConsoleEvents.File(TraceSys, "catalogInvalidated", ("part", "packs"), ("reason", reason ?? string.Empty));
        }

        /// <summary>The game's lines changed (an injection, a rescan, a room load): take a new game snapshot next time.</summary>
        /// <param name="reason">Why, for the trace file.</param>
        internal static void InvalidateGame(string reason)
        {
            Interlocked.Increment(ref Instance._gameGeneration);
            ConsoleEvents.File(TraceSys, "catalogInvalidated", ("part", "game"), ("reason", reason));
            // Pack Maker is open: take the new snapshot now, in the background, rather than in the page's next request.
            if (MakerBackends.Events != null) Instance.WarmUp(reason);
            // Studio v2: the game's lines or the loaded packs changed, so a client holding a list refetches it.
            MakerBackends.Publish("maker.catalog.changed", new System.Collections.Generic.Dictionary<string, object?>
            {
                ["catalogRevision"] = Guid.NewGuid().ToString("N"), ["reason"] = reason,
            });
        }

        /// <inheritdoc />
        public Task<MakerResult<MakerStatus>> StatusAsync(CancellationToken cancel) => MakerEvents.StatusAsync(cancel);

        /// <inheritdoc />
        public async Task<MakerResult<IReadOnlyList<MakerPack>>> PacksAsync(CancellationToken cancel)
        {
            try
            {
                var view = await ViewAsync(cancel).ConfigureAwait(false);
                if (!view.Ok)
                {
                    return MakerResult<IReadOnlyList<MakerPack>>.Fail(view.Error!, view.Message ?? string.Empty);
                }

                var packs = view.Value!.Packs(MakerCatalogPacks.Sources(), ModLoaderPaths.PacksRoot, MakerPackRules.IsWritableFolder);

                // Integration (2026-11-07): the editor makes the final call (it also runs a real write test), so a pack
                // the catalog shows as editable is one a save will accept. Only ever narrows: never turns read-only on.
                foreach (var pack in packs)
                {
                    if (!pack.Editable)
                    {
                        continue;
                    }

                    var verdict = MakerEditor.Editability(pack.Id);
                    if (!verdict.Editable)
                    {
                        pack.Editable = false;
                        pack.ReadOnlyReason = verdict.Reason;
                    }
                }

                return MakerResult<IReadOnlyList<MakerPack>>.Success(packs);
            }
            catch (Exception ex)
            {
                return Failed<IReadOnlyList<MakerPack>>("packs", ex);
            }
        }

        /// <inheritdoc />
        public async Task<MakerResult<MakerLinePage>> LinesAsync(MakerLineQuery query, CancellationToken cancel)
        {
            try
            {
                var view = await ViewAsync(cancel).ConfigureAwait(false);
                return view.Ok
                    ? view.Value!.Lines(query)
                    : MakerResult<MakerLinePage>.Fail(view.Error!, view.Message ?? string.Empty);
            }
            catch (Exception ex)
            {
                return Failed<MakerLinePage>("lines", ex);
            }
        }

        /// <inheritdoc />
        public async Task<MakerResult<MakerLine>> LineAsync(string id, string lang, CancellationToken cancel)
        {
            try
            {
                var view = await ViewAsync(cancel, ColdCaptureWait, requireCurrent: true).ConfigureAwait(false);
                return view.Ok
                    ? view.Value!.Line(id, lang)
                    : MakerResult<MakerLine>.Fail(view.Error!, view.Message ?? string.Empty);
            }
            catch (Exception ex)
            {
                return Failed<MakerLine>("line", ex);
            }
        }

        /// <summary>
        /// What Pack Maker's DLC gate hides in the current game snapshot (<see cref="MakerCatalogGame.Gate"/>).
        /// Protected operations require a current, known verdict.
        /// </summary>
        /// <param name="cancel">Cancels the wait.</param>
        internal async Task<MakerResult<MakerDlcHidden>> HiddenAsync(CancellationToken cancel)
        {
            try
            {
                var game = await GameAsync(cancel, ColdCaptureWait, requireCurrent: true).ConfigureAwait(false);
                if (!game.Ok)
                {
                    return MakerResult<MakerDlcHidden>.Fail(game.Error!, game.Message ?? "The game's lines are not loaded yet.");
                }

                return IsCurrentVerdict(game.Value!)
                    ? MakerResult<MakerDlcHidden>.Success(game.Value!.Hidden)
                    : VerdictUnavailable<MakerDlcHidden>();
            }
            catch (Exception ex)
            {
                return Failed<MakerDlcHidden>("DLC check", ex);
            }
        }

        /// <summary>
        /// The refusal for a route that names a line id or a voice clip and does not go through the catalog's line lookup (v1 voice
        /// save, v1 Play), or null when it may go ahead. No snapshot: its error (fail closed).
        /// </summary>
        /// <param name="lineId">The line id, or null.</param>
        /// <param name="clip">The voice clip, or null.</param>
        /// <param name="cancel">Cancels the wait.</param>
        internal async Task<MakerResult<bool>?> DlcRefusalAsync(string? lineId, string? clip, CancellationToken cancel)
        {
            var hidden = await HiddenAsync(cancel).ConfigureAwait(false);
            var refusal = hidden.Ok
                ? hidden.Value!.Refusal(lineId, clip)
                : MakerResult<bool>.Fail(hidden.Error!, hidden.Message ?? "The game's lines are not loaded yet.", hidden.Details);
            if (refusal != null)
            {
                CwyLsModLoader.Log.LogDebug(LogPrefix + " DLC gate refused " + (lineId != null ? "line " + lineId : "clip " + clip) + ": " + refusal.Error);
            }

            return refusal;
        }

        /// <summary>
        /// The one consistent read Studio v2's catalog answers from (<see cref="MakerStudioPacks"/>): the cached view, the pack
        /// list, the pack sources and the packs folder. Same snapshot and refresh rules as <see cref="LinesAsync"/>; no extra
        /// main-thread work. With <paramref name="editorVerdicts"/> each pack's editability is the editor's final answer
        /// (as <see cref="PacksAsync"/> gives it, including its write test of the pack folder); without, it is the rule-based
        /// answer and nothing is written, which is all a line read needs.
        /// </summary>
        internal async Task<MakerResult<MakerStudioCatalogSnapshot>> StudioSnapshotAsync(bool editorVerdicts, CancellationToken cancel)
        {
            try
            {
                var view = await ViewAsync(cancel).ConfigureAwait(false);
                if (!view.Ok)
                {
                    return MakerResult<MakerStudioCatalogSnapshot>.Fail(view.Error!, view.Message ?? string.Empty);
                }

                var sources = MakerCatalogPacks.Sources();
                IReadOnlyList<MakerPack> packs;
                if (editorVerdicts)
                {
                    var verdicts = await PacksAsync(cancel).ConfigureAwait(false);
                    if (!verdicts.Ok)
                    {
                        return MakerResult<MakerStudioCatalogSnapshot>.Fail(verdicts.Error!, verdicts.Message ?? string.Empty);
                    }

                    packs = verdicts.Value!;
                }
                else
                {
                    packs = view.Value!.Packs(sources, ModLoaderPaths.PacksRoot, MakerPackRules.IsWritableFolder);
                }

                return MakerResult<MakerStudioCatalogSnapshot>.Success(
                    new MakerStudioCatalogSnapshot(view.Value!, packs, sources, ModLoaderPaths.PacksRoot));
            }
            catch (Exception ex)
            {
                return Failed<MakerStudioCatalogSnapshot>("studio snapshot", ex);
            }
        }

        /// <summary>
        /// Starts reading the game's lines and every pack's state in the background (Pack Maker just opened, or the game's
        /// lines changed while it is open), so the page's first requests find them cached instead of each waiting for the
        /// main thread. Safe from any thread, never throws, never blocks.
        /// </summary>
        /// <param name="reason">Why, for the trace file.</param>
        public void WarmUp(string reason)
        {
            ConsoleEvents.File(TraceSys, "catalogWarmUp", ("reason", reason));
            Task.Run(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var cancel = new CancellationTokenSource(WarmUpBudget);
                    var view = await ViewAsync(cancel.Token, ColdCaptureWait).ConfigureAwait(false);
                    var warmed = view.Ok ? await MakerStudioPacks.Catalog.WarmAsync(cancel.Token).ConfigureAwait(false) : 0;
                    ConsoleEvents.File(TraceSys, "catalogWarm", ("ok", view.Ok), ("error", view.Error), ("packLanguages", warmed),
                        ("ms", Math.Round(watch.Elapsed.TotalMilliseconds, 1)), ("reason", reason));
                    if (MakerRequestTrace.Enabled)
                    {
                        CwyLsModLoader.Log.LogInfo(LogPrefix + " catalog warm-up (" + reason + "): " + (view.Ok ? "ready" : view.Error + " " + view.Message) +
                                                   ", " + warmed + " pack languages, " + MakerRequestTrace.Format(watch.Elapsed.TotalMilliseconds) + " ms");
                    }
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(LogPrefix + " catalog warm-up failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            });
        }

        /// <summary>
        /// The view queries are answered from: the cached one, or a new one when the game snapshot or the pack read is stale.
        /// The game snapshot is not taken under the gate (<see cref="GameAsync"/>); one caller rebuilds the pack read and the
        /// view at a time, the others wait and reuse it.
        /// </summary>
        private Task<MakerResult<MakerCatalogView>> ViewAsync(CancellationToken cancel) => ViewAsync(cancel, ColdCaptureWait);

        private async Task<MakerResult<MakerCatalogView>> ViewAsync(CancellationToken cancel, TimeSpan coldWait, bool requireCurrent = false)
        {
            MakerResult<MakerGameData> game;
            using (MakerRequestTrace.Phase("catalog.game"))
            {
                game = await GameAsync(cancel, coldWait, requireCurrent).ConfigureAwait(false);
            }

            if (!game.Ok)
            {
                return MakerResult<MakerCatalogView>.Fail(game.Error!, game.Message ?? string.Empty);
            }

            try
            {
                using (MakerRequestTrace.Phase("catalog.gate"))
                {
                    await _gate.WaitAsync(cancel).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return MakerResult<MakerCatalogView>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled.");
            }

            try
            {
                // The packs are read on this (worker) thread: files and the pack registry only.
                using (MakerRequestTrace.Phase("catalog.packs"))
                {
                    var sources = MakerCatalogPacks.Sources();
                    var fingerprint = MakerPackScanner.Fingerprint(sources);
                    var packGeneration = Volatile.Read(ref _packGeneration);
                    if (_content is null
                        || _contentBuiltFor != packGeneration
                        || !string.Equals(_content.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        _content = MakerPackScanner.Scan(sources, fingerprint);
                        _contentBuiltFor = packGeneration;
                        _view = null;
                        ReportPacks(_content);
                    }
                }

                if (_view is null || !ReferenceEquals(_view.Game, game.Value))
                {
                    using (MakerRequestTrace.Phase("catalog.view"))
                    {
                        _view = new MakerCatalogView(game.Value!, _content, Spoken, _dubLengths.LengthOf);
                    }

                    ConsoleEvents.File(
                        TraceSys,
                        "catalogView",
                        ("lines", _view.Game.Rows.Count),
                        ("omitted", _view.OmittedCount),
                        ("gameClips", _view.Game.ClipCount),
                        ("subtitles", _view.Content.SubtitleCount),
                        ("voiceSets", _view.Content.VoicePlan.Summarize()));
                }

                return requireCurrent && !IsCurrentVerdict(game.Value!)
                    ? VerdictUnavailable<MakerCatalogView>()
                    : MakerResult<MakerCatalogView>.Success(_view);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// The game snapshot. A current one is answered at once. A stale one (the game's lines changed, or it was taken while
        /// the voice cache was empty) starts one shared capture on the main thread and waits for it at most
        /// <see cref="StaleCaptureWait"/>, then answers from the previous snapshot while the capture finishes in the background
        /// (and announces <c>maker.catalog.changed</c> when it lands after a change, so a page refetches). With no snapshot
        /// at all it waits up to <paramref name="coldWait"/> for that capture: the game may be slow in the background.
        /// Protected callers receive a failure if a current, known verdict cannot be read.
        /// </summary>
        private async Task<MakerResult<MakerGameData>> GameAsync(CancellationToken cancel, TimeSpan coldWait, bool requireCurrent)
        {
            var generation = Volatile.Read(ref _gameGeneration);
            var current = Volatile.Read(ref _snap);
            var partialExpired = current != null && !current.Data.ClipCacheKnown && MainThread.Now - current.BuiltAt > PartialSnapshotSeconds;
            if (current != null && current.Generation == generation && !partialExpired &&
                (!requireCurrent || IsCurrentVerdict(current.Data)))
            {
                return MakerResult<MakerGameData>.Success(current.Data);
            }

            var capture = StartCapture(generation, changed: current != null && current.Generation != generation);
            var wait = current is null ? coldWait : StaleCaptureWait;
            bool landed;
            using (MakerRequestTrace.Phase(current is null ? "capture.cold" : "capture.stale"))
            {
                try
                {
                    landed = await Task.WhenAny(capture, Task.Delay(wait, cancel)).ConfigureAwait(false) == capture;
                }
                catch (OperationCanceledException)
                {
                    return MakerResult<MakerGameData>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled.");
                }
            }

            if (cancel.IsCancellationRequested && !landed)
            {
                return MakerResult<MakerGameData>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled.");
            }

            string failure;
            if (landed)
            {
                var outcome = capture.Result;
                if (outcome.Data != null)
                {
                    return requireCurrent && !IsCurrentVerdict(outcome.Data)
                        ? VerdictUnavailable<MakerGameData>()
                        : MakerResult<MakerGameData>.Success(outcome.Data);
                }

                if (outcome.NotLoaded != null)
                {
                    return current != null && !requireCurrent
                        ? MakerResult<MakerGameData>.Success(current.Data)
                        : MakerResult<MakerGameData>.Fail(MakerErrorCodes.NotReady, outcome.NotLoaded);
                }

                failure = outcome.Failure ?? "The game's lines could not be read.";
            }
            else
            {
                failure = "The game has not answered within " + wait.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                          " s (busy or in the background); Pack Maker keeps reading in the background, try again in a moment.";
                if (current != null && !requireCurrent)
                {
                    Volatile.Write(ref _servedStale, 1);
                }
            }

            // A busy main thread is not a reason to refuse a list we already have: answer from the previous snapshot.
            if (current != null && !requireCurrent)
            {
                CwyLsModLoader.Log.LogDebug(LogPrefix + " catalog snapshot not refreshed yet, answering from the previous one: " + failure);
                return MakerResult<MakerGameData>.Success(current.Data);
            }

            CwyLsModLoader.Log.LogWarning(LogPrefix + " catalog: " + failure);
            return MakerResult<MakerGameData>.Fail(MakerErrorCodes.Unavailable, failure);
        }

        private bool IsCurrentVerdict(MakerGameData data)
        {
            var snapshot = Volatile.Read(ref _snap);
            return data.Hidden.IsKnown && snapshot != null && ReferenceEquals(snapshot.Data, data)
                && snapshot.Generation == Volatile.Read(ref _gameGeneration);
        }

        private static MakerResult<T> VerdictUnavailable<T>() =>
            MakerResult<T>.Fail(MakerErrorCodes.NotReady, "The game's lines changed during the paid DLC check; try again in a moment.");

        /// <summary>
        /// The capture in flight, or a new one: <see cref="MakerCatalogGame.Capture"/> on the main thread with a long timeout,
        /// shared by every caller (none of them cancels it). Never faults: the outcome carries the failure.
        /// </summary>
        private Task<CaptureOutcome> StartCapture(int generation, bool changed)
        {
            lock (_captureLock)
            {
                if (_capture != null && !_capture.IsCompleted)
                {
                    return _capture;
                }

                // The first requester's trace sees the main-thread wait and the capture itself (later ones share the task).
                var trace = MakerRequestTrace.Current;
                var queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                trace?.Begin("main.wait");
                Func<MakerGameData> work = () =>
                {
                    trace?.End("main.wait");
                    var ranAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    trace?.Begin("main.capture");
                    try
                    {
                        return MakerCatalogGame.Capture();
                    }
                    finally
                    {
                        trace?.End("main.capture");
                        Volatile.Write(ref _lastCaptureWaitMs, (ranAt - queuedAt) * 1000d / System.Diagnostics.Stopwatch.Frequency);
                        Volatile.Write(ref _lastCaptureMs, MakerRequestTrace.MsSince(ranAt));
                    }
                };

                Task<MakerGameData> invoke;
                try
                {
                    invoke = MainThread.InvokeAsync("maker.catalog.snapshot", work, CaptureTimeout, CancellationToken.None, Owner);
                }
                catch (Exception ex)
                {
                    trace?.End("main.wait");
                    return Task.FromResult(new CaptureOutcome(null, null, "The game could not take the request now (" + ex.GetType().Name + ": " + ex.Message + ")."));
                }

                _capture = invoke.ContinueWith(t => Land(t, generation, changed, trace), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return _capture;
            }
        }

        private CaptureOutcome Land(Task<MakerGameData> task, int generation, bool changed, MakerRequestTrace? trace)
        {
            if (!task.IsCompleted || task.IsFaulted || task.IsCanceled)
            {
                trace?.End("main.wait");
                var error = task.Exception?.GetBaseException();
                string failure = error is TimeoutException
                    ? "The game did not run the snapshot within " + CaptureTimeout.TotalSeconds + " s; try again in a moment."
                    : error is MainThreadRejectedException
                        ? "The game could not take the request now (" + error.Message + ")."
                        : task.IsCanceled ? "The snapshot was cancelled." : "The snapshot failed (" + (error?.GetType().Name ?? "?") + ": " + error?.Message + ").";
                CwyLsModLoader.Log.LogWarning(LogPrefix + " catalog snapshot: " + failure);
                return new CaptureOutcome(null, null, failure);
            }

            var captured = task.Result;
            if (captured is null || !captured.IsLoaded)
            {
                return new CaptureOutcome(null, captured?.Problem ?? "The game's lines are not loaded yet.", null);
            }

            Volatile.Write(ref _snap, new GameSnap(captured, generation, MainThread.Now));
            ConsoleEvents.File(TraceSys, "catalogSnapshot", ("snapshot", captured.ToString()), ("generation", generation),
                ("mainWaitMs", Math.Round(Volatile.Read(ref _lastCaptureWaitMs), 1)), ("captureMs", Math.Round(Volatile.Read(ref _lastCaptureMs), 1)));
            if (MakerRequestTrace.Enabled)
            {
                CwyLsModLoader.Log.LogInfo(LogPrefix + " catalog snapshot " + captured.Rows.Count + " lines: main-thread wait " +
                                           MakerRequestTrace.Format(Volatile.Read(ref _lastCaptureWaitMs)) + " ms, capture " +
                                           MakerRequestTrace.Format(Volatile.Read(ref _lastCaptureMs)) + " ms");
            }

            // A page that was answered from the previous snapshot after a real change refetches now.
            if (changed && Interlocked.Exchange(ref _servedStale, 0) == 1)
            {
                MakerBackends.Publish("maker.catalog.changed", new Dictionary<string, object?>
                {
                    ["catalogRevision"] = Guid.NewGuid().ToString("N"), ["reason"] = "snapshot refreshed",
                });
            }

            return new CaptureOutcome(captured, null, null);
        }

        /// <summary>A game snapshot and what it was taken for.</summary>
        private sealed class GameSnap
        {
            internal GameSnap(MakerGameData data, int generation, double builtAt)
            {
                Data = data;
                Generation = generation;
                BuiltAt = builtAt;
            }

            internal MakerGameData Data { get; }
            internal int Generation { get; }
            internal double BuiltAt { get; }
        }

        /// <summary>How a capture ended: the data, or why the lines are not loaded, or why it failed.</summary>
        private sealed class CaptureOutcome
        {
            internal CaptureOutcome(MakerGameData? data, string? notLoaded, string? failure)
            {
                Data = data;
                NotLoaded = notLoaded;
                Failure = failure;
            }

            internal MakerGameData? Data { get; }
            internal string? NotLoaded { get; }
            internal string? Failure { get; }
        }

        private static void ReportPacks(MakerPackContent content)
        {
            ConsoleEvents.File(
                TraceSys,
                "catalogPacks",
                ("subtitles", content.SubtitleCount),
                ("languages", string.Join(",", content.SubtitleLanguages)),
                ("voiceSets", content.VoicePlan.Summarize()),
                ("rejected", content.RejectedEntries),
                ("problems", content.Problems.Count));

            foreach (var problem in content.Problems)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " catalog could not read " + problem);
            }
        }

        private static MakerResult<T> Failed<T>(string what, Exception ex)
        {
            CwyLsModLoader.Log.LogError(LogPrefix + " catalog " + what + " failed: " + ex.GetType().Name + ": " + ex.Message);
            return MakerResult<T>.Fail(MakerErrorCodes.Internal, "The catalog failed (" + ex.GetType().Name + "); see the log.");
        }
    }
}
