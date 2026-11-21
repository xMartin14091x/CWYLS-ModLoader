using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>
    /// Contract v2.3 (Pack Maker Phase C): pack details and icon for the Packs and New pack pages, and the Export page's
    /// checks, version bump, release notes and shareable zip. One backend advertises <c>packInfo</c> and <c>export</c>.
    /// </summary>
    public interface IMakerStudioExport
    {
        /// <summary><c>GET P/info</c>; works on a pack Studio created that the game has not loaded yet.</summary>
        Task<MakerResult<MakerPackInfo>> InfoAsync(string packId, CancellationToken cancel);

        /// <summary><c>GET P/icon</c>: the pack's <c>icon.png</c>; never for a reference pack.</summary>
        Task<MakerResult<MakerBinary>> ReadIconAsync(string packId, CancellationToken cancel);

        /// <summary><c>PUT P/icon</c>: a checked PNG; works on a pending Studio pack too.</summary>
        Task<MakerResult<MakerPackIcon>> SaveIconAsync(string packId, byte[] png, CancellationToken cancel);

        /// <summary><c>GET P/export</c>: the checks and the next versions, without writing anything.</summary>
        Task<MakerResult<MakerExportPreview>> CheckAsync(string packId, CancellationToken cancel);

        /// <summary><c>POST P/export</c>: bump, release notes, zip.</summary>
        Task<MakerResult<MakerExportResult>> ExportAsync(MakerExportRequest request, CancellationToken cancel);

        /// <summary><c>GET P/exports/{fileName}</c>: a zip this pack's export wrote.</summary>
        Task<MakerResult<MakerBinary>> ReadExportAsync(string packId, string fileName, CancellationToken cancel);
    }

    /// <summary>Raw bytes and their media type.</summary>
    public sealed class MakerBinary
    {
        public byte[] Bytes { get; set; } = new byte[0];
        public string MediaType { get; set; } = "application/octet-stream";
        /// <summary>A download name for <c>Content-Disposition</c>, or null to show inline.</summary>
        public string? FileName { get; set; }
    }

    public sealed class MakerChangelogEntry
    {
        public string Version { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }

    public sealed class MakerPackInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string? Author { get; set; }
        public string? Description { get; set; }
        public IReadOnlyList<string> Languages { get; set; } = new string[0];
        public bool HasIcon { get; set; }
        public long SizeBytes { get; set; }
        public bool Pending { get; set; }
        public MakerChangelogEntry? Changelog { get; set; }
    }

    public sealed class MakerPackIcon
    {
        public bool HasIcon { get; set; } = true;
        public long SizeBytes { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public static class MakerExportSeverity
    {
        public const string Ok = "ok";
        public const string Warning = "warning";
        public const string Error = "error";
    }

    public sealed class MakerExportCheck
    {
        public string Id { get; set; } = string.Empty;
        public string Severity { get; set; } = MakerExportSeverity.Ok;
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
        public IReadOnlyList<string> Paths { get; set; } = new string[0];

        /// <summary>
        /// What the check found, as a key the page words in its own language (TD-13), e.g. <c>subCount</c>; <see cref="Message"/>
        /// is the same in English for logs and older pages.
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// The values <see cref="Code"/> needs: numbers (<c>long</c>), strings, and for <c>progress</c> a <c>langs</c> list of
        /// <c>{lang, dub, voiced, sub, total}</c>. Never text from the game.
        /// </summary>
        public IReadOnlyDictionary<string, object?> Params { get; set; } = new Dictionary<string, object?>();
    }

    public sealed class MakerExportFiles
    {
        public int Included { get; set; }
        public long IncludedBytes { get; set; }
        public int Excluded { get; set; }
        public long ExcludedBytes { get; set; }
    }

    public sealed class MakerExportNextVersions
    {
        public string Patch { get; set; } = string.Empty;
        public string Minor { get; set; } = string.Empty;
        public string Major { get; set; } = string.Empty;
    }

    public sealed class MakerLastExport
    {
        public string FileName { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>The folder on this computer that holds the zip, or null when the exports folder is unknown. Not stored.</summary>
        public string? Folder { get; set; }
    }

    public sealed class MakerExportPreview
    {
        public string PackId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public MakerExportNextVersions NextVersions { get; set; } = new MakerExportNextVersions();
        public bool Ok { get; set; }
        public int ErrorCount { get; set; }
        public int WarningCount { get; set; }
        public IReadOnlyList<MakerExportCheck> Checks { get; set; } = new MakerExportCheck[0];
        public MakerExportFiles Files { get; set; } = new MakerExportFiles();
        public MakerChangelogEntry? Changelog { get; set; }
        public MakerLastExport? LastExport { get; set; }
    }

    public sealed class MakerExportRequest
    {
        public string PackId { get; set; } = string.Empty;
        public string OperationId { get; set; } = string.Empty;
        public string ExpectedVersion { get; set; } = string.Empty;
        /// <summary><c>none</c>, <c>patch</c>, <c>minor</c> or <c>major</c>.</summary>
        public string Bump { get; set; } = "none";
        public string? Notes { get; set; }
    }

    public sealed class MakerExportResult
    {
        public string PackId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PreviousVersion { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public int Files { get; set; }
        public bool Downloadable { get; set; }
        public bool ManifestUpdated { get; set; }
        public bool ChangelogUpdated { get; set; }
        public int WarningCount { get; set; }
        public string Note { get; set; } = string.Empty;

        /// <summary>
        /// The folder on this computer that holds the zip (<c>CWYLS Mods/.maker-exports/&lt;packId&gt;</c>), or null when the
        /// exports folder is unknown. Not stored in the receipt: it is where the zip is on this computer now.
        /// </summary>
        public string? Folder { get; set; }
    }
}
