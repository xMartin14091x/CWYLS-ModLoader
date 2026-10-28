using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CwyLs.ModLoader;
using CwyLs.ModLoader.Core.Events;
using HarmonyLib;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Events
{
    /// <summary>
    /// Everything the bus knows about Satone talking: which line, what it says in both the player's
    /// language and the original Japanese, why she is saying it, how long the clip is, and when she
    /// stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The voice path in this game is
    /// <c>ScenarioReader</c> -&gt; <c>HeroineService.PlayVoice</c> -&gt; <c>HeroineAI.PlayVoice</c>
    /// -&gt; <c>HeroineVoiceController.PlayVoice</c> -&gt; <c>VoiceManager.Play</c>, and
    /// <c>VoiceManager.Play</c> is reached from exactly one call site in the whole game
    /// (in <c>HeroineVoiceController</c>). That makes it the one place a clip really starts - the two
    /// layers above it are <c>async UniTaskVoid</c> and can still decide not to play - so
    /// <see cref="LineSpoken"/> is published there and the layers above only record intent.
    /// </para>
    /// <para>
    /// Text is resolved two ways, in this order. When the game's <c>ScenarioReader</c> is reading a
    /// row, a prefix on its four <c>Command*(NovelData)</c> methods has already recorded that row -
    /// so the line is attributed to the row the reader is actually speaking
    /// (<see cref="LineAttribution.Row"/>). Otherwise a reverse index from voice name to
    /// <c>LocalizationData</c>, built from <c>MasterDataLoader.LocalizationList</c>, answers
    /// (<see cref="LineAttribution.VoiceIndex"/>) - which is the right answer for a clip no
    /// scenario started, and the only one available for a mod's own or a TTS clip.
    /// </para>
    /// </remarks>
    internal static class SatoneSpeech
    {
        /// <summary>
        /// Reflection name of the private clip cache on <c>AudioManager&lt;T&gt;</c>. Verified at
        /// startup as patch target <c>voice.closed.clipcache</c> (<c>Voice/BulbulVoiceTargets</c>);
        /// the speech text comes through <c>subtitles.localization.get</c>.
        /// </summary>
        private const string ClipCacheField = "_audioClipDict";

        /// <summary>Reflection name of <c>UnityEngine.AudioClip.length</c>.</summary>
        private const string ClipLengthProperty = "length";

        private static readonly object Gate = new object();

        private static readonly Dictionary<string, Master.LocalizationData> ByVoice =
            new Dictionary<string, Master.LocalizationData>(StringComparer.Ordinal);

        private static readonly HashSet<string> WarnedCollisions = new HashSet<string>(StringComparer.Ordinal);

        private static FieldInfo? _clipCacheField;
        private static PropertyInfo? _clipLengthProperty;
        private static string _indexFingerprint = VoiceIndexFingerprint.Empty;
        private static bool _clipCacheReported;

        private static Game.LocalizationMasterWrapper? _localization;
        private static Game.LanguageSupplier? _language;
        private static Game.MasterDataLoader? _loader;

        private static string? _pendingVoice;
        private static bool _pendingMoveMouth;
        private static bool _pendingIsStory;

        private static ScenarioRowHint _pendingRow = ScenarioRowHint.None;

        private static string? _lastPublishedVoice;
        private static string? _lastPublishedId;
        private static float _lastPublishedAt;

        private static LineSpoken? _openLine;
        private static float _openLineStartedAt;

        /// <summary>Scenario type currently playing, or null when none is.</summary>
        internal static string? ScenarioType { get; private set; }

        /// <summary>Scenario group id currently playing, or null when none is.</summary>
        internal static string? ScenarioGroupId { get; private set; }

        /// <summary>How many voice names the reverse index holds; 0 until master data has loaded.</summary>
        internal static int IndexedVoiceCount
        {
            get
            {
                lock (Gate)
                {
                    return ByVoice.Count;
                }
            }
        }

        /// <summary>The language the bus reports lines in; <c>English</c> until the game says otherwise.</summary>
        internal static Game.GameLanguageType Language { get; private set; } = Game.GameLanguageType.English;

        /// <summary>Where <see cref="Language"/> came from, for the startup log.</summary>
        internal static string LanguageSource { get; private set; } = "default (LanguageSupplier not resolved yet)";

        /// <summary>True while a line is open - Satone is speaking and no LineEnded has been raised.</summary>
        internal static bool IsLineOpen => _openLine != null;

        /// <summary>
        /// How many <see cref="LineSpoken"/> publishes were dropped as a republish of the line
        /// already open (<see cref="SpeechRepeat"/>). Expected to stay 0; every increment is logged.
        /// </summary>
        internal static long SuppressedRepeats { get; private set; }

        /// <summary>How many lines were attributed to the row the scenario reader was reading.</summary>
        internal static long RowAttributedLines { get; private set; }

        /// <summary>How many lines fell back to the reverse voice-name index.</summary>
        internal static long VoiceIndexAttributedLines { get; private set; }

        /// <summary>Voice clip names the shipped data gives to more than one row, as of the last index build.</summary>
        internal static int CollidingVoiceNames { get; private set; }

        /// <summary>Records that a scenario started, so the next lines can say where they came from.</summary>
        /// <param name="scenarioType">Scenario type name.</param>
        /// <param name="groupId">Scenario group id.</param>
        internal static void EnterScenario(string scenarioType, string groupId)
        {
            ScenarioType = scenarioType;
            ScenarioGroupId = groupId;
        }

        /// <summary>Records that the scenario ended.</summary>
        internal static void ExitScenario()
        {
            ScenarioType = null;
            ScenarioGroupId = null;

            // ScenarioReader.Release() nulls _currentNovel, so a row
            // captured for this scenario must not outlive it and be used to name a later clip.
            _pendingRow = ScenarioRowHint.None;
        }

        /// <summary>
        /// Records the master-data row the scenario reader is about to speak, so the next clip is
        /// attributed to it rather than to whatever row happens to share its voice name.
        /// </summary>
        /// <remarks>
        /// Called from a prefix on each of <c>ScenarioReader.CommandText</c>,
        /// <c>CommandVoiceText</c>, <c>CommandPomodoroVoiceText</c> and
        /// <c>CommandTextLoopPlayText</c> - the four commands that reach
        /// <c>HeroineService.PlayVoice</c>. The hint
        /// is only honoured if the row names the clip that actually starts, so a row that never
        /// reaches its <c>PlayVoice</c> costs nothing.
        /// </remarks>
        /// <param name="localizationId">The <c>NovelData.LocalizationID</c> of the row being read.</param>
        /// <param name="scenarioGroupId">The <c>NovelData.ScenarioGroupID</c> of the row being read.</param>
        /// <param name="frame">The Unity frame the row was read on, for diagnostics.</param>
        internal static void NoteScenarioRow(string? localizationId, string? scenarioGroupId, int frame)
        {
            // "0" is the game's own marker for "this command has no text" and it logs an error for
            // it itself. It is not a row id.
            if (string.IsNullOrEmpty(localizationId) || localizationId == "0")
            {
                _pendingRow = ScenarioRowHint.None;
                return;
            }

            // The voice column is read now rather than at play time: this is the moment the row is
            // certain, and it is the value the reader itself will pass to PlayVoice.
            var row = RowById(localizationId!);
            _pendingRow = new ScenarioRowHint(localizationId, scenarioGroupId, row?.Voice, frame);
        }

        /// <summary>
        /// Records what the caller asked for, one layer above the clip actually starting. Both
        /// <c>HeroineService.PlayVoice</c> and <c>HeroineVoiceController.PlayVoice</c> call this,
        /// so a mod that skips the service still gets these two flags right.
        /// </summary>
        /// <param name="voice">Voice clip name.</param>
        /// <param name="moveMouth">Whether the mouth-talk motion was requested.</param>
        /// <param name="isStory">Whether story priority was requested.</param>
        internal static void NoteRequest(string? voice, bool moveMouth, bool isStory)
        {
            if (string.IsNullOrEmpty(voice))
            {
                return;
            }

            _pendingVoice = voice;
            _pendingMoveMouth = moveMouth;
            _pendingIsStory = isStory;
        }

        /// <summary>
        /// Builds the <see cref="LineSpoken"/> for a clip that has just started, publishes it and
        /// opens the line so <see cref="PollLineEnded"/> can close it.
        /// </summary>
        /// <param name="voiceManager">The <c>VoiceManager</c> instance, for the clip cache.</param>
        /// <param name="voice">The clip name the game was asked to play.</param>
        /// <param name="nowSeconds">Unscaled time, used to fall back to a duration-based line end.</param>
        internal static void BeginLine(object? voiceManager, string? voice, float nowSeconds)
        {
            if (string.IsNullOrEmpty(voice))
            {
                return;
            }

            var data = Resolve(voice!, out var attribution);

            // One LineSpoken per line spoken: a second publish of the same row, for the same clip,
            // inside the repeat window is the same line and a subscriber must not see it twice.
            // Two different lines that merely share a clip name have different ids and pass.
            if (SpeechRepeat.IsRepeat(
                    _lastPublishedVoice, _lastPublishedId, _lastPublishedAt, voice, data?.ID, nowSeconds))
            {
                NoteSuppressedRepeat(voice!, data?.ID, nowSeconds);
                return;
            }

            // A new clip cuts off whatever was playing (HeroineVoiceController calls Stop first),
            // so close the previous line rather than leaving it open forever.
            CloseLine(nowSeconds);

            var matched = string.Equals(_pendingVoice, voice, StringComparison.Ordinal);
            var moveMouth = matched && _pendingMoveMouth;
            var isStory = matched && _pendingIsStory;
            _pendingVoice = null;

            if (attribution == LineAttribution.Row)
            {
                RowAttributedLines++;
            }
            else if (attribution == LineAttribution.VoiceIndex)
            {
                VoiceIndexAttributedLines++;
            }

            var line = new LineSpoken(
                voice!,
                Text(data, CurrentLanguage()),
                Text(data, Game.GameLanguageType.Japanese),
                data?.ID,
                CurrentLanguage().ToString(),
                isStory,
                ScenarioType,
                ScenarioGroupId,
                LineSourceClassifier.Classify(ScenarioType),
                moveMouth,
                Duration(voiceManager, voice!),
                attribution);

            _openLine = line;
            _openLineStartedAt = nowSeconds;
            _lastPublishedVoice = voice;
            _lastPublishedId = data?.ID;
            _lastPublishedAt = nowSeconds;

            CWYEvents.Publish(line);
        }

        /// <summary>
        /// Picks the row behind a clip and says which source answered. The scenario reader's own
        /// row wins when it names this very clip; otherwise the reverse voice-name index does.
        /// </summary>
        /// <param name="voice">The clip that just started.</param>
        /// <param name="attribution">One of the <see cref="LineAttribution"/> values.</param>
        /// <returns>
        /// The row, or null when neither source has one. Null is not a failure: a pack-registered
        /// clip, a TTS line or a clip from a game update we have not re-indexed simply has no row,
        /// which is what <see cref="LineSpoken.NoTextInMaster"/> reports.
        /// </returns>
        private static Master.LocalizationData? Resolve(string voice, out string attribution)
        {
            attribution = SpeechAttribution.Decide(_pendingRow, voice);

            if (attribution == LineAttribution.Row)
            {
                var hinted = RowById(_pendingRow.LocalizationId!);
                if (hinted != null)
                {
                    // Consumed: one captured row answers for one clip. Anything else that plays
                    // before the next command falls back to the index, as it should.
                    _pendingRow = ScenarioRowHint.None;
                    return hinted;
                }

                CwyLsModLoader.Log.LogDebug(
                    $"EVENT the scenario reader named row '{_pendingRow.LocalizationId}' for '{voice}', but "
                    + "master data has no such row; falling back to the voice index");
                attribution = LineAttribution.VoiceIndex;
            }

            var data = Lookup(voice);
            if (data is null)
            {
                attribution = LineAttribution.None;
            }

            return data;
        }

        private static void NoteSuppressedRepeat(string voice, string? localizationId, float nowSeconds)
        {
            SuppressedRepeats++;

            var detail = $"'{voice}' (id={localizationId}) was published "
                + $"{(nowSeconds - _lastPublishedAt) * 1000f:0} ms ago and has been published again";

            if (SuppressedRepeats == 1L)
            {
                // Loud once: the game cannot produce this on its own - a voice command awaits
                // IsFinishedVoice before the next one - so the first hit
                // means something publishes twice and should be traced, not tolerated.
                CwyLsModLoader.Log.LogWarning(
                    "EVENT dropped a duplicate LineSpoken: " + detail
                    + ". One LineSpoken per line is the bus's contract; please report this.");
            }
            else
            {
                CwyLsModLoader.Log.LogDebug(
                    $"EVENT dropped duplicate LineSpoken #{SuppressedRepeats}: {detail}");
            }
        }

        /// <summary>
        /// Closes the open line when Satone has stopped. Called once a frame by
        /// <see cref="EventRunner"/>.
        /// </summary>
        /// <param name="isFinishedVoice">
        /// <c>HeroineService.IsFinishedVoice</c>, or null when the service is not resolvable - in
        /// which case the clip's own length is used instead.
        /// </param>
        /// <param name="nowSeconds">Unscaled time.</param>
        internal static void PollLineEnded(bool? isFinishedVoice, float nowSeconds)
        {
            var line = _openLine;
            if (line is null)
            {
                return;
            }

            if (isFinishedVoice == true)
            {
                CloseLine(nowSeconds);
                return;
            }

            // No HeroineService to ask: fall back to the clip length, plus a small margin so a
            // clip that is still fading out is not reported as finished early.
            if (isFinishedVoice is null && line.DurationSec > 0d
                && nowSeconds - _openLineStartedAt >= line.DurationSec + 0.25d)
            {
                CloseLine(nowSeconds);
            }
        }

        /// <summary>Forgets the open line without raising an event. Used when the room goes away.</summary>
        internal static void Forget()
        {
            _openLine = null;
            _pendingVoice = null;
            _lastPublishedVoice = null;
            _lastPublishedId = null;
            ExitScenario();
        }

        /// <summary>
        /// Rebuilds the voice-name index from master data if its contents have changed, and
        /// refreshes the language. Called once a second by <see cref="EventRunner"/>; cheap when
        /// nothing moved.
        /// </summary>
        /// <remarks>
        /// The change signal is a content fingerprint over the (id, voice) pairs, not the row count.
        /// A pack that overrides a row and the Say feature's own remove-then-add both keep the
        /// count identical, and under the old count check the index kept answering with the row
        /// that was there before - which is half of why a Say line used to report a content pack's
        /// id.
        /// </remarks>
        /// <returns>True once the index holds at least one row.</returns>
        internal static bool RefreshIndex()
        {
            if (_localization is null && GameScopes.TryProject<Game.LocalizationMasterWrapper>(out var wrapper))
            {
                _localization = wrapper;
            }

            if (_language is null && GameScopes.TryProject<Game.LanguageSupplier>(out var supplier))
            {
                _language = supplier;
            }

            var rows = Rows();
            if (rows is null)
            {
                return IndexedVoiceCount > 0;
            }

            var fingerprint = VoiceIndexFingerprint.Compute(Entries(rows));
            if (string.Equals(fingerprint, _indexFingerprint, StringComparison.Ordinal))
            {
                return IndexedVoiceCount > 0;
            }

            int voiceNames;
            lock (Gate)
            {
                ByVoice.Clear();
                foreach (var pair in rows)
                {
                    var row = pair.Value;
                    if (row is null || string.IsNullOrEmpty(row.Voice))
                    {
                        continue;
                    }

                    // Later rows win: the master data is keyed by localization id, so two rows can
                    // name the same clip. Which one wins is reported below, and scenario lines no
                    // longer depend on the answer.
                    ByVoice[row.Voice] = row;
                }

                _indexFingerprint = fingerprint;
                voiceNames = ByVoice.Count;
            }

            CwyLsModLoader.Log.LogDebug(
                $"EVENT voice index built: {voiceNames} voice names from {rows.Count} localization rows "
                + $"({fingerprint})");

            ReportCollisions(rows);
            return voiceNames > 0;
        }

        /// <summary>
        /// Names the clip names more than one row claims, once per name per session. The shipped
        /// v1.17.2 table has two of them, so this is a single line; a content pack that shadows a
        /// game clip adds its own line the first time it is seen.
        /// </summary>
        private static void ReportCollisions(IReadOnlyDictionary<string, Master.LocalizationData> rows)
        {
            var collisions = VoiceNameCollisions.Find(Entries(rows));
            CollidingVoiceNames = collisions.Count;

            var fresh = new List<VoiceNameCollision>();
            foreach (var collision in collisions)
            {
                if (WarnedCollisions.Add(collision.Voice))
                {
                    fresh.Add(collision);
                }
            }

            if (fresh.Count == 0)
            {
                return;
            }

            CwyLsModLoader.Log.LogWarning("EVENT " + VoiceNameCollisions.Summarize(fresh));
        }

        /// <summary>The live localization table, or null while the loader is not resolvable.</summary>
        private static IReadOnlyDictionary<string, Master.LocalizationData>? Rows()
        {
            if (_loader is null && GameScopes.TryProject<Game.MasterDataLoader>(out var loader))
            {
                _loader = loader;
            }

            return _loader?.LocalizationList;
        }

        /// <summary>The two columns the index and its diagnostics need, without copying the table.</summary>
        private static IEnumerable<VoiceIndexEntry> Entries(
            IReadOnlyDictionary<string, Master.LocalizationData> rows)
        {
            foreach (var pair in rows)
            {
                var row = pair.Value;
                yield return row is null
                    ? new VoiceIndexEntry(pair.Key, null)
                    : new VoiceIndexEntry(row.ID, row.Voice);
            }
        }

        /// <summary>
        /// One row by id, straight from the live table - never from the index, which can be a
        /// second behind a row the adapter itself just wrote.
        /// </summary>
        private static Master.LocalizationData? RowById(string localizationId)
        {
            try
            {
                var rows = Rows();
                return rows != null && rows.TryGetValue(localizationId, out var data) ? data : null;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not read master-data row '{localizationId}': "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static void CloseLine(float nowSeconds)
        {
            var line = _openLine;
            if (line is null)
            {
                return;
            }

            _openLine = null;
            CWYEvents.Publish(new LineEnded(line.VoiceName, Math.Max(0d, nowSeconds - _openLineStartedAt)));
        }

        private static Master.LocalizationData? Lookup(string voice)
        {
            lock (Gate)
            {
                return ByVoice.TryGetValue(voice, out var data) ? data : null;
            }
        }

        private static Game.GameLanguageType CurrentLanguage()
        {
            var supplier = _language;
            if (supplier is null)
            {
                return Language;
            }

            try
            {
                // LanguageSupplier.Get() is the game's own answer: it reads
                // SaveDataManager.Instance.SettingData.GameLanguage and substitutes the platform
                // default when the player has never chosen.
                Language = supplier.Get();
                LanguageSource = "Bulbul.LanguageSupplier.Get()";
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not read the game language, keeping {Language}: "
                    + $"{ex.GetType().Name}: {ex.Message}");
            }

            return Language;
        }

        private static string? Text(Master.LocalizationData? data, Game.GameLanguageType language)
        {
            if (data is null)
            {
                return null;
            }

            var wrapper = _localization;
            try
            {
                var text = wrapper is null
                    // No wrapper yet: Ja is the column every row is authored in, so it is the only
                    // one guaranteed to be present.
                    ? (language == Game.GameLanguageType.Japanese ? data.Ja : null)
                    : wrapper.Get(data, language);

                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not read the {language} text of '{data.ID}': "
                    + $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clip length from <c>VoiceManager</c>'s private cache. Reflection all the way down: the
        /// cache is declared on the open generic base <c>AudioManager&lt;T&gt;</c> and its values
        /// are <c>UnityEngine.AudioClip</c>, whose module this assembly does not reference.
        /// </summary>
        private static double Duration(object? voiceManager, string voice)
        {
            // Language voice sets: measure the clip that will really play when something overrides
            // the game's (a pack language's voice set while that language is selected), so line-end
            // timing follows the replacement clip.
            if (VoiceFeature.TryGetEffective(PathToName(voice), out var served) && served != null)
            {
                return served.length;
            }

            if (voiceManager is null)
            {
                return 0d;
            }

            try
            {
                // AccessTools.Field walks base types, which is required here: a private base field
                // is not inherited, so a plain GetField on VoiceManager finds nothing.
                _clipCacheField ??= AccessTools.Field(voiceManager.GetType(), ClipCacheField);
                if (_clipCacheField is null)
                {
                    if (!_clipCacheReported)
                    {
                        _clipCacheReported = true;
                        CwyLsModLoader.Log.LogWarning(
                            $"EVENT {ClipCacheField} not found on {voiceManager.GetType().Name}; "
                            + "spoken lines will report a duration of 0");
                    }

                    return 0d;
                }

                // Dictionary<string, AudioClip> implements the non-generic IDictionary, which is
                // what keeps AudioClip out of this assembly's reference set.
                if (_clipCacheField.GetValue(voiceManager) is not IDictionary cache)
                {
                    return 0d;
                }

                var clip = cache[PathToName(voice)];
                if (clip is null)
                {
                    return 0d;
                }

                _clipLengthProperty ??= clip.GetType().GetProperty(ClipLengthProperty);
                var length = _clipLengthProperty?.GetValue(clip);
                return length is float seconds ? seconds : 0d;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"EVENT could not measure '{voice}': {ex.GetType().Name}: {ex.Message}");
                return 0d;
            }
        }

        /// <summary>The cache key rule, the same as <c>AudioManager.PathToName</c>.</summary>
        private static string PathToName(string audioPath) => Path.GetFileNameWithoutExtension(audioPath);
    }
}
