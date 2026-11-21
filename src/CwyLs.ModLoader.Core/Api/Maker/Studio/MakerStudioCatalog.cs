using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Everything one Studio catalog answer is computed from, taken together so the answer is consistent.</summary>
    public sealed class MakerStudioCatalogSnapshot
    {
        /// <summary>Creates a snapshot.</summary>
        /// <param name="view">The effective catalog (game rows, every loaded pack's content).</param>
        /// <param name="packs">The v1 pack list: loaded packs in load order, then the refused ones, with the editor's final editability.</param>
        /// <param name="sources">The loaded and refused packs as the pack registry reported them.</param>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder, or null.</param>
        public MakerStudioCatalogSnapshot(MakerCatalogView view, IReadOnlyList<MakerPack> packs, IReadOnlyList<MakerPackSource> sources, string? packsRoot)
        {
            View = view ?? throw new ArgumentNullException(nameof(view));
            Packs = packs ?? throw new ArgumentNullException(nameof(packs));
            Sources = sources ?? throw new ArgumentNullException(nameof(sources));
            PacksRoot = packsRoot;
        }

        /// <summary>The effective catalog.</summary>
        public MakerCatalogView View { get; }

        /// <summary>The v1 pack list.</summary>
        public IReadOnlyList<MakerPack> Packs { get; }

        /// <summary>The loaded and refused packs.</summary>
        public IReadOnlyList<MakerPackSource> Sources { get; }

        /// <summary>The <c>CWYLS Mods</c> folder, or null.</summary>
        public string? PacksRoot { get; }

        /// <summary>True when a pack with this id (case-insensitive) loaded or was refused.</summary>
        /// <param name="id">A pack id.</param>
        public bool Knows(string id) => Sources.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where the Studio catalog gets its snapshot; the game adapter implements it over the v1 catalog.</summary>
    public interface IMakerStudioCatalogSource
    {
        /// <summary>The current snapshot, or an error such as <c>not_ready</c> or <c>unavailable</c>.</summary>
        /// <param name="editorVerdicts">
        /// True for the pack list: each pack's editability is the editor's final answer, which includes a real write test of
        /// the pack folder. False for reading a pack's lines: the rule-based answer is enough and nothing is written.
        /// </param>
        /// <param name="cancel">Cancels the request.</param>
        Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel);
    }

    /// <summary>
    /// The real Studio v2 catalog (<c>packs</c>, <c>lines</c>, <c>line</c>, <c>groups</c>): the game's lines with the selected
    /// pack's own subtitle, dub, workflow, takes and revisions, pack-scoped filters and additive progress. Reads only; it never
    /// takes the editor's gates and keeps off the per-line take reconciliation except for the lines that have takes.
    /// </summary>
    public sealed class MakerStudioCatalog : IMakerStudioCatalog
    {
        /// <summary>The group id of lines the game assigns to no group.</summary>
        public const string UngroupedId = "ungrouped";

        /// <summary><see cref="MakerStudioPack.MetadataState"/> of a pack whose creator files are readable.</summary>
        public const string ReadyState = "ready";

        /// <summary><see cref="MakerStudioPack.MetadataState"/> when <c>.maker/project.json</c> is corrupt or from a newer version.</summary>
        public const string UnavailableState = "unavailable";

        /// <summary><see cref="MakerStudioPack.MetadataState"/> when an interrupted commit waits for recovery.</summary>
        public const string RecoveryState = "recovery-required";

        /// <summary>The group ids that map to a <c>group.*</c> label key by exact name. Not a claim about the game's taxonomy.</summary>
        public static IReadOnlyList<string> LabelledGroups { get; } = new[] { "morning", "focus", "break", "rain", "clickReaction", "evening" };

        /// <summary>The filters of <see cref="MakerStudioLineQuery.Filter"/>.</summary>
        public static IReadOnlyList<string> Filters { get; } =
            new[] { "all", "todo", "draft", "done", "needs-retake", "starred", "missing-subtitle", "missing-dub" };

        private readonly IMakerStudioCatalogSource _source;
        private readonly object _gate = new object();
        private readonly Dictionary<string, MakerStudioLineStates> _states = new Dictionary<string, MakerStudioLineStates>(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, MakerPackContent>> _own = new Dictionary<string, KeyValuePair<string, MakerPackContent>>(StringComparer.Ordinal);
        private readonly ConditionalWeakTable<MakerGameData, MakerSceneContext> _scenes = new ConditionalWeakTable<MakerGameData, MakerSceneContext>();

        /// <summary>Creates the catalog.</summary>
        /// <param name="source">The snapshot source.</param>
        public MakerStudioCatalog(IMakerStudioCatalogSource source) => _source = source ?? throw new ArgumentNullException(nameof(source));

        /// <inheritdoc />
        public async Task<MakerResult<IReadOnlyList<MakerStudioPack>>> PacksAsync(CancellationToken cancel)
        {
            MakerResult<MakerStudioCatalogSnapshot> snap;
            using (MakerRequestTrace.Phase("snapshot")) snap = await _source.SnapshotAsync(true, cancel).ConfigureAwait(false);
            if (!snap.Ok) return MakerResult<IReadOnlyList<MakerStudioPack>>.Fail(snap.Error!, snap.Message ?? string.Empty);
            var snapshot = snap.Value!;
            var packs = new List<MakerStudioPack>(snapshot.Packs.Count);
            foreach (var pack in snapshot.Packs)
            {
                var source = snapshot.Sources.FirstOrDefault(s => string.Equals(s.Id, pack.Id, StringComparison.Ordinal));
                var loaded = source?.Loaded ?? false;
                var state = loaded && source!.RootPath != null ? MetadataOf(source.RootPath) : (loaded ? ReadyState : UnavailableState);
                var studioEditable = pack.Editable && state != UnavailableState;
                packs.Add(new MakerStudioPack
                {
                    Id = pack.Id, Name = pack.Name, Version = pack.Version, Languages = pack.Languages,
                    Editable = pack.Editable, ReadOnlyReason = pack.ReadOnlyReason,
                    Loaded = loaded, RestartRequired = false,
                    StudioEditable = studioEditable,
                    StudioReadOnlyReason = studioEditable ? null
                        : !pack.Editable ? pack.ReadOnlyReason
                        : "Its creator metadata is unreadable or from a newer version; Pack Maker keeps it as it is and treats the pack as read-only.",
                    MetadataState = state,
                });
            }

            foreach (var pending in MakerPendingPacks.Scan(snapshot.PacksRoot, snapshot.Knows)) packs.Add(pending.ToStudioPack());
            return MakerResult<IReadOnlyList<MakerStudioPack>>.Success(packs);
        }

        /// <inheritdoc />
        public async Task<MakerResult<MakerStudioLinePage>> LinesAsync(MakerStudioLineQuery query, CancellationToken cancel)
        {
            if (query is null) return MakerResult<MakerStudioLinePage>.Fail(MakerErrorCodes.BadRequest, "A query is required.");
            var problem = Validate(query, out var lang);
            if (problem != null) return MakerResult<MakerStudioLinePage>.Fail(MakerErrorCodes.BadRequest, problem);
            MakerResult<MakerStudioCatalogSnapshot> snap;
            using (MakerRequestTrace.Phase("snapshot")) snap = await _source.SnapshotAsync(false, cancel).ConfigureAwait(false);
            if (!snap.Ok) return MakerResult<MakerStudioLinePage>.Fail(snap.Error!, snap.Message ?? string.Empty);
            var scope = Open(snap.Value!, query.PackId, lang);
            if (!scope.Ok) return MakerResult<MakerStudioLinePage>.Fail(scope.Error!, scope.Message!);
            var s = scope.Value!;
            if (query.CatalogRevision != null && !string.Equals(query.CatalogRevision, s.Revision, StringComparison.Ordinal))
                return MakerResult<MakerStudioLinePage>.Fail(MakerStudioErrorCodes.Conflict, "The catalogue changed while you were paging; start again from the first page.");

            var needle = query.Search?.Trim() ?? string.Empty;
            var total = 0;
            var wanted = new List<MakerGameRow>(Math.Min(query.Limit, 64));
            using (MakerRequestTrace.Phase("filter"))
            {
                foreach (var row in s.View.OrderedRows)
                {
                    if (!s.View.Game.IsStoryLine(row.Id)) continue;
                    if (!s.Matches(row, query.Filter, query.Group, needle)) continue;
                    if (total >= query.Offset && wanted.Count < query.Limit) wanted.Add(row);
                    total++;
                }
            }

            var items = new List<MakerStudioLine>(wanted.Count);
            using (MakerRequestTrace.Phase("build"))
            {
                foreach (var row in wanted)
                {
                    cancel.ThrowIfCancellationRequested();
                    var line = s.Build(row, null, exact: false);
                    if (!line.Ok) return MakerResult<MakerStudioLinePage>.Fail(line.Error!, line.Message!, line.Details);
                    items.Add(line.Value!);
                }
            }

            return MakerResult<MakerStudioLinePage>.Success(new MakerStudioLinePage
            {
                Total = total, Offset = query.Offset, CatalogRevision = s.Revision, Items = items,
            });
        }

        /// <inheritdoc />
        public async Task<MakerResult<MakerStudioLine>> LineAsync(string packId, string lang, string id, string? occurrenceId, CancellationToken cancel)
        {
            if (!MakerEditRules.TryLanguage(lang, out var code, out var langProblem))
                return MakerResult<MakerStudioLine>.Fail(MakerErrorCodes.BadRequest, langProblem ?? "invalid language");
            if (!MakerEditRules.IsLineId(id)) return MakerResult<MakerStudioLine>.Fail(MakerErrorCodes.BadRequest, "invalid line id");
            MakerResult<MakerStudioCatalogSnapshot> snap;
            using (MakerRequestTrace.Phase("snapshot")) snap = await _source.SnapshotAsync(false, cancel).ConfigureAwait(false);
            if (!snap.Ok) return MakerResult<MakerStudioLine>.Fail(snap.Error!, snap.Message ?? string.Empty);
            var scope = Open(snap.Value!, packId, code);
            if (!scope.Ok) return MakerResult<MakerStudioLine>.Fail(scope.Error!, scope.Message!);
            var s = scope.Value!;
            if (!s.View.Game.IsStoryLine(id) || !s.View.Game.TryFind(id, out var row) || row is null)
                return MakerResult<MakerStudioLine>.Fail(MakerErrorCodes.NotFound, "The game has no line '" + id + "'.");
            return s.Build(row, occurrenceId, exact: true);
        }

        /// <inheritdoc />
        public async Task<MakerResult<MakerStudioGroups>> GroupsAsync(string packId, string lang, CancellationToken cancel)
        {
            if (!MakerEditRules.TryLanguage(lang, out var code, out var langProblem))
                return MakerResult<MakerStudioGroups>.Fail(MakerErrorCodes.BadRequest, langProblem ?? "invalid language");
            MakerResult<MakerStudioCatalogSnapshot> snap;
            using (MakerRequestTrace.Phase("snapshot")) snap = await _source.SnapshotAsync(false, cancel).ConfigureAwait(false);
            if (!snap.Ok) return MakerResult<MakerStudioGroups>.Fail(snap.Error!, snap.Message ?? string.Empty);
            var scope = Open(snap.Value!, packId, code);
            if (!scope.Ok) return MakerResult<MakerStudioGroups>.Fail(scope.Error!, scope.Message!);
            using (MakerRequestTrace.Phase("groups")) return MakerResult<MakerStudioGroups>.Success(scope.Value!.Groups());
        }

        /// <summary>
        /// Reads ahead what the first page of every loaded pack and language needs (the snapshot, each pack's creator state and
        /// own content, the scene context), off the main thread, so the first catalogue request after Pack Maker opens finds it
        /// cached. Never throws; stops at the first snapshot failure.
        /// </summary>
        /// <param name="cancel">Cancels the warm-up.</param>
        /// <returns>How many pack languages were read.</returns>
        public async Task<int> WarmAsync(CancellationToken cancel)
        {
            var warmed = 0;
            try
            {
                var snap = await _source.SnapshotAsync(false, cancel).ConfigureAwait(false);
                if (!snap.Ok) return 0;
                var snapshot = snap.Value!;
                foreach (var pack in snapshot.Packs)
                {
                    foreach (var lang in pack.Languages ?? new List<string>())
                    {
                        if (cancel.IsCancellationRequested) return warmed;
                        if (!MakerEditRules.TryLanguage(lang, out var code, out _)) continue;
                        if (Open(snapshot, pack.Id, code).Ok) warmed++;
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // A warm-up is an optimisation: the request that needs the data reads it again and reports the failure.
            }

            return warmed;
        }

        private static string? Validate(MakerStudioLineQuery query, out string lang)
        {
            lang = string.Empty;
            if (!MakerSegments.IsPackId(query.PackId)) return "invalid pack id";
            if (!MakerEditRules.TryLanguage(query.Lang, out lang, out var problem)) return problem ?? "invalid language";
            if (!Filters.Contains(query.Filter ?? string.Empty))
                return "filter must be " + string.Join(", ", Filters.Take(Filters.Count - 1)) + " or " + Filters[Filters.Count - 1];
            if (query.Offset < 0) return "offset must be a whole number of 0 or more";
            if (query.Limit < MakerLineRules.MinLimit || query.Limit > MakerLineRules.MaxLimit)
                return "limit must be " + MakerLineRules.MinLimit + "-" + MakerLineRules.MaxLimit;
            if (query.Search != null && query.Search.Length > MakerDefaults.MaxSearchChars)
                return "q may be at most " + MakerDefaults.MaxSearchChars + " characters";
            if (query.Group != null && query.Group.Length > 200) return "group may be at most 200 characters";
            return null;
        }

        /// <summary>Resolves the pack and takes the per-request state. Failures carry the contract's status mapping.</summary>
        private MakerResult<Scope> Open(MakerStudioCatalogSnapshot snapshot, string packId, string lang)
        {
            if (!MakerSegments.IsPackId(packId)) return MakerResult<Scope>.Fail(MakerErrorCodes.BadRequest, "invalid pack id");
            var source = snapshot.Sources.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.Ordinal));
            if (source is null)
            {
                return MakerPendingPacks.TryFind(snapshot.PacksRoot, packId, snapshot.Knows, out _)
                    ? MakerResult<Scope>.Fail(MakerStudioErrorCodes.RestartRequired, MakerPendingPacks.RestartReason)
                    : MakerResult<Scope>.Fail(MakerErrorCodes.NotFound, "no pack '" + packId + "' is installed");
            }

            var v1 = snapshot.Packs.FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.Ordinal));
            if (!source.Loaded || source.RootPath is null)
            {
                return MakerResult<Scope>.Fail(MakerErrorCodes.ReadOnlyPack,
                    v1?.ReadOnlyReason ?? ("pack '" + packId + "' did not load this session; its lines cannot be listed"));
            }

            var store = new MakerProjectStore(source.RootPath, recoveryWritable: v1?.Editable ?? false);
            MakerStudioLineStates states;
            using (MakerRequestTrace.Phase("pack.states")) states = StatesFor(store, source.RootPath, lang);
            MakerPackContent own;
            using (MakerRequestTrace.Phase("pack.own")) own = OwnFor(source);
            own.VoicePlan.TryGet(lang, out var ownSet);
            MakerSceneContext scenes;
            using (MakerRequestTrace.Phase("scenes"))
                scenes = _scenes.GetValue(snapshot.View.Game,
                    game => MakerSceneContext.Build(game.NovelRows, id => game.TryFind(id, out _)));
            var revision = MakerProjectRevision.StableId(string.Join("|",
                "catalog", snapshot.View.Game.Stamp, snapshot.View.Content.Fingerprint, own.Fingerprint, states.Stamp, packId, lang));
            return MakerResult<Scope>.Success(new Scope(snapshot.View, source, store, states, own, ownSet, scenes, lang, revision));
        }

        private MakerStudioLineStates StatesFor(MakerProjectStore store, string root, string lang)
        {
            var key = root + "\n" + lang;
            var stamp = MakerStudioLineStates.StampOf(store, lang);
            lock (_gate)
            {
                if (_states.TryGetValue(key, out var cached) && string.Equals(cached.Stamp, stamp, StringComparison.Ordinal)) return cached;
            }

            var read = MakerStudioLineStates.Read(store, lang);
            lock (_gate) { _states[key] = read; }
            return read;
        }

        private MakerPackContent OwnFor(MakerPackSource source)
        {
            var fingerprint = MakerPackScanner.Fingerprint(new[] { source });
            var key = source.RootPath + "\n" + source.Id;
            lock (_gate)
            {
                if (_own.TryGetValue(key, out var cached) && string.Equals(cached.Key, fingerprint, StringComparison.Ordinal)) return cached.Value;
            }

            var scanned = MakerPackScanner.Scan(new[] { source }, fingerprint);
            lock (_gate) { _own[key] = new KeyValuePair<string, MakerPackContent>(fingerprint, scanned); }
            return scanned;
        }

        private static string MetadataOf(string root)
        {
            // Read without the pack's gate: files are replaced atomically, and a list must not wait for a save in progress.
            try
            {
                var files = new MakerPackFiles(root);
                var header = files.ReadText(MakerStudioPaths.Project, out _);
                if (header != null && !MakerProjectJson.TryHeader(header, out _, out _)) return UnavailableState;
                return files.ListFiles(MakerStudioPaths.Transactions).Any(p => p.EndsWith(".pending.json", StringComparison.Ordinal))
                    ? RecoveryState : ReadyState;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return UnavailableState;
            }
        }

        /// <summary>The label key of a group id: an exact-name mapping, null for anything else.</summary>
        /// <param name="groupId">A group id.</param>
        public static string? LabelKeyOf(string groupId) =>
            groupId == UngroupedId || LabelledGroups.Contains(groupId) ? "group." + groupId : null;

        private sealed class Scope
        {
            private readonly MakerPackSource _source;
            private readonly MakerProjectStore _store;
            private readonly MakerStudioLineStates _states;
            private readonly MakerPackContent _own;
            private readonly LanguageVoiceSet? _ownSet;
            private readonly MakerSceneContext _scenes;
            private readonly string _lang;
            private IReadOnlyDictionary<string, IReadOnlyList<string>>? _voiceIndex;
            private static readonly IReadOnlyList<string> NoFiles = new string[0];

            internal Scope(MakerCatalogView view, MakerPackSource source, MakerProjectStore store, MakerStudioLineStates states,
                MakerPackContent own, LanguageVoiceSet? ownSet, MakerSceneContext scenes, string lang, string revision)
            {
                View = view; _source = source; _store = store; _states = states; _own = own; _ownSet = ownSet;
                _scenes = scenes; _lang = lang; Revision = revision;
            }

            internal MakerCatalogView View { get; }
            internal string Revision { get; }

            private bool OwnedDub(MakerGameRow row) => row.Voice != null && _ownSet != null && _ownSet.Find(row.Voice) != null;

            private MakerLineState StateOf(MakerGameRow row, string? ownSubtitle) =>
                MakerLineState.Of(View.IsOmittedRow(row.Id), row.Ja, row.En, row.Voice, ownSubtitle, OwnedDub(row) ? _source.Id : null);

            internal bool Matches(MakerGameRow row, string filter, string? group, string needle)
            {
                var ownSubtitle = _states.OwnSubtitle(row.Id);
                if (group != null && !string.Equals(View.PrimaryGroupOf(row) ?? UngroupedId, group, StringComparison.Ordinal)) return false;
                var state = StateOf(row, ownSubtitle);
                var summary = _states.Summary(row.Id);
                bool pass;
                switch (filter)
                {
                    case "all": pass = true; break;
                    case "starred": pass = summary.Workflow.Starred; break;
                    case "missing-subtitle": pass = state.NeedsSubtitle && !state.HasSubtitle; break;
                    case "missing-dub": pass = state.NeedsDub && !state.HasDub; break;
                    default: pass = string.Equals(summary.Workflow.Status, filter, StringComparison.Ordinal); break;
                }

                return pass && MakerLineRules.MatchesSearch(needle, row.Id, row.Voice, row.Ja, row.En, ownSubtitle);
            }

            internal MakerResult<MakerStudioLine> Build(MakerGameRow row, string? occurrenceId, bool exact)
            {
                var effective = View.Line(row.Id, _lang);
                if (!effective.Ok) return MakerResult<MakerStudioLine>.Fail(effective.Error!, effective.Message ?? string.Empty);
                var v1 = effective.Value!;
                if (!_scenes.TryContextOf(row.Id, occurrenceId, out var context))
                    return MakerResult<MakerStudioLine>.Fail(MakerErrorCodes.NotFound, "The line '" + row.Id + "' has no occurrence '" + occurrenceId + "'.");

                var summary = _states.Summary(row.Id);
                var workflow = new MakerStudioWorkflow { Status = summary.Workflow.Status, Starred = summary.Workflow.Starred };
                var takeCount = summary.TakeCount;
                var publishedId = summary.PublishedTakeId;
                var publishedRevision = summary.PublishedTakeRevision;
                var ownedSubtitle = _states.OwnSubtitle(row.Id);
                string revision;
                var fileName = MakerStudioPaths.CanNameFile(row.Id);
                if (fileName && (exact || summary.TakeCount > 0))
                {
                    // The authoritative revision (take files included) is what a mutation compares against.
                    var read = _store.ReadLineWithSubtitle(_lang, row.Id, out var currentSubtitle);
                    if (read.Ok)
                    {
                        var doc = read.Value!;
                        ownedSubtitle = string.IsNullOrEmpty(currentSubtitle) ? null : currentSubtitle;
                        revision = doc.Revision;
                        workflow = new MakerStudioWorkflow { Status = doc.Workflow.Status, Starred = doc.Workflow.Starred };
                        takeCount = doc.Takes.Count;
                        publishedId = doc.PublishedTakeId;
                        publishedRevision = doc.PublishedTakeRevision;
                    }
                    else if (exact)
                    {
                        return MakerResult<MakerStudioLine>.Fail(read.Error!, read.Message ?? string.Empty, read.Details);
                    }
                    else
                    {
                        revision = _states.RevisionOf(row.Id);
                    }
                }
                else
                {
                    revision = _states.RevisionOf(row.Id);
                }

                // Merge (W2b M5d): the clip revision and the published take are per (language, clip), from the same
                // MakerClipStore that publish checks, so every alias line agrees and expectedClipRevision matches.
                var clipRevision = MakerStudioView.ClipRevisionOf(_lang, row.Id, row.Voice, OwnClipPath(row));
                if (row.Voice != null && MakerEditRules.IsClip(row.Voice))
                {
                    try
                    {
                        var clip = ReadClip(row.Voice, exact);
                        clipRevision = clip.Revision;
                        publishedId = clip.Publication?.TakeId;
                        publishedRevision = clip.Publication?.TakeRevision;
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                    {
                        // Unreadable voice folder: keep the file-stamp revision; publish will refuse with a structured code.
                    }
                }

                return MakerResult<MakerStudioLine>.Success(new MakerStudioLine
                {
                    Id = v1.Id, Voice = v1.Voice, Group = v1.Group, DurationSec = v1.DurationSec, Ja = v1.Ja, En = v1.En,
                    Subtitle = v1.Subtitle, SubtitlePack = v1.SubtitlePack, DubPack = v1.DubPack, DubDurationSec = v1.DubDurationSec,
                    OwnedSubtitle = ownedSubtitle, OwnedDub = OwnedDub(row),
                    Workflow = workflow, TakeCount = takeCount, Revision = revision,
                    ClipRevision = clipRevision,
                    PublishedTakeId = publishedId, PublishedTakeRevision = publishedRevision, Context = context,
                    // TD-3: no Studio file can be named for this line (it has none), so it is read like a line without one.
                    ReadOnly = fileName ? null : MakerStudioLineReadOnly.IdNotAFileName,
                });
            }

            private string? OwnClipPath(MakerGameRow row) => row.Voice is null || _ownSet is null ? null : _own.FullPathOf(_ownSet.Find(row.Voice));

            /// <summary>
            /// The clip's publication state. A page lists the pack's voice folders once (<see cref="MakerPackFiles.VoiceIndex"/>)
            /// instead of once per row; one line (<paramref name="exact"/>) reads its clip directly.
            /// </summary>
            private MakerClipState ReadClip(string voice, bool exact)
            {
                var clips = new MakerClipStore(_store);
                if (exact) return clips.Read(_lang, voice);
                if (_voiceIndex is null)
                {
                    using (MakerRequestTrace.Phase("pack.voiceIndex")) _voiceIndex = _store.Files.VoiceIndex(_lang);
                }

                return clips.Read(_lang, voice, _voiceIndex.TryGetValue(voice, out var files) ? files : NoFiles);
            }

            internal MakerStudioGroups Groups()
            {
                var overall = new MakerStudioProgress();
                var progress = new Dictionary<string, MakerStudioProgress>(StringComparer.Ordinal);
                var firstSeen = new List<string>();
                foreach (var row in View.OrderedRows)
                {
                    if (!View.Game.IsStoryLine(row.Id)) continue;
                    var group = View.PrimaryGroupOf(row) ?? UngroupedId;
                    if (!progress.TryGetValue(group, out var bucket))
                    {
                        bucket = new MakerStudioProgress();
                        progress.Add(group, bucket);
                        firstSeen.Add(group);
                    }

                    var ownSubtitle = _states.OwnSubtitle(row.Id);
                    var state = StateOf(row, ownSubtitle);
                    var summary = _states.Summary(row.Id);
                    MakerStudioView.Count(bucket, state, summary.Workflow);
                    MakerStudioView.Count(overall, state, summary.Workflow);
                }

                var groups = new List<MakerStudioGroup>(progress.Count);
                var titles = new Dictionary<string, string?>(StringComparer.Ordinal);
                var gameOrder = new List<string>();
                foreach (var known in View.Game.Groups)
                {
                    titles[known.Id] = known.Title;
                    gameOrder.Add(known.Id);
                }

                var order = MakerStudioView.GroupOrder(firstSeen, gameOrder);
                for (var i = 0; i < order.Count; i++)
                {
                    titles.TryGetValue(order[i], out var title);
                    groups.Add(new MakerStudioGroup { Id = order[i], LabelKey = LabelKeyOf(order[i]), Title = title, Order = i, Progress = progress[order[i]] });
                }

                return new MakerStudioGroups { CatalogRevision = Revision, Overall = overall, Groups = groups };
            }
        }
    }
}
