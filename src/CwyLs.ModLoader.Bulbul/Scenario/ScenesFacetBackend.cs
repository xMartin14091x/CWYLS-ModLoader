using CwyLs.ModLoader.Core.Scenario;
using CwyLs.ModLoader.Mods;

namespace CwyLs.ModLoader.Bulbul.Scenario
{
    /// <summary>The game side of the Scenes facet's playback (<c>helper.Scenes.Play</c>).</summary>
    internal sealed class ScenesFacetBackend : IScenesFacetBackend
    {
        public bool IsAvailable => ScenarioFeature.IsActive;

        public string Status => ScenarioFeature.IsActive
            ? "script commands: " + ScenarioFeature.ModeDetail
            : "script commands are off: " + ScenarioFeature.InactiveReason;

        public ScenarioResult Play(string groupId, string by) => ScenarioPlayer.Play(groupId, by);
    }
}
