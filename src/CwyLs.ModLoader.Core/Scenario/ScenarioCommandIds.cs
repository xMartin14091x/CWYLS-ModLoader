using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Scenario
{
    /// <summary>
    /// The number range of script commands CWY:LS Mod Loader adds to the game's <c>CommandType</c>
    /// (50 members, 0-1000) and the fixed numbers of the
    /// framework's own commands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 5000-5999 lies outside every game value. The game's <c>ScenarioReader.ReadSentence</c> switch
    /// falls through an unknown value and stalls, so these rows are only safe once the script-command
    /// prefix is installed (<see cref="ScenarioCommandRegistry.RuntimeAvailable"/>); its pre-scan
    /// ignores unknown commands.
    /// </para>
    /// <para>
    /// 5000-5099 is the framework's: fixed numbers, so <c>"Command": 5000</c> in a pack means
    /// <c>cwy.do</c> in every session. 5100-5999 is handed out by name, in order of first use, to
    /// mods' commands and to names nobody has registered yet; those numbers are per session, so
    /// packs name them (<c>"Command": "my.mod.wave"</c>), never number them.
    /// </para>
    /// </remarks>
    public static class ScenarioCommandIds
    {
        /// <summary>The first script-command number.</summary>
        public const int First = 5000;

        /// <summary>The last script-command number.</summary>
        public const int Last = 5999;

        /// <summary>The first number of the framework's fixed block.</summary>
        public const int FrameworkFirst = 5000;

        /// <summary>The last number of the framework's fixed block.</summary>
        public const int FrameworkLast = 5099;

        /// <summary>The first number handed out by name.</summary>
        public const int DynamicFirst = 5100;

        /// <summary><c>cwy.do &lt;action&gt; [args]</c>: run an action now.</summary>
        public const int Do = 5000;

        /// <summary><c>cwy.defer &lt;action&gt; [args]</c>: run an action after the scene ends and the room is idle.</summary>
        public const int Defer = 5001;

        /// <summary><c>cwy.wait &lt;seconds&gt;</c>: hold the script.</summary>
        public const int Wait = 5002;

        /// <summary><c>cwy.flag set|clear &lt;name&gt;</c>: set a flag the <c>flag:&lt;name&gt;</c> condition reads.</summary>
        public const int Flag = 5003;

        /// <summary><c>cwy.if &lt;conditions&gt;</c>: jump by row id on a condition, like <c>IfBetweenDateTime</c>.</summary>
        public const int If = 5004;

        /// <summary><c>cwy.camera.preset</c> (camera control).</summary>
        public const int CameraPreset = 5010;

        /// <summary><c>cwy.camera.vcam</c> (camera control).</summary>
        public const int CameraVcam = 5011;

        /// <summary><c>cwy.camera.reset</c> (camera control).</summary>
        public const int CameraReset = 5012;

        /// <summary><c>cwy.say.stream</c>: the one row a streamed Say plays.</summary>
        public const int SayStream = 5020;

        /// <summary>Name of <see cref="Do"/>.</summary>
        public const string DoName = "cwy.do";

        /// <summary>Name of <see cref="Defer"/>.</summary>
        public const string DeferName = "cwy.defer";

        /// <summary>Name of <see cref="Wait"/>.</summary>
        public const string WaitName = "cwy.wait";

        /// <summary>Name of <see cref="Flag"/>.</summary>
        public const string FlagName = "cwy.flag";

        /// <summary>Name of <see cref="If"/>.</summary>
        public const string IfName = "cwy.if";

        /// <summary>Name of <see cref="CameraPreset"/>.</summary>
        public const string CameraPresetName = "cwy.camera.preset";

        /// <summary>Name of <see cref="CameraVcam"/>.</summary>
        public const string CameraVcamName = "cwy.camera.vcam";

        /// <summary>Name of <see cref="CameraReset"/>.</summary>
        public const string CameraResetName = "cwy.camera.reset";

        /// <summary>Name of <see cref="SayStream"/>.</summary>
        public const string SayStreamName = "cwy.say.stream";

        private static readonly (string Name, int Id)[] Table =
        {
            (DoName, Do),
            (DeferName, Defer),
            (WaitName, Wait),
            (FlagName, Flag),
            (IfName, If),
            (CameraPresetName, CameraPreset),
            (CameraVcamName, CameraVcam),
            (CameraResetName, CameraReset),
            (SayStreamName, SayStream),
        };

        /// <summary>The framework's fixed commands, by number.</summary>
        public static IReadOnlyList<(string Name, int Id)> BuiltIns => Table;

        /// <summary>True for 5000-5999.</summary>
        /// <param name="id">A command number.</param>
        public static bool IsInRange(int id) => id >= First && id <= Last;

        /// <summary>True for the framework's fixed block, 5000-5099.</summary>
        /// <param name="id">A command number.</param>
        public static bool IsFrameworkId(int id) => id >= FrameworkFirst && id <= FrameworkLast;

        /// <summary>The fixed number of a framework command (case-insensitive).</summary>
        /// <param name="name">A command name.</param>
        /// <param name="id">The number, or 0.</param>
        public static bool TryGetBuiltIn(string? name, out int id)
        {
            foreach (var (builtIn, number) in Table)
            {
                if (string.Equals(builtIn, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    id = number;
                    return true;
                }
            }

            id = 0;
            return false;
        }

        /// <summary>The name of a framework command number.</summary>
        /// <param name="id">A command number.</param>
        /// <param name="name">The name, or null.</param>
        public static bool TryGetBuiltInName(int id, out string? name)
        {
            foreach (var (builtIn, number) in Table)
            {
                if (number == id)
                {
                    name = builtIn;
                    return true;
                }
            }

            name = null;
            return false;
        }
    }
}
