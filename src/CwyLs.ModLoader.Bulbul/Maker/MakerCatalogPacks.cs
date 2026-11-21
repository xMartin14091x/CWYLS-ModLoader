using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The pack half of the catalog's inputs: every pack the registry loaded this session (folder,
    /// manifest, the languages its <c>language.json</c> registered) and every pack it refused.
    /// </summary>
    /// <remarks>
    /// Safe on any thread: <see cref="PackManager.Loaded"/> and <see cref="PackManager.Skipped"/> are
    /// read under the registry's lock, and the language registry is built once at startup and only
    /// read afterwards. Nothing here touches Unity.
    /// </remarks>
    internal static class MakerCatalogPacks
    {
        /// <summary>The loaded packs in load order, then the refused ones (minus any whose id a loaded pack holds).</summary>
        internal static List<MakerPackSource> Sources()
        {
            var declared = DeclaredByPack();
            var sources = new List<MakerPackSource>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pack in PackManager.Loaded)
            {
                if (pack is null || !ids.Add(pack.Id))
                {
                    continue;
                }

                sources.Add(new MakerPackSource(
                    pack.Id,
                    pack.Manifest.Name,
                    pack.Manifest.Version.ToString(),
                    pack.RootPath,
                    pack.Manifest.IsCodeMod,
                    declared.TryGetValue(pack.Id, out var codes) ? codes : null));
            }

            foreach (var skipped in PackManager.Skipped)
            {
                var id = string.IsNullOrWhiteSpace(skipped?.Id) ? skipped?.FolderName : skipped!.Id;
                if (skipped is null || string.IsNullOrWhiteSpace(id) || !ids.Add(id!))
                {
                    continue;
                }

                var problem = skipped.Detail.Length == 0 ? skipped.Reason.ToString() : skipped.Reason + " - " + skipped.Detail;
                sources.Add(new MakerPackSource(id!, id, null, null, false, null, problem));
            }

            return sources;
        }

        /// <summary>Pack id to the language codes its <c>language.json</c> registered.</summary>
        private static Dictionary<string, List<string>> DeclaredByPack()
        {
            var byPack = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var language in LanguageFeature.Registry.Languages)
                {
                    var packId = language?.Declaration?.PackId;
                    if (string.IsNullOrEmpty(packId))
                    {
                        continue;
                    }

                    if (!byPack.TryGetValue(packId!, out var codes))
                    {
                        codes = new List<string>();
                        byPack.Add(packId!, codes);
                    }

                    codes.Add(language!.Code);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(MakerCatalog.LogPrefix + " the pack languages could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }

            return byPack;
        }
    }
}
