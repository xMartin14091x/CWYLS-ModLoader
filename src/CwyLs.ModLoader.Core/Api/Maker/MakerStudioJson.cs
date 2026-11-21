using System.Collections.Generic;
using System.Linq;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>Explicit Studio v2 wire shapes. M2 exposes capabilities; later milestones add the feature shapes here.</summary>
    public static class MakerStudioJson
    {
        public static JsonObject RecoveryPreview(MakerRecoveryPreview value)
        {
            var resources = new List<object?>();
            foreach (var r in value.Resources) resources.Add(new JsonObject
            {
                ["resource"] = r.Resource, ["currentFingerprint"] = r.CurrentFingerprint,
                ["intendedFingerprint"] = r.IntendedFingerprint, ["currentExists"] = r.CurrentExists,
                ["intendedExists"] = r.IntendedExists,
            });
            return new JsonObject { ["recoveryRevision"] = value.RecoveryRevision,
                ["blockedOperationId"] = value.BlockedOperationId, ["resources"] = resources };
        }

        public static JsonObject RecoveryResolved(MakerRecoveryResolved value) => new JsonObject
        {
            ["resolved"] = value.Resolved, ["operationId"] = value.OperationId,
            ["blockedOperationId"] = value.BlockedOperationId, ["revision"] = value.Revision,
            ["archivePath"] = value.ArchivePath, ["live"] = value.Live,
        };
        public static ApiResponse ErrorResponse(int status, string code, string message, MakerRecoveryDetails? details)
        {
            if (details is null) return ApiResponse.Error(status, code, message);
            var error = new JsonObject
            {
                ["code"] = code, ["message"] = message,
                ["details"] = new JsonObject
                {
                    ["packId"] = details.PackId, ["resource"] = details.Resource,
                    ["blockedOperationId"] = details.BlockedOperationId,
                },
            };
            return new ApiResponse(status, ApiJson.Write(new JsonObject { ["error"] = error }), errorCode: code);
        }
        /// <summary>The capability document. An absent backend is 503; an installed empty backend reports no features.</summary>
        public static JsonObject Capabilities(MakerStudioCapabilities capabilities)
        {
            var features = new List<object?>();
            foreach (var feature in capabilities.Features ?? new string[0])
            {
                features.Add(feature);
            }

            return new JsonObject
            {
                ["contract"] = capabilities.Contract,
                ["features"] = features,
                // features remains the contract's installed-backend array; UI text is a separate scope capability.
                ["uiText"] = false,
                ["limits"] = new JsonObject
                {
                    ["jsonBytes"] = MakerDefaults.MaxJsonBytes,
                    ["wavBytes"] = MakerDefaults.MaxWavBytes,
                    ["wavSeconds"] = MakerDefaults.MaxWavSeconds,
                    ["subtitleRevisions"] = MakerStudioContract.SubtitleRevisions,
                    ["takesPerLine"] = MakerStudioContract.TakesPerLine,
                    ["projectBytes"] = MakerStudioContract.ProjectBytes,
                },
                ["storageSchema"] = capabilities.StorageSchema,
                ["consentVersion"] = MakerStudioContract.ConsentVersion,
            };
        }

        /// <summary>Cached game and activity status. Nullable facts stay null; staleness is explicit.</summary>
        public static JsonObject Status(MakerStudioStatus status) => new JsonObject
        {
            ["gameVersion"] = status.GameVersion ?? string.Empty,
            ["loaderVersion"] = status.LoaderVersion ?? string.Empty,
            ["gameLanguage"] = status.GameLanguage ?? string.Empty,
            ["gameLanguageIsPack"] = status.GameLanguageIsPack,
            ["roomReady"] = status.RoomReady,
            ["focused"] = status.Focused,
            ["paused"] = status.Paused,
            ["busy"] = status.Busy,
            ["reason"] = status.Reason,
            ["observedAt"] = status.ObservedAt,
            ["stale"] = status.Stale,
            ["focusOnPlay"] = status.FocusOnPlay,
        };

        public static JsonObject WorkflowSaved(MakerStudioWorkflowSaved saved) => new JsonObject
        {
            ["revision"] = saved.Revision,
            ["workflow"] = new JsonObject { ["status"] = saved.Workflow.Status, ["starred"] = saved.Workflow.Starred },
        };

        public static JsonObject Saved(MakerStudioSaved saved) => new JsonObject
        {
            ["revision"] = saved.Revision,
            ["operationId"] = saved.OperationId,
            ["saved"] = saved.Saved,
            ["live"] = saved.Live,
            ["path"] = saved.Path,
            ["durationSec"] = saved.DurationSec,
            ["note"] = saved.Note,
            ["publishedTakeId"] = saved.PublishedTakeId,
            ["effectivePackId"] = saved.EffectivePackId,
            ["restartRequired"] = saved.RestartRequired,
        };

        public static JsonObject History(MakerStudioHistoryPage page)
        {
            var items = new List<object?>();
            foreach (var item in page.Items)
            {
                items.Add(new JsonObject
                {
                    ["revisionId"] = item.RevisionId,
                    ["createdAt"] = item.CreatedAt,
                    ["text"] = item.Text,
                    ["source"] = item.Source,
                });
            }

            return new JsonObject
            {
                ["revision"] = page.Revision, ["total"] = page.Total, ["offset"] = page.Offset,
                ["items"] = items, ["historyGap"] = page.HistoryGap,
            };
        }

        public static JsonObject Glossary(MakerStudioGlossary glossary)
        {
            var entries = new List<object?>();
            foreach (var entry in glossary.Entries)
            {
                entries.Add(new JsonObject
                {
                    ["id"] = entry.Id, ["sourceJa"] = entry.SourceJa, ["sourceEn"] = entry.SourceEn,
                    ["target"] = entry.Target, ["note"] = entry.Note,
                });
            }

            return new JsonObject { ["revision"] = glossary.Revision, ["entries"] = entries };
        }

        public static JsonObject Operation(MakerStudioOperation operation) => new JsonObject
        {
            ["state"] = operation.State,
            ["revision"] = operation.Revision,
            ["saved"] = operation.Saved,
            ["recoveryRequired"] = operation.RecoveryRequired,
        };

        public static JsonObject Take(MakerStudioTake take) => new JsonObject
        {
            ["id"] = take.Id,
            ["origin"] = take.Origin,
            ["consent"] = new JsonObject
            {
                ["confirmed"] = take.Consent.Confirmed,
                ["statementVersion"] = take.Consent.StatementVersion,
                ["confirmedAt"] = take.Consent.ConfirmedAt,
            },
            ["sourceRevision"] = take.SourceRevision,
            ["sourceDurationSec"] = take.SourceDurationSec,
            ["renderDurationSec"] = take.RenderDurationSec,
            ["trimStartSec"] = take.TrimStartSec,
            ["trimEndSec"] = take.TrimEndSec,
            ["evenOutVolume"] = take.EvenOutVolume,
            ["sourcePresent"] = take.SourcePresent,
            ["renderReady"] = take.RenderReady,
            ["createdAt"] = take.CreatedAt,
            ["updatedAt"] = take.UpdatedAt,
            ["consentSource"] = take.ConsentSource,
        };

        /// <summary>v2.4 <c>GET</c>/<c>PUT P/voice</c>: origin and consent are null when no default is set.</summary>
        /// <summary>D-148: the one-click label answer.</summary>
        public static JsonObject VoiceLabelled(MakerStudioVoiceLabelled value) => new JsonObject
        {
            ["operationId"] = value.OperationId,
            ["revision"] = value.Revision,
            ["origin"] = value.Origin,
            ["labelled"] = (long)value.Labelled,
            ["paths"] = value.Paths.Select(p => (object?)p).ToList(),
        };

        public static JsonObject VoiceDefault(MakerStudioVoiceDefault value) => new JsonObject
        {
            ["revision"] = value.Revision,
            ["origin"] = value.Origin,
            ["consent"] = value.Consent is null ? null : new JsonObject
            {
                ["confirmed"] = value.Consent.Confirmed,
                ["statementVersion"] = value.Consent.StatementVersion,
                ["confirmedAt"] = value.Consent.ConfirmedAt,
            },
            ["updatedAt"] = value.UpdatedAt,
        };

        public static JsonObject TakePage(MakerStudioTakePage page)
        {
            var items = new List<object?>();
            foreach (var item in page.Items)
            {
                items.Add(Take(item));
            }

            return new JsonObject
            {
                ["revision"] = page.Revision,
                ["favouriteTakeId"] = page.FavouriteTakeId,
                ["publishedTakeId"] = page.PublishedTakeId,
                ["clipRevision"] = page.ClipRevision,
                ["items"] = items,
            };
        }

        public static JsonObject TakeSaved(MakerStudioTakeSaved saved) =>
            saved.Take != null
                ? new JsonObject { ["revision"] = saved.Revision, ["take"] = Take(saved.Take) }
                : new JsonObject { ["revision"] = saved.Revision, ["favouriteTakeId"] = saved.FavouriteTakeId };

        public static JsonObject TakeRemoved(MakerStudioTakeRemoved removed) => new JsonObject
        {
            ["revision"] = removed.Revision,
            ["removed"] = removed.Removed,
        };
    }
}
