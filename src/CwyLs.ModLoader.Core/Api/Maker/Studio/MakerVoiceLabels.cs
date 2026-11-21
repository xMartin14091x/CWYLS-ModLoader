using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>What a shipped dub's voice label (its <c>voice-provenance.json</c> entry) says about it (D-148).</summary>
    internal enum MakerDubLabelState
    {
        /// <summary>A known origin, the current consent statement, a confirmation time and the file's own hash.</summary>
        Valid,
        /// <summary>Not a Studio publication, and no valid label: one click labels it with the pack default.</summary>
        Unlabelled,
        /// <summary>A Studio publication whose label lacks a known origin or a current confirmation: fixed on the published take.</summary>
        TakeConsent,
        /// <summary>A Studio publication without a label, or whose label names other bytes: publish it again.</summary>
        Republish,
        /// <summary>Another runtime file for the same language and clip: one label cannot describe both.</summary>
        Duplicate,
    }

    /// <summary>One shipped dub file and its label state.</summary>
    internal sealed class MakerDubLabel
    {
        public string Path { get; set; } = string.Empty;
        public string Lang { get; set; } = string.Empty;
        public string Clip { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public MakerDubLabelState State { get; set; }
    }

    /// <summary>Every shipped dub of a pack with its label state, and the labels whose audio file is gone.</summary>
    internal sealed class MakerVoiceLabelScan
    {
        public List<MakerDubLabel> Dubs { get; } = new List<MakerDubLabel>();
        /// <summary>Entries whose clip has no shipped audio file (pack-relative WAV path they expect).</summary>
        public List<string> MissingAudio { get; } = new List<string>();
        public List<MakerProvenanceEntry> Entries { get; } = new List<MakerProvenanceEntry>();
        public IEnumerable<MakerDubLabel> In(MakerDubLabelState state) => Dubs.Where(d => d.State == state);
    }

    /// <summary>
    /// D-148: every dub an export ships needs a valid voice label. Shared by the export check (which refuses an unlabelled dub)
    /// and the one-click label (which labels exactly the dubs the check calls unlabelled). Pure file reads.
    /// </summary>
    internal static class MakerVoiceLabels
    {
        /// <summary>
        /// Classifies the shipped dubs (<c>audio/voice/&lt;lang&gt;/&lt;clip&gt;.wav|.ogg</c> in <paramref name="set"/>). A clip's
        /// label describes one file: the clip's first WAV (ordinal path order), else its first OGG; any other file for the same
        /// clip is a <see cref="MakerDubLabelState.Duplicate"/>. A dub is a Studio publication when its clip record names that
        /// path and the file still has the record's hash.
        /// </summary>
        /// <param name="files">The pack.</param>
        /// <param name="set">The walked file set.</param>
        /// <param name="entries">The parsed <c>voice-provenance.json</c> entries; empty when the file is absent.</param>
        public static MakerVoiceLabelScan Scan(MakerPackFiles files, MakerPackFileSet set, IReadOnlyList<MakerProvenanceEntry> entries)
        {
            var scan = new MakerVoiceLabelScan();
            scan.Entries.AddRange(entries);
            var byClip = new Dictionary<(string Lang, string Clip), List<string>>();
            foreach (var pair in set.Included)
            {
                if (!(DubLanguage(pair.Key) is string lang)) continue;
                var key = (lang, System.IO.Path.GetFileNameWithoutExtension(pair.Key));
                if (!byClip.TryGetValue(key, out var list)) byClip[key] = list = new List<string>();
                list.Add(pair.Key);
            }

            var labels = new Dictionary<(string, string), MakerProvenanceEntry>();
            foreach (var entry in entries) labels[(entry.Lang, entry.Clip)] = entry;
            foreach (var group in byClip.OrderBy(g => g.Key.Lang, StringComparer.Ordinal).ThenBy(g => g.Key.Clip, StringComparer.Ordinal))
            {
                var paths = group.Value.OrderBy(p => p, StringComparer.Ordinal).ToList();
                var chosen = paths.FirstOrDefault(IsWav) ?? paths[0];
                foreach (var path in paths)
                {
                    var dub = new MakerDubLabel { Path = path, Lang = group.Key.Lang, Clip = group.Key.Clip };
                    if (!string.Equals(path, chosen, StringComparison.Ordinal))
                    {
                        dub.State = MakerDubLabelState.Duplicate;
                        scan.Dubs.Add(dub);
                        continue;
                    }

                    dub.Sha256 = MakerPackChecks.HashFile(files.Resolve(path));
                    labels.TryGetValue(group.Key, out var label);
                    var studio = IsStudioPublication(files, group.Key.Lang, group.Key.Clip, path, dub.Sha256);
                    var hashOk = label != null && string.Equals(label.Sha256, dub.Sha256, StringComparison.OrdinalIgnoreCase);
                    var consentOk = label != null && MakerTakeRules.IsOrigin(label.Origin) && MakerVoiceProvenance.IsConfirmed(label);
                    dub.State = hashOk && consentOk ? MakerDubLabelState.Valid
                        : !studio ? MakerDubLabelState.Unlabelled
                        : label is null || !hashOk ? MakerDubLabelState.Republish
                        : MakerDubLabelState.TakeConsent;
                    scan.Dubs.Add(dub);
                }
            }

            foreach (var entry in entries)
            {
                if (!byClip.ContainsKey((entry.Lang, entry.Clip)))
                    scan.MissingAudio.Add(PackContentIndex.VoiceFolder + entry.Lang + "/" + entry.Clip + ".wav");
            }

            return scan;
        }

        /// <summary>The canonical language of a shipped dub path (<c>audio/voice/&lt;lang&gt;/&lt;clip&gt;.wav|.ogg</c>), or null.</summary>
        internal static string? DubLanguage(string relative)
        {
            if (!relative.StartsWith(PackContentIndex.VoiceFolder, StringComparison.Ordinal)) return null;
            if (!IsWav(relative) && !relative.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return null;
            var rest = relative.Substring(PackContentIndex.VoiceFolder.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0 || rest.IndexOf('/', slash + 1) >= 0) return null;
            if (System.IO.Path.GetFileNameWithoutExtension(rest.Substring(slash + 1)).Length == 0) return null;
            return LanguageVoicePlan.TryNormalizeCode(rest.Substring(0, slash), out var code, out _) ? code : null;
        }

        private static bool IsWav(string path) => path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when the clip record names <paramref name="path"/> and the file still has the published hash.</summary>
        private static bool IsStudioPublication(MakerPackFiles files, string lang, string clip, string path, string sha)
        {
            string recordPath;
            try { recordPath = MakerStudioPaths.Clip(lang, clip); }
            catch (ArgumentException) { return false; }
            string? text;
            try { text = files.ReadText(recordPath, out _); }
            catch (IOException) { return false; }
            return text != null && MakerClipStore.TryRecord(text, out var record) && record.Lang == lang && record.Clip == clip
                && string.Equals(record.Path, path, StringComparison.Ordinal) && string.Equals(record.Sha256, sha, StringComparison.OrdinalIgnoreCase);
        }
    }
}
