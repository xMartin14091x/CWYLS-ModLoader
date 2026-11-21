using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// The game members the master-data save filter patches and reads, as patch-target data. One that
    /// moves is reported at startup; the filter then stays off and pack small talk stays out of the
    /// game's data (<c>PackSaveHoldBack.PlanInjection</c>), never a silent leak into the save.
    /// </summary>
    /// <remarks>
    /// Checked against game 1.17.3. Strings only - no game type is touched - so <c>tools/seamcheck</c>
    /// can evaluate <see cref="All"/> without the game (registered by <c>MasterDataFeature.Initialize</c>).
    /// </remarks>
    public static class MasterDataPatchTargets
    {
        /// <summary>Assembly every target lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The save manager.</summary>
        public const string SaveDataManagerType = "Bulbul.SaveDataManager";

        /// <summary>The method every write of the played list goes through.</summary>
        public const string SaveScenarioProgressMethod = "SaveScenarioProgressData";

        /// <summary>
        /// <c>SaveDataManager.SaveScenarioProgressData</c> (the only writer of <c>PlayedScenarioGroupIDs</c>;
        /// prefix + finalizer), its <c>ScenarioProgressData</c> and the played list itself.
        /// </summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            new PatchTarget("masterdata.save.scenarioprogress", GameAssembly, SaveDataManagerType, MemberKind.Method, SaveScenarioProgressMethod,
                new string[0], expectedSignature: "void SaveScenarioProgressData()"),
            new PatchTarget("masterdata.save.progressdata", GameAssembly, SaveDataManagerType, MemberKind.Property, "ScenarioProgressData",
                expectedSignature: "ScenarioProgressData ScenarioProgressData { get; set; }"),
            new PatchTarget("masterdata.save.playedids", GameAssembly, "Bulbul.ScenarioProgressData", MemberKind.Field, "PlayedScenarioGroupIDs",
                expectedSignature: "List<string> PlayedScenarioGroupIDs"),
        };
    }
}
