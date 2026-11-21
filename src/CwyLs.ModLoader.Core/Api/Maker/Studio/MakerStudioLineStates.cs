using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>What the catalog needs to know about one line's creator state.</summary>
    internal sealed class MakerLineSummary
    {
        internal static readonly MakerLineSummary Absent = new MakerLineSummary();

        internal bool Present { get; set; }
        internal bool Unreadable { get; set; }
        internal string StoredRevision { get; set; } = string.Empty;
        internal string SubtitleFingerprint { get; set; } = string.Empty;
        internal MakerStudioWorkflow Workflow { get; set; } = new MakerStudioWorkflow();
        internal int TakeCount { get; set; }
        internal string? PublishedTakeId { get; set; }
        internal string? PublishedTakeRevision { get; set; }
    }

    /// <summary>
    /// The creator state of every line of one pack and language, read in bulk for catalog lists and progress: the pack's own
    /// subtitle table once, <c>.maker/lines/&lt;lang&gt;/</c> listed once, only the line files that exist parsed. A line with no
    /// file is <c>todo</c>, unstarred, without takes. This is deliberately not <see cref="MakerProjectStore.ReadLine"/>, which
    /// re-parses the subtitle table and hashes every take WAV per line; <see cref="RevisionOf"/> applies the same rule that
    /// method uses for the line revision (an in-sync file keeps its revision, a drifted subtitle gets the external one, no file
    /// gets the initial one), and a test pins the two together.
    /// </summary>
    internal sealed class MakerStudioLineStates
    {
        private readonly MakerProjectStore _store;
        private readonly string _lang;
        private readonly Dictionary<string, MakerLineSummary> _lines;
        private readonly Dictionary<string, string>? _table;

        private MakerStudioLineStates(MakerProjectStore store, string lang, string stamp,
            Dictionary<string, MakerLineSummary> lines, Dictionary<string, string>? table, string? tableProblem, string? folderProblem)
        {
            _store = store; _lang = lang; Stamp = stamp; _lines = lines; _table = table;
            TableProblem = tableProblem; FolderProblem = folderProblem;
        }

        /// <summary>Changes whenever a line file or the subtitle table of the pack changes.</summary>
        internal string Stamp { get; }

        /// <summary>Why the pack's subtitle table cannot be used as creator state, or null.</summary>
        internal string? TableProblem { get; }

        /// <summary>Why the line folder could not be listed, or null.</summary>
        internal string? FolderProblem { get; }

        /// <summary>How many line files exist.</summary>
        internal int FileCount => _lines.Count;

        /// <summary>A cheap fingerprint of the files <see cref="Read"/> would read: names, sizes and write times.</summary>
        internal static string StampOf(MakerProjectStore store, string lang)
        {
            var text = new StringBuilder();
            try
            {
                text.Append(FileStamp(store, MakerEditRules.SubtitlePath(lang))).Append('\n');
                var folder = LineFolder(lang);
                foreach (var path in store.Files.ListFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
                {
                    text.Append(path).Append(' ').Append(FileStamp(store, path)).Append('\n');
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                text.Append("unreadable:").Append(ex.GetType().Name);
            }

            return MakerProjectRevision.StableId(text.ToString());
        }

        /// <summary>Reads the states. Never throws for a damaged file: it is reported as unreadable.</summary>
        internal static MakerStudioLineStates Read(MakerProjectStore store, string lang)
        {
            var stamp = StampOf(store, lang);
            var lines = new Dictionary<string, MakerLineSummary>(StringComparer.Ordinal);
            string? folderProblem = null;
            try
            {
                var folder = LineFolder(lang) + "/";
                foreach (var path in store.Files.ListFiles(LineFolder(lang)))
                {
                    if (!path.StartsWith(folder, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal)) continue;
                    var id = path.Substring(folder.Length, path.Length - folder.Length - ".json".Length);
                    if (id.Length == 0 || id.IndexOf('/') >= 0) continue;
                    lines[id] = Parse(store, path);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                folderProblem = ex.Message;
            }

            Dictionary<string, string>? table = null;
            string? tableProblem = null;
            try
            {
                var text = store.Files.ReadText(MakerEditRules.SubtitlePath(lang), out _);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (!JsonReader.TryParse(text!, out var parsed, out _) || !(parsed is JsonObject obj) || obj.Values.Any(v => !(v is string)))
                    {
                        tableProblem = "data/subtitles/" + lang + ".json is not a valid subtitle table";
                    }
                    else
                    {
                        table = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var pair in obj) table[pair.Key] = (string)pair.Value!;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                tableProblem = ex.Message;
            }

            return new MakerStudioLineStates(store, lang, stamp, lines, table, tableProblem, folderProblem);
        }

        /// <summary>The summary of a line; the shared absent one when it has no file.</summary>
        internal MakerLineSummary Summary(string id) => _lines.TryGetValue(id, out var found) ? found : MakerLineSummary.Absent;

        /// <summary>The pack's own subtitle for a line; an empty entry counts as none, as it does for the game.</summary>
        internal string? OwnSubtitle(string id) =>
            _table != null && _table.TryGetValue(id, out var text) && text.Length > 0 ? text : null;

        /// <summary>The line revision <see cref="MakerProjectStore.ReadLine"/> would report for a line without takes.</summary>
        internal string RevisionOf(string id)
        {
            // Some game line ids cannot name a sidecar file (MakerStudioPaths' file-name rules); such a line has no
            // creator metadata, so it is listed with a stable initial revision instead of failing the whole page.
            string path;
            try { path = MakerStudioPaths.Line(_lang, id); }
            catch (ArgumentException) { path = "no-sidecar:" + _lang + "/" + id; }
            var raw = _table != null && _table.TryGetValue(id, out var text) ? text : null;
            var fingerprint = _store.SubtitleFingerprint(raw);
            var summary = Summary(id);
            if (!summary.Present) return MakerProjectRevision.Initial("line", path, fingerprint);
            if (summary.Unreadable) return MakerProjectRevision.Initial("unreadable", path, summary.StoredRevision);
            return string.Equals(summary.SubtitleFingerprint, fingerprint, StringComparison.Ordinal)
                ? summary.StoredRevision
                : MakerProjectRevision.Initial("external", summary.StoredRevision, fingerprint);
        }

        private static MakerLineSummary Parse(MakerProjectStore store, string path)
        {
            try
            {
                var text = store.Files.ReadText(path, out _);
                if (text != null && MakerProjectJson.TryLine(text, out var doc, out _))
                {
                    return new MakerLineSummary
                    {
                        Present = true, StoredRevision = doc.Revision, SubtitleFingerprint = doc.SubtitleFingerprint,
                        Workflow = new MakerStudioWorkflow { Status = doc.Workflow.Status, Starred = doc.Workflow.Starred },
                        TakeCount = doc.Takes.Count, PublishedTakeId = doc.PublishedTakeId, PublishedTakeRevision = doc.PublishedTakeRevision,
                    };
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                // Falls through: an unreadable file is reported, never fatal to a list.
            }

            return new MakerLineSummary { Present = true, Unreadable = true, StoredRevision = path };
        }

        private static string LineFolder(string lang) => MakerStudioPaths.Root + "/lines/" + lang;

        private static string FileStamp(MakerProjectStore store, string relative)
        {
            try
            {
                var info = new FileInfo(store.Files.Resolve(relative));
                return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "-";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return "?";
            }
        }
    }
}
