using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Bulbul.MasterData;
using Game = global::Bulbul;
using Master = global::Bulbul.MasterData;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// Turns a <see cref="SayLine"/> into the game's own master-data rows and puts them where
    /// <c>ScenarioReader</c> looks for them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rows.</b> One line becomes exactly the shape the game's own <c>MotionScenario</c>
    /// groups have, which is also what <c>packs/cwy.example/data/*.json</c> ships:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// one <c>LocalizationData</c> - <c>Ja</c> takes the Japanese text, every other column takes
    /// the line, <c>Voice</c> takes the clip name (empty for a text-only line);
    /// </description></item>
    /// <item><description>
    /// one <c>NovelData</c> with <c>Command = VoiceText</c> pointing at that localization row,
    /// <c>Arg1 = "1"</c> to move her mouth, and the expression, motion
    /// and gaze columns filled in;
    /// </description></item>
    /// <item><description>
    /// one <c>NovelData</c> with <c>Command = EndScenario</c>, so the group closes itself
    /// (the game's own one-row groups rely on running off the end instead, which works too - this
    /// is the explicit version the example pack uses);
    /// </description></item>
    /// <item><description>
    /// one <c>ScenarioGroupData</c> of type <c>MotionScenario</c> with a unique episode number
    /// (<see cref="SayIdGenerator.FirstEpisode"/> and up), because the game looks groups up by
    /// <c>(ScenarioType, EpisodeNumber)</c> as well as by id.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>How they get in.</b> <c>MasterDataLoader</c>'s three collections are public fields, so
    /// no reflection is needed - the same direct assignment the master-data injector uses. What
    /// does need care is <em>which</em> collection is there:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// after the master-data feature has injected, the loader holds a <c>Dictionary</c> and two
    /// <c>List</c>s that the injector built and owns, so rows are added <em>in place</em>. That keeps the injector's
    /// own reference-identity check working, so its next baseline is still the game's rows;
    /// </description></item>
    /// <item><description>
    /// with the master-data feature switched off, the loader holds the ScriptableObjects' own
    /// <c>Items</c> lists - appending to those would outlive a <c>Dispose</c>/<c>Load</c> cycle
    /// and corrupt the game's data, so this class copies them
    /// once into collections of its own and assigns those instead;
    /// </description></item>
    /// <item><description>
    /// while the master-data feature is enabled but has not injected yet, nothing is written at
    /// all: the line waits a few frames rather than racing the injector.
    /// </description></item>
    /// </list>
    /// <para>
    /// Rows are kept after the line is spoken - the ids are unique, so nothing collides, and the
    /// game's talk log can still resolve them - up to <see cref="MaxLines"/>, after which the
    /// oldest line's rows are removed to make room.
    /// </para>
    /// </remarks>
    internal static class SatoneSayRows
    {
        /// <summary>How many lines keep their rows before the oldest are recycled.</summary>
        internal const int MaxLines = 500;

        /// <summary>The scenario type every Say group uses: the game's short "motion" talk.</summary>
        internal const Master.ScenarioType Scenario = Master.ScenarioType.MotionScenario;

        /// <summary>Suffix of the speaking row's id.</summary>
        internal const string SpeakRowSuffix = "_001";

        /// <summary>Suffix of the closing row's id.</summary>
        internal const string EndRowSuffix = "_end";

        private static readonly object Gate = new object();
        private static readonly List<SayLine> Live = new List<SayLine>();

        private static object? _ownedLocalization;
        private static object? _ownedNovel;
        private static object? _ownedGroups;

        /// <summary>How many lines have rows in the game right now.</summary>
        internal static int LiveCount
        {
            get { lock (Gate) { return Live.Count; } }
        }

        /// <summary>How many lines have had their rows recycled to stay under <see cref="MaxLines"/>.</summary>
        internal static long Recycled { get; private set; }

        /// <summary>Why the last <see cref="Install"/> could not write, or empty. Never null.</summary>
        internal static string LastProblem { get; private set; } = string.Empty;

        /// <summary>
        /// True when rows could be written right now.
        /// </summary>
        /// <param name="reason">Why not, or <see cref="string.Empty"/>. Never null.</param>
        internal static bool IsReady(out string reason) =>
            TryCollections(out _, out _, out _, out reason);

        /// <summary>True when this line's rows are in the game's master data right now.</summary>
        /// <param name="line">The line to look for.</param>
        internal static bool Contains(SayLine line)
        {
            if (line is null || !TryCollections(out var localization, out var novels, out _, out _))
            {
                return false;
            }

            if (!localization!.ContainsKey(line.LocalizationId))
            {
                return false;
            }

            foreach (var row in novels!)
            {
                if (row != null && string.Equals(row.ScenarioGroupID, line.GroupId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Writes a line's rows into the game's master data, replacing its own earlier rows if it
        /// already had some. Safe to repeat.
        /// </summary>
        /// <param name="line">The resolved line.</param>
        /// <param name="problem">Why it could not be written, or empty. Never null.</param>
        /// <returns>True when the game can now play <see cref="SayLine.GroupId"/>.</returns>
        internal static bool Install(SayLine line, out string problem)
        {
            if (line is null)
            {
                problem = "no line";
                LastProblem = problem;
                return false;
            }

            if (!TryCollections(out var localization, out var novels, out var groups, out problem))
            {
                LastProblem = problem;
                return false;
            }

            lock (Gate)
            {
                try
                {
                    Remove(line.GroupId, line.LocalizationId, localization!, novels!, groups!);

                    localization![line.LocalizationId] = BuildLocalization(line);
                    novels!.Add(BuildSpeakRow(line));
                    novels.Add(BuildEndRow(line));
                    groups!.Add(BuildGroup(line));

                    Remember(line);
                    Recycle(localization, novels, groups);
                }
                catch (Exception ex)
                {
                    problem = ex.GetType().Name + ": " + ex.Message;
                    LastProblem = problem;
                    CwyLsModLoader.Log.LogError("SATONE say could not add the rows for " + line + ": " + problem);
                    return false;
                }
            }

            problem = string.Empty;
            LastProblem = string.Empty;
            return true;
        }

        /// <summary>
        /// Forgets every line this class remembers, without touching the game's collections.
        /// Called when the master data has been reloaded and our rows went with it.
        /// </summary>
        internal static void Forget()
        {
            lock (Gate)
            {
                Live.Clear();
            }

            _ownedLocalization = null;
            _ownedNovel = null;
            _ownedGroups = null;
        }

        /// <summary>The live lines, oldest first. A detached copy.</summary>
        internal static IReadOnlyList<SayLine> Snapshot()
        {
            lock (Gate)
            {
                return Live.ToArray();
            }
        }

        private static void Remember(SayLine line)
        {
            for (var i = 0; i < Live.Count; i++)
            {
                if (Live[i].Id == line.Id)
                {
                    Live[i] = line;
                    return;
                }
            }

            Live.Add(line);
        }

        private static void Recycle(
            Dictionary<string, Master.LocalizationData> localization,
            List<Master.NovelData> novels,
            List<Master.ScenarioGroupData> groups)
        {
            while (Live.Count > MaxLines)
            {
                var oldest = Live[0];
                Live.RemoveAt(0);
                Remove(oldest.GroupId, oldest.LocalizationId, localization, novels, groups);
                Recycled++;
            }
        }

        private static void Remove(
            string groupId,
            string localizationId,
            Dictionary<string, Master.LocalizationData> localization,
            List<Master.NovelData> novels,
            List<Master.ScenarioGroupData> groups)
        {
            localization.Remove(localizationId);
            novels.RemoveAll(row => row != null && string.Equals(row.ScenarioGroupID, groupId, StringComparison.Ordinal));
            groups.RemoveAll(group => group != null && string.Equals(group.ID, groupId, StringComparison.Ordinal));
        }

        /// <summary>
        /// The three collections, writable. See the class remarks for why this is not simply a cast.
        /// </summary>
        private static bool TryCollections(
            out Dictionary<string, Master.LocalizationData>? localization,
            out List<Master.NovelData>? novels,
            out List<Master.ScenarioGroupData>? groups,
            out string reason)
        {
            localization = null;
            novels = null;
            groups = null;

            var loader = MasterDataFeature.ResolveLoader();
            if (loader is null)
            {
                reason = "the game's MasterDataLoader is not reachable yet";
                return false;
            }

            if (loader.LocalizationList is null || loader.NovelMasterList is null || loader.ScenarioGroupMasterList is null)
            {
                reason = "the game's master data has not finished loading";
                return false;
            }

            var ours = ReferenceEquals(loader.LocalizationList, _ownedLocalization)
                && ReferenceEquals(loader.NovelMasterList, _ownedNovel)
                && ReferenceEquals(loader.ScenarioGroupMasterList, _ownedGroups);

            if (!ours && MasterDataFeature.Enabled && !MasterDataFeature.IsInjected)
            {
                // Writing now would either touch the ScriptableObjects' own lists or be thrown away
                // by the injection that is about to happen. A few frames of waiting is the answer.
                reason = "the pack rows have not been injected yet (MasterData." + MasterDataFeature.ConfigKeyEnabled
                    + "=true, injected=false)";
                return false;
            }

            if (loader.LocalizationList is Dictionary<string, Master.LocalizationData> dictionary
                && loader.NovelMasterList is List<Master.NovelData> novelList
                && loader.ScenarioGroupMasterList is List<Master.ScenarioGroupData> groupList
                && (ours || MasterDataFeature.IsInjected))
            {
                localization = dictionary;
                novels = novelList;
                groups = groupList;
                reason = string.Empty;
                return true;
            }

            return TryOwnCopies(loader, out localization, out novels, out groups, out reason);
        }

        /// <summary>
        /// Replaces the loader's collections with copies this class owns. Only reached when the
        /// master-data feature is switched off, so there is no injector whose baseline this could disturb - and it is the
        /// only safe way to add a row when the loader is holding the assets' own lists.
        /// </summary>
        private static bool TryOwnCopies(
            Game.MasterDataLoader loader,
            out Dictionary<string, Master.LocalizationData>? localization,
            out List<Master.NovelData>? novels,
            out List<Master.ScenarioGroupData>? groups,
            out string reason)
        {
            localization = null;
            novels = null;
            groups = null;

            try
            {
                var dictionary = new Dictionary<string, Master.LocalizationData>(
                    loader.LocalizationList.Count + 1, StringComparer.Ordinal);
                foreach (var pair in loader.LocalizationList)
                {
                    if (pair.Value != null)
                    {
                        dictionary[pair.Key] = pair.Value;
                    }
                }

                var novelList = new List<Master.NovelData>(loader.NovelMasterList.Count + 2);
                foreach (var row in loader.NovelMasterList)
                {
                    novelList.Add(row);
                }

                var groupList = new List<Master.ScenarioGroupData>(loader.ScenarioGroupMasterList.Count + 1);
                foreach (var group in loader.ScenarioGroupMasterList)
                {
                    groupList.Add(group);
                }

                loader.LocalizationList = dictionary;
                loader.NovelMasterList = novelList;
                loader.ScenarioGroupMasterList = groupList;

                _ownedLocalization = dictionary;
                _ownedNovel = novelList;
                _ownedGroups = groupList;

                localization = dictionary;
                novels = novelList;
                groups = groupList;

                CwyLsModLoader.Log.LogDebug(
                    "SATONE say took copies of the game's master-data collections ("
                    + dictionary.Count + " localization, " + novelList.Count + " novel, "
                    + groupList.Count + " group row(s)); the game's own ScriptableObject lists are untouched");

                reason = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                reason = "could not copy the game's master-data collections: "
                    + ex.GetType().Name + ": " + ex.Message;
                CwyLsModLoader.Log.LogError("SATONE say " + reason);
                return false;
            }
        }

        private static Master.LocalizationData BuildLocalization(SayLine line)
        {
            var text = line.Text;
            return new Master.LocalizationData
            {
                ID = line.LocalizationId,
                Ja = line.TextJa,
                En = text,
                ZhHans = text,
                ZhHant = text,
                Pt = text,
                Ko = text,
                Ru = text,
                Voice = line.VoiceName
            };
        }

        private static Master.NovelData BuildSpeakRow(SayLine line) =>
            new Master.NovelData
            {
                ID = line.GroupId + SpeakRowSuffix,
                ScenarioGroupID = line.GroupId,
                Command = Master.CommandType.VoiceText,

                // CommandVoiceText treats an Arg1 of 1 as "move her mouth". Without a clip there
                // is nothing to move it for, and HeroineVoiceController enables the talk animation
                // before it notices the empty name, so a text-only row asks for 0.
                Arg1 = line.HasVoice && line.Request.MoveMouth ? "1" : "0",
                Arg2 = 0f,
                Arg3 = "0",
                Arg4 = "0",
                Talker = Master.TalkerType.Heroine,
                LocalizationID = line.LocalizationId,

                // The reader's VoiceText path does not read these three; they are applied through
                // the Satone motion API just before the scenario starts (see SatoneSayFeature.Perform).
                // They stay on the row because that is where the game's own writers put them, and
                // because the talk log and any future ChangeMotion row read them from here.
                BodyMotion = line.MotionId,
                FacialMotion = line.FacialId,
                LookScale = line.LookScale ?? 0f,
                LookSpeedSeconds = line.Request.LookSeconds
            };

        private static Master.NovelData BuildEndRow(SayLine line) =>
            new Master.NovelData
            {
                ID = line.GroupId + EndRowSuffix,
                ScenarioGroupID = line.GroupId,
                Command = Master.CommandType.EndScenario,
                Arg1 = "0",

                // Zero on purpose. This row used to carry 1.25 f, copied from the example pack,
                // as if it were a tail pause - but CommandEndScenario only ends the story, so no
                // column of this row is ever read. The speak row's Arg2 would not have helped
                // either: CommandVoiceText ignores it and then calls AutoNext() with the default
                // wait of 0 s, unlike CommandText, which honours it.
                // A VoiceText group therefore closes as soon as the clip finishes; the hold after
                // a Say line comes from our own restore instead - InitHeroineFacialAfterDelay's
                // 0.77 s. If a real tail pause is ever wanted, the game's own way is a
                // CommandType.WaitForSeconds row before this one.
                Arg2 = 0f,
                Arg3 = "0",
                Arg4 = "0",
                Talker = Master.TalkerType.None,
                LocalizationID = "0",
                BodyMotion = SayLine.NoMotion,
                FacialMotion = SayLine.NoMotion,
                LookScale = 0f,
                LookSpeedSeconds = 1f
            };

        private static Master.ScenarioGroupData BuildGroup(SayLine line) =>
            new Master.ScenarioGroupData
            {
                ID = line.GroupId,
                TitleLocalizationID = "0",
                SubtitleLocalizationID = "0",
                Scenario = Scenario,
                EpisodeNumber = line.EpisodeNumber,
                UnlockLevel = 0,
                UnlockStoryEpisode = "None",
                PlayTiming = "None",
                ContentType = "None",
                Arg1 = "0",
                Arg2 = "0",
                Arg3 = "0"
            };
    }
}
