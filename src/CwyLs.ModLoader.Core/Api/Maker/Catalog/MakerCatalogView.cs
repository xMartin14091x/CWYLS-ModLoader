using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Catalog
{
    /// <summary>
    /// The catalog's answers, from one game snapshot and one read of the packs: the line list with
    /// search, filters and paging, one line, and the pack list. No Unity and no game types, so every
    /// rule is unit-tested; the adapter (<c>Bulbul/Maker/MakerCatalog</c>) only supplies the inputs.
    /// </summary>
    /// <remarks>
    /// <para>Where each <see cref="MakerLine"/> field comes from:</para>
    /// <list type="table">
    /// <item><term>Id, Voice, Ja, En</term><description>the game's <c>LocalizationMaster</c> row (packs' <c>data/localization.json</c> overrides included, as injected).</description></item>
    /// <item><term>Group</term><description>the <c>ScenarioGroupID</c> of the first <c>NovelMaster</c> row that names the line, else the group or source it was last spoken from; <see cref="MakerLineRules.OmittedGroup"/> for an omitted line.</description></item>
    /// <item><term>DurationSec</term><description>the game's own clip length from the voice manager's cache, else the length seen when the game played its own clip; null for an omitted line.</description></item>
    /// <item><term>Subtitle, SubtitlePack</term><description>the last loaded pack whose <c>data/subtitles/&lt;lang&gt;.json</c> has a non-empty entry for the id.</description></item>
    /// <item><term>DubPack, DubDurationSec</term><description>the pack whose <c>audio/voice/&lt;lang&gt;/&lt;voice&gt;.wav|.ogg</c> the voice feature would play, and that file's length from its header.</description></item>
    /// </list>
    /// <para>Thread-safe: everything it holds is immutable or (the two caches) locked.</para>
    /// </remarks>
    public sealed class MakerCatalogView
    {
        private static readonly MakerLine[] NoLines = new MakerLine[0];

        private readonly MakerSpokenCache? _spoken;
        private readonly Func<string, double?>? _dubLength;
        private readonly MakerGameRow[] _ordered;
        private readonly HashSet<string> _omitted;

        /// <summary>Creates a view.</summary>
        /// <param name="game">The game snapshot.</param>
        /// <param name="content">The packs' subtitles and voice sets.</param>
        /// <param name="spoken">Lengths and groups learnt from spoken lines; null means none.</param>
        /// <param name="dubLength">Reads a dub file's length from its absolute path; null leaves dub lengths unknown.</param>
        public MakerCatalogView(
            MakerGameData game,
            MakerPackContent content,
            MakerSpokenCache? spoken = null,
            Func<string, double?>? dubLength = null)
        {
            Game = game ?? throw new ArgumentNullException(nameof(game));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            _spoken = spoken;
            _dubLength = dubLength;

            // The game's order, with omitted lines moved to the end (stable), so a page of "all"
            // reaches the lines that matter first.
            _omitted = new HashSet<string>(StringComparer.Ordinal);
            var kept = new List<MakerGameRow>(game.Rows.Count);
            var tail = new List<MakerGameRow>();
            foreach (var row in game.Rows)
            {
                if (IsOmitted(row))
                {
                    _omitted.Add(row.Id);
                    tail.Add(row);
                }
                else
                {
                    kept.Add(row);
                }
            }

            kept.AddRange(tail);
            _ordered = kept.ToArray();
        }

        /// <summary>The game snapshot.</summary>
        public MakerGameData Game { get; }

        /// <summary>The packs' content.</summary>
        public MakerPackContent Content { get; }

        /// <summary>How many lines are omitted (see <see cref="MakerLineRules"/>).</summary>
        public int OmittedCount => _omitted.Count;

        /// <summary>The rows in the order <see cref="Lines"/> lists them: the game's order, omitted rows last.</summary>
        public IReadOnlyList<MakerGameRow> OrderedRows => _ordered;

        /// <summary>True when the game never speaks the row (see <see cref="MakerLineRules.OmittedGroup"/>).</summary>
        /// <param name="id">A localisation id.</param>
        public bool IsOmittedRow(string? id) => !string.IsNullOrEmpty(id) && _omitted.Contains(id!);

        /// <summary>The group <see cref="Lines"/> reports for a row: <see cref="MakerLineRules.OmittedGroup"/>, its novel group, the group it was spoken from, or null.</summary>
        /// <param name="row">A row of <see cref="Game"/>.</param>
        public string? PrimaryGroupOf(MakerGameRow row) =>
            row is null ? throw new ArgumentNullException(nameof(row)) : _omitted.Contains(row.Id) ? MakerLineRules.OmittedGroup : GroupOf(row);

        /// <summary>One page of lines.</summary>
        /// <param name="query">The query; its language is required.</param>
        /// <returns>The page, or <c>bad_request</c> for a missing language, an unknown filter or a page out of range.</returns>
        public MakerResult<MakerLinePage> Lines(MakerLineQuery? query)
        {
            if (query is null)
            {
                return MakerResult<MakerLinePage>.Fail(MakerErrorCodes.BadRequest, "A query is required.");
            }

            if (!MakerLineRules.TryParseLang(query.Lang, out var lang))
            {
                return MakerResult<MakerLinePage>.Fail(
                    MakerErrorCodes.BadRequest, "lang is required and must be a language code such as th or pt-br.");
            }

            if (!MakerLineRules.TryParseFilter(query.Filter, out var filter))
            {
                return MakerResult<MakerLinePage>.Fail(
                    MakerErrorCodes.BadRequest, "filter must be all, missing-subtitle, missing-dub or done.");
            }

            if (query.Offset < 0)
            {
                return MakerResult<MakerLinePage>.Fail(MakerErrorCodes.BadRequest, "offset cannot be negative.");
            }

            if (query.Limit < MakerLineRules.MinLimit || query.Limit > MakerLineRules.MaxLimit)
            {
                return MakerResult<MakerLinePage>.Fail(
                    MakerErrorCodes.BadRequest,
                    "limit must be between " + MakerLineRules.MinLimit + " and " + MakerLineRules.MaxLimit + ".");
            }

            var needle = query.Search?.Trim() ?? string.Empty;
            var subtitles = Content.SubtitlesFor(lang);
            Content.VoicePlan.TryGet(lang, out var set);

            var total = 0;
            var items = new List<MakerGameRow>(Math.Min(query.Limit, _ordered.Length));
            foreach (var row in _ordered)
            {
                var subtitle = SubtitleOf(subtitles, row.Id);
                var dub = row.Voice is null ? null : set?.Find(row.Voice);
                var state = MakerLineState.Of(_omitted.Contains(row.Id), row.Ja, row.En, row.Voice, subtitle?.Text, dub?.PackId);

                if (!MakerLineRules.Matches(state, filter)
                    || !MakerLineRules.MatchesSearch(needle, row.Id, row.Voice, row.Ja, row.En, subtitle?.Text))
                {
                    continue;
                }

                if (total >= query.Offset && items.Count < query.Limit)
                {
                    items.Add(row);
                }

                total++;
            }

            var lines = items.Count == 0 ? NoLines : new MakerLine[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                lines[i] = Build(items[i], subtitles, set);
            }

            return MakerResult<MakerLinePage>.Success(new MakerLinePage { Total = total, Offset = query.Offset, Items = lines });
        }

        /// <summary>One line by its localisation id.</summary>
        /// <param name="id">The localisation id, case-sensitively.</param>
        /// <param name="lang">The language its subtitle and dub are for.</param>
        /// <returns>The line, <c>bad_request</c> for a missing id or language, or <c>not_found</c>.</returns>
        public MakerResult<MakerLine> Line(string? id, string? lang)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return MakerResult<MakerLine>.Fail(MakerErrorCodes.BadRequest, "A line id is required.");
            }

            if (!MakerLineRules.TryParseLang(lang, out var code))
            {
                return MakerResult<MakerLine>.Fail(
                    MakerErrorCodes.BadRequest, "lang is required and must be a language code such as th or pt-br.");
            }

            if (!Game.TryFind(id, out var row) || row is null)
            {
                return MakerResult<MakerLine>.Fail(MakerErrorCodes.NotFound, "The game has no line '" + id + "'.");
            }

            Content.VoicePlan.TryGet(code, out var set);
            return MakerResult<MakerLine>.Success(Build(row, Content.SubtitlesFor(code), set));
        }

        /// <summary>The state of one line in one language, for diagnostics and tests.</summary>
        /// <param name="id">The localisation id.</param>
        /// <param name="lang">The language.</param>
        /// <returns>The state, or null when the game has no such line.</returns>
        public MakerLineState? StateOf(string? id, string? lang)
        {
            if (!Game.TryFind(id, out var row) || row is null)
            {
                return null;
            }

            var subtitle = SubtitleOf(Content.SubtitlesFor(lang), row.Id);
            var dub = Content.FindDub(lang, row.Voice);
            return MakerLineState.Of(_omitted.Contains(row.Id), row.Ja, row.En, row.Voice, subtitle?.Text, dub?.PackId);
        }

        /// <summary>
        /// The pack list: every pack that loaded, in load order, then every pack that was refused,
        /// each with its languages and whether Pack Maker may write to it.
        /// </summary>
        /// <param name="packs">The packs, as the adapter read them from the pack registry.</param>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder.</param>
        /// <param name="isWritable">Answers whether a folder may be written to (see <see cref="MakerPackRules.IsWritableFolder"/>).</param>
        public IReadOnlyList<MakerPack> Packs(IEnumerable<MakerPackSource>? packs, string? packsRoot, Func<string, bool>? isWritable)
        {
            var loaded = new List<MakerPack>();
            var refused = new List<MakerPack>();
            if (packs is null)
            {
                return loaded;
            }

            foreach (var pack in packs)
            {
                if (pack is null)
                {
                    continue;
                }

                var access = MakerPackRules.Decide(pack, packsRoot, isWritable);
                var languages = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var code in pack.DeclaredLanguages)
                {
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        languages.Add(code.Trim().ToLowerInvariant());
                    }
                }

                foreach (var code in Content.LanguagesOf(pack.Id))
                {
                    languages.Add(code);
                }

                var entry = new MakerPack
                {
                    Id = pack.Id,
                    Name = pack.Name,
                    Version = pack.Version,
                    Languages = new List<string>(languages),
                    Editable = access.Editable,
                    ReadOnlyReason = access.Reason,
                };

                (pack.Loaded ? loaded : refused).Add(entry);
            }

            loaded.AddRange(refused);
            return loaded;
        }

        /// <summary>
        /// True when the game never speaks the line: its id ends in <see cref="MakerLineRules.OmitSuffix"/>,
        /// or it names a voice that exists nowhere (the game's cache was read and lacks it, no mod or
        /// pack registered it, and no pack ships it globally or in any language).
        /// </summary>
        private bool IsOmitted(MakerGameRow row)
        {
            if (MakerLineRules.IsOmittedId(row.Id))
            {
                return true;
            }

            return row.Voice != null
                && Game.ClipCacheKnown
                && !Game.HasGameClip(row.Voice)
                && !Game.IsRegistered(row.Voice)
                && !Content.ProvidesVoice(row.Voice);
        }

        private MakerLine Build(MakerGameRow row, IReadOnlyDictionary<string, MakerSubtitle>? subtitles, LanguageVoiceSet? set)
        {
            var omitted = _omitted.Contains(row.Id);
            var subtitle = SubtitleOf(subtitles, row.Id);
            var dub = row.Voice is null ? null : set?.Find(row.Voice);

            return new MakerLine
            {
                Id = row.Id,
                Voice = row.Voice,
                Group = omitted ? MakerLineRules.OmittedGroup : GroupOf(row),
                DurationSec = omitted ? null : GameDurationOf(row.Voice),
                Ja = row.Ja,
                En = row.En,
                Subtitle = subtitle?.Text,
                SubtitlePack = subtitle?.PackId,
                DubPack = dub?.PackId,
                DubDurationSec = dub is null ? null : DubLengthOf(dub),
            };
        }

        private string? GroupOf(MakerGameRow row)
        {
            if (row.Group != null)
            {
                return row.Group;
            }

            return _spoken != null && _spoken.TryGetGroup(row.Id, out var spoken) ? spoken : null;
        }

        private double? GameDurationOf(string? voice)
        {
            if (voice is null)
            {
                return null;
            }

            if (Game.TryGetClipLength(voice, out var seconds))
            {
                return Math.Round(seconds, 3);
            }

            return _spoken != null && _spoken.TryGetDuration(voice, out seconds) ? seconds : (double?)null;
        }

        private double? DubLengthOf(VoiceLoadStep step)
        {
            if (_dubLength is null)
            {
                return null;
            }

            var path = Content.FullPathOf(step);
            return path is null ? null : _dubLength(path);
        }

        /// <summary>A non-empty subtitle; an empty entry is no subtitle (the game falls back past it).</summary>
        private static MakerSubtitle? SubtitleOf(IReadOnlyDictionary<string, MakerSubtitle>? table, string id) =>
            table != null && table.TryGetValue(id, out var found) && found.Text.Length > 0 ? found : null;
    }
}
