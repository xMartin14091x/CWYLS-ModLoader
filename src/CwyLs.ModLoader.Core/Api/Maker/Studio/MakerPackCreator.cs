using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;
using CwyLs.ModLoader.Core.Language;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>
    /// Creates a content pack for Studio (<c>POST /v1/maker/studio/packs</c>, the <c>newPacks</c> feature). The skeleton is built
    /// in a hidden staging folder of <c>CWYLS Mods</c> (the pack scan ignores folders that start with a dot), checked with the
    /// loader's own manifest, language and subtitle-table parsers, and moved into place with one rename, so a pack folder never
    /// exists half-written and an existing folder is never touched. The creation receipt lives inside the new pack, which is what
    /// lets an exact replay answer the same 201 and a reused <c>operationId</c> answer 409.
    /// </summary>
    public sealed class MakerPackCreator : IMakerStudioNewPacks
    {
        /// <summary>The receipt kind of a pack creation.</summary>
        public const string ReceiptKind = "pack-create";

        /// <summary>The response note: the pack is on disk but the game has not loaded it.</summary>
        public const string RestartNote = "Restart the game once to load this pack; until then it cannot be edited or played.";

        private const int MaxDescription = 500;

        private const int MaxText = 100;
        private static readonly object Gate = new object();
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly Func<string?> _packsRoot;
        private readonly Func<string, bool> _isKnownPack;
        private readonly Func<string, string?> _languageOwner;
        private readonly Func<string> _gameVersion;
        private readonly Func<string> _loaderVersion;
        private readonly Action<string, IReadOnlyDictionary<string, object?>> _publish;
        private readonly IApiClock _clock;
        private readonly Action? _beforeMove;
        private readonly int _maxPackCount;
        private readonly long _maxPackBytes;
        private readonly long _minimumFreeBytes;
        private readonly Func<string, long> _freeBytes;
        private const int MaxStorageEntries = 100000;

        /// <summary>Preserves the original constructor entry point for already compiled adapters.</summary>
        public MakerPackCreator(Func<string?> packsRoot, Func<string, bool> isKnownPack, Func<string, string?> languageOwner,
            Func<string>? gameVersion, Func<string>? loaderVersion, Action<string, IReadOnlyDictionary<string, object?>>? publish,
            IApiClock? clock, Action? beforeMove)
            : this(packsRoot, isKnownPack, languageOwner, gameVersion, loaderVersion, publish, clock, beforeMove,
                1024, 8L * 1024 * 1024, 64L * 1024 * 1024, null) { }

        /// <summary>Creates the service.</summary>
        /// <param name="packsRoot">The absolute <c>CWYLS Mods</c> folder, or null when there is none.</param>
        /// <param name="isKnownPack">True when a pack with this id loaded or was refused this session.</param>
        /// <param name="languageOwner">The id of the pack that registered a language code this session, or null when the code is unregistered.</param>
        /// <param name="gameVersion">The game version to write into the manifest.</param>
        /// <param name="loaderVersion">The loader version to write into the manifest.</param>
        /// <param name="publish">Event sink; defaults to <see cref="MakerBackends.Publish"/>.</param>
        /// <param name="clock">The clock; defaults to the system clock.</param>
        /// <param name="beforeMove">Test seam: runs after staging and before the rename.</param>
        /// <param name="maxPackCount">Maximum regular, visible pack folders (including existing packs); defaults to 1024.</param>
        /// <param name="maxPackBytes">Maximum bytes in the new pack skeleton; defaults to 8 MiB. Other packs do not count.</param>
        /// <param name="minimumFreeBytes">Free space to keep after staging; defaults to 64 MiB.</param>
        /// <param name="freeBytes">Test seam for available disk bytes.</param>
        public MakerPackCreator(
            Func<string?> packsRoot, Func<string, bool> isKnownPack, Func<string, string?> languageOwner,
            Func<string>? gameVersion = null, Func<string>? loaderVersion = null,
            Action<string, IReadOnlyDictionary<string, object?>>? publish = null, IApiClock? clock = null, Action? beforeMove = null,
            int maxPackCount = 1024, long maxPackBytes = 8L * 1024 * 1024,
            long minimumFreeBytes = 64L * 1024 * 1024, Func<string, long>? freeBytes = null)
        {
            _packsRoot = packsRoot ?? throw new ArgumentNullException(nameof(packsRoot));
            _isKnownPack = isKnownPack ?? throw new ArgumentNullException(nameof(isKnownPack));
            _languageOwner = languageOwner ?? throw new ArgumentNullException(nameof(languageOwner));
            _gameVersion = gameVersion ?? new Func<string>(() => CwyLs.ModLoader.Core.GameVersionGate.ExpectedGameVersion);
            _loaderVersion = loaderVersion ?? new Func<string>(() => CwyLs.ModLoader.Core.Mods.CwyApi.PluginVersionText);
            _publish = publish ?? MakerBackends.Publish;
            _clock = clock ?? SystemApiClock.Instance;
            _beforeMove = beforeMove;
            if (maxPackCount < 1 || maxPackBytes < 1 || minimumFreeBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxPackCount));
            _maxPackCount = maxPackCount;
            _maxPackBytes = maxPackBytes;
            _minimumFreeBytes = minimumFreeBytes;
            _freeBytes = freeBytes ?? (path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace);
        }

        /// <summary>Null when creation can work now; otherwise the reason it cannot (no packs folder, or it is read-only).</summary>
        public string? UnavailableReason()
        {
            var root = _packsRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return "The CWYLS Mods folder does not exist.";
            if (MakerPackRules.IsLink(root!)) return "The CWYLS Mods folder is a symbolic link or junction; creation requires a real folder.";
            return MakerPackRules.IsWritableFolder(root!) ? null : "The CWYLS Mods folder is read-only.";
        }

        /// <inheritdoc />
        public Task<MakerResult<MakerStudioPackCreated>> CreatePackAsync(MakerStudioCreatePackRequest request, CancellationToken cancel)
        {
            try
            {
                cancel.ThrowIfCancellationRequested();
                return Task.FromResult(Create(request));
            }
            catch (OperationCanceledException)
            {
                return Task.FromResult(MakerResult<MakerStudioPackCreated>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled."));
            }
        }

        private MakerResult<MakerStudioPackCreated> Create(MakerStudioCreatePackRequest request)
        {
            var problem = ValidateSyntax(request, out var code, out var declaration);
            if (problem != null) return Failure(MakerErrorCodes.BadRequest, problem);

            var unavailable = UnavailableReason();
            if (unavailable != null) return Failure(MakerErrorCodes.Unavailable, unavailable);
            var root = _packsRoot()!;

            lock (Gate)
            {
                var fingerprint = MakerOperationLedger.Fingerprint(ReceiptKind, request.Id, request.Name, request.Author,
                    code, declaration?.Caption, declaration?.EnglishName, declaration?.Script,
                    declaration?.Fallback is null ? null : string.Join(",", declaration.Fallback));
                // v2.3: the description joins the fingerprint only when sent, so receipts written before it still replay.
                if (request.Description != null) fingerprint = MakerOperationLedger.Fingerprint(ReceiptKind, fingerprint, request.Description);

                var replay = FindReceipt(root, request.OperationId, out var receipt);
                if (replay == ReceiptState.Unreadable)
                    return Failure(MakerStudioErrorCodes.Conflict, "operationId is held by an unreadable receipt; use another operationId.");
                if (replay == ReceiptState.Found)
                {
                    if (receipt!.Fingerprint != fingerprint)
                        return Failure(MakerStudioErrorCodes.Conflict, "operationId was already used for another request");
                    return MakerStudioCatalogJson.TryPackCreated(receipt.Response, out var again)
                        ? MakerResult<MakerStudioPackCreated>.Success(again)
                        : Failure(MakerStudioErrorCodes.Conflict, "the creation receipt is incomplete; use another operationId.");
                }

                problem = ValidateRegistry(code, declaration);
                if (problem != null) return Failure(MakerErrorCodes.BadRequest, problem);

                var collision = CollisionWith(root, request.Id);
                if (collision != null) return Failure(MakerStudioErrorCodes.Conflict, collision);

                return Stage(root, request, code, declaration, fingerprint);
            }
        }

        private MakerResult<MakerStudioPackCreated> Stage(string root, MakerStudioCreatePackRequest request, string code,
            MakerStudioLanguage? declaration, string fingerprint)
        {
            var target = Path.Combine(root, request.Id);
            var manifest = ManifestText(request);
            if (!PackManifestParser.TryParse(manifest, out var parsed, out var manifestProblem) || parsed is null
                || parsed.IsCodeMod || !string.Equals(parsed.Id, request.Id, StringComparison.Ordinal))
                return Failure(MakerErrorCodes.BadRequest, "the manifest would not load: " + (manifestProblem ?? "unexpected content"));
            var payloads = new Dictionary<string, byte[]> { [PackContentIndex.ManifestFileName] = Utf8.GetBytes(manifest) };
            if (declaration != null)
            {
                var languageText = LanguageText(code, declaration);
                if (!LanguageDeclarationParser.TryParse(languageText, request.Id, out _, out var languageProblem))
                    return Failure(MakerErrorCodes.BadRequest, languageProblem ?? "the language declaration would not load");
                payloads[LanguageDeclarationParser.FileName] = Utf8.GetBytes(languageText);
            }
            const string emptyTable = "{}\n";
            var table = SubtitleTables.Parse(emptyTable, MakerEditRules.SubtitlePath(code), code);
            if (table.Rows.Count != 0 || table.Rejected.Count != 0)
                return Failure(MakerErrorCodes.Internal, "the empty subtitle table did not parse as empty");
            payloads[MakerEditRules.SubtitlePath(code)] = Utf8.GetBytes(emptyTable);
            var header = new MakerProjectHeader { ProjectId = MakerProjectRevision.New() };
            payloads[MakerStudioPaths.Project] = Utf8.GetBytes(MakerProjectJson.WriteHeader(header));
            var pending = new MakerPendingPack(request.Id, request.Name.Trim(), parsed.Version.ToString(),
                new[] { code }, declaration != null ? new[] { code } : new string[0], target);
            var created = new MakerStudioPackCreated
            {
                Pack = pending.ToStudioPack(), RestartRequired = true, Live = false, Note = RestartNote,
            };
            var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, ReceiptKind, header.ProjectId, true,
                _clock.Now, MakerStudioCatalogJson.PackCreated(created));
            payloads[MakerStudioPaths.Receipt(request.OperationId)] = Utf8.GetBytes(MakerProjectJson.WriteReceipt(receipt));

            var staging = Path.Combine(root, ".maker-create-" + Guid.NewGuid().ToString("N"));
            var ownsStaging = false;
            try
            {
                var storage = StorageProblem(root, payloads.Values.Sum(bytes => (long)bytes.Length));
                if (storage != null) return Failure(MakerStudioErrorCodes.StorageLimit, storage + " Folder: '" + target + "'. Existing files were preserved.");
                // A custom language that another pending pack already declares would register twice after the restart.
                if (declaration != null)
                {
                    foreach (var existingPending in MakerPendingPacks.Scan(root, _isKnownPack))
                    {
                        if (existingPending.DeclaredLanguages.Contains(code))
                            return Failure(MakerStudioErrorCodes.Conflict,
                                "language '" + code + "' is already declared by the pending pack '" + existingPending.Id + "'");
                    }
                }


                if (Directory.Exists(staging) || File.Exists(staging) || MakerPackRules.IsLink(staging))
                    return Failure(MakerErrorCodes.Unavailable, "The staging path is occupied; retry.");
                Directory.CreateDirectory(staging);
                ownsStaging = true;
                var files = new MakerPackFiles(staging);
                foreach (var payload in payloads) files.WriteAtomic(payload.Key, payload.Value);

                _beforeMove?.Invoke();
                try
                {
                    // One rename: it fails, leaving both folders as they are, when anything now exists at the target.
                    Directory.Move(staging, target);
                }
                catch (IOException) when (Directory.Exists(target) || File.Exists(target))
                {
                    return Failure(MakerStudioErrorCodes.Conflict, "a pack folder named '" + request.Id + "' appeared; nothing was changed");
                }

                Publish(request.Id);
                return MakerResult<MakerStudioPackCreated>.Success(created);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return Failure(MakerErrorCodes.Unavailable, "The pack folder could not be created (" + ex.GetType().Name + "): " + ex.Message);
            }
            finally
            {
                // Our own hidden folder, created a moment ago under a random name; after a successful move it is gone already.
                try { if (ownsStaging && Directory.Exists(staging) && !MakerPackRules.IsLink(staging)) Directory.Delete(staging, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* a leftover hidden folder is ignored by the pack scan */ }
            }
        }

        private void Publish(string packId)
        {
            try
            {
                _publish("maker.pack.created", new Dictionary<string, object?> { ["packId"] = packId, ["restartRequired"] = true });
                _publish("maker.catalog.changed", new Dictionary<string, object?>
                {
                    ["catalogRevision"] = MakerProjectRevision.New(), ["reason"] = "pack.created",
                });
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                // Events are hints; the pack is already on disk and the response says so.
            }
        }

        private static string? ValidateSyntax(MakerStudioCreatePackRequest request, out string code, out MakerStudioLanguage? declaration)
        {
            code = string.Empty;
            declaration = null;
            if (request is null) return "a request is required";
            if (!MakerStudioPaths.IsOperationId(request.OperationId)) return "operationId must be 32 lowercase hexadecimal characters";
            if (!PackManifest.IsValidId(request.Id)) return "id must match " + PackManifest.IdPattern + " (3 to 64 lowercase letters, digits, '.', '_' and '-')";
            var segment = SafeFileNames.ProblemWith(request.Id);
            if (segment != null) return "id is not a safe folder name: " + segment;
            if (MakerEditRules.IsReferencePack(request.Id)) return "id belongs to the loader's reference packs, which are replaced on every deploy; choose another id";
            var name = TextProblem(request.Name, "name", true);
            if (name != null) return name;
            var author = TextProblem(request.Author, "author", false);
            if (author != null) return author;
            var description = DescriptionProblem(request.Description);
            if (description != null) return description;

            var language = request.Language;
            if (language is null) return "language is required";
            if (!MakerEditRules.TryLanguage(language.Code, out code, out var codeProblem)) return codeProblem;
            if (!MakerEditRules.AcceptsSubtitles(code, out var builtIn)) return builtIn;

            var hasFields = language.Caption != null || language.EnglishName != null || language.Script != null || language.Fallback != null;
            if (!hasFields) return null;

            var caption = TextProblem(language.Caption, "language.caption", true);
            if (caption != null) return caption;
            var english = TextProblem(language.EnglishName, "language.englishName", true);
            if (english != null) return english;
            if (language.Script != null && TextProblem(language.Script, "language.script", true) is string script) return script;
            if (language.Fallback != null && language.Fallback.Count > 8) return "language.fallback may name at most 8 languages";
            declaration = language;
            // The loader's own parser has the last word (script, fallback codes, repeats); it runs again on the staged file.
            return LanguageDeclarationParser.TryParse(LanguageText(code, language), request.Id, out _, out var declarationProblem) ? null : declarationProblem;
        }

        private string? ValidateRegistry(string code, MakerStudioLanguage? declaration)
        {
            var owner = _languageOwner(code);
            if (owner != null)
                return declaration != null
                    ? "language '" + code + "' is already registered by pack '" + owner + "'; send only its code, or choose a code no pack has registered"
                    : null;
            return declaration is null ? "language.caption is required for an unregistered language" : null;
        }

        // Check only incoming bytes and the shallow folder count. Unrelated pack contents,
        // hidden export archives and symlink targets do not spend a new pack's byte budget.
        private string? StorageProblem(string root, long incoming)
        {
            try
            {
                if (MakerPackRules.IsLink(root)) return "The packs root is a symbolic link or junction; creation requires a real folder.";
                if (incoming > _maxPackBytes) return "The new pack skeleton byte limit would be exceeded (" + _maxPackBytes + " bytes); shorten its metadata, then retry.";
                var folders = 0;
                var entries = 0;
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    if (++entries > MaxStorageEntries) return "The packs folder has too many entries to check safely; move unused entries elsewhere, then retry.";
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) continue;
                    if (++folders >= _maxPackCount) return "The pack folder count limit is reached (" + _maxPackCount + "); for cleanup, move unused packs out of CWYLS Mods, then retry.";
                }
                // Reserve directory/atomic-write overhead as well as serialized bytes before creating a folder.
                var available = _freeBytes(root);
                const long stagingOverhead = 64 * 1024;
                if (available < _minimumFreeBytes || incoming > available - _minimumFreeBytes
                    || stagingOverhead > available - _minimumFreeBytes - incoming)
                    return "Insufficient free space to stage the new pack and retain " + _minimumFreeBytes + " free bytes; free disk space for cleanup, then retry.";
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                return "Pack storage could not be checked safely: " + ex.Message;
            }
        }

        private static string? DescriptionProblem(string? value)
        {
            if (value is null) return null;
            if (!MakerEditRules.TryCodePointCount(value, out var count)) return "description contains an unpaired Unicode surrogate";
            if (count > MaxDescription) return "description may be at most " + MaxDescription + " characters";
            foreach (var c in value)
            {
                if ((c < ' ' && c != '\n') || c == '\u007f') return "description must not contain control characters other than a line break";
            }

            return null;
        }

        private static string? TextProblem(string? value, string name, bool required)
        {
            if (value is null) return required ? name + " is required" : null;
            if (string.IsNullOrWhiteSpace(value)) return required ? name + " must not be blank" : null;
            if (!MakerEditRules.TryCodePointCount(value, out var count)) return name + " contains an unpaired Unicode surrogate";
            if (count > MaxText) return name + " may be at most " + MaxText + " characters";
            foreach (var c in value)
            {
                if (c < ' ' || c == '\u007f') return name + " must not contain control characters";
            }

            return null;
        }

        private string ManifestText(MakerStudioCreatePackRequest request)
        {
            var text = new StringBuilder("{\n");
            text.Append("  \"id\": ").Append(MiniJson.String(request.Id)).Append(",\n");
            text.Append("  \"name\": ").Append(MiniJson.String(request.Name.Trim())).Append(",\n");
            text.Append("  \"version\": \"0.1.0\",\n");
            if (!string.IsNullOrWhiteSpace(request.Author)) text.Append("  \"author\": ").Append(MiniJson.String(request.Author!.Trim())).Append(",\n");
            text.Append("  \"description\": ").Append(MiniJson.String(string.IsNullOrWhiteSpace(request.Description) ? "Created with Pack Maker Studio." : request.Description!.Trim())).Append(",\n");
            text.Append("  \"gameVersion\": ").Append(MiniJson.String(_gameVersion())).Append(",\n");
            text.Append("  \"loaderVersion\": ").Append(MiniJson.String(_loaderVersion())).Append("\n}\n");
            return text.ToString();
        }

        private static string LanguageText(string code, MakerStudioLanguage language)
        {
            var text = new StringBuilder("{\n  \"schema\": 1,\n");
            text.Append("  \"code\": ").Append(MiniJson.String(code)).Append(",\n");
            text.Append("  \"caption\": ").Append(MiniJson.String(language.Caption?.Trim())).Append(",\n");
            text.Append("  \"englishName\": ").Append(MiniJson.String(language.EnglishName?.Trim()));
            if (!string.IsNullOrWhiteSpace(language.Script)) text.Append(",\n  \"script\": ").Append(MiniJson.String(language.Script!.Trim()));
            if (language.Fallback != null)
            {
                var items = new List<string>();
                foreach (var item in language.Fallback) items.Add(MiniJson.String(item));
                text.Append(",\n  \"fallback\": [").Append(string.Join(", ", items)).Append("]");
            }

            return text.Append("\n}\n").ToString();
        }

        private enum ReceiptState { None, Found, Unreadable }

        private ReceiptState FindReceipt(string root, string operationId, out MakerOperationReceipt? receipt)
        {
            receipt = null;
            string[] folders;
            try { folders = Directory.GetDirectories(root); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return ReceiptState.None; }
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                if (string.IsNullOrEmpty(name) || name[0] == '.' || MakerPackRules.IsLink(folder)) continue;
                var path = Path.Combine(folder, ".maker", "transactions", operationId + ".json");
                try
                {
                    if (MakerPackRules.IsLink(Path.Combine(folder, ".maker"))
                        || MakerPackRules.IsLink(Path.Combine(folder, ".maker", "transactions"))
                        || MakerPackRules.IsLink(path)) continue;
                    if (!File.Exists(path)) continue;
                    if (!MakerProjectJson.TryReceipt(File.ReadAllText(path), out var parsed, out _)
                        || !DateTimeOffset.TryParse(parsed.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var at))
                        return ReceiptState.Unreadable;
                    // Pack receipts of other kinds are the pack's own business; only a creation receipt answers here, and
                    // only inside the retention window every receipt has.
                    if (parsed.Kind != ReceiptKind || _clock.Now - at >= TimeSpan.FromHours(24)) continue;
                    receipt = parsed;
                    return ReceiptState.Found;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return ReceiptState.Unreadable;
                }
            }

            return ReceiptState.None;
        }

        private string? CollisionWith(string root, string id)
        {
            if (_isKnownPack(id)) return "a pack with the id '" + id + "' is already installed; nothing was changed";
            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(root))
                {
                    if (string.Equals(Path.GetFileName(entry), id, StringComparison.OrdinalIgnoreCase))
                        return "a folder named '" + Path.GetFileName(entry) + "' already exists in CWYLS Mods (names are compared ignoring case); nothing was changed";
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "the CWYLS Mods folder could not be read: " + ex.Message;
            }

            return null;
        }

        private static MakerResult<MakerStudioPackCreated> Failure(string code, string message) =>
            MakerResult<MakerStudioPackCreated>.Fail(code, message);
    }
}
