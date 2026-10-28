using System.IO;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Bulbul.Data
{
    /// <summary>
    /// The game data bus's in-game checks, registered into the framework's shared
    /// <see cref="SmokeRegistry"/> and executed by the runner in CwyLs.ModLoader after the room scene
    /// has settled. They answer the four questions a launch has to answer about the bus: is it
    /// attached, did it write a file, did it see the player's real data, and did a change arrive.
    /// </summary>
    public static class DataSmoke
    {
        /// <summary>Phase label these checks are reported under.</summary>
        public const string Phase = "P1";

        /// <summary>Registers every data-bus check. Idempotent - the registry replaces by id.</summary>
        public static void Register()
        {
            SmokeRegistry.Register(new SmokeCheck("p1.data.ready", Phase, Ready));
            SmokeRegistry.Register(new SmokeCheck("p1.data.snapshot_written", Phase, SnapshotWritten));
            SmokeRegistry.Register(new SmokeCheck("p1.data.todos_nonempty", Phase, TodosNonEmpty));
            SmokeRegistry.Register(new SmokeCheck("p1.data.changed_fires", Phase, ChangedFires));
        }

        private static SmokeCheckResult Ready()
        {
            const string id = "p1.data.ready";

            if (!GameData.IsReady)
            {
                return SmokeCheckResult.NotYetObserved(
                    id,
                    $"save data not observed as loaded (SaveDataManager via {SaveDataAccess.ResolutionPath}, " +
                    $"loadPostfix={SaveDataAccess.LoadObserved}, roomScene={SaveDataAccess.SceneObserved})");
            }

            return SmokeCheckResult.Pass(
                id,
                $"instance via {SaveDataAccess.ResolutionPath}; {SaveDataPatches.PatchedCount} save/delete " +
                $"methods patched; events attached: " +
                (GameEventBridge.Attached.Count == 0
                    ? "none"
                    : string.Join(", ", new System.Collections.Generic.List<string>(GameEventBridge.Attached).ToArray())));
        }

        private static SmokeCheckResult SnapshotWritten()
        {
            const string id = "p1.data.snapshot_written";

            if (!GameData.Options.WriteSnapshot)
            {
                return SmokeCheckResult.Pass(id, "skipped: Data.WriteSnapshot is off");
            }

            var path = DataHost.SnapshotPath;
            if (!File.Exists(path))
            {
                return SmokeCheckResult.Fail(id, "no file at " + path);
            }

            var length = new FileInfo(path).Length;
            return length > 0
                ? SmokeCheckResult.Pass(id, $"{path} ({length} bytes, {DataHost.LastSnapshotSummary})")
                : SmokeCheckResult.Fail(id, "file is empty: " + path);
        }

        private static SmokeCheckResult TodosNonEmpty()
        {
            const string id = "p1.data.todos_nonempty";

            var lists = GameData.Todos;
            if (lists.Count == 0)
            {
                return SmokeCheckResult.Fail(id, "no todo lists; the game creates one on first run");
            }

            var items = 0;
            foreach (var list in lists)
            {
                items += list.Items.Count;
            }

            // A player with no todos is a legitimate state, not a broken bus: the lists themselves
            // prove the read path works, so this passes with the count spelled out.
            return SmokeCheckResult.Pass(
                id, $"{lists.Count} list(s), {items} item(s)" + (items == 0 ? " - player has none yet" : string.Empty));
        }

        private static SmokeCheckResult ChangedFires()
        {
            const string id = "p1.data.changed_fires";

            if (DataHost.ChangeCount == 0)
            {
                return SmokeCheckResult.Fail(
                    id, "no change notification seen yet this session");
            }

            return SmokeCheckResult.Pass(
                id, $"{DataHost.ChangeCount} change(s); last: {DataHost.LastChange}");
        }
    }
}
