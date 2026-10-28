using System;

namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// The base class of every code mod. A mod's <c>entryDll</c> contains exactly
    /// one concrete subclass; the framework creates it with its parameterless constructor and calls
    /// <see cref="Entry"/> once, in dependency order, on the Unity main thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An abstract class rather than an interface so the framework can add members later without
    /// breaking a compiled mod.
    /// </para>
    /// <para>
    /// Everything the mod needs comes through <see cref="ICwyModHelper"/>: the helper is created
    /// for this mod alone, so every log line, event handler, console command, hotkey, config
    /// entry and data file carries the mod's id, every call into the mod is wrapped (a throwing
    /// mod is logged against its id and the others keep running), and when it stops the framework
    /// takes back what it registered through the helper and its Harmony patches (under its id, or
    /// declared in its assemblies). Its own threads, objects and files are not taken back. Do not
    /// start threads that touch Unity; use
    /// <see cref="ICwyModHelper.MainThread"/>. Do not keep a MonoBehaviour of your own: use
    /// <see cref="ICwyModEvents.EveryFrame"/>.
    /// </para>
    /// </remarks>
    public abstract class CwyMod
    {
        private ICwyModHelper? _helper;

        /// <summary>
        /// This mod's helper. Set before <see cref="Entry"/> runs; reading it in the constructor
        /// throws.
        /// </summary>
        /// <exception cref="InvalidOperationException">Read before the framework attached the helper.</exception>
        public ICwyModHelper Helper =>
            _helper ?? throw new InvalidOperationException(
                "the helper is attached just before Entry runs; do not use it in the constructor");

        /// <summary>Shorthand for <c>Helper.Log</c>.</summary>
        public ICwyModLog Log => Helper.Log;

        /// <summary>Shorthand for <c>Helper.Manifest</c>.</summary>
        public ICwyModManifest Manifest => Helper.Manifest;

        /// <summary>
        /// Called once when the mod loads, on the main thread, after every mod it depends on has
        /// entered. Register what you need here: events, commands, hotkeys, config. The room and
        /// the player's data are usually not ready yet; subscribe to <c>RoomReady</c> for that.
        /// An exception marks the mod failed; the framework then takes back what it registered
        /// through the helper and removes its Harmony patches (see <see cref="ICwyModHelper.Harmony"/>).
        /// </summary>
        /// <param name="helper">This mod's helper (also available as <see cref="Helper"/>).</param>
        public abstract void Entry(ICwyModHelper helper);

        /// <summary>
        /// The object other mods get from <c>helper.ModRegistry.GetApi&lt;T&gt;("your.id")</c>, or
        /// null (the default) for none. Called once, after every mod's <see cref="Entry"/>. Make
        /// it implement a public interface the other mod references.
        /// </summary>
        public virtual object? GetApi() => null;

        /// <summary>
        /// Called once when the game quits, in reverse load order, on the main thread. Save what
        /// you need; subscriptions are removed by the framework afterwards.
        /// </summary>
        public virtual void OnShutdown()
        {
        }

        /// <summary>Attaches the helper. Called by the loader just before <see cref="Entry"/>.</summary>
        /// <param name="helper">The mod's helper.</param>
        internal void Attach(ICwyModHelper helper) => _helper = helper;
    }
}
