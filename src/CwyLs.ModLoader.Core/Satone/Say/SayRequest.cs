using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Subtitles;
using CwyLs.ModLoader.Core.Voice;

namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>
    /// One brand-new line for Satone to speak: the text, the languages it exists in, the clip that
    /// speaks it and the expression, motion and gaze it is performed with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity-free on purpose. The adapter (<c>SatoneSayFeature</c>) turns this into the game's own
    /// master-data rows and hands them to <c>ScenarioReader</c>; everything expressible without
    /// touching the engine - validation, id generation, queueing - lives here and is unit-tested.
    /// </para>
    /// <para>
    /// <b>Which column gets which text.</b> <see cref="Text"/> fills every language column of the
    /// game's localization row except Japanese, which takes <see cref="TextJa"/> (falling back to
    /// <see cref="Text"/>). The game shows whichever column matches the player's language, so a
    /// caller that only has English still gets a line on screen in any language.
    /// <see cref="Texts"/> is separate: it is this line's own text table, and it is the only way to
    /// give the line a text in a language the game has no column for - a language a content pack
    /// declares. While the player has such a language chosen, the game's own text box asks the text
    /// seam for this row and shows that entry (or the language's declared fallback).
    /// </para>
    /// </remarks>
    public sealed class SayRequest
    {
        /// <summary>Seconds the gaze tween takes when <see cref="LookScale"/> is set.</summary>
        public const float DefaultLookSeconds = 0.6f;

        /// <summary>Longest text a single line may carry, in characters.</summary>
        /// <remarks>
        /// The story UI types the text out one character at a time and the reader waits for it, so
        /// a runaway LLM answer would hold the scenario open for minutes. Anything longer is
        /// refused with a reason rather than silently truncated.
        /// </remarks>
        public const int MaxTextLength = 500;

        /// <summary>An empty text table, so callers never have to allocate one.</summary>
        public static readonly IReadOnlyDictionary<string, string> NoTexts =
            new Dictionary<string, string>(0, StringComparer.Ordinal);

        /// <summary>
        /// What Satone says, in <see cref="Language"/>. Required; blank is refused.
        /// </summary>
        public string Text { get; init; } = string.Empty;

        /// <summary>
        /// The Japanese column of the game's row. Null means "use <see cref="Text"/>", which is
        /// what a mod with no translation should do - the line is then the same in every language.
        /// </summary>
        public string? TextJa { get; init; }

        /// <summary>
        /// Per-language texts, keyed by language code (<c>ja</c>, or a code a pack declares). They
        /// are served through a registered <see cref="ISubtitleSource"/>: while the player has a
        /// pack language chosen, the game's text box shows this line in that language.
        /// Null or empty means "only what the game's own columns hold".
        /// </summary>
        public IReadOnlyDictionary<string, string>? Texts { get; init; }

        /// <summary>
        /// The voice clip name to speak, e.g. <c>Voice_CWY_Test</c>. Must satisfy
        /// <see cref="VoiceNameRules.Pattern"/>. Null or empty means a text-only line: the game
        /// shows the text and does not move her mouth.
        /// </summary>
        public string? VoiceName { get; init; }

        /// <summary>
        /// Expression name, alias or id for the Satone table (<c>smile</c>, <c>10</c>). Null leaves
        /// her expression alone.
        /// </summary>
        public string? Facial { get; init; }

        /// <summary>
        /// Body-motion name, alias or id for the Satone table (<c>wild001_good</c>, or its id <c>61</c>). Null
        /// leaves her motion alone.
        /// </summary>
        public string? Motion { get; init; }

        /// <summary>
        /// How far she turns towards the player while she speaks, 0..1. Null leaves the gaze to
        /// the game.
        /// </summary>
        public float? LookScale { get; init; }

        /// <summary>How long the gaze turn takes, in seconds. Ignored when <see cref="LookScale"/> is null.</summary>
        public float LookSeconds { get; init; } = DefaultLookSeconds;

        /// <summary>
        /// The language <see cref="Text"/> is written in, for the subtitle table and the logs.
        /// Null means English, which is what the game's <c>En</c> column holds.
        /// </summary>
        public string? Language { get; init; }

        /// <summary>
        /// True to run the mouth-talk animation while the clip plays (the game's <c>isMoveMouse</c>
        /// on a <c>VoiceText</c> row). Ignored for a text-only line.
        /// </summary>
        public bool MoveMouth { get; init; } = true;

        /// <summary><see cref="Language"/> normalised, never empty (<c>en</c> by default).</summary>
        public string NormalizedLanguage
        {
            get
            {
                var code = SubtitleLanguages.Normalize(Language);
                return code.Length == 0 ? SubtitleLanguages.English : code;
            }
        }

        /// <summary>True when a clip name was given, so the line is spoken rather than only shown.</summary>
        public bool HasVoiceName => !string.IsNullOrEmpty(VoiceName);

        /// <summary>The simplest request: a line of English text and nothing else.</summary>
        /// <param name="text">What she says.</param>
        public static SayRequest For(string text) => new SayRequest { Text = text };

        /// <summary>
        /// Checks the request without touching the game.
        /// </summary>
        /// <param name="problem">
        /// Why it was refused, phrased for a log line; <see cref="string.Empty"/> when it is fine.
        /// </param>
        /// <returns>True when the request can be turned into rows.</returns>
        public bool Validate(out string problem)
        {
            if (string.IsNullOrWhiteSpace(Text))
            {
                problem = "a Say line needs text";
                return false;
            }

            if (Text.Length > MaxTextLength)
            {
                problem = "the text is " + Text.Length + " characters; the limit is " + MaxTextLength;
                return false;
            }

            if (HasVoiceName && !VoiceNameRules.IsValid(VoiceName))
            {
                problem = "'" + VoiceName + "' is not a usable voice name: " + VoiceNameRules.CharacterRuleReason;
                return false;
            }

            if (LookScale.HasValue && (LookScale.Value < 0f || LookScale.Value > 1f))
            {
                problem = "LookScale must be between 0 and 1, not " + LookScale.Value;
                return false;
            }

            if (Texts != null)
            {
                foreach (var pair in Texts)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        problem = "a subtitle text was given without a language code";
                        return false;
                    }
                }
            }

            problem = string.Empty;
            return true;
        }

        /// <summary>
        /// The full subtitle table for this line: <see cref="Texts"/>, plus <see cref="Text"/>
        /// under <see cref="NormalizedLanguage"/> and the Japanese text under <c>ja</c>. Keys are
        /// normalised; an explicit entry in <see cref="Texts"/> always wins.
        /// </summary>
        /// <returns>A detached, ordinal-keyed table. Never null.</returns>
        public IReadOnlyDictionary<string, string> BuildTexts()
        {
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);

            Put(texts, NormalizedLanguage, Text);
            Put(texts, SubtitleLanguages.Japanese, string.IsNullOrWhiteSpace(TextJa) ? Text : TextJa!);

            if (Texts != null)
            {
                foreach (var pair in Texts)
                {
                    var code = SubtitleLanguages.Normalize(pair.Key);
                    if (code.Length == 0 || string.IsNullOrWhiteSpace(pair.Value))
                    {
                        continue;
                    }

                    // An explicit table entry is the caller's translation and outranks the
                    // defaults put in above.
                    texts[code] = pair.Value;
                }
            }

            return texts;
        }

        private static void Put(IDictionary<string, string> texts, string code, string text)
        {
            if (code.Length > 0 && !string.IsNullOrWhiteSpace(text) && !texts.ContainsKey(code))
            {
                texts.Add(code, text);
            }
        }
    }
}
