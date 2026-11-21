using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    public enum MakerOperationCheckKind { New = 0, Replay = 1, Conflict = 2, RecoveryRequired = 3 }
    public sealed class MakerOperationCheck
    {
        public MakerOperationCheckKind Kind { get; set; }
        public MakerOperationReceipt? Receipt { get; set; }
        public string? Message { get; set; }
        public MakerRecoveryDetails? Details { get; set; }
    }

    /// <summary>Receipts are read under the pack gate after recovery, with expiry based on their recorded creation time.</summary>
    public sealed class MakerOperationLedger
    {
        private readonly MakerProjectStore _store;
        public MakerOperationLedger(MakerProjectStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

        public MakerOperationCheck Check(string operationId, string fingerprint)
        {
            lock (_store.SyncRoot)
            {
                if (!MakerStudioPaths.IsOperationId(operationId))
                    return new MakerOperationCheck { Kind = MakerOperationCheckKind.Conflict, Message = "invalid operationId" };
                var recovery = _store.RecoverPending();
                if (!recovery.Ok) return new MakerOperationCheck { Kind = MakerOperationCheckKind.RecoveryRequired, Message = recovery.Message, Details = recovery.Details };
                var text = _store.Files.ReadText(MakerStudioPaths.Receipt(operationId), out _);
                if (text is null) return new MakerOperationCheck { Kind = MakerOperationCheckKind.New };
                if (!MakerProjectJson.TryReceipt(text, out var receipt, out var problem) || !Created(receipt, out _))
                    return new MakerOperationCheck { Kind = MakerOperationCheckKind.RecoveryRequired, Message = "Unreadable operation receipt: " + problem, Details = Details(operationId) };
                if (Expired(receipt)) return new MakerOperationCheck { Kind = MakerOperationCheckKind.New };
                return receipt.Fingerprint == fingerprint
                    ? new MakerOperationCheck { Kind = MakerOperationCheckKind.Replay, Receipt = receipt }
                    : new MakerOperationCheck { Kind = MakerOperationCheckKind.Conflict, Message = "operationId was already used for another request" };
            }
        }

        public MakerCommitFile ReceiptTarget(MakerOperationReceipt receipt) =>
            _store.Target(MakerStudioPaths.Receipt(receipt.OperationId), MakerProjectStore.Utf8(MakerProjectJson.WriteReceipt(receipt)));

        public MakerResult<MakerStudioOperation> Read(string operationId)
        {
            lock (_store.SyncRoot)
            {
                if (!MakerStudioPaths.IsOperationId(operationId)) return MakerResult<MakerStudioOperation>.Fail(MakerErrorCodes.BadRequest, "invalid operationId");
                var recovery = _store.RecoverPending();
                if (!recovery.Ok) return MakerResult<MakerStudioOperation>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                var text = _store.Files.ReadText(MakerStudioPaths.Receipt(operationId), out _);
                if (text is null) return MakerResult<MakerStudioOperation>.Fail(MakerErrorCodes.NotFound, "no retained operation");
                if (!MakerProjectJson.TryReceipt(text, out var receipt, out var problem) || !Created(receipt, out _))
                    return MakerResult<MakerStudioOperation>.Fail(MakerStudioErrorCodes.RecoveryRequired, "Unreadable receipt: " + problem, Details(operationId));
                if (Expired(receipt)) return MakerResult<MakerStudioOperation>.Fail(MakerErrorCodes.NotFound, "operation receipt expired");
                return MakerResult<MakerStudioOperation>.Success(new MakerStudioOperation
                { State = "committed", Revision = receipt.Revision, Saved = receipt.Saved, RecoveryRequired = false });
            }
        }

        /// <summary>Best-effort final live outcome update. Failure cannot invalidate an already committed save.</summary>
        public bool UpdateResponse(string operationId, JsonObject response, Action? beforeWrite = null)
        {
            lock (_store.SyncRoot)
            {
                try
                {
                    if (_store.Files.Exists(MakerStudioPaths.Pending(operationId))) return false;
                    var path = MakerStudioPaths.Receipt(operationId);
                    var text = _store.Files.ReadText(path, out _);
                    if (text is null || !MakerProjectJson.TryReceipt(text, out var receipt, out _)) return false;
                    receipt.Response = response;
                    var bytes = MakerProjectStore.Utf8(MakerProjectJson.WriteReceipt(receipt));
                    if (_store.Files.BytesUnder(MakerStudioPaths.Root) + bytes.Length > _store.Quota) return false;
                    beforeWrite?.Invoke();
                    _store.Files.WriteAtomic(path, bytes);
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { return false; }
            }
        }

        public void Prune()
        {
            lock (_store.SyncRoot)
            {
                if (!_store.RecoverPending().Ok) return;
                var receipts = new List<(string Path, DateTimeOffset Created)>();
                foreach (var path in _store.Files.ListFiles(MakerStudioPaths.Transactions))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!path.EndsWith(".json", StringComparison.Ordinal) || !MakerStudioPaths.IsOperationId(name)) continue;
                    var text = _store.Files.ReadText(path, out _);
                    if (text != null && MakerProjectJson.TryReceipt(text, out var receipt, out _) && Created(receipt, out var at))
                        receipts.Add((path, at));
                }
                var ordered = receipts.OrderByDescending(x => x.Created).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();
                for (var i = 0; i < ordered.Count; i++)
                {
                    var op = Path.GetFileNameWithoutExtension(ordered[i].Path);
                    if (_store.Files.Exists(MakerStudioPaths.Pending(op))) continue;
                    if (i >= 100 || _store.Clock.Now - ordered[i].Created >= TimeSpan.FromHours(24))
                    {
                        try { _store.Files.Delete(ordered[i].Path); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }

        private bool Expired(MakerOperationReceipt receipt) =>
            !Created(receipt, out var at) || _store.Clock.Now - at >= TimeSpan.FromHours(24);
        private static bool Created(MakerOperationReceipt receipt, out DateTimeOffset at) =>
            DateTimeOffset.TryParse(receipt.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
        private MakerRecoveryDetails Details(string operationId) => RecoveryDetails(operationId);
        internal MakerRecoveryDetails RecoveryDetails(string operationId) => new MakerRecoveryDetails
        {
            PackId = Path.GetFileName(_store.Files.Root), Resource = MakerStudioPaths.Receipt(operationId), BlockedOperationId = operationId,
        };

        public static string Fingerprint(string kind, params object?[] values)
        {
            var text = kind + "\n" + string.Join("\n", values.Select(v => v is null ? "<null>" : v.ToString()));
            return MakerPackFiles.Fingerprint(System.Text.Encoding.UTF8.GetBytes(text));
        }
        public static MakerOperationReceipt Receipt(string operationId, string fingerprint, string kind, string revision, bool saved,
            DateTimeOffset now, JsonObject response) => new MakerOperationReceipt
        {
            OperationId = operationId, Fingerprint = fingerprint, Kind = kind, Revision = revision, Saved = saved,
            CreatedAt = MakerProjectRevision.At(now), Response = response,
        };
    }
}
