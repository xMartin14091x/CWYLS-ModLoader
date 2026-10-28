using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Console
{
    /// <summary>
    /// The game member the console's Escape guard patches (<see cref="ConsoleEscapePatch"/>), as
    /// patch-target data, so a game update that moves it is reported at startup.
    /// </summary>
    /// <remarks>
    /// <c>Bulbul.SettingService</c>, observed in game 1.17.3. The game's only Escape reader in the
    /// room is <c>RoomGameManager.Update</c>, which calls this method with <c>WindowModeType.Window</c>.
    /// </remarks>
    public static class ConsolePatchTargets
    {
        /// <summary>Assembly the target lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The settings service.</summary>
        public const string SettingServiceTypeName = "Bulbul.SettingService";

        /// <summary>The method the game's Escape handling calls to leave fullscreen.</summary>
        public const string ChangeWindowModeMethod = "ChangeWindowMode";

        /// <summary>The seam.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            new PatchTarget(
                "console.settingservice.changewindowmode", GameAssembly, SettingServiceTypeName,
                MemberKind.Method, ChangeWindowModeMethod,
                new[] { "Bulbul.WindowModeType" },
                expectedSignature: "void ChangeWindowMode(WindowModeType)"),
        };
    }
}
