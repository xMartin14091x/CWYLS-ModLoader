using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Api.Maker.Editor
{
    /// <summary>What a subtitle save did on disk.</summary>
    public sealed class MakerSubtitleWrite
    {
        /// <summary>The table's pack-relative path, e.g. <c>data/subtitles/th.json</c>.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>False when the table refused the edit; see <see cref="Problem"/>.</summary>
        public bool Ok { get; set; }

        /// <summary>Why the table could not be edited (not valid JSON, a non-text entry), or null.</summary>
        public string? Problem { get; set; }

        /// <summary>True when the file was written (false for a no-op: same text, or nothing to remove).</summary>
        public bool Written { get; set; }

        /// <summary>The entry's text before, or null.</summary>
        public string? Previous { get; set; }

        /// <summary>The entry's text after, or null when it has none.</summary>
        public string? Current { get; set; }

        /// <summary>The pack-relative path the first-edit safety copy went to, or null when none was made now.</summary>
        public string? SafetyCopy { get; set; }
    }

    /// <summary>What a dub save or removal did on disk.</summary>
    public sealed class MakerVoiceWrite
    {
        /// <summary>The dub's pack-relative path, e.g. <c>audio/voice/th/Voice_X.wav</c>.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>True when a dub file was there before.</summary>
        public bool Existed { get; set; }

        /// <summary>True when Pack Maker deleted its own earlier file (removal only).</summary>
        public bool Deleted { get; set; }

        /// <summary>Creator files moved aside: original path to backup path.</summary>
        public List<KeyValuePair<string, string>> MovedAside { get; } = new List<KeyValuePair<string, string>>();

        /// <summary>Backups moved back into place (removal only), by original path.</summary>
        public List<string> Restored { get; } = new List<string>();
    }

    /// <summary>
    /// Compatibility façade for the v1 editor contract. Subtitle work lives in
    /// <see cref="MakerSubtitleStore"/> and dub work in <see cref="MakerVoiceStore"/>.
    /// </summary>
    public static class MakerStores
    {
        /// <summary>
        /// Sets (<paramref name="text"/>) or removes (null) one entry in <c>data/subtitles/&lt;code&gt;.json</c>.
        /// </summary>
        /// <remarks>
        /// Before the first edit Pack Maker ever makes to an existing table, a byte-for-byte copy goes
        /// to <c>_maker_backup/data/subtitles/&lt;code&gt;.json</c> (only when no copy is there yet), so a
        /// creator can always get their own version back.
        /// </remarks>
        /// <param name="files">The pack.</param>
        /// <param name="code">A canonical language code.</param>
        /// <param name="id">The localisation id.</param>
        /// <param name="text">The text, or null to remove.</param>
        public static MakerSubtitleWrite SaveSubtitle(MakerPackFiles files, string code, string id, string? text)
            => MakerSubtitleStore.Save(files, code, id, text);

        /// <summary>
        /// The path a dub of <paramref name="clip"/> in <paramref name="code"/> is written to:
        /// <c>audio/voice/&lt;code&gt;/&lt;clip&gt;.wav</c>, in the spelling of a language folder the pack
        /// already has when it differs only in case (<c>TH/</c>), so one language keeps one folder.
        /// </summary>
        /// <param name="files">The pack.</param>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name.</param>
        public static string VoicePath(MakerPackFiles files, string code, string clip)
            => MakerVoiceStore.VoicePath(files, code, clip);

        /// <summary>
        /// Writes a dub: every other file the pack has for this name in this language
        /// (<c>&lt;clip&gt;.ogg</c>, or a <c>.wav</c> in another spelling of the language folder) is moved
        /// aside first, and so is an existing dub at the target that Pack Maker did not write. Then the
        /// WAV is written atomically and recorded in the ledger.
        /// </summary>
        /// <param name="files">The pack.</param>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name.</param>
        /// <param name="wav">The canonical WAV (<see cref="MakerWav.Canonical"/>).</param>
        public static MakerVoiceWrite SaveVoice(MakerPackFiles files, string code, string clip, byte[] wav)
            => MakerVoiceStore.Save(files, code, clip, wav);

        /// <summary>
        /// Removes a dub: the WAV at the target is deleted when Pack Maker wrote it and moved aside
        /// otherwise; then every primary backup for this name in this language whose place is free is
        /// moved back (an <c>.ogg</c> the save displaced comes back). A creator file elsewhere that Pack
        /// Maker never displaced is left alone.
        /// </summary>
        /// <param name="files">The pack.</param>
        /// <param name="code">A canonical language code.</param>
        /// <param name="clip">The voice name.</param>
        public static MakerVoiceWrite RemoveVoice(MakerPackFiles files, string code, string clip)
            => MakerVoiceStore.Remove(files, code, clip);
    }
}
