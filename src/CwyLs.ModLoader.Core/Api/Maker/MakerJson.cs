using System.Collections.Generic;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// The wire shapes of the maker routes (CONTRACT.md "Routes"), member by member in contract order.
    /// Every member is chosen here explicitly, so a new property on a DTO never reaches the browser by accident.
    /// </summary>
    public static class MakerJson
    {
        /// <summary><c>{gameVersion, loaderVersion, gameLanguage, gameLanguageIsPack, roomReady}</c>.</summary>
        /// <param name="s">The status.</param>
        public static JsonObject Status(MakerStatus s) => new JsonObject
        {
            ["gameVersion"] = s.GameVersion ?? string.Empty,
            ["loaderVersion"] = s.LoaderVersion ?? string.Empty,
            ["gameLanguage"] = s.GameLanguage ?? string.Empty,
            ["gameLanguageIsPack"] = s.GameLanguageIsPack,
            ["roomReady"] = s.RoomReady,
        };

        /// <summary><c>{packs:[{id, name, version, languages[], editable, readOnlyReason}]}</c>.</summary>
        /// <param name="packs">The packs.</param>
        public static JsonObject Packs(IReadOnlyList<MakerPack> packs)
        {
            var list = new List<object?>(packs.Count);
            foreach (var p in packs)
            {
                if (p is null)
                {
                    continue;
                }

                var languages = new List<object?>();
                foreach (var code in p.Languages ?? new string[0])
                {
                    languages.Add(code);
                }

                list.Add(new JsonObject
                {
                    ["id"] = p.Id ?? string.Empty,
                    ["name"] = p.Name ?? string.Empty,
                    ["version"] = p.Version ?? string.Empty,
                    ["languages"] = languages,
                    ["editable"] = p.Editable,
                    ["readOnlyReason"] = p.ReadOnlyReason,
                });
            }

            return new JsonObject { ["packs"] = list };
        }

        /// <summary><c>{total, offset, items:[Line]}</c>.</summary>
        /// <param name="page">The page.</param>
        public static JsonObject LinePage(MakerLinePage page)
        {
            var items = new List<object?>(page.Items?.Count ?? 0);
            foreach (var line in page.Items ?? new MakerLine[0])
            {
                if (line != null)
                {
                    items.Add(Line(line));
                }
            }

            return new JsonObject { ["total"] = page.Total, ["offset"] = page.Offset, ["items"] = items };
        }

        /// <summary><c>{id, voice, group, durationSec, ja, en, subtitle, subtitlePack, dubPack, dubDurationSec}</c>.</summary>
        /// <param name="l">The line.</param>
        public static JsonObject Line(MakerLine l) => new JsonObject
        {
            ["id"] = l.Id ?? string.Empty,
            ["voice"] = l.Voice,
            ["group"] = l.Group,
            ["durationSec"] = l.DurationSec,
            ["ja"] = l.Ja ?? string.Empty,
            ["en"] = l.En ?? string.Empty,
            ["subtitle"] = l.Subtitle,
            ["subtitlePack"] = l.SubtitlePack,
            ["dubPack"] = l.DubPack,
            ["dubDurationSec"] = l.DubDurationSec,
        };

        /// <summary><c>{live, path, durationSec, note}</c>.</summary>
        /// <param name="s">What a save or play did.</param>
        public static JsonObject Saved(MakerSaved s) => new JsonObject
        {
            ["live"] = s.Live,
            ["path"] = s.Path,
            ["durationSec"] = s.DurationSec,
            ["note"] = s.Note ?? string.Empty,
        };
    }
}
