using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CwyLs.ModLoader.Core.Api.Maker
{
    // Pack Maker "Dub & Subtitle Studio" contract (ticket DS-00, research/2026-11-07/dub-studio/CONTRACT.md).
    // The maker routes (DS-01, CwyLs.ModLoader.Api) call these interfaces on the API's worker threads; the game
    // adapter (CwyLs.ModLoader.Bulbul) implements them: the catalog in DS-02, the editor in DS-03. Implementations
    // must not touch Unity off the main thread: hop with the main-thread dispatcher and give up after the timeout.
    // Every call answers with data or a MakerError; none throws for an expected failure.

    /// <summary>Read side: what the game and the packs hold. Implemented in DS-02.</summary>
    public interface IMakerCatalog
    {
        /// <summary>Game, loader, language and room state for the status pill.</summary>
        Task<MakerResult<MakerStatus>> StatusAsync(CancellationToken cancel);

        /// <summary>The content packs Pack Maker may write to, and why the others are read-only.</summary>
        Task<MakerResult<IReadOnlyList<MakerPack>>> PacksAsync(CancellationToken cancel);

        /// <summary>One page of the game's voiced lines, with the subtitle and dub state for <see cref="MakerLineQuery.Lang"/>.</summary>
        Task<MakerResult<MakerLinePage>> LinesAsync(MakerLineQuery query, CancellationToken cancel);

        /// <summary>One line by its localisation id, or <c>not_found</c>.</summary>
        Task<MakerResult<MakerLine>> LineAsync(string id, string lang, CancellationToken cancel);
    }

    /// <summary>Write side: subtitles, dubs and playback. Implemented in DS-03.</summary>
    public interface IMakerEditor
    {
        /// <summary>Sets (text) or removes (null) one subtitle in a pack's <c>data/subtitles/&lt;lang&gt;.json</c>, then reloads it live.</summary>
        Task<MakerResult<MakerSaved>> SaveSubtitleAsync(string packId, string lang, string id, string? text, CancellationToken cancel);

        /// <summary>Sets (WAV bytes) or removes (null) one dub at <c>audio/voice/&lt;lang&gt;/&lt;clip&gt;.wav</c> in a pack, then swaps it in live.</summary>
        Task<MakerResult<MakerSaved>> SaveVoiceAsync(string packId, string lang, string clip, byte[]? wav, CancellationToken cancel);

        /// <summary>Makes Satone say the line in the game: <c>original</c> = the game's own clip, <c>dub</c> = the pack's clip for <paramref name="lang"/>.</summary>
        Task<MakerResult<MakerSaved>> PlayAsync(string id, string variant, string lang, CancellationToken cancel);
    }

    /// <summary>Where adapters publish maker events (line spoken, saved, reloaded, language changed). Implemented in DS-01.</summary>
    public interface IMakerEventSink
    {
        /// <summary>Records one event. <paramref name="data"/> holds strings, numbers, booleans or null only. Safe from any thread; never throws.</summary>
        void Publish(string type, IReadOnlyDictionary<string, object?> data);
    }

    /// <summary>The slots the maker routes read. The adapter fills Catalog and Editor in its Awake; DS-01 fills Events.</summary>
    public static class MakerBackends
    {
        private static readonly object Gate = new object();
        private static IMakerCatalog? _catalog;
        private static IMakerEditor? _editor;
        private static IMakerEventSink? _events;
        private static MakerStudioBackends? _studio;

        public static IMakerCatalog? Catalog { get { lock (Gate) { return _catalog; } } }
        public static IMakerEditor? Editor { get { lock (Gate) { return _editor; } } }
        public static IMakerEventSink? Events { get { lock (Gate) { return _events; } } }
        /// <summary>The optional Studio v2 feature backends. Null until the v2 implementation is installed.</summary>
        public static MakerStudioBackends? Studio { get { lock (Gate) { return _studio; } } }

        public static void UseCatalog(IMakerCatalog? catalog) { lock (Gate) { _catalog = catalog; } }
        public static void UseEditor(IMakerEditor? editor) { lock (Gate) { _editor = editor; } }
        public static void UseEvents(IMakerEventSink? events) { lock (Gate) { _events = events; } }
        /// <summary>Installs or removes the optional Studio v2 feature backends without changing the v1 slots.</summary>
        public static void UseStudio(MakerStudioBackends? studio) { lock (Gate) { _studio = studio; } }

        /// <summary>Publishes through <see cref="Events"/> when one is installed; does nothing otherwise.</summary>
        public static void Publish(string type, IReadOnlyDictionary<string, object?> data) => Events?.Publish(type, data);

        private static Action<string>? _warmUp;

        /// <summary>Installs what <see cref="WarmUp"/> runs (the game adapter: read the catalogue ahead in the background).</summary>
        /// <param name="warmUp">Starts the warm-up and returns at once; null removes it.</param>
        public static void UseWarmUp(Action<string>? warmUp) { lock (Gate) { _warmUp = warmUp; } }

        /// <summary>Pack Maker opened: start reading the catalogue ahead so the page's first requests find it cached. Never throws.</summary>
        /// <param name="reason">Why, for the trace file.</param>
        public static void WarmUp(string reason)
        {
            Action<string>? warmUp;
            lock (Gate) { warmUp = _warmUp; }
            try
            {
                warmUp?.Invoke(reason);
            }
            catch
            {
                // An optimisation: the first request reads the catalogue itself.
            }
        }
    }

    /// <summary>Error codes the maker routes map to HTTP (CONTRACT.md "Errors").</summary>
    public static class MakerErrorCodes
    {
        public const string BadRequest = "bad_request";              // 400
        public const string NotFound = "not_found";                  // 404
        /// <summary>The request body did not finish within the bounded deadline (408).</summary>
        public const string RequestTimeout = ApiErrorCodes.RequestTimeout; // 408
        public const string ReadOnlyPack = "read_only_pack";         // 403
        public const string NotReady = "not_ready";                  // 409: room not ready, Satone busy, language not loaded
        public const string TooLarge = "too_large";                  // 413
        public const string UnsupportedMedia = "unsupported_media_type"; // 415
        public const string Unavailable = "unavailable";             // 503: no backend installed, main thread timed out
        public const string Internal = "internal";                   // 500
    }

    /// <summary>A value or an error code with a message a creator can read.</summary>
    public sealed class MakerResult<T>
    {
        private MakerResult(T? value, string? error, string? message, MakerRecoveryDetails? details = null) { Value = value; Error = error; Message = message; Details = details; }
        public T? Value { get; }
        public string? Error { get; }
        public string? Message { get; }
        public MakerRecoveryDetails? Details { get; }
        public bool Ok => Error == null;
        public static MakerResult<T> Success(T value) => new MakerResult<T>(value, null, null);
        public static MakerResult<T> Fail(string error, string message, MakerRecoveryDetails? details = null) => new MakerResult<T>(default, error, message, details);
    }

    /// <summary>v2.1 recovery identity. Clients must not parse diagnostic prose to find the blocked operation.</summary>
    public sealed class MakerRecoveryDetails
    {
        public string PackId { get; set; } = "";
        public string Resource { get; set; } = "";
        public string BlockedOperationId { get; set; } = "";
    }

    public sealed class MakerStatus
    {
        public string GameVersion { get; set; } = "";
        public string LoaderVersion { get; set; } = "";
        /// <summary>The language the game shows now, e.g. <c>en</c>, <c>ja</c> or a pack language such as <c>th</c>.</summary>
        public string GameLanguage { get; set; } = "";
        /// <summary>True when <see cref="GameLanguage"/> comes from a content pack.</summary>
        public bool GameLanguageIsPack { get; set; }
        /// <summary>The room is loaded and Satone can speak.</summary>
        public bool RoomReady { get; set; }
    }

    public sealed class MakerPack
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        /// <summary>Language codes the pack declares (language.json) or has subtitles/voices for.</summary>
        public IReadOnlyList<string> Languages { get; set; } = new string[0];
        public bool Editable { get; set; }
        /// <summary>Why it is read-only, when it is.</summary>
        public string? ReadOnlyReason { get; set; }
    }

    public sealed class MakerLineQuery
    {
        public string Lang { get; set; } = "";
        /// <summary>Case-insensitive text in the id, the voice clip name, the JA/EN text or the subtitle.</summary>
        public string? Search { get; set; }
        /// <summary><c>all</c>, <c>missing-subtitle</c>, <c>missing-dub</c> or <c>done</c>.</summary>
        public string Filter { get; set; } = "all";
        public int Offset { get; set; }
        /// <summary>1..500.</summary>
        public int Limit { get; set; } = 100;
    }

    public sealed class MakerLinePage
    {
        public int Total { get; set; }
        public int Offset { get; set; }
        public IReadOnlyList<MakerLine> Items { get; set; } = new MakerLine[0];
    }

    public sealed class MakerLine
    {
        /// <summary>The game's localisation id (the key of <c>data/subtitles/&lt;lang&gt;.json</c>).</summary>
        public string Id { get; set; } = "";
        /// <summary>The game's voice clip name (the file name of a dub), or null when the line has no voice.</summary>
        public string? Voice { get; set; }
        /// <summary>Scenario group or source, for grouping (e.g. a story chapter, <c>clickReaction</c>).</summary>
        public string? Group { get; set; }
        /// <summary>Length of the game's own clip in seconds, when known.</summary>
        public double? DurationSec { get; set; }
        /// <summary>The game's Japanese and English text: reference text only, never audio.</summary>
        public string Ja { get; set; } = "";
        public string En { get; set; } = "";
        /// <summary>The subtitle in the query language and the pack it comes from, or null.</summary>
        public string? Subtitle { get; set; }
        public string? SubtitlePack { get; set; }
        /// <summary>The dub for the query language: the pack and the clip length, or null.</summary>
        public string? DubPack { get; set; }
        public double? DubDurationSec { get; set; }
    }

    /// <summary>What a save or play did.</summary>
    public sealed class MakerSaved
    {
        /// <summary>True when the game already uses the change (no restart needed).</summary>
        public bool Live { get; set; }
        /// <summary>Pack-relative path written or removed, e.g. <c>data/subtitles/th.json</c>; null for play.</summary>
        public string? Path { get; set; }
        public double? DurationSec { get; set; }
        /// <summary>One plain sentence for the status bar.</summary>
        public string Note { get; set; } = "";
    }
}
