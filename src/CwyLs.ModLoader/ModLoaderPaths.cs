using System.IO;
using BepInEx;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// The directories the framework owns. Resolved from BepInEx's own path table rather than from
    /// anything hard-coded, so the same DLL works on macOS and Windows, and named here once so that
    /// no plugin builds its own <c>Path.Combine(BepInExRootPath, …)</c> and drifts.
    /// </summary>
    public static class ModLoaderPaths
    {
        /// <summary>Content packs live beside the game, not inside BepInEx: &lt;game root&gt;/CWYLS Mods.</summary>
        public static string PacksRoot => Path.Combine(Paths.GameRootPath, "CWYLS Mods");

        /// <summary>
        /// Launch save backups, next to the game's own save folder: &lt;Application.persistentDataPath&gt;/CWYLS Backups
        /// (macOS <c>~/Library/Application Support/nestopi/Chill With You</c>, Windows
        /// <c>%USERPROFILE%\AppData\LocalLow\nestopi\Chill With You</c>). Outside the game folder, so uninstalling or
        /// reinstalling the game in Steam leaves them alone, and
        /// outside <c>SaveData</c>, the folder Steam Cloud syncs. Created by the backup feature when it first makes
        /// one, not by <see cref="EnsureCreated"/>.
        /// </summary>
        public static string BackupsRoot =>
            Path.Combine(UnityEngine.Application.persistentDataPath, Core.Backups.SaveBackupNames.RootFolderName);

        /// <summary>Trace/JSONL output of the game trace: &lt;BepInEx&gt;/trace.</summary>
        public static string TraceRoot => Path.Combine(Paths.BepInExRootPath, "trace");

        /// <summary>
        /// Everything the framework writes for out-of-process readers: &lt;BepInEx&gt;/data.
        /// The game data bus writes <c>snapshot.json</c> here, and the loader smoke checks use a
        /// <c>_smoke</c> scratch folder inside it.
        /// </summary>
        public static string DataRoot => Path.Combine(Paths.BepInExRootPath, "data");

        /// <summary>Creates all three directories if they are missing. Idempotent.</summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(PacksRoot);
            Directory.CreateDirectory(TraceRoot);
            Directory.CreateDirectory(DataRoot);
        }
    }
}
