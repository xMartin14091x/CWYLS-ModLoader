using System.Collections.Generic;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Satone.Say;
using CwyLs.ModLoader.Mods;

namespace CwySample.Hello
{
    /// <summary>
    /// The sample code mod (C1-FRONTDOOR). Satone welcomes the player back once per session when
    /// the room is ready, and the console command <c>hello [name]</c> makes her greet by name.
    /// Everything goes through the helper - no Harmony, no game types - so this is also the
    /// smallest proof that the front door works end to end.
    /// </summary>
    public sealed class ModEntry : CwyMod
    {
        /// <summary>Let the game's own arrival scene start first.</summary>
        private const double FirstTrySeconds = 4;

        /// <summary>How often to ask again while Satone is busy.</summary>
        private const double RetrySeconds = 1;

        /// <summary>After this long, queue the greeting anyway; Say itself waits and falls back.</summary>
        private const double GiveUpSeconds = 60;

        private bool _greeted;
        private double _roomReadyAt;

        /// <inheritdoc />
        public override void Entry(ICwyModHelper helper)
        {
            helper.Events.Subscribe<RoomReady>(_ => OnRoomReady());
            helper.Console.Register(
                "hello",
                "Satone greets you, by name if you give one.",
                "hello [name]",
                Hello);

            helper.Log.Info("ready: a welcome once per session when the room is up; console command: hello [name]");
        }

        /// <summary>RoomReady comes again after every room load; the welcome is once per session.</summary>
        private void OnRoomReady()
        {
            if (_greeted)
            {
                return;
            }

            _roomReadyAt = Helper.MainThread.Now;
            Helper.MainThread.PostAfter(FirstTrySeconds, "welcome", TryWelcome);
        }

        /// <summary>
        /// Waits (one second at a time, without blocking a frame) until Satone can be interrupted,
        /// so the welcome does not collide with the game's own arrival lines.
        /// </summary>
        private void TryWelcome()
        {
            if (_greeted)
            {
                return;
            }

            var waited = Helper.MainThread.Now - _roomReadyAt;
            if (!Helper.Satone.CanInteract && waited < GiveUpSeconds)
            {
                Helper.MainThread.PostAfter(RetrySeconds, "welcome", TryWelcome);
                return;
            }

            _greeted = true;
            var handle = Helper.Satone.Say(Line("Welcome back!"));
            Helper.Log.Info(
                "welcome queued as line " + handle.Id + " after " +
                waited.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s");
        }

        /// <summary>The <c>hello [name]</c> console command.</summary>
        private string? Hello(IReadOnlyList<string> args)
        {
            var name = args.Count == 0 ? string.Empty : string.Join(" ", args).Trim();
            var handle = Helper.Satone.Say(Line(name.Length == 0 ? "Hello!" : "Hello, " + name + "!"));
            if (handle.IsCompleted && handle.Outcome == SayOutcome.Rejected)
            {
                return "Satone cannot say it: " + handle.Detail;
            }

            return "Satone will greet " + (name.Length == 0 ? "you" : name) + " (line " + handle.Id + ")";
        }

        /// <summary>
        /// One English line with a smile. The game shows it whatever language the player picked:
        /// <c>Text</c> fills every language column of the game's row (<c>TextJa</c> would give the
        /// Japanese one its own text).
        /// </summary>
        private static SayRequest Line(string english) =>
            new SayRequest
            {
                Text = english,
                Language = "en",
                // Texts would add the line in languages a content pack declares (not the game's own).
                Facial = "smile",
            };
    }
}
