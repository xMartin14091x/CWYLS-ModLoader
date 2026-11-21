using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Editor;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// Pack Maker's write side (ticket DS-03, <see cref="IMakerEditor"/>): saving a subtitle or a dub
    /// writes the pack file and the game uses it at once, with no restart; Play makes Satone say a line
    /// in the game, original or dub, in the game's own text box.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Threads.</b> The maker routes call this on the API's worker threads. Every file operation
    /// runs there (<see cref="MakerSubtitleStore"/> / <see cref="MakerVoiceStore"/>, one save at a time); everything that touches Unity -
    /// building a clip, swapping it in, starting a line - hops to the main thread through
    /// <see cref="MainThread.InvokeAsync{T}"/> with a 2 s timeout. A timeout after the file was written
    /// is not an error: the save stands and the answer says it is not live yet.
    /// </para>
    /// <para>
    /// <b>Live subtitles.</b> After the table is written, the text every loaded pack together now gives
    /// the line (later pack over earlier, read from disk) is set as a live entry of the packs' text
    /// source (<see cref="MasterDataSubtitleSource.SetLive"/>), which the game's text seam asks before
    /// the merged tables.
    /// </para>
    /// <para>
    /// <b>Live dubs.</b> After the WAV is written, the samples (already decoded off the main thread)
    /// become an <c>AudioClip</c> on the main thread and are swapped into the language set
    /// (<see cref="LanguageVoices.SwapLive"/>), which the game's clip lookup asks before the set's
    /// table. A removal leaves whatever the packs now give: the restored <c>.ogg</c>, another pack's
    /// file, or the game's own clip.
    /// </para>
    /// <para>
    /// <b>Saves.</b> Nothing here reads or writes the game's save data; every change lives in the
    /// pack folder and in memory.
    /// </para>
    /// </remarks>
    public sealed partial class MakerEditor : IMakerEditor
    {
        /// <summary>The word every console line of the editor starts with.</summary>
        public const string LogPrefix = "MAKER";

        /// <summary>The <c>sys</c> tag of the editor's trace events.</summary>
        public const string TraceSys = "maker";

        /// <summary>The main-thread owner name of the editor's work.</summary>
        internal const string Owner = "maker";

        /// <summary>How long a worker waits for the main thread (RULES.md: 2 s).</summary>
        internal static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(2);

        /// <summary>How long a write-probe answer is trusted.</summary>
        private const double ProbeSeconds = 10d;

        private static readonly object Gate = new object();
        private static readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);
        private static readonly Dictionary<string, KeyValuePair<DateTime, string?>> Probes =
            new Dictionary<string, KeyValuePair<DateTime, string?>>(StringComparer.Ordinal);

        private static MakerEditor? _instance;

        private MakerEditor()
        {
        }

        /// <summary>
        /// Raised after every save or removal that changed a pack file, so whoever caches pack state
        /// (the catalog, DS-02) can refresh. Raised on the editor's worker thread, after the live
        /// update; each handler runs in its own try/catch, so one failure is one log line.
        /// </summary>
        public static event Action<MakerEditorChange>? Changed;

        /// <summary>The one editor, created by <see cref="Initialize"/>.</summary>
        public static MakerEditor? Instance
        {
            get { lock (Gate) { return _instance; } }
        }

        /// <summary>
        /// Creates the editor and arms the dub hold (<see cref="MakerDubHold"/>). Called once from
        /// <c>BulbulPlugin.Awake</c> as <c>MakerBackends.UseEditor(MakerEditor.Initialize(Config))</c>;
        /// a second call returns the same editor.
        /// </summary>
        /// <param name="config">The adapter's config file (the dub hold binds its switch there).</param>
        /// <returns>The editor.</returns>
        public static MakerEditor Initialize(ConfigFile? config = null)
        {
            lock (Gate)
            {
                if (_instance != null)
                {
                    return _instance;
                }

                _instance = new MakerEditor();
            }

            try
            {
                MakerDubHold.Initialize(config);
            }
            catch (Exception ex)
            {
                // The editor works without the hold; a dub may then be cut where the game times out.
                CwyLsModLoader.Log.LogError(LogPrefix + " dub hold could not start: " + ex.GetType().Name + ": " + ex.Message);
            }

            try
            {
                MakerGameFocus.Initialize(config);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " focus on play could not start: " + ex.GetType().Name + ": " + ex.Message);
            }

            ConsoleEvents.Setup(
                "maker.editor",
                "ready: live subtitles, live dubs, play in game",
                ("dubHold", MakerDubHold.Detail));
            return _instance;
        }

        /// <summary>
        /// Whether Pack Maker may write to a pack, and why not (CONTRACT.md "Editable packs"): the
        /// decision the catalog reports as <c>editable</c> / <c>readOnlyReason</c>. Safe from any thread;
        /// the folder's write test is remembered for 10 s.
        /// </summary>
        /// <param name="packId">A pack id.</param>
        public static MakerPackVerdict Editability(string? packId)
        {
            var id = packId ?? string.Empty;
            var pack = PackManager.Find(id);
            var skipped = pack is null ? PackManager.FindSkipped(id) : null;
            var facts = new MakerPackFacts
            {
                Id = id,
                Loaded = pack != null,
                SkipReason = skipped is null ? null : skipped.Reason + " (" + skipped.Detail + ")",
                IsCodeMod = pack?.Manifest.IsCodeMod ?? false,
                RootPath = pack?.RootPath,
                PacksRoot = ModLoaderPaths.PacksRoot,
            };

            var verdict = MakerPackAccess.Judge(facts);
            if (!verdict.Editable || pack is null)
            {
                return verdict;
            }

            // Only a pack that passed every other rule gets a probe file written into it.
            facts.WriteProblem = WriteProblem(pack.RootPath);
            return MakerPackAccess.Judge(facts);
        }

        /// <summary>Resolves an editable pack, or the error to answer with.</summary>
        private static bool TryOpen(
            string packId, out LoadedPack? pack, out MakerPackFiles? files, out MakerResult<MakerSaved>? refusal)
        {
            pack = null;
            files = null;
            refusal = null;

            if (!MakerEditRules.IsPackId(packId))
            {
                refusal = Fail(MakerErrorCodes.BadRequest, "'" + packId + "' is not a pack id");
                return false;
            }

            var verdict = Editability(packId);
            if (!verdict.Known)
            {
                refusal = Fail(MakerErrorCodes.NotFound, verdict.Reason ?? "no such pack");
                return false;
            }

            if (!verdict.Editable)
            {
                refusal = Fail(MakerErrorCodes.ReadOnlyPack, packId + " is read-only: " + verdict.Reason);
                return false;
            }

            pack = PackManager.Find(packId);
            if (pack is null)
            {
                refusal = Fail(MakerErrorCodes.NotFound, "no pack '" + packId + "' is installed");
                return false;
            }

            files = new MakerPackFiles(pack.RootPath);
            return true;
        }

        private static string? WriteProblem(string root)
        {
            var now = DateTime.UtcNow;
            lock (Gate)
            {
                if (Probes.TryGetValue(root, out var cached) && (now - cached.Key).TotalSeconds < ProbeSeconds)
                {
                    return cached.Value;
                }
            }

            var problem = new MakerPackFiles(root).WriteProblem();
            lock (Gate)
            {
                Probes[root] = new KeyValuePair<DateTime, string?>(now, problem);
            }

            return problem;
        }

        /// <summary>Runs a function on the main thread with the 2 s timeout.</summary>
        private static Task<T> OnMain<T>(string label, Func<T> work, CancellationToken cancel) =>
            MainThread.InvokeAsync(label, work, MainThreadTimeout, cancel, Owner);

        /// <summary>The console line and the <see cref="Changed"/> event of one save.</summary>
        private static void Report(string kind, string packId, string code, string key, bool removed, MakerSaved saved, bool written)
        {
            ConsoleEvents.Emit(
                TraceSys,
                kind == MakerEditorChange.VoiceKind ? "voiceSaved" : "subtitleSaved",
                kind == MakerEditorChange.VoiceKind ? ConsoleTags.Voice : ConsoleTags.Subtitle,
                LogPrefix + " " + (removed ? "removed " : "saved ") + code + "/" + key + " in " + packId,
                "live,path",
                ("pack", packId),
                ("lang", code),
                ("key", key),
                ("removed", removed),
                ("live", saved.Live),
                ("path", saved.Path),
                ("seconds", saved.DurationSec),
                ("note", saved.Note));

            if (!written)
            {
                return;
            }

            var change = new MakerEditorChange
            {
                Kind = kind,
                PackId = packId,
                Lang = code,
                Key = key,
                Removed = removed,
                Live = saved.Live,
                Path = saved.Path ?? string.Empty,
                DurationSec = saved.DurationSec,
            };

            var handlers = Changed;
            if (handlers is null)
            {
                return;
            }

            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<MakerEditorChange>)handler)(change);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        LogPrefix + " a Changed handler (" + handler.Method.DeclaringType?.Name + "." + handler.Method.Name
                        + ") failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private static MakerResult<MakerSaved> Fail(string code, string message) => MakerResult<MakerSaved>.Fail(code, message);

        /// <summary>An unexpected failure: one log line, and <c>unavailable</c> or <c>internal</c>.</summary>
        private static MakerResult<MakerSaved> Failed(string what, Exception ex)
        {
            var inner = ex is AggregateException aggregate && aggregate.InnerException != null ? aggregate.InnerException : ex;
            if (inner is TimeoutException || inner is MainThreadRejectedException)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " " + what + ": the game did not answer in time (" + inner.Message + ")");
                return Fail(MakerErrorCodes.Unavailable, "The game did not answer in time; try again.");
            }

            if (inner is IOException || inner is UnauthorizedAccessException)
            {
                CwyLsModLoader.Log.LogWarning(LogPrefix + " " + what + " failed: " + inner.GetType().Name + ": " + inner.Message);
                return Fail(MakerErrorCodes.Internal, "The pack folder could not be written: " + inner.Message);
            }

            CwyLsModLoader.Log.LogError(LogPrefix + " " + what + " failed: " + inner.GetType().Name + ": " + inner.Message);
            return Fail(MakerErrorCodes.Internal, what + " failed: " + inner.Message);
        }
    }
}
