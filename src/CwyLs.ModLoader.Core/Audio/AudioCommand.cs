using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Audio
{
    /// <summary>The <c>audio</c> console command's verbs.</summary>
    public enum AudioVerb
    {
        /// <summary><c>audio</c> / <c>audio status</c>: one summary line.</summary>
        Status,

        /// <summary><c>audio list [all|replaced|&lt;table&gt;]</c>.</summary>
        List,

        /// <summary><c>audio ab &lt;name|table|all&gt; [original|pack]</c>: the A/B switch.</summary>
        Ab,

        /// <summary><c>audio play &lt;name&gt;</c>: plays a one-shot row through the game's own service.</summary>
        Play,

        /// <summary><c>audio music</c>: the pack tracks.</summary>
        Music,

        /// <summary><c>audio on</c>: replacements back on.</summary>
        On,

        /// <summary><c>audio off</c>: every row back to the game's clip until <c>audio on</c>.</summary>
        Off,

        /// <summary><c>audio reload</c>: re-read the packs' audio.</summary>
        Reload,

        /// <summary><c>audio help</c>.</summary>
        Help,
    }

    /// <summary>A parsed <c>audio</c> command.</summary>
    public sealed class AudioCommandRequest
    {
        internal AudioCommandRequest(AudioVerb verb, string target, AudioVariant? variant, string problem)
        {
            Verb = verb;
            Target = target;
            Variant = variant;
            Problem = problem;
        }

        /// <summary>The verb.</summary>
        public AudioVerb Verb { get; }

        /// <summary>The row spec or list filter; empty when none was given.</summary>
        public string Target { get; }

        /// <summary>For <see cref="AudioVerb.Ab"/>: the variant asked for; null = toggle.</summary>
        public AudioVariant? Variant { get; }

        /// <summary>Why the line is not a valid command; empty when it is.</summary>
        public string Problem { get; }

        /// <summary>True when the line parsed.</summary>
        public bool IsValid => Problem.Length == 0;
    }

    /// <summary>Parses the arguments of the <c>audio</c> console command.</summary>
    public static class AudioCommandParser
    {
        /// <summary>The usage line <c>help</c> shows.</summary>
        public const string Usage =
            "audio [status] | list [all|replaced|<table>] | ab <name|table|all> [original|pack] | play <name> | music | on | off | reload";

        /// <summary>Parses the arguments after <c>audio</c>.</summary>
        /// <param name="args">The arguments.</param>
        /// <returns>The request; check <see cref="AudioCommandRequest.IsValid"/>.</returns>
        public static AudioCommandRequest Parse(IReadOnlyList<string>? args)
        {
            if (args is null || args.Count == 0)
            {
                return new AudioCommandRequest(AudioVerb.Status, string.Empty, null, string.Empty);
            }

            var verb = (args[0] ?? string.Empty).Trim().ToLowerInvariant();
            switch (verb)
            {
                case "status":
                    return Simple(AudioVerb.Status, args);
                case "list":
                case "ls":
                    return args.Count > 2
                        ? Error(AudioVerb.List, "audio list takes at most one filter: all, replaced or a table")
                        : new AudioCommandRequest(AudioVerb.List, args.Count == 2 ? args[1].Trim() : string.Empty, null, string.Empty);
                case "ab":
                case "switch":
                    return ParseAb(args);
                case "play":
                    if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
                    {
                        return Error(AudioVerb.Play, "usage: audio play <name>");
                    }

                    return new AudioCommandRequest(AudioVerb.Play, args[1].Trim(), null, string.Empty);
                case "music":
                case "tracks":
                    return Simple(AudioVerb.Music, args);
                case "on":
                    return Simple(AudioVerb.On, args);
                case "off":
                    return Simple(AudioVerb.Off, args);
                case "reload":
                    return Simple(AudioVerb.Reload, args);
                case "help":
                case "?":
                    return new AudioCommandRequest(AudioVerb.Help, string.Empty, null, string.Empty);
                default:
                    return Error(AudioVerb.Help, "unknown audio command '" + args[0] + "'; " + Usage);
            }
        }

        /// <summary>Reads a variant word: <c>original</c>/<c>orig</c>/<c>game</c> or <c>pack</c>/<c>new</c>/<c>mod</c>.</summary>
        /// <param name="text">The word.</param>
        /// <param name="variant">The variant.</param>
        /// <returns>True when recognised.</returns>
        public static bool TryParseVariant(string? text, out AudioVariant variant)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "original":
                case "orig":
                case "game":
                case "a":
                    variant = AudioVariant.Original;
                    return true;
                case "pack":
                case "new":
                case "mod":
                case "b":
                    variant = AudioVariant.Pack;
                    return true;
                default:
                    variant = AudioVariant.Pack;
                    return false;
            }
        }

        private static AudioCommandRequest ParseAb(IReadOnlyList<string> args)
        {
            if (args.Count < 2 || args.Count > 3 || string.IsNullOrWhiteSpace(args[1]))
            {
                return Error(AudioVerb.Ab, "usage: audio ab <name|table|all> [original|pack]");
            }

            AudioVariant? variant = null;
            if (args.Count == 3)
            {
                if (string.Equals(args[2].Trim(), "toggle", StringComparison.OrdinalIgnoreCase))
                {
                    variant = null;
                }
                else if (TryParseVariant(args[2], out var parsed))
                {
                    variant = parsed;
                }
                else
                {
                    return Error(AudioVerb.Ab, "'" + args[2] + "' is not a variant; use original or pack");
                }
            }

            return new AudioCommandRequest(AudioVerb.Ab, args[1].Trim(), variant, string.Empty);
        }

        private static AudioCommandRequest Simple(AudioVerb verb, IReadOnlyList<string> args) =>
            args.Count == 1
                ? new AudioCommandRequest(verb, string.Empty, null, string.Empty)
                : Error(verb, "audio " + args[0] + " takes no arguments");

        private static AudioCommandRequest Error(AudioVerb verb, string problem) =>
            new AudioCommandRequest(verb, string.Empty, null, problem);
    }

    /// <summary>The rows a spec names, or why none.</summary>
    public sealed class AudioRowSpecResult
    {
        internal AudioRowSpecResult(IReadOnlyList<AudioRowInfo> rows, string problem)
        {
            Rows = rows;
            Problem = problem;
        }

        /// <summary>The rows.</summary>
        public IReadOnlyList<AudioRowInfo> Rows { get; }

        /// <summary>Why none matched; empty when some did.</summary>
        public string Problem { get; }

        /// <summary>True when at least one row matched.</summary>
        public bool Ok => Problem.Length == 0;
    }

    /// <summary>
    /// Turns a spec into rows: <c>all</c>, a table (<c>ambient</c>), a key (<c>ambient/LightRain</c>),
    /// a row name (<c>LightRain</c>) or a game clip name (<c>Rainthunder</c>); case-insensitive.
    /// </summary>
    public static class AudioRowSpec
    {
        /// <summary>Resolves a spec.</summary>
        /// <param name="spec">The spec.</param>
        /// <param name="rows">Every row.</param>
        /// <param name="replacedOnly">True to keep only rows that have a replacement (what the A/B switch can flip).</param>
        public static AudioRowSpecResult Resolve(string? spec, IReadOnlyList<AudioRowInfo>? rows, bool replacedOnly)
        {
            var all = rows ?? Array.Empty<AudioRowInfo>();
            var text = (spec ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return new AudioRowSpecResult(Array.Empty<AudioRowInfo>(), "name a row, a table or all");
            }

            var matched = new List<AudioRowInfo>();
            if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
            {
                matched.AddRange(all);
            }
            else if (AudioTables.TryParse(text, out var table))
            {
                foreach (var row in all)
                {
                    if (row.Table == table)
                    {
                        matched.Add(row);
                    }
                }
            }
            else
            {
                var slash = text.IndexOf('/');
                foreach (var row in all)
                {
                    var hit = slash > 0
                        ? string.Equals(row.Key, text, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(row.Name, text, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(row.Row.OriginalClipName, text, StringComparison.OrdinalIgnoreCase);
                    if (hit)
                    {
                        matched.Add(row);
                    }
                }
            }

            if (matched.Count == 0)
            {
                return new AudioRowSpecResult(Array.Empty<AudioRowInfo>(), "no row matches '" + text + "'" + Suggest(text, all));
            }

            if (!replacedOnly)
            {
                return new AudioRowSpecResult(matched, string.Empty);
            }

            var replaced = new List<AudioRowInfo>();
            foreach (var row in matched)
            {
                if (row.HasReplacement)
                {
                    replaced.Add(row);
                }
            }

            if (replaced.Count > 0)
            {
                return new AudioRowSpecResult(replaced, string.Empty);
            }

            var first = matched[0];
            var why = matched.Count == 1
                ? first.Key + (first.IsRefused ? " is refused: " + first.Refusal : " has no pack clip")
                : "none of the " + matched.Count + " matching rows has a pack clip";
            return new AudioRowSpecResult(Array.Empty<AudioRowInfo>(), why + "; nothing to switch");
        }

        private static string Suggest(string text, IReadOnlyList<AudioRowInfo> rows)
        {
            var wanted = AudioPaidContent.Normalize(text);
            if (wanted.Length == 0)
            {
                return string.Empty;
            }

            var hits = new List<string>();
            foreach (var row in rows)
            {
                if (AudioPaidContent.Normalize(row.Name).Contains(wanted)
                    || AudioPaidContent.Normalize(row.Row.OriginalClipName).Contains(wanted))
                {
                    hits.Add(row.Key);
                    if (hits.Count == 5)
                    {
                        break;
                    }
                }
            }

            return hits.Count == 0 ? "; try 'audio list'" : "; did you mean " + string.Join(", ", hits.ToArray());
        }
    }

    /// <summary>The text of <c>audio list</c>.</summary>
    public static class AudioListFormatter
    {
        /// <summary>
        /// Formats rows: a header, then one line per row -
        /// <c>ambient/LightRain  32.0s  pack=yes (cwy.demo.audio 32.0s)  using=pack  playing=pack vol=0.42</c>.
        /// </summary>
        /// <param name="rows">Every row.</param>
        /// <param name="filter"><c>all</c> or empty (every row), <c>replaced</c> (rows with a replacement or a refusal), or a table.</param>
        /// <returns>The text, or a one-line problem for an unknown filter.</returns>
        public static string Format(IReadOnlyList<AudioRowInfo>? rows, string? filter)
        {
            var all = rows ?? Array.Empty<AudioRowInfo>();
            var wanted = (filter ?? string.Empty).Trim();
            var replacedOnly = string.Equals(wanted, "replaced", StringComparison.OrdinalIgnoreCase);
            AudioTable? table = null;
            if (wanted.Length > 0 && !replacedOnly && !string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!AudioTables.TryParse(wanted, out var parsed))
                {
                    return "unknown filter '" + wanted + "'; use all, replaced, or a table: ambient, ambient_se, system_se, timer_se, direction_music";
                }

                table = parsed;
            }

            int replaced = 0, usingOriginal = 0, refused = 0;
            foreach (var row in all)
            {
                if (row.HasReplacement)
                {
                    replaced++;
                    if (row.Variant == AudioVariant.Original)
                    {
                        usingOriginal++;
                    }
                }

                if (row.IsRefused)
                {
                    refused++;
                }
            }

            var sb = new StringBuilder();
            sb.Append("audio: ").Append(all.Count).Append(" row(s); ")
              .Append(replaced).Append(" replaced (").Append(replaced - usingOriginal).Append(" using pack, ")
              .Append(usingOriginal).Append(" switched to original); ")
              .Append(refused).Append(" refused (paid)");

            var width = 0;
            foreach (var row in all)
            {
                width = Math.Max(width, row.Key.Length);
            }

            var shown = 0;
            foreach (var row in all)
            {
                if (table.HasValue && row.Table != table.Value)
                {
                    continue;
                }

                if (replacedOnly && !row.HasReplacement && !row.IsRefused)
                {
                    continue;
                }

                shown++;
                sb.Append('\n').Append(FormatRow(row, width));
            }

            if (shown == 0)
            {
                sb.Append('\n').Append(replacedOnly ? "(no row is replaced or refused)" : "(no rows)");
            }

            return sb.ToString();
        }

        /// <summary>One row's line.</summary>
        /// <param name="row">The row.</param>
        /// <param name="keyWidth">Column width for the key.</param>
        public static string FormatRow(AudioRowInfo row, int keyWidth = 0)
        {
            if (row is null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            var sb = new StringBuilder();
            sb.Append(row.Key.PadRight(Math.Max(keyWidth, row.Key.Length)));
            sb.Append("  ").Append(Seconds(row.Row.OriginalSeconds));
            if (row.HasReplacement)
            {
                sb.Append("  pack=yes (").Append(row.ReplacementSource).Append(' ').Append(Seconds(row.ReplacementSeconds)).Append(')');
                sb.Append("  using=").Append(row.Variant == AudioVariant.Original ? "original" : "pack");
            }
            else if (row.IsRefused)
            {
                sb.Append("  pack=REFUSED (paid content)");
            }
            else
            {
                sb.Append("  pack=no");
            }

            sb.Append("  playing=");
            switch (row.Playing)
            {
                case AudioPlaying.Original:
                    sb.Append("original");
                    break;
                case AudioPlaying.Replacement:
                    sb.Append("pack");
                    break;
                case AudioPlaying.None:
                    sb.Append('-');
                    break;
                default:
                    sb.Append('?');
                    break;
            }

            if ((row.Playing == AudioPlaying.Original || row.Playing == AudioPlaying.Replacement) && row.PlayingVolume >= 0)
            {
                sb.Append(" vol=").Append(row.PlayingVolume.ToString("0.00", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        /// <summary><c>32.0s</c>, or <c>?s</c> when unknown.</summary>
        /// <param name="seconds">A length.</param>
        public static string Seconds(double seconds) =>
            seconds > 0 ? seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "?s";
    }
}
