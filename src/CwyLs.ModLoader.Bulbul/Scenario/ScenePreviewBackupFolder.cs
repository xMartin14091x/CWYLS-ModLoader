using System;
using System.IO;
using CwyLs.ModLoader.Core.Scenario;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Copies the game's save folder (<c>Application.persistentDataPath/SaveData</c>, every account and
    /// version under it) to <c>BepInEx/data/backups/save-scene-*</c> before <c>scene play</c> previews a
    /// game story the player has not seen. Keeps the newest <see cref="ScenePreviewBackups.Keep"/>.
    /// </summary>
    internal static class ScenePreviewBackupFolder
    {
        /// <summary>Where the backups go.</summary>
        internal static string Root => Path.Combine(ModLoaderPaths.DataRoot, "backups");

        /// <summary>Makes one backup. False (with the reason) when nothing was copied; the preview must not start then.</summary>
        internal static bool TryMake(string groupId, out string pathOrProblem)
        {
            try
            {
                var source = Path.Combine(Application.persistentDataPath, "SaveData");
                if (!Directory.Exists(source))
                {
                    pathOrProblem = "the save folder " + source + " was not found";
                    return false;
                }

                var target = Path.Combine(Root, ScenePreviewBackups.FolderName(DateTime.Now, groupId));
                var files = Copy(source, Path.Combine(target, "SaveData"));
                if (files == 0)
                {
                    pathOrProblem = "the save folder " + source + " holds no files";
                    return false;
                }

                Prune();
                pathOrProblem = target;
                return true;
            }
            catch (Exception ex)
            {
                pathOrProblem = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static int Copy(string from, string to)
        {
            Directory.CreateDirectory(to);
            var count = 0;
            foreach (var file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
                count++;
            }

            foreach (var folder in Directory.GetDirectories(from))
            {
                count += Copy(folder, Path.Combine(to, Path.GetFileName(folder)));
            }

            return count;
        }

        private static void Prune()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var folder in Directory.GetDirectories(Root))
            {
                names.Add(Path.GetFileName(folder));
            }

            foreach (var old in ScenePreviewBackups.ToPrune(names))
            {
                try
                {
                    Directory.Delete(Path.Combine(Root, old), recursive: true);
                }
                catch (Exception ex)
                {
                    ScenarioRuntime.WarnAlways("removing the old save backup " + old + " failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    }
}
