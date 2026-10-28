using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core.Satone.Say;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Satone.Motion
{
    /// <summary>
    /// Everything about a <see cref="Satone.Say(string, SayOptions)"/> call except the text:
    /// the clip, the translations, the expression, the motion and the gaze.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the engine-facing half of <see cref="SayRequest"/>. It exists separately only
    /// because it can carry a live <see cref="AudioClip"/>, which the Unity-free core cannot name.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// Satone.Say("Hello from the Mod Loader.", new SayOptions
    /// {
    ///     VoiceName = "Voice_CWY_Test",
    ///     Facial = "smile",
    ///     Motion = "wild001_good",
    ///     TextJa = "モッドローダーからこんにちは。"
    /// });
    /// </code>
    /// </example>
    public sealed class SayOptions
    {
        /// <summary>
        /// Per-language translations, keyed by language code. The game's text box shows the one for
        /// the pack language the player chose; in the game's own languages it shows the line's own
        /// text. Mutable and never null, so a collection initialiser
        /// (<c>Texts = { ["th"] = "…" }</c>) works.
        /// </summary>
        public Dictionary<string, string> Texts { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Japanese column of the game's row; null repeats the line's own text.</summary>
        public string? TextJa { get; set; }

        /// <summary>
        /// The voice clip name to speak. With <see cref="Clip"/> set this is the name the clip is
        /// registered under; without it, the name must already resolve - a pack clip, a clip
        /// another mod registered, or one of the game's own 1381 lines. Null and no clip means a
        /// text-only line.
        /// </summary>
        public string? VoiceName { get; set; }

        /// <summary>
        /// A clip to register and speak, for example one a TTS engine just synthesised. It is
        /// registered under <see cref="VoiceName"/>, or under a generated <c>CWY_Say_&lt;n&gt;</c>
        /// when no name was given.
        /// </summary>
        public AudioClip? Clip { get; set; }

        /// <summary>Expression name, alias or id from the Satone table; null leaves her face alone.</summary>
        public string? Facial { get; set; }

        /// <summary>Body-motion name, alias or id from the Satone table; null leaves her motion alone.</summary>
        public string? Motion { get; set; }

        /// <summary>How far she turns towards the player, 0..1; null leaves the gaze to the game.</summary>
        public float? LookScale { get; set; }

        /// <summary>How long the gaze turn takes, in seconds.</summary>
        public float LookSeconds { get; set; } = SayRequest.DefaultLookSeconds;

        /// <summary>The language the line is written in; null means English.</summary>
        public string? Language { get; set; }

        /// <summary>False to leave her mouth still while the clip plays.</summary>
        public bool MoveMouth { get; set; } = true;

        /// <summary>Adds one translation and returns this, for chaining.</summary>
        /// <param name="lang">Language code, e.g. <c>th</c>.</param>
        /// <param name="text">The line in that language.</param>
        /// <returns>This instance.</returns>
        public SayOptions With(string lang, string text)
        {
            if (!string.IsNullOrWhiteSpace(lang) && !string.IsNullOrWhiteSpace(text))
            {
                Texts[lang] = text;
            }

            return this;
        }

        /// <summary>Turns these options plus a line of text into a core request.</summary>
        /// <param name="text">What she says.</param>
        /// <returns>The request the feature validates and queues.</returns>
        public SayRequest ToRequest(string text) =>
            new SayRequest
            {
                Text = text ?? string.Empty,
                TextJa = TextJa,
                Texts = Texts.Count == 0 ? null : new Dictionary<string, string>(Texts, StringComparer.OrdinalIgnoreCase),
                VoiceName = VoiceName,
                Facial = Facial,
                Motion = Motion,
                LookScale = LookScale,
                LookSeconds = LookSeconds,
                Language = Language,
                MoveMouth = MoveMouth
            };
    }
}
