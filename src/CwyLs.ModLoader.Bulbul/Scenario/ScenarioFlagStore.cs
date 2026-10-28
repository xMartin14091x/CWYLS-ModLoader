using System;
using System.IO;
using CwyLs.ModLoader.Core.Conditions;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Scenario;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>
    /// Keeps <see cref="ScenarioFlags.Shared"/> in CWY:LS Mod Loader's own data file,
    /// <c>BepInEx/data/script/flags.json</c> (never the game's save), and tells the condition system
    /// when a flag changed (<see cref="UpdatePoint.FlagChanged"/>, detail <c>flag:&lt;name&gt;</c>).
    /// </summary>
    internal static class ScenarioFlagStore
    {
        private static readonly object FileGate = new object();
        private static bool _armed;

        /// <summary>The data file.</summary>
        internal static string FilePath => Path.Combine(ModLoaderPaths.DataRoot, "script", "flags.json");

        /// <summary>What the load at startup said.</summary>
        internal static string LoadDetail { get; private set; } = "not loaded";

        /// <summary>Loads the file (once) and starts saving every change.</summary>
        internal static void Arm()
        {
            if (_armed)
            {
                return;
            }

            _armed = true;
            Load();
            ScenarioFlags.Shared.Changed += OnChanged;
        }

        private static void Load()
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                LoadDetail = "no flags yet";
                return;
            }

            try
            {
                var result = ScenarioFlags.Shared.Load(File.ReadAllText(path));
                LoadDetail = result.Message;
                if (!result.IsOk)
                {
                    ScenarioRuntime.WarnAlways("the flags file " + path + " was not loaded: " + result.Message + "; flags start empty and the next change rewrites it");
                }
            }
            catch (Exception ex)
            {
                LoadDetail = ex.GetType().Name + ": " + ex.Message;
                ScenarioRuntime.WarnAlways("reading " + path + " failed: " + LoadDetail + "; flags start empty");
            }
        }

        private static void OnChanged(ScenarioFlagChange change)
        {
            try
            {
                Save();
            }
            catch (Exception ex)
            {
                ScenarioRuntime.WarnAlways("saving the flags to " + FilePath + " failed: " + ex.GetType().Name + ": " + ex.Message
                                           + "; the change holds for this session only");
            }

            ConsoleEvents.Emit(
                ScenarioRuntime.Owner,
                "flag",
                ConsoleTags.Script,
                "flag " + change.Name + (change.Value ? " set" : " cleared"),
                "by",
                ("flag", change.Name),
                ("value", change.Value),
                ("by", change.By));

            try
            {
                ConditionRegistry.Shared.RaiseChanged(new ConditionsChanged(UpdatePoint.FlagChanged, ConditionTokens.Flag + ":" + change.Name));
            }
            catch (Exception ex)
            {
                ScenarioRuntime.WarnAlways("telling the conditions that flag " + change.Name + " changed failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Save()
        {
            var path = FilePath;
            var json = ScenarioFlags.Shared.ToJson();
            lock (FileGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp~";
                File.WriteAllText(temp, json);
                if (!File.Exists(path))
                {
                    File.Move(temp, path);
                    return;
                }

                try
                {
                    File.Replace(temp, path, null);
                }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException || ex is UnauthorizedAccessException)
                {
                    // Not atomic, but the file is small and rewritten whole on the next change.
                    ConsoleEvents.File(ScenarioRuntime.Owner, "flagsReplaceFallback", ("error", ex.GetType().Name + ": " + ex.Message));
                    File.Copy(temp, path, true);
                    File.Delete(temp);
                }
            }
        }
    }
}
