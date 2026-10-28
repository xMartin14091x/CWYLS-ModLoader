using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Core.MasterData;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Packs;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// Reads the installed packs' master-data files off disk into the Unity-free
    /// <see cref="MasterDataPackInput"/> the merge takes.
    /// <para>
    /// This is the only part of the feature that touches a filesystem, which is what keeps every
    /// parsing and merging rule unit-testable.
    /// </para>
    /// </summary>
    internal static class MasterDataPackReader
    {
        /// <summary>
        /// Reads every loaded pack, in <c>PackManager</c>'s dependency order.
        /// </summary>
        /// <returns>One input per pack that ships at least one master-data file.</returns>
        internal static List<MasterDataPackInput> ReadAll()
        {
            var inputs = new List<MasterDataPackInput>();

            foreach (var pack in PackManager.Loaded)
            {
                var input = Read(pack);
                if (input != null)
                {
                    inputs.Add(input!);
                }
            }

            return inputs;
        }

        /// <summary>Reads one pack, or returns null when it ships no master data.</summary>
        /// <param name="pack">A loaded pack.</param>
        internal static MasterDataPackInput? Read(LoadedPack pack)
        {
            if (pack is null)
            {
                return null;
            }

            var input = new MasterDataPackInput(pack.Id)
            {
                LocalizationJson = ReadText(pack, pack.Index.Localization),
                NovelJson = ReadText(pack, pack.Index.Novel),
                ScenarioGroupsJson = ReadText(pack, pack.Index.ScenarioGroups)
            };

            // Subtitle tables are a master-data convention that PackContentIndex does not classify, so
            // they arrive as PackContentKind.Other and are picked out by path here. Index.Files is
            // ordinally sorted, so the language order is stable between runs.
            foreach (var file in pack.Index.OfKind(PackContentKind.Other))
            {
                if (SubtitleTables.LanguageOf(file.Path) is null)
                {
                    continue;
                }

                input.AddSubtitles(file.Path, ReadText(pack, file));
            }

            return input.IsEmpty ? null : input;
        }

        /// <summary>
        /// Reads one indexed file as UTF-8 text, or returns null when there is no such file or it
        /// could not be read. Every failure is logged: a pack whose data file is unreadable must
        /// say so, not quietly contribute nothing.
        /// </summary>
        private static string? ReadText(LoadedPack pack, PackFile? file)
        {
            if (file is null)
            {
                return null;
            }

            try
            {
                var absolute = pack.FullPath(file);
                if (!File.Exists(absolute))
                {
                    CwyLsModLoader.Log.LogWarning(
                        "MASTERDATA " + pack.Id + "/" + file.Path + " was indexed but is not on disk any more");
                    return null;
                }

                // ReadAllText detects a UTF-8 BOM and otherwise assumes UTF-8, which is what the
                // non-Latin tables need.
                return File.ReadAllText(absolute);
            }
            catch (Exception ex) when (ex is IOException
                                       || ex is UnauthorizedAccessException
                                       || ex is ArgumentException
                                       || ex is NotSupportedException)
            {
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA could not read " + pack.Id + "/" + file.Path + ": "
                    + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
