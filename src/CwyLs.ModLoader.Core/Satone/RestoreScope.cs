using System;

namespace CwyLs.ModLoader.Core.Satone
{
    /// <summary>
    /// Which of Satone's three mod-controlled channels a restore touches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game has no single "reset Satone" call. Each channel has its own idiom, and the game
    /// pairs them by hand at every site that changes her (for example
    /// <c>RoomGameManager.OnEndPlayedReaction</c>, <c>HeroineGameEndTalk.OnEnter:44-46</c>):
    /// expression -&gt; <c>InitHeroineFacialAfterDelay(0.77f)</c>, gaze -&gt;
    /// <c>LookInitSlowly()</c>, motion -&gt; re-dispatch the current AI state. This flags enum is
    /// how a caller says which of those three it wants.
    /// </para>
    /// <para>
    /// The mouth is deliberately absent: <c>Enable_Talk</c> restores itself from the voice clip's
    /// completion callback (<c>HeroineVoiceController</c>).
    /// </para>
    /// </remarks>
    [Flags]
    public enum RestoreScope
    {
        /// <summary>Nothing.</summary>
        None = 0,

        /// <summary>The expression - the Animator's <c>Facial</c> integer.</summary>
        Face = 1,

        /// <summary>The body motion - the Animator's <c>MotionType</c> integer.</summary>
        Motion = 2,

        /// <summary>The gaze - the Animator's <c>LookScale</c> float and the IK look weight.</summary>
        Gaze = 4,

        /// <summary>All three channels.</summary>
        All = Face | Motion | Gaze
    }

    /// <summary>Helpers for reading and printing a <see cref="RestoreScope"/>.</summary>
    public static class RestoreScopes
    {
        /// <summary>The three single channels, in the order they are printed.</summary>
        public static readonly RestoreScope[] Channels =
        {
            RestoreScope.Face,
            RestoreScope.Motion,
            RestoreScope.Gaze
        };

        /// <summary>True when <paramref name="scope"/> contains <paramref name="channel"/>.</summary>
        /// <param name="scope">The scope to test.</param>
        /// <param name="channel">A single channel, e.g. <see cref="RestoreScope.Face"/>.</param>
        public static bool Has(this RestoreScope scope, RestoreScope channel) =>
            channel != RestoreScope.None && (scope & channel) == channel;

        /// <summary>How many single channels <paramref name="scope"/> contains.</summary>
        /// <param name="scope">The scope to count.</param>
        public static int Count(this RestoreScope scope)
        {
            var count = 0;
            foreach (var channel in Channels)
            {
                if (scope.Has(channel))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// e.g. <c>face+gaze</c>, <c>all</c>, <c>none</c>. Lower case, so it reads well inside a
        /// log line.
        /// </summary>
        /// <param name="scope">The scope to print.</param>
        public static string Describe(this RestoreScope scope)
        {
            if (scope == RestoreScope.None)
            {
                return "none";
            }

            if ((scope & RestoreScope.All) == RestoreScope.All)
            {
                return "all";
            }

            var text = string.Empty;
            foreach (var channel in Channels)
            {
                if (!scope.Has(channel))
                {
                    continue;
                }

                text = text.Length == 0 ? Name(channel) : text + "+" + Name(channel);
            }

            return text.Length == 0 ? "none" : text;
        }

        /// <summary>The single-channel name: <c>face</c>, <c>motion</c> or <c>gaze</c>.</summary>
        /// <param name="channel">A single channel.</param>
        public static string Name(RestoreScope channel)
        {
            switch (channel)
            {
                case RestoreScope.Face:
                    return "face";
                case RestoreScope.Motion:
                    return "motion";
                case RestoreScope.Gaze:
                    return "gaze";
                default:
                    return channel.ToString().ToLowerInvariant();
            }
        }
    }
}
