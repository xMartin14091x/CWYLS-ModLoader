using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.PackLines;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.PackLines
{
    /// <summary>
    /// Startup greetings (game 1.18.0): a pack line in a <c>gamestart.*</c> pool can replace the line
    /// Satone says right after the startup screen. Picked by weight at the game's own pick
    /// (<c>GameStartTalkSelector.GetGameStartTalk</c>), never added to the game's deck.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The pick.</b> <c>HeroineStateGameStartDirection.ReadyHeroineSpeak</c> decides the kind
    /// (scenario type) by the time since the last launch, asks <c>GetGameStartTalk(type)</c> for an
    /// episode, calls <c>UseGameStartTalk(type)</c>, then looks the group up by
    /// <c>(Scenario, EpisodeNumber)</c> and readies it. In the postfix, the pack lines of the kind's
    /// pool that are offered in the language shown and whose clips are loaded roll once
    /// (<see cref="PackLineWeights.Roll"/>, our own <see cref="Random"/>): a line's <c>Weight</c>, or an
    /// equal share with the game's lines when it has none. A win replaces the episode, after checking
    /// that the game's lookup will find exactly that group and a novel row for it.
    /// </para>
    /// <para>
    /// <b>The save.</b> The decks are <c>StartDirectionData</c>'s <c>RandomList</c>s, kept in the save
    /// (<c>SaveHeroineData</c>). They are never widened and never hold a pack episode: only the
    /// returned number changes. After a pack pick, <c>UseGameStartTalk</c> is skipped once, so the deck
    /// keeps the game line it drew (the player did not hear it) and that call's save is not made.
    /// </para>
    /// <para>
    /// <b>Clips.</b> The greeting is picked about half a second before the room is ready, when the
    /// language set's background preload has not started. So the clips of every startup line of the
    /// language shown are queued first (<see cref="LanguageVoices.Prefetch"/>, which loads them before
    /// the room) as soon as the lines are injected and the language is known, and the pick itself
    /// only takes lines whose clips have loaded (<see cref="PackLinesVoiceCheck"/>). A line whose clip
    /// is not ready is left out of that launch's roll and the trace says why: a pack greeting never
    /// plays silent.
    /// </para>
    /// <para>
    /// <b>No repeat across launches.</b> The startup line played last is kept in
    /// <c>BepInEx/data/packlines/last.json</c> (<see cref="PackLineLastPlayed"/>, one key for every
    /// startup pool), never in the save.
    /// </para>
    /// <para>Main thread only; every entry point is called inside a never-throwing patch body.</para>
    /// </remarks>
    internal static class PackLinesGameStart
    {
        /// <summary>The group name the clip prefetch is traced under.</summary>
        internal const string PrefetchGroup = "packlines:gamestart";

        private const int NoSkip = int.MinValue;

        // Our own generator: the game's UnityEngine.Random stream (its 35 % time-of-day roll, its
        // shuffles) is never drawn from.
        private static readonly Random Rng = new Random();

        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        private static int _useSkip = NoSkip;
        private static bool _reached;
        private static string _outcome = "not reached yet";
        private static Dictionary<string, string>? _last;
        private static string _warmedCode = string.Empty;
        private static PackLineRegistry? _warmedRegistry;
        private static IDisposable? _tick;

        /// <summary>The mod's own record of the startup line played last (never the save).</summary>
        internal static string LastFilePath => Path.Combine(ModLoaderPaths.DataRoot, "packlines", "last.json");

        /// <summary>What this launch's greeting was, for <c>lines list</c> and the smoke detail.</summary>
        internal static string Outcome => _outcome;

        /// <summary>
        /// Starts watching for the moment the startup clips can be queued: once a second
        /// (<see cref="GameClockTick"/>, published from boot on) until the greeting has been picked.
        /// Called once when the feature turns on. Idempotent.
        /// </summary>
        internal static void Arm()
        {
            if (_tick != null)
            {
                return;
            }

            try
            {
                _tick = CWYEvents.Subscribe<GameClockTick>(_ => Warm("tick"));
            }
            catch (Exception ex)
            {
                // Not fatal: the injection still queues the clips when the language is known by then,
                // and the pick never takes a line whose clip is not loaded.
                CwyLsModLoader.Log.LogDebug(
                    PackLineRejection.LogPrefix + " startup clips are queued at injection only (no clock tick): "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Queues the clips of every startup line offered in the language shown at the front of the
        /// language set's load order. Does something once per (lines, language) pair, and nothing
        /// once the greeting has been picked or while the language is not known yet (the save has
        /// not loaded). Never throws.
        /// </summary>
        /// <param name="why">What asked, for the trace.</param>
        internal static void Warm(string why)
        {
            try
            {
                WarmCore(why);
            }
            catch (Exception ex)
            {
                PackLinesRuntime.ReportOnce("startup clip preload", ex);
            }
        }

        /// <summary>
        /// The postfix of <c>GetGameStartTalk</c>: the episode the game will look the greeting up by,
        /// either its own pick or a pack line's. Arms the one-time <c>UseGameStartTalk</c> skip on a
        /// pack pick, as the very last step, so a failure anywhere before leaves the game's pick whole.
        /// </summary>
        /// <param name="scenario">The <c>ScenarioType</c> value the game asked for.</param>
        /// <param name="gamePick">The episode the game drew.</param>
        /// <returns>The episode to return to the game.</returns>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int AfterPick(int scenario, int gamePick)
        {
            _useSkip = NoSkip;
            _reached = true;
            StopWatching();

            var pool = PoolOf(scenario);
            if (pool is null)
            {
                // GameStart_First_CameraTouch (the first launch's tutorial greeting) is not a pool.
                return gamePick;
            }

            var lines = PackLinesRuntime.Registry.LinesOf(pool.Id);
            if (lines.Count == 0)
            {
                _outcome = "the game's own " + pool.Id + " line (no pack line in that pool)";
                return gamePick;
            }

            var code = LanguageFeature.CurrentCode;
            var offered = new List<PackLine>(lines.Count);
            var notReady = new List<string>();
            foreach (var line in lines)
            {
                if (!line.IsOfferedIn(code))
                {
                    continue;
                }

                var silent = PackLinesVoiceCheck.FirstSilent(line);
                if (silent != null)
                {
                    notReady.Add(line.GroupId + " (clip " + silent + " not loaded)");
                    continue;
                }

                offered.Add(line);
            }

            var unweighted = 0;
            foreach (var line in offered)
            {
                if (!line.IsWeighted)
                {
                    unweighted++;
                }
            }

            var share = PackLineWeights.EqualShare(pool.VanillaCount, unweighted);
            var candidates = new List<PackLineCandidate>(offered.Count);
            foreach (var line in offered)
            {
                candidates.Add(new PackLineCandidate(line, line.Weight ?? share));
            }

            var key = PackLineLastPlayed.KeyOf(pool);
            Last().TryGetValue(key, out var last);
            var winner = PackLineWeights.Roll(candidates, last, Rng.NextDouble(), Rng.NextDouble());
            if (winner is null)
            {
                var reason = candidates.Count > 0
                    ? "the roll went to the game"
                    : notReady.Count > 0
                        ? "no offered line had its clip loaded"
                        : "no line offered in " + (code.Length == 0 ? "(language not known)" : code);
                Kept(pool, gamePick, reason, candidates.Count, notReady);
                return gamePick;
            }

            var why = WhyNotFindable(winner, MasterDataFeature.ResolveLoader());
            if (why != null)
            {
                if (Warned.Add(winner.GroupId + "|" + why))
                {
                    CwyLsModLoader.Log.LogWarning(
                        PackLineRejection.LogPrefix + " startup line '" + winner.GroupId + "' was picked but " + why
                        + "; the game's own greeting plays instead");
                }

                Kept(pool, gamePick, "picked " + winner.GroupId + " but " + why, candidates.Count, notReady);
                return gamePick;
            }

            Remember(key, winner.SourceGroupId);
            var weight = winner.Weight ?? share;
            _outcome = "pack line " + winner.GroupId + " (" + pool.Id + " #" + winner.Episode.ToString(CultureInfo.InvariantCulture) + ")";
            ConsoleEvents.File(
                PackLinesFeature.TraceSys,
                "pick",
                ("pool", pool.Id),
                ("group", winner.GroupId),
                ("episode", winner.Episode),
                ("weight", Format(weight) + (winner.IsWeighted ? string.Empty : " (equal share)")),
                ("game", gamePick),
                ("candidates", candidates.Count),
                ("notReady", notReady.Count == 0 ? null : string.Join(", ", notReady)));

            _useSkip = scenario;
            return winner.Episode;
        }

        /// <summary>
        /// The prefix of <c>UseGameStartTalk</c> asks: true exactly once, right after a pack line took
        /// the pick of this scenario type - skip the game's deck advance (and the save it triggers).
        /// </summary>
        /// <param name="scenario">The <c>ScenarioType</c> value the game passes.</param>
        internal static bool TakeUseSkip(int scenario)
        {
            var armed = _useSkip;
            _useSkip = NoSkip;
            if (armed == NoSkip || armed != scenario)
            {
                return false;
            }

            ConsoleEvents.File(PackLinesFeature.TraceSys, "startDeckKept", ("scenario", scenario));
            return true;
        }

        /// <summary>
        /// Why the game's lookup would not play exactly this line, or null when it would:
        /// <c>HeroineStateGameStartDirection</c> takes the first group with the pool's
        /// <c>Scenario</c> and <c>EpisodeNumber == (float)episode</c> (a null group throws there), and
        /// reads that group's first novel row at once (a group without rows throws).
        /// </summary>
        /// <param name="line">A startup line.</param>
        /// <param name="loader">The game's master-data loader, or null.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static string? WhyNotFindable(PackLine line, Game.MasterDataLoader? loader)
        {
            var groups = loader?.ScenarioGroupMasterList;
            var novels = loader?.NovelMasterList;
            if (groups is null || novels is null)
            {
                return "the game's master data is not loaded";
            }

            var episode = (float)line.Episode;
            Master.ScenarioGroupData? first = null;
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group != null && (int)group.Scenario == line.Pool.Scenario && group.EpisodeNumber == episode)
                {
                    first = group;
                    break;
                }
            }

            if (first is null)
            {
                return "the game's master data has no " + line.Pool.ScenarioName + " group with episode "
                       + line.Episode.ToString(CultureInfo.InvariantCulture);
            }

            if (!string.Equals(first.ID, line.GroupId, StringComparison.Ordinal))
            {
                return "the game would find group '" + first.ID + "' first for " + line.Pool.ScenarioName + " episode "
                       + line.Episode.ToString(CultureInfo.InvariantCulture);
            }

            for (var i = 0; i < novels.Count; i++)
            {
                var row = novels[i];
                if (row != null && string.Equals(row.ScenarioGroupID, first.ID, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return "group '" + first.ID + "' has no novel rows in the game's master data";
        }

        /// <summary>Every registered startup line (all languages), in registry order.</summary>
        /// <param name="registry">The registry.</param>
        internal static List<PackLine> StartupLines(PackLineRegistry registry)
        {
            var lines = new List<PackLine>();
            foreach (var line in registry.Lines)
            {
                if (line.Pool.Family == PackLineFamily.GameStart)
                {
                    lines.Add(line);
                }
            }

            return lines;
        }

        /// <summary>The live state of a startup pool, for <c>lines list</c>.</summary>
        /// <param name="pool">A <see cref="PackLineFamily.GameStart"/> pool.</param>
        internal static string Describe(PackLinePool pool)
        {
            Last().TryGetValue(PackLineLastPlayed.KeyOf(pool), out var last);
            return "last startup line: " + (string.IsNullOrEmpty(last) ? "none yet" : last) + "; this launch: " + _outcome;
        }

        /// <summary>
        /// The answer of <c>lines test &lt;id&gt;</c> for a startup line - its group id, or the id the
        /// pack wrote for a <c>gamestart.any</c> line - or null when the id is neither.
        /// </summary>
        /// <param name="id">The id the player typed.</param>
        /// <param name="checkClips">True on the main thread: also say whether the clips are loaded now.</param>
        internal static string? AnswerTest(string id, bool checkClips)
        {
            var matches = new List<PackLine>();
            foreach (var line in StartupLines(PackLinesRuntime.Registry))
            {
                if (string.Equals(line.GroupId, id, StringComparison.Ordinal)
                    || string.Equals(line.SourceGroupId, id, StringComparison.Ordinal))
                {
                    matches.Add(line);
                }
            }

            if (matches.Count == 0)
            {
                return null;
            }

            var first = matches[0];
            var pools = new List<string>();
            foreach (var line in matches)
            {
                pools.Add(line.Pool.Id);
            }

            var code = LanguageFeature.CurrentCode;
            var text = new StringBuilder();
            text.Append(first.SourceGroupId).Append(" is a startup greeting (").Append(string.Join(", ", pools))
                .Append("): it can only play as the first line after the startup screen, so restart the game to hear it. ");

            if (first.IsWeighted)
            {
                text.Append("Weight ").Append(Format(first.Weight!.Value))
                    .Append(first.Weight.Value >= 1d
                        ? ": it takes every launch that reaches one of these pools (split evenly with any other Weight 1 line there)"
                        : ": about that share of the launches that reach one of these pools");
            }
            else
            {
                text.Append("No Weight: an equal share with the game's own lines of that kind (e.g. 1 in ")
                    .Append((first.Pool.VanillaCount + 1).ToString(CultureInfo.InvariantCulture)).Append(" for ")
                    .Append(first.Pool.Id).Append(" as the only pack line); \"Weight\": 1 makes it certain");
            }

            text.Append(". Right now: ");
            if (!first.IsOfferedIn(code))
            {
                text.Append("not offered (").Append(string.Join("/", first.Languages)).Append(" only; the game shows ")
                    .Append(code.Length == 0 ? "nothing yet" : code).Append(')');
            }
            else
            {
                text.Append("offered in ").Append(code);
                if (checkClips)
                {
                    var silent = PackLinesVoiceCheck.FirstSilent(first);
                    text.Append(silent is null
                        ? ", clips loaded"
                        : ", clip " + silent + " not loaded yet (at launch it is queued before the greeting)");
                }
            }

            text.Append(". Not on the very first launch (tutorial), nor when a limited-time event opening plays instead of the greeting.");
            return text.ToString();
        }

        /// <summary>
        /// For the smoke check: every startup line's group and first novel row, looked up the way the
        /// game does. Read-only.
        /// </summary>
        /// <returns>How many lines and pools were checked, and one entry per line that would not play.</returns>
        internal static (int Lines, int Pools, List<string> Problems) CheckFindable()
        {
            var lines = StartupLines(PackLinesRuntime.Registry);
            var pools = new HashSet<string>(StringComparer.Ordinal);
            var problems = new List<string>();
            var loader = MasterDataFeature.ResolveLoader();
            foreach (var line in lines)
            {
                pools.Add(line.Pool.Id);
                var why = WhyNotFindable(line, loader);
                if (why != null)
                {
                    problems.Add(line.GroupId + ": " + why);
                }
            }

            return (lines.Count, pools.Count, problems);
        }

        // ---- helpers ----------------------------------------------------------------------------

        private static void WarmCore(string why)
        {
            if (_reached)
            {
                return;
            }

            var registry = PackLinesRuntime.Registry;
            var code = LanguageFeature.CurrentCode;
            if (code.Length == 0
                || (ReferenceEquals(registry, _warmedRegistry) && string.Equals(code, _warmedCode, StringComparison.Ordinal)))
            {
                return;
            }

            _warmedRegistry = registry;
            _warmedCode = code;
            if (!LanguageVoices.Enabled)
            {
                return;
            }

            var names = new List<string>();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in StartupLines(registry))
            {
                if (!line.IsOfferedIn(code))
                {
                    continue;
                }

                sources.Add(line.SourceGroupId);
                foreach (var voice in line.Voices)
                {
                    if (!names.Contains(voice))
                    {
                        names.Add(voice);
                    }
                }
            }

            if (names.Count == 0)
            {
                return;
            }

            // Loads before the room: Prefetch puts the clips at the front of the queue, and the
            // queue's prioritised clips are loaded whether or not the room is ready
            // (LanguageVoiceTable.TakeNext with includeBackground false).
            LanguageVoices.Prefetch(names, PrefetchGroup);
            ConsoleEvents.File(
                PackLinesFeature.TraceSys,
                "startWarm",
                ("lang", code),
                ("lines", sources.Count),
                ("clips", names.Count),
                ("inForce", LanguageVoices.ActiveCode.Length == 0 ? "none" : LanguageVoices.ActiveCode),
                ("why", why));
        }

        /// <summary>The greeting has been picked: the clock watch has nothing left to do.</summary>
        private static void StopWatching()
        {
            var tick = _tick;
            _tick = null;
            try
            {
                tick?.Dispose();
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogDebug(
                    PackLineRejection.LogPrefix + " the startup clip watch could not be removed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static PackLinePool? PoolOf(int scenario)
        {
            foreach (var pool in PackLinePools.ForScenario(scenario))
            {
                if (pool.Family == PackLineFamily.GameStart)
                {
                    return pool;
                }
            }

            return null;
        }

        private static void Kept(PackLinePool pool, int gamePick, string reason, int candidates, List<string> notReady)
        {
            // "Never twice in a row" means consecutive launches: once a game greeting has played in
            // between, the last pack line may come up again.
            Forget(PackLineLastPlayed.KeyOf(pool));
            _outcome = "the game's own " + pool.Id + " line #" + gamePick.ToString(CultureInfo.InvariantCulture) + " (" + reason + ")";
            ConsoleEvents.File(
                PackLinesFeature.TraceSys,
                "startKept",
                ("pool", pool.Id),
                ("game", gamePick),
                ("why", reason),
                ("candidates", candidates),
                ("notReady", notReady.Count == 0 ? null : string.Join(", ", notReady)));
        }

        private static string Format(double weight) => weight.ToString("0.####", CultureInfo.InvariantCulture);

        private static Dictionary<string, string> Last()
        {
            if (_last != null)
            {
                return _last;
            }

            var text = string.Empty;
            try
            {
                if (File.Exists(LastFilePath))
                {
                    text = File.ReadAllText(LastFilePath);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    PackLineRejection.LogPrefix + " reading " + LastFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message
                    + "; no startup line counts as played last");
            }

            _last = PackLineLastPlayed.Parse(text, out var problem);
            if (problem.Length > 0)
            {
                CwyLsModLoader.Log.LogWarning(
                    PackLineRejection.LogPrefix + " " + LastFilePath + ": " + problem
                    + "; no startup line counts as played last and the next pack pick rewrites it");
            }

            return _last;
        }

        private static void Forget(string key)
        {
            var last = Last();
            if (!last.Remove(key))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastFilePath)!);
                File.WriteAllText(LastFilePath, PackLineLastPlayed.ToJson(last));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    PackLineRejection.LogPrefix + " writing " + LastFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Remember(string key, string sourceGroupId)
        {
            var last = Last();
            if (last.TryGetValue(key, out var old) && string.Equals(old, sourceGroupId, StringComparison.Ordinal))
            {
                return;
            }

            last[key] = sourceGroupId;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastFilePath)!);
                File.WriteAllText(LastFilePath, PackLineLastPlayed.ToJson(last));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    PackLineRejection.LogPrefix + " writing " + LastFilePath + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
