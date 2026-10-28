using System;
using CwyLs.ModLoader.Core.Mods;
using CwyLs.ModLoader.Core.Scenario;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// Scenes: custom script commands that pack scenes call (<c>"Command": "my.mod.wave"</c> in
    /// <c>data/novel.json</c>), numbered in 5000-5999 (<see cref="ScenarioCommandIds"/>); playing a pack
    /// scene; and the named flags scripts set (<c>cwy.flag</c>) and the <c>flag:&lt;name&gt;</c> condition
    /// reads. Registering always works; the rows run only once the scenario-command seam is
    /// installed - <see cref="ICwyModFacet.IsAvailable"/> says whether it is. Only the framework
    /// implements this interface.
    /// </summary>
    public interface ICwyModScenes : ICwyModFacet
    {
        /// <summary>
        /// Registers a script command of this mod's own. The name must start with <c>&lt;modId&gt;.</c>.
        /// The handler runs on the main thread with this mod's attribution when a scene reaches a row
        /// of it; see <see cref="ScenarioCommandHandler"/> for the continue / jump / wait contract.
        /// </summary>
        /// <param name="descriptor">What the command is.</param>
        /// <param name="handler">What runs a row of it.</param>
        /// <returns>The handle; refused (with the reason) when the name is unusable or taken.</returns>
        CwyRegistration RegisterCommand(ScenarioCommandDescriptor descriptor, ScenarioCommandHandler handler);

        /// <summary>The number a command name has this session (a built-in's fixed number, or one handed out on first use).</summary>
        /// <param name="name">A command name.</param>
        /// <param name="id">The number, or 0.</param>
        /// <returns>False for an unusable name.</returns>
        bool TryGetCommandId(string name, out int id);

        /// <summary>
        /// Plays a pack scene (a group a content pack added; the console's <c>scene list</c> shows them)
        /// now: long story types through the game's own pipeline, other types as a click-style talk.
        /// Refused while a scene plays, while the room is busy, when Satone cannot talk, for the game's
        /// own scenes and for scenes with lint errors. Main thread only.
        /// </summary>
        /// <param name="groupId">The pack scene's group id.</param>
        /// <returns>What happened, or why not.</returns>
        ScenarioResult Play(string groupId);

        /// <summary>True when the flag is set (case-insensitive); the same flags <c>cwy.flag</c> sets.</summary>
        /// <param name="name">The flag: 1-64 letters, digits, <c>_ . -</c>.</param>
        bool IsFlagSet(string name);

        /// <summary>
        /// Sets or clears a flag. Flags are kept in CWY:LS Mod Loader's own data file (never the game's save)
        /// and tell the conditions (<c>flag:&lt;name&gt;</c>) they changed. Any thread.
        /// </summary>
        /// <param name="name">The flag.</param>
        /// <param name="value">True to set, false to clear.</param>
        /// <returns>Accepted, or refused for an unusable name or a full table.</returns>
        ScenarioResult SetFlag(string name, bool value);
    }

    /// <summary>
    /// The adapter's side of the Scenes facet: scene playback, which needs the game. Framework
    /// assemblies only; registered with <c>ModFacetBackends.Use&lt;IScenesFacetBackend&gt;("scenes", …)</c>.
    /// </summary>
    internal interface IScenesFacetBackend : ICwyModFacet
    {
        /// <summary>Plays a pack scene for <paramref name="by"/>.</summary>
        /// <param name="groupId">The group id.</param>
        /// <param name="by">Who asks, e.g. <c>mod:my.mod</c>.</param>
        ScenarioResult Play(string groupId, string by);
    }

    /// <summary>The per-mod Scenes facet, over <see cref="ScenarioCommandRegistry.Shared"/>, <see cref="ScenarioFlags.Shared"/> and the playback backend.</summary>
    internal sealed class ModScenes : ICwyModScenes
    {
        /// <summary>The answer when no playback backend is installed.</summary>
        internal const string NoPlayback = "scene playback is not available: the game adapter's script feature is not installed";

        private readonly ModHelper _helper;

        internal ModScenes(ModHelper helper) => _helper = helper;

        private static ScenarioCommandRegistry Registry => ScenarioCommandRegistry.Shared;

        public bool IsAvailable => Registry.RuntimeAvailable;

        public string Status => Registry.RuntimeDetail;

        public CwyRegistration RegisterCommand(ScenarioCommandDescriptor descriptor, ScenarioCommandHandler handler)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            var name = descriptor.Name;
            if (!_helper.IsActive)
            {
                return CwyRegistration.Refused(name, "the mod " + _helper.ModId + " is not running");
            }

            if (string.Equals(_helper.ModId, ScenarioCommandRegistry.FrameworkOwner, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(name, "a mod cannot use the framework's owner name \"" + ScenarioCommandRegistry.FrameworkOwner + "\"");
            }

            ScenarioCommandHandler wrapped = context => _helper.Guard(ModStage.Script, "script " + name, () => handler(context));
            if (!Registry.TryRegister(_helper.ModId, descriptor, wrapped, out var registration, out var problem))
            {
                return Refuse(name, problem);
            }

            if (!Registry.RuntimeAvailable)
            {
                _helper.WarnOnce("scenes", Registry.RuntimeDetail + "; '" + name + "' is registered but its rows do not run yet");
            }

            return _helper.Track(name, registration!);
        }

        public bool TryGetCommandId(string name, out int id) => Registry.TryResolve(name, out id);

        public ScenarioResult Play(string groupId)
        {
            if (!_helper.IsActive)
            {
                return ScenarioResult.Refused("the mod " + _helper.ModId + " is not running");
            }

            var backend = ModFacetBackends.Get<IScenesFacetBackend>();
            if (backend is null)
            {
                _helper.WarnOnce("scenes:play", NoPlayback + "; Play does nothing");
                return ScenarioResult.Refused(NoPlayback);
            }

            return backend.Play(groupId, "mod:" + _helper.ModId);
        }

        public bool IsFlagSet(string name) => ScenarioFlags.Shared.IsSet(name);

        public ScenarioResult SetFlag(string name, bool value)
        {
            if (!_helper.IsActive)
            {
                return ScenarioResult.Refused("the mod " + _helper.ModId + " is not running");
            }

            return ScenarioFlags.Shared.Set(name, value, "mod:" + _helper.ModId);
        }

        private CwyRegistration Refuse(string name, string problem)
        {
            _helper.WarnOnce("script:" + name, "script command '" + name + "' refused: " + problem);
            return CwyRegistration.Refused(name, problem);
        }
    }
}
