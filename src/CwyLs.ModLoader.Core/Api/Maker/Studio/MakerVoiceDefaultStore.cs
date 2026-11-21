using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>The stored pack voice default (<c>.maker/voice.json</c>, contract v2.4). Origin and consent are both null when none is set.</summary>
    public sealed class MakerVoiceDefaultDocument
    {
        public int Schema { get; set; } = MakerStudioContract.StorageSchema;
        public string Revision { get; set; } = string.Empty;
        public string? Origin { get; set; }
        public MakerStudioConsent? Consent { get; set; }
        public string? UpdatedAt { get; set; }
        /// <summary>
        /// v2.5 storage member <c>inherited</c>: the attestation every <c>consentSource:"pack"</c> take follows. Equal to
        /// the default while one is set; after a clear, the last default. Null in a v2.4 file: inheriting takes then keep
        /// their creation snapshot until the default next changes.
        /// </summary>
        public string? InheritedOrigin { get; set; }
        public MakerStudioConsent? InheritedConsent { get; set; }
    }

    /// <summary>
    /// The pack's voice origin and consent (contract v2.4), set once so new takes can inherit it. A separate file, so
    /// <c>project.json</c> (whose parse failure marks a pack unavailable) is untouched. Writes follow
    /// <see cref="MakerGlossaryStore.Save"/>: store lock, pending recovery, operation ledger, revision check, receipt, commit.
    /// v2.5: inheriting takes follow the default. They are resolved when read (<see cref="ResolveInherited"/>), not
    /// rewritten, because a normal commit holds at most 100 files and a dubbed pack has far more lines; a change rewrites
    /// the disclosures of published inheriting takes in <c>voice-provenance.json</c> in the same commit.
    /// </summary>
    public sealed class MakerVoiceDefaultStore
    {
        /// <summary>The <c>consent_required</c> message for an inheriting take when the pack has no default.</summary>
        public const string NoDefaultMessage = "set the pack's voice origin and consent first, or send origin and consent with the take";

        private const string Kind = "voice.default";
        private readonly MakerProjectStore _store;
        private readonly MakerOperationLedger _operations;

        public MakerVoiceDefaultStore(MakerProjectStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _operations = new MakerOperationLedger(store);
        }

        public MakerResult<MakerStudioVoiceDefault> Read()
        {
            var result = ReadDocument();
            return result.Ok
                ? MakerResult<MakerStudioVoiceDefault>.Success(ToWire(result.Value!))
                : MakerResult<MakerStudioVoiceDefault>.Fail(result.Error!, result.Message!, result.Details);
        }

        /// <summary>The stored default; an absent file is "none set" with a stable initial revision.</summary>
        internal MakerResult<MakerVoiceDefaultDocument> ReadDocument()
        {
            lock (_store.SyncRoot)
            {
                try
                {
                    var recovery = _store.RecoverPending();
                    if (!recovery.Ok) return MakerResult<MakerVoiceDefaultDocument>.Fail(recovery.Error!, recovery.Message!, recovery.Details);
                    var header = _store.ReadHeader();
                    if (!header.Ok) return MakerResult<MakerVoiceDefaultDocument>.Fail(header.Error!, header.Message!, header.Details);
                    var path = MakerStudioPaths.VoiceDefault;
                    var collision = _store.CaseCollision(path);
                    if (collision != null) return MakerResult<MakerVoiceDefaultDocument>.Fail(MakerStudioErrorCodes.Conflict, collision);
                    var text = _store.ReadTextSnapshot(path, out _, out _);
                    if (text is null)
                    {
                        return MakerResult<MakerVoiceDefaultDocument>.Success(new MakerVoiceDefaultDocument
                        {
                            Revision = MakerProjectRevision.Initial("voice", path, string.Empty),
                        });
                    }

                    return TryParse(text, out var document, out var problem)
                        ? MakerResult<MakerVoiceDefaultDocument>.Success(document)
                        : MakerResult<MakerVoiceDefaultDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, path + " is unreadable: " + problem);
                }
                catch (UnauthorizedAccessException ex)
                {
                    return MakerResult<MakerVoiceDefaultDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable, ex.Message);
                }
            }
        }

        public MakerResult<MakerStudioVoiceDefault> Save(MakerStudioVoiceDefaultRequest request)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            var validation = Validate(request.Origin, request.Consent);
            if (validation != null) return MakerResult<MakerStudioVoiceDefault>.Fail(MakerErrorCodes.BadRequest, validation);
            lock (_store.SyncRoot)
            {
                var recovered = _store.RecoverPending();
                if (!recovered.Ok) return MakerResult<MakerStudioVoiceDefault>.Fail(recovered.Error!, recovered.Message!, recovered.Details);

                var set = request.Origin != null;
                var fingerprint = MakerOperationLedger.Fingerprint(Kind, request.PackId, request.ExpectedRevision, request.Origin,
                    set ? (request.Consent!.Confirmed ? "1" : "0") : null, set ? request.Consent!.StatementVersion : null);
                var checkedOperation = _operations.Check(request.OperationId, fingerprint);
                if (checkedOperation.Kind == MakerOperationCheckKind.Replay) return Replay(checkedOperation.Receipt!, _operations.RecoveryDetails(request.OperationId));
                if (checkedOperation.Kind != MakerOperationCheckKind.New)
                {
                    return MakerResult<MakerStudioVoiceDefault>.Fail(
                        checkedOperation.Kind == MakerOperationCheckKind.Conflict ? MakerStudioErrorCodes.Conflict : MakerStudioErrorCodes.RecoveryRequired,
                        checkedOperation.Message ?? "the operation cannot continue", checkedOperation.Details);
                }

                var headerResult = _store.ReadHeader();
                if (!headerResult.Ok) return MakerResult<MakerStudioVoiceDefault>.Fail(headerResult.Error!, headerResult.Message!, headerResult.Details);
                var currentResult = ReadDocument();
                if (!currentResult.Ok) return MakerResult<MakerStudioVoiceDefault>.Fail(currentResult.Error!, currentResult.Message!, currentResult.Details);
                var current = currentResult.Value!;
                if (!string.Equals(current.Revision, request.ExpectedRevision, StringComparison.Ordinal))
                {
                    return MakerResult<MakerStudioVoiceDefault>.Fail(MakerStudioErrorCodes.Conflict, "the pack's voice default changed; reload it before saving");
                }

                // Setting: confirmedAt is now whenever the origin changes or no default was set; an identical PUT is a no-op.
                // Clearing an unset default is a no-op too; clearing a set one keeps the file so the revision stays monotonic.
                var changed = set
                    ? current.Origin is null || current.Consent is null || !string.Equals(current.Origin, request.Origin, StringComparison.Ordinal)
                    : current.Origin != null || current.Consent != null;
                MakerCommitFile? provenance = null;
                if (changed)
                {
                    var nowIso = MakerProjectRevision.At(_store.Clock.Now);
                    current.Revision = MakerProjectRevision.New();
                    // v2.5: inheriting takes follow a new default; a clear leaves them at the default being cleared.
                    if (!set) (current.InheritedOrigin, current.InheritedConsent) = (current.Origin, Copy(current.Consent));
                    current.Origin = request.Origin;
                    current.Consent = set
                        ? new MakerStudioConsent { Confirmed = true, StatementVersion = MakerStudioContract.ConsentVersion, ConfirmedAt = nowIso }
                        : null;
                    if (set) (current.InheritedOrigin, current.InheritedConsent) = (current.Origin, Copy(current.Consent));
                    current.UpdatedAt = nowIso;
                    var follow = FollowPublished(current);
                    if (!follow.Ok) return MakerResult<MakerStudioVoiceDefault>.Fail(follow.Error!, follow.Message!, follow.Details);
                    provenance = follow.Value;
                }

                var wire = ToWire(current);
                var receipt = MakerOperationLedger.Receipt(request.OperationId, fingerprint, Kind, current.Revision, changed, _store.Clock.Now, MakerStudioJson.VoiceDefault(wire));
                var targets = new List<MakerCommitFile>();
                var header = _store.HeaderTarget(headerResult.Value!);
                if (header != null) targets.Add(header);
                if (changed) targets.Add(_store.Target(MakerStudioPaths.VoiceDefault, MakerProjectStore.Utf8(Write(current))));
                if (provenance != null) targets.Add(provenance);
                targets.Add(_operations.ReceiptTarget(receipt));
                var committed = _store.Commit(request.OperationId, targets, kind: Kind);
                if (!committed.Ok) return MakerResult<MakerStudioVoiceDefault>.Fail(committed.Error!, committed.Message!, committed.Details);
                _operations.Prune();
                return MakerResult<MakerStudioVoiceDefault>.Success(wire);
            }
        }

        /// <summary>Null when valid: origin and consent both null (clear), or a known origin with a confirmed <c>consent-1</c>.</summary>
        internal static string? Validate(string? origin, MakerStudioConsent? consent)
        {
            if (origin is null && consent is null) return null;
            if (origin is null || consent is null) return "send origin and consent together, or both null to clear the default";
            if (!MakerTakeRules.ValidateOrigin(origin, out var originProblem)) return originProblem;
            if (!MakerTakeRules.ValidateConsent(consent, out var consentProblem)) return consentProblem;
            if (!consent.Confirmed) return "consent.confirmed must be true";
            return null;
        }

        internal static string Write(MakerVoiceDefaultDocument value)
        {
            var members = new List<KeyValuePair<string, string>>
            {
                Pair("schema", MiniJson.Number(value.Schema)),
                Pair("revision", MiniJson.String(value.Revision)),
                Pair("origin", MiniJson.String(value.Origin)),
                Pair("consent", WriteConsent(value.Consent)),
                Pair("updatedAt", MiniJson.String(value.UpdatedAt)),
            };
            // v2.5 member; a v2.4 build ignores it (its parser reads known members only).
            if (value.InheritedOrigin != null && value.InheritedConsent != null)
                members.Add(Pair("inherited", MiniJson.Object(
                    Pair("origin", MiniJson.String(value.InheritedOrigin)), Pair("consent", WriteConsent(value.InheritedConsent)))));
            return MiniJson.Object(members);
        }

        private static string WriteConsent(MakerStudioConsent? consent) => consent is null ? "null" : MiniJson.Object(
            Pair("confirmed", MiniJson.Bool(consent.Confirmed)),
            Pair("statementVersion", MiniJson.String(consent.StatementVersion)),
            Pair("confirmedAt", MiniJson.String(consent.ConfirmedAt)));

        private static MakerStudioConsent? Copy(MakerStudioConsent? consent) => consent is null ? null : new MakerStudioConsent
        {
            Confirmed = consent.Confirmed, StatementVersion = consent.StatementVersion, ConfirmedAt = consent.ConfirmedAt,
        };

        /// <summary>v2.5: <see cref="MakerProjectStore.ReadLine"/> with inheriting takes resolved (take pages, take mutations, publish).</summary>
        internal static MakerResult<MakerLineDocument> ReadLineResolved(MakerProjectStore store, string lang, string id)
        {
            var read = store.ReadLine(lang, id);
            return read.Ok ? ResolveInherited(store, read.Value!) : read;
        }

        /// <summary>
        /// v2.5: points every <c>consentSource:"pack"</c> take of <paramref name="line"/> at the pack's inherited attestation,
        /// so reads, mutations and publish all see the current default. A v2.4 file (no <c>inherited</c>) or no file leaves
        /// the creation snapshot. Fails only when the line has inheriting takes and <c>.maker/voice.json</c> is unreadable.
        /// </summary>
        internal static MakerResult<MakerLineDocument> ResolveInherited(MakerProjectStore store, MakerLineDocument line)
        {
            if (!line.Takes.Any(t => string.Equals(t.ConsentSource, MakerStudioConsentSources.Pack, StringComparison.Ordinal)))
                return MakerResult<MakerLineDocument>.Success(line);
            var text = store.ReadTextSnapshot(MakerStudioPaths.VoiceDefault, out _, out _);
            if (text is null) return MakerResult<MakerLineDocument>.Success(line);
            if (!TryParse(text, out var document, out var problem))
                return MakerResult<MakerLineDocument>.Fail(MakerStudioErrorCodes.MetadataUnavailable,
                    MakerStudioPaths.VoiceDefault + " is unreadable (" + problem + "), so the takes that inherit it cannot be resolved");
            if (document.InheritedOrigin is null || document.InheritedConsent is null) return MakerResult<MakerLineDocument>.Success(line);
            foreach (var take in line.Takes)
            {
                if (!string.Equals(take.ConsentSource, MakerStudioConsentSources.Pack, StringComparison.Ordinal)) continue;
                take.Origin = document.InheritedOrigin;
                take.Consent = Copy(document.InheritedConsent)!;
            }

            return MakerResult<MakerLineDocument>.Success(line);
        }

        /// <summary>
        /// v2.5.1: the new <c>voice-provenance.json</c> when <paramref name="take"/> is the clip's Studio publication (the clip
        /// record names this line and take) and its disclosure differs from the take's attestation; null when nothing differs
        /// or there is no file or entry. Hash and publication time are kept. An unreadable file refuses the change.
        /// IG-01: a take without a confirmed consent (origin changed without a fresh confirmation, consent withdrawn) is
        /// disclosed with its current origin and <see cref="MakerVoiceProvenance.Unconfirmed"/>, which export refuses.
        /// </summary>
        internal static MakerResult<MakerCommitFile?> Disclose(MakerProjectStore store, string lang, string lineId, string clip, MakerTakeRecord take)
        {
            var text = store.ReadTextSnapshot(MakerStudioPaths.Provenance, out _, out _);
            var recordText = store.Files.ReadText(MakerStudioPaths.Clip(lang, clip), out _);
            if (text is null || recordText is null || !MakerClipStore.TryRecord(recordText, out var record)
                || record.LineId != lineId || record.TakeId != take.Id)
                return MakerResult<MakerCommitFile?>.Success(null);
            if (!MakerVoiceProvenance.TryRead(text, out var entries, out var problem))
                return MakerResult<MakerCommitFile?>.Fail(MakerStudioErrorCodes.MetadataUnavailable,
                    MakerStudioPaths.Provenance + " is unreadable (" + problem + "); fix or remove it before changing this take's origin or consent");
            var confirmed = take.Consent.Confirmed && take.Consent.ConfirmedAt != null
                && take.Consent.StatementVersion == MakerStudioContract.ConsentVersion;
            var confirmedAt = confirmed ? take.Consent.ConfirmedAt! : MakerVoiceProvenance.Unconfirmed;
            var entry = entries.FirstOrDefault(e => e.Lang == lang && e.Clip == clip);
            if (entry is null || (entry.Origin == take.Origin && entry.StatementVersion == take.Consent.StatementVersion
                && entry.ConfirmedAt == confirmedAt)) return MakerResult<MakerCommitFile?>.Success(null);
            (entry.Origin, entry.StatementVersion, entry.ConfirmedAt) = (take.Origin, take.Consent.StatementVersion, confirmedAt);
            return MakerResult<MakerCommitFile?>.Success(store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
        }

        /// <summary>
        /// v2.5: the new <c>voice-provenance.json</c> when a published take that inherits the default (the clip record names
        /// its line and take, the line still publishes that take to that clip) has a disclosure that differs from
        /// <paramref name="next"/>'s inherited attestation; null when nothing differs. Hash and publication time are kept;
        /// disclosures of takes with their own consent are untouched. An unreadable line refuses the change; an unreadable
        /// provenance file refuses it only when a published take inherits the default (TD-8), since only such a take's
        /// disclosure would have to change.
        /// </summary>
        private MakerResult<MakerCommitFile?> FollowPublished(MakerVoiceDefaultDocument next)
        {
            var text = _store.ReadTextSnapshot(MakerStudioPaths.Provenance, out _, out _);
            var origin = next.InheritedOrigin;
            var statement = next.InheritedConsent?.StatementVersion;
            var confirmedAt = next.InheritedConsent?.ConfirmedAt;
            if (text is null || origin is null || statement is null || confirmedAt is null) return MakerResult<MakerCommitFile?>.Success(null);
            if (!MakerVoiceProvenance.TryRead(text, out var entries, out var problem))
                return UnreadableProvenance(problem);
            var updated = 0;
            foreach (var entry in entries)
            {
                var published = InheritingPublication(entry.Lang, entry.Clip);
                if (!published.Ok) return MakerResult<MakerCommitFile?>.Fail(published.Error!, published.Message!);
                if (published.Value is null) continue;
                if (entry.Origin == origin && entry.StatementVersion == statement && entry.ConfirmedAt == confirmedAt) continue;
                (entry.Origin, entry.StatementVersion, entry.ConfirmedAt) = (origin, statement, confirmedAt);
                updated++;
            }

            return MakerResult<MakerCommitFile?>.Success(updated == 0 ? null : _store.Target(MakerStudioPaths.Provenance, MakerVoiceProvenance.Write(entries)));
        }

        /// <summary>
        /// An unreadable <c>voice-provenance.json</c>: the change goes ahead (the file untouched) unless a clip record shows a
        /// published take that inherits the default, whose disclosure is in that file.
        /// </summary>
        private MakerResult<MakerCommitFile?> UnreadableProvenance(string problem)
        {
            MakerResult<MakerCommitFile?> Refuse(string why) => MakerResult<MakerCommitFile?>.Fail(MakerStudioErrorCodes.MetadataUnavailable,
                MakerStudioPaths.Provenance + " is unreadable (" + problem + ") and " + why + "; fix or remove it before changing the pack's voice default");
            IReadOnlyList<string> records;
            try { records = _store.Files.ListFiles(MakerStudioPaths.Clips); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return Refuse("the clip records cannot be listed (" + ex.Message + ")"); }
            foreach (var path in records)
            {
                if (!MakerStudioPaths.IsClipRecordPath(path)) continue;
                var parts = path.Split('/');
                var published = InheritingPublication(parts[2], parts[3].Substring(0, parts[3].Length - ".json".Length));
                if (!published.Ok) return MakerResult<MakerCommitFile?>.Fail(published.Error!, published.Message!);
                if (published.Value != null) return Refuse("a published take inherits the default, so its disclosure must follow");
            }

            return MakerResult<MakerCommitFile?>.Success(null);
        }

        /// <summary>
        /// The published take of one clip that inherits the default (its record names the line and take, the line still
        /// publishes that take to that clip), or null. Fails when that line is unreadable.
        /// </summary>
        private MakerResult<MakerTakeRecord?> InheritingPublication(string lang, string clip)
        {
            string recordPath;
            try { recordPath = MakerStudioPaths.Clip(lang, clip); }
            catch (ArgumentException) { return MakerResult<MakerTakeRecord?>.Success(null); }
            var recordText = _store.ReadTextSnapshot(recordPath, out _, out _);
            if (recordText is null || !MakerClipStore.TryRecord(recordText, out var record)
                || record.Lang != lang || record.Clip != clip) return MakerResult<MakerTakeRecord?>.Success(null);
            string linePath;
            try { linePath = MakerStudioPaths.Line(lang, record.LineId); }
            catch (ArgumentException) { return MakerResult<MakerTakeRecord?>.Success(null); }
            var lineText = _store.ReadTextSnapshot(linePath, out _, out _);
            if (lineText is null) return MakerResult<MakerTakeRecord?>.Success(null);
            if (!MakerProjectJson.TryLine(lineText, out var line, out var lineProblem))
                return MakerResult<MakerTakeRecord?>.Fail(MakerStudioErrorCodes.MetadataUnavailable,
                    linePath + " is unreadable (" + lineProblem + "); fix it before changing the pack's voice default");
            if (line.PublishedTakeId != record.TakeId || line.PublishedClip != clip) return MakerResult<MakerTakeRecord?>.Success(null);
            var take = line.Takes.FirstOrDefault(t => string.Equals(t.Id, record.TakeId, StringComparison.Ordinal));
            return MakerResult<MakerTakeRecord?>.Success(
                take != null && string.Equals(take.ConsentSource, MakerStudioConsentSources.Pack, StringComparison.Ordinal) ? take : null);
        }

        internal static bool TryParse(string text, out MakerVoiceDefaultDocument value, out string problem)
        {
            value = new MakerVoiceDefaultDocument();
            if (!JsonReader.TryParse(text, out var parsed, out var error) || !(parsed is JsonObject obj))
            {
                problem = error ?? "the top-level value is not an object";
                return false;
            }

            if (!obj.TryGetValue("schema", out var schema) || !(schema is long s) || s != MakerStudioContract.StorageSchema)
            {
                problem = "schema must be " + MakerStudioContract.StorageSchema;
                return false;
            }

            if (!(obj.TryGetValue("revision", out var revisionNode) && revisionNode is string revision && revision.Length > 0))
            {
                problem = "revision must be text";
                return false;
            }

            if (!TryNode(obj, "origin", out var originNode, out problem) || !TryNode(obj, "consent", out var consentNode, out problem)
                || !TryNode(obj, "updatedAt", out var updatedNode, out problem))
                return false;
            if (updatedNode != null && !(updatedNode is string))
            {
                problem = "updatedAt must be text or null";
                return false;
            }

            if ((originNode is null) != (consentNode is null))
            {
                problem = "origin and consent must be set or cleared together";
                return false;
            }

            if (originNode != null)
            {
                if (!(originNode is string origin) || !MakerTakeRules.IsOrigin(origin))
                {
                    problem = "origin must be own, ai-assisted or ai-generated";
                    return false;
                }

                if (!TryConsent(consentNode, out var consent))
                {
                    problem = "consent must be a confirmed " + MakerStudioContract.ConsentVersion + " object";
                    return false;
                }

                value.Origin = origin;
                value.Consent = consent;
            }

            if (obj.TryGetValue("inherited", out var inheritedNode) && inheritedNode != null)
            {
                if (!(inheritedNode is JsonObject inherited)
                    || !(inherited.TryGetValue("origin", out var inheritedOrigin) && inheritedOrigin is string io && MakerTakeRules.IsOrigin(io))
                    || !inherited.TryGetValue("consent", out var inheritedConsent) || !TryConsent(inheritedConsent, out var ic))
                {
                    problem = "inherited must hold an origin and a confirmed " + MakerStudioContract.ConsentVersion + " consent";
                    return false;
                }

                value.InheritedOrigin = io;
                value.InheritedConsent = ic;
            }

            value.Revision = revision;
            value.UpdatedAt = updatedNode as string;
            problem = string.Empty;
            return true;
        }

        private static bool TryNode(JsonObject obj, string key, out object? node, out string problem)
        {
            if (!obj.TryGetValue(key, out node))
            {
                problem = key + " is required";
                return false;
            }

            problem = string.Empty;
            return true;
        }

        private static bool TryConsent(object? node, out MakerStudioConsent consent)
        {
            consent = new MakerStudioConsent();
            if (!(node is JsonObject c)
                || !(c.TryGetValue("confirmed", out var confirmed) && confirmed is bool yes && yes)
                || !(c.TryGetValue("statementVersion", out var version) && version is string v && v == MakerStudioContract.ConsentVersion)
                || !(c.TryGetValue("confirmedAt", out var at) && at is string confirmedAt))
                return false;
            consent = new MakerStudioConsent { Confirmed = true, StatementVersion = v, ConfirmedAt = confirmedAt };
            return true;
        }

        private static MakerStudioVoiceDefault ToWire(MakerVoiceDefaultDocument value) => new MakerStudioVoiceDefault
        {
            Revision = value.Revision,
            Origin = value.Origin,
            Consent = value.Consent is null ? null : new MakerStudioConsent
            {
                Confirmed = value.Consent.Confirmed, StatementVersion = value.Consent.StatementVersion, ConfirmedAt = value.Consent.ConfirmedAt,
            },
            UpdatedAt = value.UpdatedAt,
        };

        private static MakerResult<MakerStudioVoiceDefault> Replay(MakerOperationReceipt receipt, MakerRecoveryDetails details)
        {
            var r = receipt.Response;
            if (!(r.TryGetValue("revision", out var revisionNode) && revisionNode is string revision)
                || !r.TryGetValue("origin", out var originNode) || (originNode != null && !(originNode is string))
                || !r.TryGetValue("consent", out var consentNode) || (consentNode != null && !(consentNode is JsonObject))
                || !r.TryGetValue("updatedAt", out var updatedNode) || (updatedNode != null && !(updatedNode is string)))
                return MakerResult<MakerStudioVoiceDefault>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the voice default receipt is incomplete", details);
            MakerStudioConsent? consent = null;
            if (consentNode is JsonObject c)
            {
                if (!(c.TryGetValue("confirmed", out var confirmed) && confirmed is bool yes)
                    || !(c.TryGetValue("statementVersion", out var version) && version is string v)
                    || !c.TryGetValue("confirmedAt", out var at) || (at != null && !(at is string)))
                    return MakerResult<MakerStudioVoiceDefault>.Fail(MakerStudioErrorCodes.RecoveryRequired, "the voice default receipt is incomplete", details);
                consent = new MakerStudioConsent { Confirmed = yes, StatementVersion = v, ConfirmedAt = at as string };
            }

            return MakerResult<MakerStudioVoiceDefault>.Success(new MakerStudioVoiceDefault
            {
                Revision = revision, Origin = originNode as string, Consent = consent, UpdatedAt = updatedNode as string,
            });
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }
}
