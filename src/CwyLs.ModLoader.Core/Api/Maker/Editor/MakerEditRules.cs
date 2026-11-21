using System;
using System.Text.RegularExpressions;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>
    /// The Pack Maker editor's input rules and the pack-relative paths it may write (ticket DS-03,
    /// CONTRACT.md "Principles" and "Segments").
    /// </summary>
    /// <remarks>
    /// <para>
    /// The maker routes (DS-01) already refuse a malformed segment with 400 before the editor is
    /// called; the editor checks again here, because it is the one that turns the segments into file
    /// paths, and a second gate costs nothing.
    /// </para>
    /// <para>
    /// Pack Maker writes exactly two kinds of file inside a pack, <c>data/subtitles/&lt;lang&gt;.json</c>
    /// and <c>audio/voice/&lt;lang&gt;/&lt;clip&gt;.wav</c>, plus its own bookkeeping under
    /// <see cref="BackupFolder"/> (moved-aside creator files and the ledger of what it wrote). The
    /// loader never reads that folder: <c>PackContentIndex</c> gives it no meaning.
    /// </para>
    /// </remarks>
    public static class MakerEditRules
    {
        /// <summary>Longest subtitle text accepted, in characters (CONTRACT.md: 1-2000).</summary>
        public const int MaxSubtitleLength = 2000;

        /// <summary>Largest voice body accepted, in bytes (CONTRACT.md: 16 MiB).</summary>
        public const int MaxVoiceBytes = 16 * 1024 * 1024;

        /// <summary>Longest dub accepted, in seconds (CONTRACT.md: 60 s).</summary>
        public const double MaxVoiceSeconds = 60d;

        /// <summary>Lowest sample rate accepted (CONTRACT.md: 8-48 kHz).</summary>
        public const int MinSampleRate = 8000;

        /// <summary>Highest sample rate accepted.</summary>
        public const int MaxSampleRate = 48000;

        /// <summary>The pack folder Pack Maker keeps creator files it moved aside in, and its ledger.</summary>
        public const string BackupFolder = "_maker_backup";

        /// <summary>Pack-relative path of the ledger of files Pack Maker wrote (path to content hash).</summary>
        public const string LedgerPath = BackupFolder + "/maker-written.json";

        /// <summary>The file extension of every dub Pack Maker writes.</summary>
        public const string VoiceExtension = ".wav";

        /// <summary>
        /// Pack ids the loader ships as references and examples. <c>./deploy-packs.sh</c> replaces
        /// these folders wholesale, so an edit made in one would be lost on the next deploy.
        /// </summary>
        private static readonly string[] ReferenceIds = { "cwy.example" };

        /// <summary>Id prefixes of the loader's own demo and sample packs (same reason as <see cref="ReferenceIds"/>).</summary>
        private static readonly string[] ReferencePrefixes = { "cwy.demo.", "cwy.sample." };

        // \A and \z, not ^ and $: in .NET, $ also matches before a trailing newline.
        private static readonly Regex PackIdRegex =
            new Regex(@"\A[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant);

        private static readonly Regex LangRegex =
            new Regex(@"\A[a-z]{2,3}(-[a-z0-9]{2,8})?\z", RegexOptions.CultureInvariant);

        private static readonly Regex IdRegex =
            new Regex(@"\A[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant);

        /// <summary>True when <paramref name="packId"/> is a well-formed pack segment.</summary>
        /// <param name="packId">The candidate.</param>
        public static bool IsPackId(string? packId) => packId != null && PackIdRegex.IsMatch(packId);

        /// <summary>True when <paramref name="id"/> is a well-formed localisation id segment.</summary>
        /// <param name="id">The candidate.</param>
        public static bool IsLineId(string? id) => MakerSegments.IsId(id);

        /// <summary>
        /// True when <paramref name="clip"/> is a well-formed clip segment <em>and</em> a voice name the
        /// loader can register: the contract allows dots, a voice file name may not have one.
        /// </summary>
        /// <param name="clip">The candidate.</param>
        public static bool IsClip(string? clip) =>
            clip != null && IdRegex.IsMatch(clip) && VoiceNameRules.IsValid(clip) && clip.IndexOf('.') < 0;

        /// <summary>
        /// Checks a language segment and returns its canonical code (<c>thai</c> and <c>th</c> are
        /// one language, <see cref="SubtitleLanguages.Normalize"/>).
        /// </summary>
        /// <param name="lang">The segment as sent.</param>
        /// <param name="code">The canonical code, or empty.</param>
        /// <param name="problem">Why it was refused, or null.</param>
        /// <returns>True when the segment names a language code.</returns>
        public static bool TryLanguage(string? lang, out string code, out string? problem)
        {
            code = string.Empty;
            if (lang is null || !LangRegex.IsMatch(lang))
            {
                problem = "'" + lang + "' is not a language code (e.g. th)";
                return false;
            }

            code = SubtitleLanguages.Normalize(lang);
            if (!LanguageVoicePlan.TryNormalizeCode(code, out code, out problem))
            {
                code = string.Empty;
                return false;
            }

            problem = null;
            return true;
        }

        /// <summary>
        /// Checks that a language may carry a subtitle table: a pack table is only ever asked for a
        /// language the game has no column for, so a table for one of the game's own seven would never
        /// be shown.
        /// </summary>
        /// <param name="code">A canonical code from <see cref="TryLanguage"/>.</param>
        /// <param name="problem">Why it was refused, or null.</param>
        public static bool AcceptsSubtitles(string code, out string? problem)
        {
            if (SubtitleLanguages.IsInGame(code))
            {
                problem = "'" + code + "' is one of the game's own languages; subtitle tables are for a language a pack adds";
                return false;
            }

            problem = null;
            return true;
        }

        /// <summary>Checks a subtitle text: 1 to <see cref="MaxSubtitleLength"/> characters, not only whitespace.</summary>
        /// <param name="text">The text.</param>
        /// <param name="problem">Why it was refused, or null.</param>
        public static bool IsSubtitleText(string? text, out string? problem)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                problem = "the subtitle is empty; to remove one, delete it instead";
                return false;
            }

            if (!TryCodePointCount(text, out var count))
            {
                problem = "the subtitle contains an unpaired Unicode surrogate";
                return false;
            }
            if (count > MaxSubtitleLength)
            {
                problem = "the subtitle is " + count + " code points; the limit is " + MaxSubtitleLength;
                return false;
            }

            problem = null;
            return true;
        }

        /// <summary>Counts Unicode code points and refuses unpaired UTF-16 surrogates.</summary>
        public static bool TryCodePointCount(string? text, out int count)
        {
            count = 0;
            if (text is null) return true;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
                else if (char.IsSurrogate(text[i])) return false;
                count++;
            }

            return true;
        }

        /// <summary>Pack-relative path of a language's subtitle table, e.g. <c>data/subtitles/th.json</c>.</summary>
        /// <param name="code">A canonical language code.</param>
        public static string SubtitlePath(string code) => SubtitleTables.Folder + code + SubtitleTables.Extension;

        /// <summary>Pack-relative path of a dub, e.g. <c>audio/voice/th/Voice_X.wav</c>.</summary>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name.</param>
        public static string VoicePath(string code, string clip) =>
            PackContentIndex.VoiceFolder + code + "/" + clip + VoiceExtension;

        /// <summary>Where a pack file goes when Pack Maker moves it aside: the same path under <see cref="BackupFolder"/>.</summary>
        /// <param name="relativePath">A pack-relative path.</param>
        public static string BackupPathOf(string relativePath) => BackupFolder + "/" + relativePath;

        /// <summary>True when the pack is one the loader ships as a reference, demo or sample.</summary>
        /// <param name="packId">A pack id.</param>
        public static bool IsReferencePack(string? packId)
        {
            if (string.IsNullOrEmpty(packId))
            {
                return false;
            }

            foreach (var id in ReferenceIds)
            {
                if (string.Equals(id, packId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (var prefix in ReferencePrefixes)
            {
                if (packId!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
