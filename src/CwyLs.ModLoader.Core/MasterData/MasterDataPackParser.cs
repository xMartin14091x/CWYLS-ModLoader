using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// Reads the three master-data files a content pack may ship and turns them into rows.
    /// <para>
    /// Every file is a JSON <em>array</em> of objects. Member names are the game's own column
    /// names matched case-insensitively, so <c>ID</c>, <c>Id</c> and <c>id</c> are the same member
    /// and <c>ScenarioGroupID</c> and <c>scenarioGroupId</c> are too. A member the schema does not
    /// know is refused - a typo must not fail silently - unless its name starts with
    /// <see cref="ExtensionPrefix"/>, which is reserved for a pack's own notes and is ignored.
    /// </para>
    /// <para>
    /// Nothing here throws for bad content. A malformed document, a row that is not an object, a
    /// missing id: each becomes a <see cref="MasterDataRejection"/> naming the file and the row
    /// index, and the rest of the file still loads.
    /// </para>
    /// </summary>
    public static class MasterDataPackParser
    {
        /// <summary>Pack-relative path of the localization file.</summary>
        public const string LocalizationFile = "data/localization.json";

        /// <summary>Pack-relative path of the novel file.</summary>
        public const string NovelFile = "data/novel.json";

        /// <summary>Pack-relative path of the scenario-group file.</summary>
        public const string ScenarioGroupsFile = "data/scenario_groups.json";

        /// <summary>Member-name prefix reserved for a pack's own annotations; never validated.</summary>
        public const string ExtensionPrefix = "x-";

        private static readonly string[] LocalizationMembers =
        {
            "ID", "Ja", "En", "ZhHans", "ZhHant", "Pt", "Ko", "Ru", "Voice"
        };

        private static readonly string[] NovelMembers =
        {
            "ID", "ScenarioGroupID", "Command", "Arg1", "Arg2", "Arg3", "Arg4", "Talker",
            "LocalizationID", "BodyMotion", "FacialMotion", "LookScale", "LookSpeedSeconds", "LookEaseType"
        };

        // "Languages" and "Pool" are CWY members, not game columns: they mark a group as
        // a pack line exclusive to some languages (ScenarioGroupRow.Languages / .Pool). A file that
        // does not use them parses to exactly the rows it did before they existed.
        private static readonly string[] ScenarioGroupMembers =
        {
            "ID", "TitleLocalizationID", "SubtitleLocalizationID", "Scenario", "EpisodeNumber",
            "UnlockLevel", "UnlockStoryEpisode", "PlayTiming", "ContentType", "Arg1", "Arg2", "Arg3",
            "Languages", "Pool"
        };

        /// <summary>Parses <c>data/localization.json</c>.</summary>
        /// <param name="json">File contents; null or blank is reported as an empty file.</param>
        /// <param name="file">Display path used in rejections, e.g. <c>cwy.example/data/localization.json</c>.</param>
        public static MasterDataParse<LocalizationRow> ParseLocalization(string? json, string file) =>
            Parse(json, file, LocalizationMembers, ReadLocalization);

        /// <summary>Parses <c>data/novel.json</c>.</summary>
        /// <param name="json">File contents; null or blank is reported as an empty file.</param>
        /// <param name="file">Display path used in rejections.</param>
        public static MasterDataParse<NovelRow> ParseNovel(string? json, string file) =>
            Parse(json, file, NovelMembers, ReadNovel);

        /// <summary>Parses <c>data/scenario_groups.json</c>.</summary>
        /// <param name="json">File contents; null or blank is reported as an empty file.</param>
        /// <param name="file">Display path used in rejections.</param>
        public static MasterDataParse<ScenarioGroupRow> ParseScenarioGroups(string? json, string file) =>
            Parse(json, file, ScenarioGroupMembers, ReadScenarioGroup);

        private static MasterDataParse<T> Parse<T>(
            string? json, string file, string[] knownMembers, Func<RowReader, T?> read)
            where T : class
        {
            var rows = new List<T>();
            var rejected = new List<MasterDataRejection>();

            if (string.IsNullOrWhiteSpace(json))
            {
                rejected.Add(new MasterDataRejection(
                    file, MasterDataRejection.WholeFile, null, "the file is empty"));
                return new MasterDataParse<T>(file, rows, rejected);
            }

            if (!JsonReader.TryParse(json!, out var document, out var error))
            {
                rejected.Add(new MasterDataRejection(file, MasterDataRejection.WholeFile, null, error!));
                return new MasterDataParse<T>(file, rows, rejected);
            }

            if (!(document is IList<object?> array))
            {
                rejected.Add(new MasterDataRejection(
                    file,
                    MasterDataRejection.WholeFile,
                    null,
                    "the top-level value must be an array of rows, not " + JsonReader.TypeName(document)));
                return new MasterDataParse<T>(file, rows, rejected);
            }

            for (var i = 0; i < array.Count; i++)
            {
                if (!(array[i] is JsonObject obj))
                {
                    rejected.Add(new MasterDataRejection(
                        file, i, null, "a row must be an object, not " + JsonReader.TypeName(array[i])));
                    continue;
                }

                var reader = new RowReader(obj, file, i, rejected, knownMembers);
                var row = read(reader);

                // A row is kept only when nothing in it was refused: half a row is worse than none,
                // because the game would play it with silently wrong values.
                if (reader.Ok && row != null)
                {
                    rows.Add(row);
                }
            }

            return new MasterDataParse<T>(file, rows, rejected);
        }

        private static LocalizationRow? ReadLocalization(RowReader row)
        {
            var id = row.RequiredString("ID");
            var ja = row.OptionalString("Ja", string.Empty);
            var en = row.OptionalString("En", string.Empty);

            return new LocalizationRow
            {
                Id = id,
                Ja = ja,
                En = en,
                ZhHans = row.OptionalString("ZhHans", string.Empty),
                ZhHant = row.OptionalString("ZhHant", string.Empty),
                Pt = row.OptionalString("Pt", string.Empty),
                Ko = row.OptionalString("Ko", string.Empty),
                Ru = row.OptionalString("Ru", string.Empty),
                Voice = row.OptionalString("Voice", string.Empty)
            };
        }

        private static NovelRow? ReadNovel(RowReader row) =>
            new NovelRow
            {
                Id = row.RequiredString("ID"),
                ScenarioGroupId = row.RequiredString("ScenarioGroupID"),
                Command = row.RequiredCommand("Command"),
                Arg1 = row.OptionalString("Arg1", "0"),
                Arg2 = row.OptionalFloat("Arg2", 0f),
                Arg3 = row.OptionalString("Arg3", "0"),
                Arg4 = row.OptionalString("Arg4", "0"),
                Talker = row.OptionalEnum("Talker", MasterDataEnums.Talker, 0),
                LocalizationId = row.OptionalString("LocalizationID", "0"),
                BodyMotion = row.OptionalInt("BodyMotion", -1),
                FacialMotion = row.OptionalInt("FacialMotion", -1),
                LookScale = row.OptionalFloat("LookScale", 0f),
                LookSpeedSeconds = row.OptionalFloat("LookSpeedSeconds", 1f),
                LookEaseType = row.OptionalEnum("LookEaseType", MasterDataEnums.Ease, 0)
            };

        private static ScenarioGroupRow? ReadScenarioGroup(RowReader row) =>
            new ScenarioGroupRow
            {
                Id = row.RequiredString("ID"),
                TitleLocalizationId = row.OptionalString("TitleLocalizationID", "0"),
                SubtitleLocalizationId = row.OptionalString("SubtitleLocalizationID", "0"),
                Scenario = row.RequiredEnum("Scenario", MasterDataEnums.Scenario),
                EpisodeNumber = row.OptionalFloat("EpisodeNumber", 0f),
                UnlockLevel = row.OptionalInt("UnlockLevel", 0),
                UnlockStoryEpisode = row.OptionalString("UnlockStoryEpisode", "None"),
                PlayTiming = row.OptionalString("PlayTiming", "None"),
                ContentType = row.OptionalString("ContentType", "None"),
                Arg1 = row.OptionalString("Arg1", "0"),
                Arg2 = row.OptionalString("Arg2", "0"),
                Arg3 = row.OptionalString("Arg3", "0"),
                Languages = row.OptionalCodeList("Languages"),
                Pool = row.OptionalString("Pool", string.Empty).Trim()
            };

        /// <summary>
        /// One row being read: case-insensitive member lookup, typed accessors, and a rejection
        /// list it writes into. <see cref="Ok"/> goes false the moment anything is refused, and the
        /// caller then drops the row.
        /// </summary>
        private sealed class RowReader
        {
            private readonly Dictionary<string, object?> _members;
            private readonly string _file;
            private readonly int _index;
            private readonly List<MasterDataRejection> _rejected;

            internal RowReader(
                JsonObject row,
                string file,
                int index,
                List<MasterDataRejection> rejected,
                string[] knownMembers)
            {
                _file = file;
                _index = index;
                _rejected = rejected;
                _members = new Dictionary<string, object?>(row.Count, StringComparer.OrdinalIgnoreCase);
                Ok = true;

                foreach (var pair in row)
                {
                    if (_members.ContainsKey(pair.Key))
                    {
                        // JsonReader already refuses an exact duplicate key, so this is a pair like
                        // "id" and "ID": legal JSON, but ambiguous once names are case-insensitive.
                        Reject(pair.Key, "two members differ only in case; keep one");
                        continue;
                    }

                    _members.Add(pair.Key, pair.Value);

                    if (pair.Key.StartsWith(ExtensionPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!IsKnown(pair.Key, knownMembers))
                    {
                        Reject(
                            pair.Key,
                            "unknown member; expected one of " + string.Join(", ", knownMembers)
                            + " (prefix a member with \"" + ExtensionPrefix + "\" to have it ignored)");
                    }
                }
            }

            /// <summary>False once anything in this row has been refused.</summary>
            internal bool Ok { get; private set; }

            internal string RequiredString(string name)
            {
                if (!_members.TryGetValue(name, out var value))
                {
                    Reject(name, "is required");
                    return string.Empty;
                }

                if (!(value is string text))
                {
                    Reject(name, "must be a string, not " + JsonReader.TypeName(value));
                    return string.Empty;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Reject(name, "is required and cannot be blank");
                    return string.Empty;
                }

                return text;
            }

            internal string OptionalString(string name, string fallback)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    return fallback;
                }

                if (value is string text)
                {
                    return text;
                }

                Reject(
                    name,
                    "must be a string, not " + JsonReader.TypeName(value)
                    + " (the game stores this column as text, so quote the value)");
                return fallback;
            }

            /// <summary>
            /// An optional array of language codes (the pack-line <c>Languages</c> member): null when the member
            /// is absent or null; otherwise a non-empty array of non-blank strings, returned trimmed and
            /// in file order. Normalising the spelling is the pack-lines screen's job, not the parser's.
            /// </summary>
            internal IReadOnlyList<string>? OptionalCodeList(string name)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    return null;
                }

                if (!(value is IList<object?> items))
                {
                    Reject(name, "must be an array of language codes such as [\"th\"], not " + JsonReader.TypeName(value));
                    return null;
                }

                if (items.Count == 0)
                {
                    Reject(name, "is empty; name at least one language code, or leave the member out for a line every language may play");
                    return null;
                }

                var codes = new List<string>(items.Count);
                for (var i = 0; i < items.Count; i++)
                {
                    if (!(items[i] is string code) || string.IsNullOrWhiteSpace(code))
                    {
                        Reject(name, "item " + i.ToString(CultureInfo.InvariantCulture)
                                     + " must be a non-blank language code string, not " + JsonReader.TypeName(items[i]));
                        return null;
                    }

                    codes.Add(code.Trim());
                }

                return codes;
            }

            internal int OptionalInt(string name, int fallback)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    return fallback;
                }

                switch (value)
                {
                    case long number when number >= int.MinValue && number <= int.MaxValue:
                        return (int)number;

                    case long tooBig:
                        Reject(name, "must fit in a 32-bit integer; " + tooBig.ToString(CultureInfo.InvariantCulture)
                                     + " does not");
                        return fallback;

                    case double real when Math.Abs(real % 1d) < double.Epsilon
                                          && real >= int.MinValue && real <= int.MaxValue:
                        return (int)real;

                    default:
                        Reject(name, "must be a whole number, not " + JsonReader.TypeName(value));
                        return fallback;
                }
            }

            internal float OptionalFloat(string name, float fallback)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    return fallback;
                }

                switch (value)
                {
                    case long number:
                        return number;

                    case double real when !double.IsNaN(real) && !double.IsInfinity(real):
                        return (float)real;

                    default:
                        Reject(name, "must be a number, not " + JsonReader.TypeName(value));
                        return fallback;
                }
            }

            internal int RequiredEnum(string name, EnumTable table)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    Reject(name, "is required; name a member of " + table.TypeName + " ("
                                 + table.Describe() + ")");
                    return 0;
                }

                return ReadEnum(name, table, value, 0);
            }

            /// <summary>
            /// A Novel <c>Command</c>: a game command, or a registered <c>cwy.*</c> script command
            /// (5000–5999). Anything else is rejected with the usual enum message.
            /// </summary>
            internal int RequiredCommand(string name)
            {
                if (_members.TryGetValue(name, out var value) && value != null)
                {
                    if (value is string token && MasterDataEnums.TryParseCommand(token, out var parsed))
                    {
                        return parsed;
                    }

                    if (value is long n && n >= int.MinValue && n <= int.MaxValue
                        && MasterDataEnums.IsCommandDefined((int)n))
                    {
                        return (int)n;
                    }
                }

                return RequiredEnum(name, MasterDataEnums.Command);
            }

            internal int OptionalEnum(string name, EnumTable table, int fallback)
            {
                if (!_members.TryGetValue(name, out var value) || value is null)
                {
                    return fallback;
                }

                return ReadEnum(name, table, value, fallback);
            }

            private int ReadEnum(string name, EnumTable table, object value, int fallback)
            {
                if (value is string token)
                {
                    if (table.TryParseText(token, out var parsed))
                    {
                        return parsed;
                    }

                    Reject(name, "\"" + token + "\" is not a member of " + table.TypeName
                                 + " (" + table.Describe() + ")");
                    return fallback;
                }

                if (value is long number)
                {
                    if (number >= int.MinValue && number <= int.MaxValue && table.IsDefined((int)number))
                    {
                        return (int)number;
                    }

                    Reject(name, number.ToString(CultureInfo.InvariantCulture) + " is not a value of "
                                 + table.TypeName + "; name the member instead");
                    return fallback;
                }

                Reject(name, "must be a member name or number of " + table.TypeName + ", not "
                             + JsonReader.TypeName(value));
                return fallback;
            }

            private void Reject(string member, string reason)
            {
                Ok = false;
                _rejected.Add(new MasterDataRejection(_file, _index, member, reason));
            }

            private static bool IsKnown(string name, string[] knownMembers)
            {
                foreach (var known in knownMembers)
                {
                    if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
