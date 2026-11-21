using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>What a scene start does about the clip of its first voiced line (<see cref="LanguageVoiceFirstLine"/>).</summary>
    public enum LanguageVoiceFirstLineAction
    {
        /// <summary>
        /// Nothing beyond the usual prefetch: the language has no set, the set does not cover the line,
        /// its clip is loaded or failed (a failed clip is never retried here), or something answers the
        /// name before the set does (a mod's runtime clip, a Pack Maker swap).
        /// </summary>
        None = 0,

        /// <summary>
        /// Decode the file now, on the main thread, so the clip is held before the line asks for it: a
        /// WAV no larger than <see cref="LanguageVoiceFirstLine.MaxLoadNowBytes"/>. The managed 16-bit PCM
        /// decoder is synchronous.
        /// </summary>
        LoadNow,

        /// <summary>
        /// Start the engine's request for the file now, ahead of the queue: an Ogg Vorbis file (only the
        /// engine decodes Vorbis, and only asynchronously) or a WAV too large to decode in one frame.
        /// </summary>
        StartNow,

        /// <summary>The engine's request for the file is already in flight: finish it before any other.</summary>
        FinishFirst,
    }

    /// <summary>
    /// DS-12: the first voiced line of a scene must not play the game's clip only because the background
    /// preload of the language set has not reached it yet. The <c>ScenarioReader.StartReady</c> prefetch
    /// asks this which line is first and what to do about its clip; the rest of the scene stays queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <em>when</em> a clip loads changes, never <em>which</em> clip plays: the answer is about the
    /// clip the set of the language in force names for the line (the same table the lookup asks), and it
    /// is <see cref="LanguageVoiceFirstLineAction.None"/> whenever <see cref="LanguageVoiceRules"/> lets
    /// something else answer the name first.
    /// </para>
    /// <para>Unity-free so the rules are unit-tested; the loading itself is checked in game.</para>
    /// </remarks>
    public static class LanguageVoiceFirstLine
    {
        /// <summary>
        /// The largest WAV decoded at once on the main thread: 2 MiB, about 23 s of 16-bit mono at
        /// 44.1 kHz. A larger file goes through the engine like an Ogg, so one scene start never costs
        /// more than a few milliseconds of decoding.
        /// </summary>
        public const long MaxLoadNowBytes = 2L * 1024 * 1024;

        /// <summary>The first voiced line of a scenario: the first non-blank voice name, in reading order.</summary>
        /// <param name="names">The scenario's voice names, in reading order.</param>
        /// <returns>The name, or null when no line is voiced.</returns>
        public static string? FirstVoiced(IEnumerable<string?>? names)
        {
            if (names is null)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return null;
        }

        /// <summary>
        /// True when something answers the name before the language set, so its clip need not be
        /// hurried: a mod's runtime registration (<see cref="LanguageVoiceRules.AsksLanguageSet"/>), or a
        /// Pack Maker swap, removal or Play pin in force for the name. A global pack clip does not: the
        /// set outranks it once its clip is loaded.
        /// </summary>
        /// <param name="registered">What the global registry holds for the name, or null.</param>
        /// <param name="makerSwap">True when Pack Maker has swapped, hidden or pinned the name for the language in force.</param>
        public static bool Outranked(VoiceEntry? registered, bool makerSwap) =>
            makerSwap || !LanguageVoiceRules.AsksLanguageSet(registered);

        /// <summary>Decides what to do about the clip of a scene's first voiced line.</summary>
        /// <param name="lookup">What the set of the language in force says about the line (<see cref="LanguageVoiceTable.Lookup"/>).</param>
        /// <param name="state">The clip's state (<see cref="LanguageVoiceTable.StateOf"/>), or null when the set does not cover it.</param>
        /// <param name="outranked">See <see cref="Outranked"/>.</param>
        /// <param name="fileBytes">The size of a WAV file, or a negative number when it is not known (the file is then left to the engine, which reports why it cannot load it).</param>
        public static LanguageVoiceFirstLineAction Decide(
            LanguageVoiceLookup lookup, LanguageVoiceState? state, bool outranked, long fileBytes)
        {
            if (outranked || lookup.Outcome != LanguageVoiceOutcome.NotLoaded || lookup.Step is null)
            {
                return LanguageVoiceFirstLineAction.None;
            }

            LanguageVoiceFirstLineAction engine;
            switch (state)
            {
                case LanguageVoiceState.Pending:
                    engine = LanguageVoiceFirstLineAction.StartNow;
                    break;
                case LanguageVoiceState.Loading:
                    engine = LanguageVoiceFirstLineAction.FinishFirst;
                    break;
                default:
                    return LanguageVoiceFirstLineAction.None;
            }

            if (lookup.Step.Loader != VoiceLoaderKind.WavSync)
            {
                return engine;
            }

            // A WAV that is already in flight is still decoded now: its request is then dropped.
            return fileBytes >= 0 && fileBytes <= MaxLoadNowBytes ? LanguageVoiceFirstLineAction.LoadNow : engine;
        }
    }
}
