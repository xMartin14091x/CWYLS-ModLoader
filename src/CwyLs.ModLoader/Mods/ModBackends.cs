using System;
using CwyLs.ModLoader.Core.Data;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>The game adapter's event bus, as the mod API needs it.</summary>
    public interface IModEventSource
    {
        /// <summary>Subscribes to one typed event; handlers run on the main thread.</summary>
        /// <typeparam name="T">An event record.</typeparam>
        /// <param name="handler">The (already wrapped) handler.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable Subscribe<T>(Action<T> handler)
            where T : ICwyEvent;

        /// <summary>Subscribes to every event.</summary>
        /// <param name="handler">The (already wrapped) handler.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable SubscribeAny(Action<AnyEvent> handler);
    }

    /// <summary>The data plugin's bus, as the mod API needs it.</summary>
    public interface IModGameDataSource
    {
        /// <summary>True once the player's save is loaded.</summary>
        bool IsReady { get; }

        /// <summary>A complete snapshot. Main thread only.</summary>
        GameDataSnapshot Snapshot();

        /// <summary>Subscribes to change notifications.</summary>
        /// <param name="handler">The (already wrapped) handler.</param>
        /// <returns>Dispose to unsubscribe.</returns>
        IDisposable Subscribe(Action<GameDataChanged> handler);
    }

    /// <summary>The game adapter's window factory, as the mod API needs it.</summary>
    public interface IModUiSource
    {
        /// <summary>Builds a hidden window. Main thread only.</summary>
        /// <param name="owner">The owning mod's id, for object names and logs.</param>
        /// <param name="options">What to build.</param>
        /// <param name="problem">Why not, or empty.</param>
        /// <returns>The window, or null.</returns>
        ICwyModWindow? CreateWindow(string owner, CwyWindowOptions options, out string problem);
    }

    /// <summary>
    /// Where the game-specific facets of the mod helper come from. CwyLs.ModLoader cannot reference
    /// the game, so the adapter plugins (CwyLs.ModLoader.Bulbul, CwyLs.ModLoader.Bulbul.Data) register
    /// their implementations here from their <c>Awake</c>, which runs before any mod loads. A
    /// facet whose backend was never registered reports <c>IsAvailable = false</c>.
    /// </summary>
    /// <remarks>
    /// API freeze: the <c>Use*</c> registrations are internal, visible only to the
    /// framework's own assemblies (<c>AssemblyInfo.cs</c>), so a mod cannot replace a backend.
    /// </remarks>
    public static class ModBackends
    {
        /// <summary>The event bus, or null.</summary>
        public static IModEventSource? Events { get; private set; }

        /// <summary>Satone, or null.</summary>
        public static ICwyModSatone? Satone { get; private set; }

        /// <summary>The player-data bus, or null.</summary>
        public static IModGameDataSource? GameData { get; private set; }

        /// <summary>The language state, or null.</summary>
        public static ICwyModLanguage? Language { get; private set; }

        /// <summary>The window factory, or null.</summary>
        public static IModUiSource? Ui { get; private set; }

        /// <summary>Registers the event bus. A second registration replaces the first.</summary>
        /// <param name="source">The implementation.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        internal static void UseEvents(IModEventSource source) =>
            Events = Registered(source ?? throw new ArgumentNullException(nameof(source)), "events");

        /// <summary>Registers Satone. A second registration replaces the first.</summary>
        /// <param name="satone">The implementation.</param>
        /// <exception cref="ArgumentNullException"><paramref name="satone"/> is null.</exception>
        internal static void UseSatone(ICwyModSatone satone) =>
            Satone = Registered(satone ?? throw new ArgumentNullException(nameof(satone)), "satone");

        /// <summary>Registers the player-data bus. A second registration replaces the first.</summary>
        /// <param name="source">The implementation.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        internal static void UseGameData(IModGameDataSource source) =>
            GameData = Registered(source ?? throw new ArgumentNullException(nameof(source)), "gameData");

        /// <summary>Registers the language state. A second registration replaces the first.</summary>
        /// <param name="language">The implementation.</param>
        /// <exception cref="ArgumentNullException"><paramref name="language"/> is null.</exception>
        internal static void UseLanguage(ICwyModLanguage language) =>
            Language = Registered(language ?? throw new ArgumentNullException(nameof(language)), "language");

        /// <summary>Registers the window factory. A second registration replaces the first.</summary>
        /// <param name="source">The implementation.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        internal static void UseUi(IModUiSource source) =>
            Ui = Registered(source ?? throw new ArgumentNullException(nameof(source)), "ui");

        /// <summary>
        /// Which facets have a backend, e.g. <c>events,satone,gameData,language,ui,audio,conditions</c>:
        /// the typed slots, then the generic <see cref="ModFacetBackends"/> table, then the Core
        /// registries that back Conditions, Actions and Scenes once the game side installed them.
        /// </summary>
        /// <returns>A comma-separated list, or <c>none</c>.</returns>
        public static string Describe()
        {
            var parts = new System.Collections.Generic.List<string>(12);
            if (Events != null) { parts.Add("events"); }
            if (Satone != null) { parts.Add("satone"); }
            if (GameData != null) { parts.Add("gameData"); }
            if (Language != null) { parts.Add("language"); }
            if (Ui != null) { parts.Add("ui"); }

            var generic = ModFacetBackends.Describe();
            if (!string.IsNullOrEmpty(generic))
            {
                parts.Add(generic);
            }

            if (Core.Conditions.ConditionRegistry.Shared.HasEvaluator) { parts.Add("conditions"); }
            if (Core.Actions.ActionServices.Shared.HasRunner) { parts.Add("actions"); }
            if (Core.Scenario.ScenarioCommandRegistry.Shared.RuntimeAvailable) { parts.Add("scenes"); }
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static T Registered<T>(T backend, string facet)
            where T : class
        {
            ConsoleEvents.File("mods", "backend", ("facet", facet), ("type", backend.GetType().FullName));
            return backend;
        }
    }
}
