using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>A content pack Studio created whose folder is on disk but which no pack scan has loaded yet.</summary>
    public sealed class MakerPendingPack
    {
        /// <summary>Creates a pending pack description.</summary>
        public MakerPendingPack(string id, string name, string version, IReadOnlyList<string> languages, IReadOnlyList<string> declaredLanguages, string rootPath)
        {
            Id = id; Name = name; Version = version; Languages = languages; DeclaredLanguages = declaredLanguages; RootPath = rootPath;
        }

        /// <summary>The pack id (also its folder name).</summary>
        public string Id { get; }

        /// <summary>The manifest name.</summary>
        public string Name { get; }

        /// <summary>The manifest version.</summary>
        public string Version { get; }

        /// <summary>The language codes the pack has a subtitle table or a declaration for, ordinally sorted.</summary>
        public IReadOnlyList<string> Languages { get; }

        /// <summary>The codes its <c>language.json</c> declares, if it has one.</summary>
        public IReadOnlyList<string> DeclaredLanguages { get; }

        /// <summary>The absolute pack folder.</summary>
        public string RootPath { get; }

        /// <summary>The Studio pack entry of a pending pack: not loaded, not editable until one restart.</summary>
        public MakerStudioPack ToStudioPack() => new MakerStudioPack
        {
            Id = Id, Name = Name, Version = Version, Languages = Languages,
            Editable = false, ReadOnlyReason = MakerPendingPacks.RestartReason,
            Loaded = false, RestartRequired = true,
            StudioEditable = false, StudioReadOnlyReason = MakerPendingPacks.RestartReason,
            MetadataState = MakerPendingPacks.PendingState,
        };
    }

    /// <summary>
    /// Finds the packs Studio created and the game has not loaded. A pending pack is a folder directly in <c>CWYLS Mods</c>
    /// (not a hidden one, not a link) with a valid content-only <c>manifest.json</c> whose id is the folder name and a
    /// <c>.maker/project.json</c>, which neither the loaded nor the refused packs of this session include. Nothing is written.
    /// </summary>
    public static class MakerPendingPacks
    {
        /// <summary>The <see cref="MakerStudioPack.MetadataState"/> of a pending pack.</summary>
        public const string PendingState = "pending";

        /// <summary>Why a pending pack cannot be edited or played yet (also the 409 message).</summary>
        public const string RestartReason = "It was created after the game started: restart the game once to load it.";

        /// <summary>Lists the pending packs, ordered by id.</summary>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder; null or missing means none.</param>
        /// <param name="isKnownId">True when the id belongs to a loaded or refused pack of this session (compared case-insensitively by the caller).</param>
        public static IReadOnlyList<MakerPendingPack> Scan(string? packsRoot, Func<string, bool> isKnownId)
        {
            var found = new List<MakerPendingPack>();
            if (string.IsNullOrWhiteSpace(packsRoot) || !Directory.Exists(packsRoot)) return found;
            string[] folders;
            try { folders = Directory.GetDirectories(packsRoot!); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return found; }
            Array.Sort(folders, StringComparer.Ordinal);
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                if (string.IsNullOrEmpty(name) || name[0] == '.' || MakerPackRules.IsLink(folder)) continue;
                var pack = Read(folder, name);
                if (pack != null && !isKnownId(pack.Id)) found.Add(pack);
            }

            return found;
        }

        /// <summary>Finds one pending pack by id (case-insensitive).</summary>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder.</param>
        /// <param name="packId">The pack id.</param>
        /// <param name="isKnownId">As for <see cref="Scan"/>.</param>
        /// <param name="pack">The pack, or null.</param>
        public static bool TryFind(string? packsRoot, string? packId, Func<string, bool> isKnownId, out MakerPendingPack? pack)
        {
            pack = null;
            if (string.IsNullOrEmpty(packId)) return false;
            foreach (var candidate in Scan(packsRoot, isKnownId))
            {
                if (string.Equals(candidate.Id, packId, StringComparison.OrdinalIgnoreCase)) { pack = candidate; return true; }
            }

            return false;
        }

        private static MakerPendingPack? Read(string folder, string name)
        {
            try
            {
                var files = new MakerPackFiles(folder);
                var manifestPath = files.Resolve(PackContentIndex.ManifestFileName);
                if (!File.Exists(manifestPath) || !File.Exists(files.Resolve(MakerStudioPaths.Project))) return null;
                if (!PackManifestParser.TryParse(File.ReadAllText(manifestPath), out var manifest, out _) || manifest is null) return null;
                if (manifest.IsCodeMod || !string.Equals(manifest.Id, name, StringComparison.Ordinal)) return null;

                var declared = new List<string>();
                var languagePath = files.Resolve(LanguageDeclarationParser.FileName);
                if (File.Exists(languagePath)
                    && LanguageDeclarationParser.TryParse(File.ReadAllText(languagePath), manifest.Id, out var declarations, out _))
                {
                    foreach (var declaration in declarations) declared.Add(declaration.Code);
                }

                var languages = new SortedSet<string>(declared, StringComparer.Ordinal);
                var subtitles = files.Resolve("data/subtitles");
                if (Directory.Exists(subtitles) && !MakerPackRules.IsLink(subtitles))
                {
                    foreach (var file in Directory.GetFiles(subtitles))
                    {
                        var code = SubtitleTables.LanguageOf(SubtitleTables.Folder + Path.GetFileName(file));
                        if (code != null) languages.Add(code);
                    }
                }

                return new MakerPendingPack(manifest.Id, manifest.Name, manifest.Version.ToString(),
                    new List<string>(languages), declared, folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
            {
                return null;
            }
        }
    }
}
