using System;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// The one way this Unity-free namespace says something out loud. The plugin points
    /// <see cref="Sink"/> at the BepInEx log during initialisation; a unit test leaves it alone and
    /// the messages go nowhere.
    /// </summary>
    public static class VoiceDiagnostics
    {
        /// <summary>Where warnings go. Null (the default) discards them.</summary>
        public static Action<string>? Sink { get; set; }

        /// <summary>Reports a warning, if anyone is listening.</summary>
        /// <param name="message">The message; blank messages are dropped.</param>
        public static void Warn(string? message)
        {
            var sink = Sink;
            if (sink is null || string.IsNullOrEmpty(message))
            {
                return;
            }

            try
            {
                sink(message!);
            }
            catch (Exception)
            {
                // A logger that throws must not take the voice lookup down with it, and there is
                // by definition nowhere left to report it.
            }
        }
    }
}
