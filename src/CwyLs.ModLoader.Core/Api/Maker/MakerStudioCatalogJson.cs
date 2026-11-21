using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>The Studio v2 catalog and pack-creation wire shapes, in the member order of CONTRACT-v2 section 4 and 5.</summary>
    public static class MakerStudioCatalogJson
    {
        /// <summary><c>StudioPack</c>.</summary>
        public static JsonObject Pack(MakerStudioPack pack)
        {
            var languages = new List<object?>();
            foreach (var language in pack.Languages) languages.Add(language);
            return new JsonObject
            {
                ["id"] = pack.Id, ["name"] = pack.Name, ["version"] = pack.Version, ["languages"] = languages,
                ["editable"] = pack.Editable, ["readOnlyReason"] = pack.ReadOnlyReason,
                ["loaded"] = pack.Loaded, ["restartRequired"] = pack.RestartRequired,
                ["studioEditable"] = pack.StudioEditable, ["studioReadOnlyReason"] = pack.StudioReadOnlyReason,
                ["metadataState"] = pack.MetadataState,
            };
        }

        /// <summary><c>{packs:[StudioPack]}</c>.</summary>
        public static JsonObject Packs(IReadOnlyList<MakerStudioPack> packs)
        {
            var items = new List<object?>();
            foreach (var pack in packs) items.Add(Pack(pack));
            return new JsonObject { ["packs"] = items };
        }

        /// <summary><c>PackCreated</c>.</summary>
        public static JsonObject PackCreated(MakerStudioPackCreated created) => new JsonObject
        {
            ["pack"] = Pack(created.Pack), ["restartRequired"] = created.RestartRequired, ["live"] = created.Live, ["note"] = created.Note,
        };

        /// <summary>Reads a <c>PackCreated</c> back from a stored receipt; false when a member is missing or mistyped.</summary>
        public static bool TryPackCreated(JsonObject json, out MakerStudioPackCreated created)
        {
            created = new MakerStudioPackCreated();
            if (!(json.TryGetValue("pack", out var packValue) && packValue is JsonObject pack)
                || !(json.TryGetValue("restartRequired", out var restart) && restart is bool restartRequired)
                || !(json.TryGetValue("live", out var live) && live is bool liveNow)
                || !(json.TryGetValue("note", out var note) && note is string noteText)
                || !TryPack(pack, out var studioPack))
            {
                return false;
            }

            created = new MakerStudioPackCreated { Pack = studioPack, RestartRequired = restartRequired, Live = liveNow, Note = noteText };
            return true;
        }

        private static bool TryPack(JsonObject json, out MakerStudioPack pack)
        {
            pack = new MakerStudioPack();
            if (!(json.TryGetValue("id", out var id) && id is string idText)
                || !(json.TryGetValue("name", out var name) && name is string nameText)
                || !(json.TryGetValue("version", out var version) && version is string versionText)
                || !(json.TryGetValue("languages", out var languageValue) && languageValue is IList<object?> languageList)
                || !(json.TryGetValue("editable", out var editable) && editable is bool editableFlag)
                || !(json.TryGetValue("loaded", out var loaded) && loaded is bool loadedFlag)
                || !(json.TryGetValue("restartRequired", out var restart) && restart is bool restartFlag)
                || !(json.TryGetValue("studioEditable", out var studio) && studio is bool studioFlag)
                || !(json.TryGetValue("metadataState", out var state) && state is string stateText))
            {
                return false;
            }

            var languages = new List<string>();
            foreach (var item in languageList)
            {
                if (!(item is string code)) return false;
                languages.Add(code);
            }

            json.TryGetValue("readOnlyReason", out var reason);
            json.TryGetValue("studioReadOnlyReason", out var studioReason);
            pack = new MakerStudioPack
            {
                Id = idText, Name = nameText, Version = versionText, Languages = languages, Editable = editableFlag,
                ReadOnlyReason = reason as string, Loaded = loadedFlag, RestartRequired = restartFlag,
                StudioEditable = studioFlag, StudioReadOnlyReason = studioReason as string, MetadataState = stateText,
            };
            return true;
        }

        /// <summary><c>context</c>.</summary>
        public static JsonObject Context(MakerStudioContext context)
        {
            var occurrences = new List<object?>();
            foreach (var o in context.Occurrences)
            {
                occurrences.Add(new JsonObject
                {
                    ["occurrenceId"] = o.OccurrenceId, ["groupId"] = o.GroupId, ["order"] = o.Order,
                    ["previousId"] = o.PreviousId, ["nextId"] = o.NextId, ["talker"] = o.Talker,
                    ["bodyMotion"] = o.BodyMotion, ["facialMotion"] = o.FacialMotion,
                });
            }

            return new JsonObject
            {
                ["occurrences"] = occurrences, ["selectedOccurrenceId"] = context.SelectedOccurrenceId,
                ["mood"] = context.Mood, ["pose"] = context.Pose, ["source"] = context.Source,
            };
        }

        /// <summary><c>StudioLine</c>.</summary>
        public static JsonObject Line(MakerStudioLine line) => new JsonObject
        {
            ["id"] = line.Id, ["voice"] = line.Voice, ["group"] = line.Group, ["durationSec"] = line.DurationSec,
            ["ja"] = line.Ja, ["en"] = line.En, ["subtitle"] = line.Subtitle, ["subtitlePack"] = line.SubtitlePack,
            ["dubPack"] = line.DubPack, ["dubDurationSec"] = line.DubDurationSec,
            ["ownedSubtitle"] = line.OwnedSubtitle, ["ownedDub"] = line.OwnedDub,
            ["workflow"] = new JsonObject { ["status"] = line.Workflow.Status, ["starred"] = line.Workflow.Starred },
            ["takeCount"] = line.TakeCount, ["revision"] = line.Revision, ["clipRevision"] = line.ClipRevision,
            ["publishedTakeId"] = line.PublishedTakeId, ["publishedTakeRevision"] = line.PublishedTakeRevision,
            ["context"] = Context(line.Context), ["readOnly"] = line.ReadOnly,
        };

        /// <summary><c>StudioLinePage</c>.</summary>
        public static JsonObject LinePage(MakerStudioLinePage page)
        {
            var items = new List<object?>();
            foreach (var line in page.Items) items.Add(Line(line));
            return new JsonObject
            {
                ["total"] = page.Total, ["offset"] = page.Offset, ["catalogRevision"] = page.CatalogRevision, ["items"] = items,
            };
        }

        /// <summary><c>Progress</c>.</summary>
        public static JsonObject Progress(MakerStudioProgress p) => new JsonObject
        {
            ["totalLines"] = p.TotalLines, ["dubbableLines"] = p.DubbableLines, ["subtitlePresent"] = p.SubtitlePresent,
            ["dubPresent"] = p.DubPresent, ["todo"] = p.Todo, ["draft"] = p.Draft, ["done"] = p.Done,
            ["needsRetake"] = p.NeedsRetake, ["starred"] = p.Starred,
        };

        /// <summary><c>Groups</c>.</summary>
        public static JsonObject Groups(MakerStudioGroups groups)
        {
            var items = new List<object?>();
            foreach (var g in groups.Groups)
            {
                items.Add(new JsonObject
                {
                    ["id"] = g.Id, ["labelKey"] = g.LabelKey, ["title"] = g.Title, ["order"] = g.Order, ["progress"] = Progress(g.Progress),
                });
            }

            return new JsonObject { ["catalogRevision"] = groups.CatalogRevision, ["overall"] = Progress(groups.Overall), ["groups"] = items };
        }
    }
}
