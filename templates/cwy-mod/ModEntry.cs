using BepInEx.Configuration;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Mods;

namespace MyMod
{
    /// <summary>
    /// The mod's entry class. The DLL named by <c>entryDll</c> must contain exactly one concrete
    /// class deriving from <see cref="CwyMod"/>, with a parameterless constructor.
    /// </summary>
    public sealed class ModEntry : CwyMod
    {
        private ConfigEntry<bool>? _greetOnArrival;
        private int _arrivals;

        /// <summary>
        /// Runs once, on the main thread, after the mods this one depends on. Register everything
        /// here. The room is not loaded yet - react to RoomReady for that. If this throws, the mod
        /// is marked failed and everything it registered is removed; other mods keep running.
        /// </summary>
        public override void Entry(ICwyModHelper helper)
        {
            // Config: BepInEx/config/cwy.mod.<id>.cfg, created on first use.
            _greetOnArrival = helper.Config.Bind("General", "GreetOnArrival", true, "Say hello when the room is ready.");

            // Persistent data: BepInEx/data/mods/<id>/, written atomically. Keys are file names.
            var saved = helper.Data.ReadText("arrivals.txt");
            _arrivals = int.TryParse(saved, out var count) ? count : 0;

            // Game events run on the main thread. A handler that throws is logged against this mod.
            helper.Events.Subscribe<RoomReady>(_ => OnRoomReady());

            // Console command (Ctrl+Shift+C), listed under this mod's id in `help`.
            helper.Console.Register("mymod", "Shows how often you arrived.", "mymod", _ => "arrivals: " + _arrivals);

            // Hotkey: id becomes "mod:<mod id>/say", rebindable in this mod's .cfg as [Hotkeys] say.Primary / say.Alt.
            helper.Input.Register("say", "Satone says a test line.", "Ctrl+Shift+Y", null,
                () => helper.Satone.Say("This is my mod talking."));

            helper.Log.Info("loaded; arrivals so far: " + _arrivals);
        }

        /// <summary>
        /// Optional: an object other mods get through helper.ModRegistry.GetApi&lt;T&gt;("yourname.mymod").
        /// Return something that implements a public interface both mods reference.
        /// </summary>
        public override object? GetApi() => null;

        /// <summary>Runs once when the game quits. Subscriptions are removed by the framework afterwards.</summary>
        public override void OnShutdown() => Helper.Data.WriteText("arrivals.txt", _arrivals.ToString());

        private void OnRoomReady()
        {
            _arrivals++;
            if (_greetOnArrival?.Value != true)
            {
                return;
            }

            // Text shows in every game language (TextJa sets the Japanese one); Texts adds pack languages.
            Helper.Satone.Say(new SayRequest
            {
                Text = "Hello again!",
                Facial = "smile",
            });
        }
    }
}
