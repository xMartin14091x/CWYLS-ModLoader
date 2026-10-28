using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Subtitles;

namespace CwyLs.ModLoader.Core.Satone.Say
{
    /// <summary>
    /// A <see cref="SayRequest"/> with every id and index resolved: what the adapter needs to build
    /// the game's rows and start the scenario, and nothing that needs the engine.
    /// </summary>
    public sealed class SayLine
    {
        /// <summary>"no expression/motion change", the value the game's own rows use.</summary>
        public const int NoMotion = -1;

        private readonly IReadOnlyDictionary<string, string> _texts;

        /// <summary>Builds a resolved line.</summary>
        /// <param name="id">The line number from <see cref="SayIdGenerator.Next"/>.</param>
        /// <param name="request">The request it came from. Required.</param>
        /// <param name="voiceName">
        /// The clip name the row will carry: the caller's name, the generated one when a clip was
        /// supplied, or <see cref="string.Empty"/> for a text-only line.
        /// </param>
        /// <param name="facialId">Expression id, or <see cref="NoMotion"/>.</param>
        /// <param name="motionId">Body-motion id, or <see cref="NoMotion"/>.</param>
        /// <param name="queuedAt">Seconds on the caller's clock when the line was queued.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is below 1.</exception>
        public SayLine(int id, SayRequest request, string? voiceName, int facialId, int motionId, double queuedAt)
        {
            if (id < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(id), id, "a line id starts at 1");
            }

            Id = id;
            Request = request ?? throw new ArgumentNullException(nameof(request));
            GroupId = SayIdGenerator.GroupId(id);
            LocalizationId = SayIdGenerator.LocalizationId(id);
            EpisodeNumber = SayIdGenerator.EpisodeFor(id);
            VoiceName = voiceName ?? string.Empty;
            FacialId = facialId;
            MotionId = motionId;
            QueuedAt = queuedAt;
            _texts = request.BuildTexts();
        }

        /// <summary>The line number, unique for the session.</summary>
        public int Id { get; }

        /// <summary>The request this line was built from.</summary>
        public SayRequest Request { get; }

        /// <summary>The scenario group id the rows carry, e.g. <c>cwy_say_7</c>.</summary>
        public string GroupId { get; }

        /// <summary>The localization row id, e.g. <c>cwy_say_7_001</c>.</summary>
        public string LocalizationId { get; }

        /// <summary>The group's episode number, unique among <c>MotionScenario</c> groups.</summary>
        public float EpisodeNumber { get; }

        /// <summary>The clip name on the row; empty for a text-only line.</summary>
        public string VoiceName { get; }

        /// <summary>True when the line has a clip to play.</summary>
        public bool HasVoice => VoiceName.Length > 0;

        /// <summary>Expression id to set before she speaks, or <see cref="NoMotion"/>.</summary>
        public int FacialId { get; }

        /// <summary>Body-motion id to set before she speaks, or <see cref="NoMotion"/>.</summary>
        public int MotionId { get; }

        /// <summary>Gaze weight to set before she speaks, or null to leave the gaze alone.</summary>
        public float? LookScale => Request.LookScale;

        /// <summary>What she says, in <see cref="SayRequest.Language"/>.</summary>
        public string Text => Request.Text;

        /// <summary>The Japanese column of the row: the caller's, or <see cref="Text"/>.</summary>
        public string TextJa => string.IsNullOrWhiteSpace(Request.TextJa) ? Request.Text : Request.TextJa!;

        /// <summary>Seconds on the caller's clock when the line was queued.</summary>
        public double QueuedAt { get; }

        /// <summary>Every language this line has text in, keyed by normalised code.</summary>
        public IReadOnlyDictionary<string, string> Texts => _texts;

        /// <summary>The text in one language.</summary>
        /// <param name="lang">Any spelling of a language code.</param>
        /// <param name="text">The text, or <see cref="string.Empty"/>. Never null.</param>
        /// <returns>True when this line has that language.</returns>
        public bool TryText(string? lang, out string text)
        {
            var code = SubtitleLanguages.Normalize(lang);
            if (code.Length > 0 && _texts.TryGetValue(code, out var found) && !string.IsNullOrEmpty(found))
            {
                text = found;
                return true;
            }

            text = string.Empty;
            return false;
        }

        /// <summary>How long the line has been waiting.</summary>
        /// <param name="nowSeconds">The current time on the same clock as <see cref="QueuedAt"/>.</param>
        public double WaitedSeconds(double nowSeconds) => Math.Max(0d, nowSeconds - QueuedAt);

        /// <summary>The text, cut to <paramref name="max"/> characters with an ellipsis.</summary>
        /// <param name="max">Longest result, in characters. Values below 4 are raised to 4.</param>
        public string ShortText(int max = 60)
        {
            var limit = max < 4 ? 4 : max;
            var text = Text.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
        }

        /// <summary>A one-line description for a log or the trace bus.</summary>
        public override string ToString() =>
            "#" + Id.ToString(CultureInfo.InvariantCulture) + " \"" + ShortText() + "\""
            + " voice=" + (HasVoice ? VoiceName : "none")
            + " facial=" + Show(FacialId)
            + " motion=" + Show(MotionId)
            + " look=" + (LookScale.HasValue ? LookScale.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-")
            + " langs=" + string.Join("/", Keys());

        private string[] Keys()
        {
            var keys = new string[_texts.Count];
            var i = 0;
            foreach (var pair in _texts)
            {
                keys[i++] = pair.Key;
            }

            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }

        private static string Show(int id) =>
            id == NoMotion ? "-" : id.ToString(CultureInfo.InvariantCulture);
    }
}
