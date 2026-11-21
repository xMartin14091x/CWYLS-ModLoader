using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Constants frozen by the Studio v2 contract.</summary>
    public static class MakerStudioContract
    {
        public const int Version = 2;
        public const int StorageSchema = 1;
        public const int SubtitleRevisions = 20;
        public const int TakesPerLine = 3;
        public const long ProjectBytes = 2L * 1024 * 1024 * 1024;
        public const string ConsentVersion = "consent-1";

        public const string ConsentEnglish =
            "I confirm this take is my own voice, or I have the speaker's permission to use it in this pack, and the origin I picked is true. I will not present an AI voice as the original voice actor.";

        public const string ConsentThai =
            "ฉันยืนยันว่าเทคนี้เป็นเสียงของฉันเอง หรือได้รับอนุญาตจากเจ้าของเสียงให้ใช้ในแพ็กนี้ และแหล่งที่มาที่เลือกเป็นความจริง จะไม่นำเสียง AI ไปอ้างว่าเป็นเสียงของนักพากย์ต้นฉบับ";
    }

    /// <summary>Stable feature names returned by <c>GET /v1/maker/studio/capabilities</c>.</summary>
    public static class MakerStudioFeatures
    {
        public const string Catalog = "catalog";
        public const string Workflow = "workflow";
        public const string Subtitles = "subtitles";
        public const string Dubs = "dubs";
        public const string Projects = "projects";
        /// <summary>v2.1: pack creation, advertised only when its backend is installed.</summary>
        public const string NewPacks = "newPacks";
        public const string Playback = "playback";
        /// <summary>v2.3: pack details and icon (<c>GET P/info</c>, <c>GET</c>/<c>PUT P/icon</c>).</summary>
        public const string PackInfo = "packInfo";
        /// <summary>v2.3: export checks, version bump, release notes and the shareable zip.</summary>
        public const string Export = "export";
        /// <summary>v2.4: the pack's voice origin and consent, set once and inherited by new takes (<c>GET</c>/<c>PUT P/voice</c>).</summary>
        public const string VoiceDefault = "voiceDefault";
        /// <summary>D-148: labels every unlabelled dub with the pack's voice default in one click (<c>POST P/voice/label</c>).</summary>
        public const string VoiceLabel = "voiceLabel";
    }

    /// <summary>Studio v2 error codes added to the existing Maker error envelope.</summary>
    public static class MakerStudioErrorCodes
    {
        public const string ConsentRequired = "consent_required";
        /// <summary>v2.3, 409: an export check is an error, so no zip is built.</summary>
        public const string ExportBlocked = "export_blocked";
        public const string Conflict = "conflict";
        public const string GamePaused = "game_paused";
        public const string MetadataUnavailable = "metadata_unavailable";
        public const string RecoveryRequired = "recovery_required";
        public const string RenderRequired = "render_required";
        public const string RestartRequired = "restart_required";
        public const string StorageLimit = "storage_limit";
        public const string TakeInUse = "take_in_use";
        public const string TakeLimit = "take_limit";
    }

    /// <summary>
    /// Optional Studio v2 services. Keeping one slot per feature lets later milestones ship independently and
    /// leaves the v1 <see cref="IMakerCatalog"/> and <see cref="IMakerEditor"/> interfaces source-compatible.
    /// </summary>
    public sealed class MakerStudioBackends
    {
        private readonly IReadOnlyList<string> _features;

        /// <summary>Creates an immutable set of Studio services safe to publish to HTTP worker threads.</summary>
        public MakerStudioBackends(
            IMakerStudioStatus? status = null,
            IMakerStudioCatalog? catalog = null,
            IMakerStudioWorkflow? workflow = null,
            IMakerStudioSubtitles? subtitles = null,
            IMakerStudioDubs? dubs = null,
            IMakerStudioProjects? projects = null,
            IMakerStudioPlayback? playback = null,
            IMakerStudioNewPacks? newPacks = null,
            IMakerStudioRecovery? recovery = null,
            IMakerStudioExport? export = null,
            IMakerStudioVoiceDefaults? voiceDefaults = null,
            IMakerStudioVoiceLabels? voiceLabels = null)
        {
            Status = status;
            Catalog = catalog;
            Workflow = workflow;
            Subtitles = subtitles;
            Dubs = dubs;
            Projects = projects;
            Playback = playback;
            NewPacks = newPacks;
            Recovery = recovery;
            Export = export;
            VoiceDefaults = voiceDefaults;
            VoiceLabels = voiceLabels;

            var features = new List<string>(6);
            if (Catalog != null) features.Add(MakerStudioFeatures.Catalog);
            if (Workflow != null) features.Add(MakerStudioFeatures.Workflow);
            if (Subtitles != null) features.Add(MakerStudioFeatures.Subtitles);
            if (Dubs != null) features.Add(MakerStudioFeatures.Dubs);
            if (Projects != null) features.Add(MakerStudioFeatures.Projects);
            if (Playback != null) features.Add(MakerStudioFeatures.Playback);
            if (NewPacks != null) features.Add(MakerStudioFeatures.NewPacks);
            if (Export != null)
            {
                features.Add(MakerStudioFeatures.PackInfo);
                features.Add(MakerStudioFeatures.Export);
            }
            if (VoiceDefaults != null) features.Add(MakerStudioFeatures.VoiceDefault);
            if (VoiceLabels != null) features.Add(MakerStudioFeatures.VoiceLabel);
            _features = features.AsReadOnly();
        }

        /// <summary>
        /// Cross-cutting connection/activity status. It does not advertise a feature by itself; the feature
        /// names below stay coarse-grained and are advertised only when their whole backend is installed.
        /// </summary>
        public IMakerStudioStatus? Status { get; }
        public IMakerStudioCatalog? Catalog { get; }
        public IMakerStudioWorkflow? Workflow { get; }
        public IMakerStudioSubtitles? Subtitles { get; }
        public IMakerStudioDubs? Dubs { get; }
        public IMakerStudioProjects? Projects { get; }
        public IMakerStudioPlayback? Playback { get; }
        /// <summary>Optional pack creation service, independent of existing project storage.</summary>
        public IMakerStudioNewPacks? NewPacks { get; }
        /// <summary>Recovery is part of projects; it does not introduce another feature string.</summary>
        public IMakerStudioRecovery? Recovery { get; }
        /// <summary>v2.3 pack details, icon and export; advertises <c>packInfo</c> and <c>export</c>.</summary>
        public IMakerStudioExport? Export { get; }
        /// <summary>v2.4 pack voice origin and consent; advertises <c>voiceDefault</c>.</summary>
        public IMakerStudioVoiceDefaults? VoiceDefaults { get; }
        /// <summary>D-148 one-click voice labels for dubs made outside Studio; advertises <c>voiceLabel</c>.</summary>
        public IMakerStudioVoiceLabels? VoiceLabels { get; }

        /// <summary>Installed feature names in contract order. Empty means no feature is advertised.</summary>
        public IReadOnlyList<string> Features => _features;
    }

    /// <summary>Cached Studio connection/activity status, available before the wider catalog feature is complete.</summary>
    public interface IMakerStudioStatus
    {
        /// <summary>Returns an already-completed cached result. Must not dispatch, block or start background work.</summary>
        Task<MakerResult<MakerStudioStatus>> StatusAsync(CancellationToken cancel);
    }

    public interface IMakerStudioCatalog
    {
        Task<MakerResult<IReadOnlyList<MakerStudioPack>>> PacksAsync(CancellationToken cancel);
        Task<MakerResult<MakerStudioLinePage>> LinesAsync(MakerStudioLineQuery query, CancellationToken cancel);
        Task<MakerResult<MakerStudioLine>> LineAsync(string packId, string lang, string id, string? occurrenceId, CancellationToken cancel);
        Task<MakerResult<MakerStudioGroups>> GroupsAsync(string packId, string lang, CancellationToken cancel);
    }

    public interface IMakerStudioWorkflow
    {
        Task<MakerResult<MakerStudioWorkflowSaved>> UpdateWorkflowAsync(MakerStudioWorkflowRequest request, CancellationToken cancel);
    }

    public interface IMakerStudioSubtitles
    {
        Task<MakerResult<MakerStudioHistoryPage>> HistoryAsync(string packId, string lang, string id, int offset, int limit, CancellationToken cancel);
        Task<MakerResult<MakerStudioSaved>> SaveSubtitleAsync(MakerStudioSubtitleRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioSaved>> RevertSubtitleAsync(MakerStudioRevertRequest request, CancellationToken cancel);
    }

    public interface IMakerStudioDubs
    {
        Task<MakerResult<MakerStudioTakePage>> TakesAsync(string packId, string lang, string id, CancellationToken cancel);
        Task<MakerResult<MakerStudioAudio>> ReadAudioAsync(string packId, string lang, string id, string takeId, string kind, CancellationToken cancel);
        Task<MakerResult<MakerStudioTakeSaved>> CreateTakeAsync(MakerStudioCreateTakeRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioTakeSaved>> SaveTakeAudioAsync(MakerStudioTakeAudioRequest request, byte[] wav, CancellationToken cancel);
        Task<MakerResult<MakerStudioTakeSaved>> UpdateTakeAsync(MakerStudioUpdateTakeRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioTakeRemoved>> DeleteTakeAsync(MakerStudioDeleteTakeRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioTakeSaved>> SetFavouriteAsync(MakerStudioFavouriteRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioSaved>> PublishAsync(MakerStudioPublishRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioSaved>> UnpublishAsync(MakerStudioUnpublishRequest request, CancellationToken cancel);
    }

    public interface IMakerStudioProjects
    {
        Task<MakerResult<MakerStudioGlossary>> GlossaryAsync(string packId, string lang, CancellationToken cancel);
        Task<MakerResult<MakerStudioGlossary>> SaveGlossaryAsync(MakerStudioGlossaryRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel);
        Task<MakerResult<MakerStudioOperation>> OperationAsync(string packId, string operationId, CancellationToken cancel);
    }

    /// <summary>
    /// v2.4: the pack's voice origin and consent. A take created without its own origin and consent copies this
    /// default at creation time (<c>consentSource:"pack"</c>); later changes do not rewrite existing takes.
    /// A separate interface, so existing fakes of <see cref="IMakerStudioDubs"/> and <see cref="IMakerStudioProjects"/> still compile.
    /// </summary>
    public interface IMakerStudioVoiceDefaults
    {
        Task<MakerResult<MakerStudioVoiceDefault>> VoiceDefaultAsync(string packId, CancellationToken cancel);
        Task<MakerResult<MakerStudioVoiceDefault>> SaveVoiceDefaultAsync(MakerStudioVoiceDefaultRequest request, CancellationToken cancel);
    }

    /// <summary>
    /// D-148: labels every shipped dub that has no valid voice label (and is not a Studio publication) with the pack's voice
    /// default. The creator's click is the confirmation; without a default nothing is labelled.
    /// </summary>
    public interface IMakerStudioVoiceLabels
    {
        Task<MakerResult<MakerStudioVoiceLabelled>> LabelDubsAsync(MakerStudioVoiceLabelRequest request, CancellationToken cancel);
    }

    /// <summary>Creates a new pack; registration enables the v2.1 newPacks capability.</summary>
    public interface IMakerStudioNewPacks
    {
        Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel);
    }

    public interface IMakerStudioRecovery
    {
        Task<MakerResult<MakerRecoveryPreview>> PreviewRecoveryAsync(string packId, string blockedOperationId, CancellationToken cancel);
        Task<MakerResult<MakerRecoveryResolved>> ResolveRecoveryAsync(MakerRecoveryRequest request, CancellationToken cancel);
    }

    public sealed class MakerRecoveryPreview
    {
        public string RecoveryRevision { get; set; } = "";
        public string BlockedOperationId { get; set; } = "";
        public IReadOnlyList<MakerRecoveryResource> Resources { get; set; } = new MakerRecoveryResource[0];
    }

    public sealed class MakerRecoveryResource
    {
        public string Resource { get; set; } = "";
        public string? CurrentFingerprint { get; set; }
        public string? IntendedFingerprint { get; set; }
        public bool CurrentExists { get; set; }
        public bool IntendedExists { get; set; }
    }

    public sealed class MakerRecoveryRequest
    {
        public string PackId { get; set; } = "";
        public string BlockedOperationId { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string ExpectedRecoveryRevision { get; set; } = "";
        public string Strategy { get; set; } = "";
    }

    public sealed class MakerRecoveryResolved
    {
        public bool Resolved { get; set; }
        public string OperationId { get; set; } = "";
        public string BlockedOperationId { get; set; } = "";
        public string Revision { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public bool Live { get; set; }
    }

    public interface IMakerStudioPlayback
    {
        Task<MakerResult<MakerStudioSaved>> PlayAsync(MakerStudioPlayRequest request, CancellationToken cancel);
    }

    public sealed class MakerStudioCapabilities
    {
        public int Contract { get; set; } = MakerStudioContract.Version;
        public IReadOnlyList<string> Features { get; set; } = new string[0];
        public int StorageSchema { get; set; } = MakerStudioContract.StorageSchema;
    }

    public sealed class MakerStudioStatus
    {
        public string GameVersion { get; set; } = string.Empty;
        public string LoaderVersion { get; set; } = string.Empty;
        public string GameLanguage { get; set; } = string.Empty;
        public bool GameLanguageIsPack { get; set; }
        public bool RoomReady { get; set; }
        public bool? Focused { get; set; }
        public bool? Paused { get; set; }
        public bool? Busy { get; set; }
        public string? Reason { get; set; }
        public string? ObservedAt { get; set; }
        public bool Stale { get; set; }
        public bool FocusOnPlay { get; set; }
    }

    public sealed class MakerStudioPack
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public IReadOnlyList<string> Languages { get; set; } = new string[0];
        public bool Editable { get; set; }
        public string? ReadOnlyReason { get; set; }
        public bool Loaded { get; set; }
        public bool RestartRequired { get; set; }
        public bool StudioEditable { get; set; }
        public string? StudioReadOnlyReason { get; set; }
        public string MetadataState { get; set; } = string.Empty;
    }

    public sealed class MakerStudioLineQuery
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string? Search { get; set; }
        public string? Group { get; set; }
        public string Filter { get; set; } = "all";
        public int Offset { get; set; }
        public int Limit { get; set; } = 100;
        public string? CatalogRevision { get; set; }
    }

    public sealed class MakerStudioLinePage
    {
        public int Total { get; set; }
        public int Offset { get; set; }
        public string CatalogRevision { get; set; } = string.Empty;
        public IReadOnlyList<MakerStudioLine> Items { get; set; } = new MakerStudioLine[0];
    }

    public sealed class MakerStudioLine
    {
        public string Id { get; set; } = string.Empty;
        public string? Voice { get; set; }
        public string? Group { get; set; }
        public double? DurationSec { get; set; }
        public string Ja { get; set; } = string.Empty;
        public string En { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? SubtitlePack { get; set; }
        public string? DubPack { get; set; }
        public double? DubDurationSec { get; set; }
        public string? OwnedSubtitle { get; set; }
        public bool OwnedDub { get; set; }
        public MakerStudioWorkflow Workflow { get; set; } = new MakerStudioWorkflow();
        public int TakeCount { get; set; }
        public string Revision { get; set; } = string.Empty;
        public string ClipRevision { get; set; } = string.Empty;
        public string? PublishedTakeId { get; set; }
        public string? PublishedTakeRevision { get; set; }
        public MakerStudioContext Context { get; set; } = new MakerStudioContext();

        /// <summary>Why Studio cannot change this line in any pack (a <see cref="MakerStudioLineReadOnly"/> code), or null.</summary>
        public string? ReadOnly { get; set; }
    }

    /// <summary>The codes of <see cref="MakerStudioLine.ReadOnly"/>; the page words them.</summary>
    public static class MakerStudioLineReadOnly
    {
        /// <summary>
        /// The line id cannot name Studio's file for it (<c>.maker/lines/&lt;lang&gt;/&lt;id&gt;.json</c>): a Windows device
        /// name such as <c>nul</c> or a trailing dot (<see cref="Studio.MakerStudioPaths.CanNameFile"/>). Edits answer 400.
        /// </summary>
        public const string IdNotAFileName = "id_not_a_file_name";
    }

    public sealed class MakerStudioWorkflow
    {
        public string Status { get; set; } = "todo";
        public bool Starred { get; set; }
    }

    public sealed class MakerStudioContext
    {
        public IReadOnlyList<MakerStudioOccurrence> Occurrences { get; set; } = new MakerStudioOccurrence[0];
        public string? SelectedOccurrenceId { get; set; }
        public string? Mood { get; set; }
        public string? Pose { get; set; }
        public string? Source { get; set; }
    }

    public sealed class MakerStudioOccurrence
    {
        public string OccurrenceId { get; set; } = string.Empty;
        public string? GroupId { get; set; }
        public int Order { get; set; }
        public string? PreviousId { get; set; }
        public string? NextId { get; set; }
        public string? Talker { get; set; }
        public string? BodyMotion { get; set; }
        public string? FacialMotion { get; set; }
    }

    public sealed class MakerStudioProgress
    {
        public int TotalLines { get; set; }
        public int DubbableLines { get; set; }
        public int SubtitlePresent { get; set; }
        public int DubPresent { get; set; }
        public int Todo { get; set; }
        public int Draft { get; set; }
        public int Done { get; set; }
        public int NeedsRetake { get; set; }
        public int Starred { get; set; }
    }

    public sealed class MakerStudioGroups
    {
        public string CatalogRevision { get; set; } = string.Empty;
        public MakerStudioProgress Overall { get; set; } = new MakerStudioProgress();
        public IReadOnlyList<MakerStudioGroup> Groups { get; set; } = new MakerStudioGroup[0];
    }

    public sealed class MakerStudioGroup
    {
        public string Id { get; set; } = string.Empty;
        public string? LabelKey { get; set; }
        public string? Title { get; set; }
        public int Order { get; set; }
        public MakerStudioProgress Progress { get; set; } = new MakerStudioProgress();
    }

    public sealed class MakerStudioHistoryPage
    {
        public string Revision { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Offset { get; set; }
        public IReadOnlyList<MakerStudioHistoryItem> Items { get; set; } = new MakerStudioHistoryItem[0];
        public bool HistoryGap { get; set; }
    }

    public sealed class MakerStudioHistoryItem
    {
        public string RevisionId { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string? Text { get; set; }
        public string Source { get; set; } = string.Empty;
    }

    public sealed class MakerStudioTakePage
    {
        public string Revision { get; set; } = string.Empty;
        public string? FavouriteTakeId { get; set; }
        public string? PublishedTakeId { get; set; }
        /// <summary>The line's clip revision for publish/unpublish; null when the catalog cannot name the clip right now.</summary>
        public string? ClipRevision { get; set; }
        public IReadOnlyList<MakerStudioTake> Items { get; set; } = new MakerStudioTake[0];
    }

    public sealed class MakerStudioTake
    {
        public string Id { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public MakerStudioConsent Consent { get; set; } = new MakerStudioConsent();
        public string? SourceRevision { get; set; }
        public double? SourceDurationSec { get; set; }
        public double? RenderDurationSec { get; set; }
        public double? TrimStartSec { get; set; }
        public double? TrimEndSec { get; set; }
        public bool EvenOutVolume { get; set; }
        public bool SourcePresent { get; set; }
        public bool RenderReady { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        /// <summary>v2.4: <c>take</c> when the take carries its own origin and consent, <c>pack</c> when it copied the pack default.</summary>
        public string ConsentSource { get; set; } = MakerStudioConsentSources.Take;
    }

    /// <summary>v2.4 values of <see cref="MakerStudioTake.ConsentSource"/>.</summary>
    public static class MakerStudioConsentSources
    {
        public const string Take = "take";
        public const string Pack = "pack";
    }

    /// <summary>v2.4: the pack's voice origin and consent. Origin and consent are both null when no default is set.</summary>
    public sealed class MakerStudioVoiceDefault
    {
        public string Revision { get; set; } = string.Empty;
        public string? Origin { get; set; }
        public MakerStudioConsent? Consent { get; set; }
        public string? UpdatedAt { get; set; }
    }

    /// <summary>v2.4: sets (origin and consent) or clears (both null) the pack's voice default.</summary>
    public sealed class MakerStudioVoiceDefaultRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string? Origin { get; set; }
        public MakerStudioConsent? Consent { get; set; }
    }

    /// <summary>
    /// D-148: labels the pack's unlabelled dubs. <see cref="MakerStudioMutation.ExpectedRevision"/> is the voice default's revision
    /// and <see cref="Origin"/> its origin, as the page showed them; the consent must be a confirmed <c>consent-1</c>.
    /// </summary>
    public sealed class MakerStudioVoiceLabelRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public MakerStudioConsent Consent { get; set; } = new MakerStudioConsent();
    }

    /// <summary>D-148: what one click labelled.</summary>
    public sealed class MakerStudioVoiceLabelled
    {
        public string OperationId { get; set; } = string.Empty;
        /// <summary>The voice default's revision (unchanged by labelling).</summary>
        public string Revision { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        /// <summary>How many dubs were labelled (0 when none needed it).</summary>
        public int Labelled { get; set; }
        /// <summary>The labelled dub files (pack-relative, at most 20).</summary>
        public List<string> Paths { get; set; } = new List<string>();
    }

    public sealed class MakerStudioConsent
    {
        public bool Confirmed { get; set; }
        public string StatementVersion { get; set; } = MakerStudioContract.ConsentVersion;
        public string? ConfirmedAt { get; set; }
    }

    public sealed class MakerStudioAudio
    {
        public byte[] Bytes { get; set; } = new byte[0];
        public string Kind { get; set; } = string.Empty;
        public double DurationSec { get; set; }
    }

    public sealed class MakerStudioGlossary
    {
        public string Revision { get; set; } = string.Empty;
        public IReadOnlyList<MakerStudioGlossaryEntry> Entries { get; set; } = new MakerStudioGlossaryEntry[0];
    }

    public sealed class MakerStudioGlossaryEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? SourceJa { get; set; }
        public string? SourceEn { get; set; }
        public string Target { get; set; } = string.Empty;
        public string? Note { get; set; }
    }

    public sealed class MakerStudioSaved
    {
        public string Revision { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public bool Saved { get; set; }
        public bool Live { get; set; }
        public string? Path { get; set; }
        public double? DurationSec { get; set; }
        public string Note { get; set; } = string.Empty;
        public string? PublishedTakeId { get; set; }
        public string? EffectivePackId { get; set; }
        public bool RestartRequired { get; set; }
    }

    public sealed class MakerStudioOperation
    {
        public string State { get; set; } = string.Empty;
        public string? Revision { get; set; }
        public bool Saved { get; set; }
        public bool RecoveryRequired { get; set; }
    }

    public sealed class MakerStudioPackCreated
    {
        public MakerStudioPack Pack { get; set; } = new MakerStudioPack();
        public bool RestartRequired { get; set; } = true;
        public bool Live { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    public sealed class MakerStudioWorkflowRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? Status { get; set; }
        public bool? Starred { get; set; }
    }

    public sealed class MakerStudioWorkflowSaved
    {
        public string Revision { get; set; } = string.Empty;
        public MakerStudioWorkflow Workflow { get; set; } = new MakerStudioWorkflow();
    }

    public sealed class MakerStudioSubtitleRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? Text { get; set; }
    }

    public sealed class MakerStudioRevertRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string RevisionId { get; set; } = string.Empty;
    }

    public sealed class MakerStudioCreateTakeRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        /// <summary>Null together with <see cref="Consent"/>: the take inherits the pack's voice default (v2.4).</summary>
        public string? Origin { get; set; }
        /// <summary>Null together with <see cref="Origin"/>: the take inherits the pack's voice default (v2.4).</summary>
        public MakerStudioConsent? Consent { get; set; }
    }

    public sealed class MakerStudioTakeAudioRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string TakeId { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
    }

    public sealed class MakerStudioUpdateTakeRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string TakeId { get; set; } = string.Empty;
        public string? Origin { get; set; }
        public MakerStudioConsent? Consent { get; set; }
        /// <summary>True when the patch contained <c>trimStartSec</c>; the value may deliberately be null to clear it.</summary>
        public bool HasTrimStartSec { get; set; }
        public double? TrimStartSec { get; set; }
        /// <summary>True when the patch contained <c>trimEndSec</c>; the value may deliberately be null to clear it.</summary>
        public bool HasTrimEndSec { get; set; }
        public double? TrimEndSec { get; set; }
        public bool? EvenOutVolume { get; set; }
        /// <summary>v2.5.1: <c>followPackDefault:true</c> puts the take back on the pack voice default (<c>consentSource:"pack"</c>).</summary>
        public bool FollowPackDefault { get; set; }
    }

    public sealed class MakerStudioDeleteTakeRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string TakeId { get; set; } = string.Empty;
    }

    public sealed class MakerStudioFavouriteRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string? TakeId { get; set; }
    }

    public sealed class MakerStudioPublishRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string TakeId { get; set; } = string.Empty;
        public string ExpectedClipRevision { get; set; } = string.Empty;
    }

    public sealed class MakerStudioUnpublishRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string ExpectedClipRevision { get; set; } = string.Empty;
    }

    public sealed class MakerStudioTakeSaved
    {
        public string Revision { get; set; } = string.Empty;
        public MakerStudioTake? Take { get; set; }
        public string? FavouriteTakeId { get; set; }
    }

    /// <summary>The exact response of deleting a take.</summary>
    public sealed class MakerStudioTakeRemoved
    {
        public string Revision { get; set; } = string.Empty;
        public bool Removed { get; set; }
    }

    public sealed class MakerStudioGlossaryRequest : MakerStudioMutation
    {
        public string PackId { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public IReadOnlyList<MakerStudioGlossaryEntry> Entries { get; set; } = new MakerStudioGlossaryEntry[0];
    }

    public sealed class MakerStudioCreatePackRequest
    {
        public string OperationId { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Author { get; set; }
        /// <summary>v2.3: optional manifest description (at most 500 characters).</summary>
        public string? Description { get; set; }
        public MakerStudioLanguage Language { get; set; } = new MakerStudioLanguage();
    }

    public sealed class MakerStudioLanguage
    {
        public string Code { get; set; } = string.Empty;
        public string? Caption { get; set; }
        public string? EnglishName { get; set; }
        public string? Script { get; set; }
        public IReadOnlyList<string>? Fallback { get; set; }
    }

    /// <summary>Transient Studio playback; never implicitly saves text or publishes a take.</summary>
    public sealed class MakerStudioPlayRequest
    {
        /// <summary>The voiced story line to play; normal story and DLC scope checks apply.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary><c>original</c>: game voice and text; <c>dub</c>: pack voice and subtitle;
        /// <c>sub</c>: original voice with the selected pack's current subtitle for <see cref="Lang"/>.
        /// Subtitle lookup falls back to the effective subtitle, then the game's text, as with <c>dub</c>.</summary>
        public string Variant { get; set; } = string.Empty;
        /// <summary>Subtitle and dub language; aliases are canonicalized before playback.</summary>
        public string Lang { get; set; } = string.Empty;
        /// <summary>Selected loaded pack; required and editable for <c>sub</c>. Pending packs require a restart.</summary>
        public string? PackId { get; set; }
        /// <summary>Optional line revision precondition; requires <see cref="PackId"/>.</summary>
        public string? ExpectedRevision { get; set; }
    }

    public abstract class MakerStudioMutation
    {
        public string ExpectedRevision { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
    }
}
