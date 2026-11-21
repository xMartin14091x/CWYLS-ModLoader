using System.Collections.Generic;
using System.Threading;
using CwyLs.ModLoader.Core.Api.Maker;

namespace CwyLs.ModLoader.Tests
{
    /// <summary>
    /// Synchronous views of the Studio catalog and creator for tests. Both complete their work before returning (no
    /// await inside), so a blocking read is exact; keeping it here keeps test methods free of blocking waits.
    /// </summary>
    internal static class MakerStudioTestExtensions
    {
        internal static MakerResult<MakerStudioPackCreated> CreateNow(this IMakerStudioNewPacks creator, MakerStudioCreatePackRequest request, CancellationToken cancel = default) =>
            creator.CreatePackAsync(request, cancel).GetAwaiter().GetResult();

        internal static MakerResult<IReadOnlyList<MakerStudioPack>> PacksNow(this IMakerStudioCatalog catalog) =>
            catalog.PacksAsync(default).GetAwaiter().GetResult();

        internal static MakerResult<MakerStudioLinePage> LinesNow(this IMakerStudioCatalog catalog, MakerStudioLineQuery query) =>
            catalog.LinesAsync(query, default).GetAwaiter().GetResult();

        internal static MakerResult<MakerStudioLine> LineNow(this IMakerStudioCatalog catalog, string packId, string lang, string id, string? occurrenceId) =>
            catalog.LineAsync(packId, lang, id, occurrenceId, default).GetAwaiter().GetResult();

        internal static MakerResult<MakerStudioGroups> GroupsNow(this IMakerStudioCatalog catalog, string packId, string lang) =>
            catalog.GroupsAsync(packId, lang, default).GetAwaiter().GetResult();
    }
}
