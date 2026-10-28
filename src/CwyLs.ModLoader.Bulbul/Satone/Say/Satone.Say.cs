using CwyLs.ModLoader.Core.Satone.Say;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// The speaking half of the Satone API. The expression, motion and look half lives in
    /// <c>Satone/Motion/Satone.cs</c>; this is the same class, declared <c>partial</c> so the two
    /// features never had to share a file.
    /// </summary>
    /// <example>
    /// <code>
    /// using CwyLs.ModLoader.Bulbul.Satone.Motion;
    ///
    /// // text only - the story text box, no voice
    /// Satone.Say("Welcome back.");
    ///
    /// // a clip a TTS engine just made, the Japanese text, a smile and a motion
    /// // (a pack language's translation goes in Texts, keyed by its code)
    /// var handle = Satone.Say("Hello from the Mod Loader.", new SayOptions
    /// {
    ///     Clip = synthesized,
    ///     Facial = "smile",
    ///     Motion = "wild001_good",
    ///     TextJa = "モッドローダーからこんにちは。"
    /// });
    ///
    /// await handle.Completion;   // SayOutcome.Spoken when the game played it through
    /// </code>
    /// </example>
    public static partial class Satone
    {
        /// <summary>
        /// Makes Satone say a brand-new line, performed by the game itself.
        /// </summary>
        /// <param name="text">
        /// What she says. This becomes the game's own localization row, so the story text box
        /// types it out and the talk log keeps it.
        /// </param>
        /// <param name="options">
        /// The clip, the translations, the expression, the motion and the gaze. Null means a
        /// text-only line in English with no motion change.
        /// </param>
        /// <returns>
        /// A handle. The call returns as soon as the line is queued; wait for
        /// <see cref="SayHandle.Completion"/> to know how it went. A refused line comes back
        /// already completed with <see cref="SayOutcome.Rejected"/> and a reason - it never throws.
        /// </returns>
        /// <remarks>
        /// Safe to call from any thread: the queue is lock-guarded and everything that touches the
        /// game happens on the next frame of <see cref="SatoneSayHost"/>.
        /// </remarks>
        public static SayHandle Say(string text, SayOptions? options = null) =>
            SatoneSayFeature.Say(text, options);

        /// <summary>
        /// Makes Satone say a line described by a core <see cref="SayRequest"/> - the overload a
        /// mod uses when it already has the request in hand, for example deserialised from its own
        /// script format.
        /// </summary>
        /// <param name="request">The line and how it is performed.</param>
        /// <param name="clip">
        /// A clip to register under the request's voice name first; null to use a name that
        /// already resolves.
        /// </param>
        /// <returns>The handle; see <see cref="Say(string, SayOptions)"/>.</returns>
        public static SayHandle Say(SayRequest request, AudioClip? clip = null) =>
            SatoneSayFeature.Say(request, clip);

        /// <summary>True while one of our lines is being spoken.</summary>
        public static bool IsSaying => SatoneSayFeature.IsSpeaking;

        /// <summary>How many of our lines are waiting their turn.</summary>
        public static int SayQueueLength => SatoneSayFeature.WaitingCount;

        /// <summary>
        /// Why the last queue tick could not start a line - "the game is already playing …",
        /// "Satone cannot be interrupted right now". Empty when nothing is in the way.
        /// </summary>
        public static string SayBlockedReason => SatoneSayFeature.BlockedReason;
    }
}
