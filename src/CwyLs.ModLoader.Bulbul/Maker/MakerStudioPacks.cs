using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Studio;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The game-facing bindings of Studio's real catalog and pack creation (<see cref="MakerStudioCatalog"/>,
    /// <see cref="MakerPackCreator"/>): where the packs folder is, which packs and languages this session knows, and the
    /// versions to write. The rules live in Core; nothing here touches Unity off the main thread.
    /// </summary>
    internal static class MakerStudioPacks
    {
        /// <summary>The Studio catalog, served from the v1 catalog's cached snapshot.</summary>
        internal static MakerStudioCatalog Catalog { get; } = new MakerStudioCatalog(new SnapshotSource());

        /// <summary>The pack creator.</summary>
        internal static MakerPackCreator Creator { get; } =
            new MakerPackCreator(() => ModLoaderPaths.PacksRoot, IsKnownPack, LanguageOwner, GameVersion, () => CwyLsModLoader.PluginVersion);

        /// <summary>The creator when it can work now (the packs folder exists and is writable), otherwise null so <c>newPacks</c> is not advertised.</summary>
        internal static IMakerStudioNewPacks? CreatorWhenUsable() => Creator.UnavailableReason() is null ? Creator : null;

        /// <summary>
        /// Contract v2.3 pack details, icon and export. Zips go to <c>CWYLS Mods/.maker-exports/</c> (a dot folder, so the
        /// pack scan skips it); progress warnings come from the Studio catalogue when it answers within 3 s. The DLC gate's
        /// verdict refuses a pack with content for paid DLC lines the player is not known to own, or when it cannot be read.
        /// </summary>
        internal static MakerPackExporter Exporter { get; } = new MakerPackExporter(
            MakerStudioEditor.Open, PendingRoot, () => Path.Combine(ModLoaderPaths.PacksRoot, MakerPackExporter.ExportsFolderName),
            progress: (pack, lang, cancel) => Catalog.GroupsAsync(pack, lang, cancel),
            paid: cancel => MakerCatalog.Instance.HiddenAsync(cancel));

        private static string? PendingRoot(string packId) =>
            !IsKnownPack(packId) && MakerPendingPacks.TryFind(ModLoaderPaths.PacksRoot, packId, IsKnownPack, out var pending) ? pending?.RootPath : null;

        /// <summary>
        /// A Studio pack route on a pack Studio created that the game has not loaded: 409 <c>restart_required</c>, not a
        /// misleading 404. Null for any other pack.
        /// </summary>
        /// <param name="packId">The pack id from the route.</param>
        internal static MakerResult<MakerProjectStore>? PendingRefusal(string packId) =>
            !IsKnownPack(packId) && MakerPendingPacks.TryFind(ModLoaderPaths.PacksRoot, packId, IsKnownPack, out _)
                ? MakerResult<MakerProjectStore>.Fail(MakerStudioErrorCodes.RestartRequired, MakerPendingPacks.RestartReason)
                : null;

        private static bool IsKnownPack(string id)
        {
            foreach (var pack in PackManager.Loaded)
            {
                if (string.Equals(pack.Id, id, StringComparison.OrdinalIgnoreCase)) return true;
            }

            foreach (var skipped in PackManager.Skipped)
            {
                if (string.Equals(skipped.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(skipped.FolderName, id, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static string? LanguageOwner(string code)
        {
            try
            {
                foreach (var language in LanguageFeature.Registry.Languages)
                {
                    if (language != null && string.Equals(language.Code, code, StringComparison.Ordinal))
                    {
                        return language.Declaration?.PackId ?? "the game";
                    }
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(MakerCatalog.LogPrefix + " the registered languages could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            return null;
        }

        /// <summary>The running game's version from the cached status, or the version this loader was built for when that is unknown or not a version.</summary>
        private static string GameVersion()
        {
            var cached = MakerEvents.CachedStatus()?.GameVersion;
            return !string.IsNullOrEmpty(cached) && SemVer.TryParse(cached!, out _, out _) ? cached! : GameVersionGate.ExpectedGameVersion;
        }

        private sealed class SnapshotSource : IMakerStudioCatalogSource
        {
            public Task<MakerResult<MakerStudioCatalogSnapshot>> SnapshotAsync(bool editorVerdicts, CancellationToken cancel) =>
                MakerCatalog.Instance.StudioSnapshotAsync(editorVerdicts, cancel);
        }
    }
}
