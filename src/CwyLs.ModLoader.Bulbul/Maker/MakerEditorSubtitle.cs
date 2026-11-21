using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Bulbul.Subtitles;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>Subtitle writes and live subtitle reconciliation for <see cref="MakerEditor"/>.</summary>
    public sealed partial class MakerEditor
    {
        /// <inheritdoc />
        public async Task<MakerResult<MakerSaved>> SaveSubtitleAsync(
            string packId, string lang, string id, string? text, CancellationToken cancel)
        {
            try
            {
                return await MakerStudioEditor.Instance.SaveV1SubtitleAsync(packId, lang, id, text, cancel).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failed("saving a subtitle", ex);
            }
        }

        /// <summary>Applies and reports subtitle bytes which a Studio transaction already committed.</summary>
        internal static MakerSaved ApplyCommittedSubtitle(
            string packId, string code, string id, bool removed, MakerSubtitleWrite write)
            => ApplyCommittedSubtitle(packId, code, id, removed, write, out _);

        internal static MakerSaved ApplyCommittedSubtitle(
            string packId, string code, string id, bool removed, MakerSubtitleWrite write, out string? effectivePackId)
        {
            var effective = EffectiveSubtitle(code, id, packId, write.Current);
            effectivePackId = effective.HasValue ? effective.PackId : null;
            var saved = LiveSubtitle(code, id, removed, packId, write, effective);
            Report(MakerEditorChange.SubtitleKind, packId, code, id, removed, saved, write.Written);
            MakerBackends.Publish("maker.subtitle.saved", new Dictionary<string, object?>
            {
                ["packId"] = packId,
                ["lang"] = code,
                ["id"] = id,
                ["removed"] = removed,
            });
            return saved;
        }

        private static MakerSaved LiveSubtitle(
            string code, string id, bool removed, string packId, MakerSubtitleWrite write, MakerLayer<string> effective)
        {
            var saved = new MakerSaved { Path = write.Path };
            var verb = removed ? "Removed" : "Saved";

            if (!write.Written)
            {
                saved.Live = true;
                saved.Note = removed
                    ? "Nothing to remove: " + packId + " has no " + code + " subtitle for this line."
                    : "Unchanged: " + write.Path + " already says that.";
                return saved;
            }

            if (!MasterDataFeature.Enabled)
            {
                saved.Note = verb + " in " + write.Path + ", but pack text is off (MasterData.Enabled=false), so the game does not show it.";
                return saved;
            }

            MasterDataSubtitleSource.SetLive(code, id, effective.Value);

            if (!LanguageFeature.Registry.TryGet(code, out _))
            {
                saved.Note = verb + " in " + write.Path + "; no pack declares " + code
                             + " this session, so the game shows it after a pack declares the language and the game restarts.";
                return saved;
            }

            var others = effective.HasValue && !string.Equals(effective.PackId, packId, StringComparison.OrdinalIgnoreCase);
            if (!removed && others)
            {
                saved.Note = verb + " in " + write.Path + ", but " + effective.PackId + " loads later and has its own "
                             + code + " text for this line, so the game shows that one.";
                return saved;
            }

            saved.Live = true;
            saved.Note = removed
                ? (others
                    ? "Removed; the game now shows " + effective.PackId + "'s " + code + " text for this line."
                    : "Removed; the game falls back for this line (the language's fallback text, or the game's own).")
                : "Saved; the game shows it the next time this line appears in " + code + ".";
            return saved;
        }

        private static MakerLayer<string> EffectiveSubtitle(string code, string id, string savedPackId, string? savedText)
        {
            var layers = new List<MakerLayer<string>>();
            foreach (var pack in PackManager.Loaded)
            {
                if (string.Equals(pack.Id, savedPackId, StringComparison.OrdinalIgnoreCase))
                {
                    layers.Add(new MakerLayer<string>(pack.Id, savedText));
                    continue;
                }

                string? text = null;
                try
                {
                    var path = pack.FullPath(MakerEditRules.SubtitlePath(code));
                    if (File.Exists(path))
                    {
                        text = MakerSubtitleFile.Read(File.ReadAllText(path), id);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                    CwyLsModLoader.Log.LogDebug(LogPrefix + " could not read " + pack.Id + "'s " + code + " table: " + ex.Message);
                }

                layers.Add(new MakerLayer<string>(pack.Id, string.IsNullOrEmpty(text) ? null : text));
            }

            return MakerLayers.Effective(layers);
        }
    }
}
