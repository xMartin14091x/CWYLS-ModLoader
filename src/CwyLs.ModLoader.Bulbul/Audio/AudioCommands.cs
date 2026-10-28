using System;
using System.Collections.Generic;
using System.Text;
using CwyLs.ModLoader.Core.Audio;
using Framework = CwyLs.ModLoader.Console;

namespace CwyLs.ModLoader.Bulbul.Audio
{
    /// <summary>
    /// The <c>audio</c> console command (group <c>game</c>): status, the row list, the A/B switch, a
    /// one-shot preview, the pack tracks, on/off and reload. Registered by the feature itself, so the
    /// console's own files need no change. Runs on the main thread (the console's).
    /// </summary>
    internal static class AudioCommands
    {
        /// <summary>The command name.</summary>
        internal const string Name = "audio";

        private static bool _registered;

        /// <summary>Registers the command once.</summary>
        internal static void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            Framework.ConsoleFeature.Register(
                Name,
                "Game sounds from packs: list the rows, A/B the original against the pack clip, pack music.",
                AudioCommandParser.Usage,
                "game",
                Run);
        }

        /// <summary>Runs one <c>audio</c> line.</summary>
        /// <param name="args">The arguments after the name.</param>
        /// <returns>What to print.</returns>
        internal static string Run(IReadOnlyList<string> args)
        {
            var request = AudioCommandParser.Parse(args);
            if (!request.IsValid)
            {
                return request.Problem;
            }

            if (!AudioFeature.Enabled && request.Verb != AudioVerb.Help)
            {
                return AudioFeature.Status();
            }

            switch (request.Verb)
            {
                case AudioVerb.Status:
                    return AudioFeature.Status();

                case AudioVerb.List:
                    AudioFeature.Apply("list");
                    return AudioFeature.RowsKnown
                        ? AudioListFormatter.Format(AudioFeature.Rows(), request.Target)
                        : "the game's sound tables are not loaded yet; try again once the room is up";

                case AudioVerb.Ab:
                    return Ab(request);

                case AudioVerb.Play:
                    return AudioFeature.Play(request.Target);

                case AudioVerb.Music:
                    return Music();

                case AudioVerb.On:
                    AudioFeature.SetActive(true);
                    return string.Empty;

                case AudioVerb.Off:
                    AudioFeature.SetActive(false);
                    return string.Empty;

                case AudioVerb.Reload:
                    AudioFeature.Reload("console");
                    return "reloading the packs' audio; the AUDIO lines follow when the clips have decoded";

                default:
                    return Help();
            }
        }

        private static string Ab(AudioCommandRequest request)
        {
            var result = AudioFeature.Select(request.Target, request.Variant);
            if (!result.Ok)
            {
                return result.Problem;
            }

            // Each row already printed its AUDIO line; say something only for several rows or a failure.
            if (result.Outcomes.Count == 1 && result.Outcomes[0].Mode != AudioSwitchMode.Failed)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var failed = 0;
            foreach (var outcome in result.Outcomes)
            {
                if (outcome.Mode == AudioSwitchMode.Failed)
                {
                    failed++;
                    sb.Append('\n').Append(outcome.Key).Append(": ").Append(outcome.Detail);
                }
            }

            return result.Outcomes.Count + " row(s) switched" + (failed > 0 ? ", " + failed + " failed:" : string.Empty) + sb;
        }

        private static string Music()
        {
            var tracks = AudioFeature.MusicTracks();
            if (tracks.Count == 0)
            {
                return AudioFeature.PackMusicEnabled
                    ? "no pack music (put .ogg or .wav files in a pack's audio/music/)"
                    : "pack music is off (" + AudioFeature.ConfigSection + "." + AudioFeature.ConfigKeyPackMusic + ", or a seam is missing: " + AudioFeature.MissingSeams + ")";
            }

            var sb = new StringBuilder();
            sb.Append(tracks.Count).Append(" pack track(s):");
            foreach (var info in tracks)
            {
                sb.Append('\n')
                  .Append("'").Append(info.Track.Title).Append("'  ")
                  .Append(info.Track.PackId).Append('/').Append(info.Track.RelativePath)
                  .Append("  ").Append(AudioListFormatter.Seconds(info.Seconds))
                  .Append("  tag=").Append(info.Track.Tag.ToString().ToLowerInvariant())
                  .Append("  ").Append(info.Listed ? "listed" : "NOT listed: " + info.Problem)
                  .Append("  uuid=").Append(info.Track.Uuid);
            }

            return sb.ToString();
        }

        private static string Help() =>
            "usage: " + AudioCommandParser.Usage + "\n"
            + "  audio list replaced         rows a pack replaces (length, pack clip, which is selected, what is playing)\n"
            + "  audio ab LightRain original the game's own rain; 'audio ab LightRain pack' the pack's; no word toggles\n"
            + "  audio ab all                every replaced row at once (toggle)\n"
            + "  audio play Click            plays a one-shot row once (loops: switch them on in the game)\n"
            + "  audio music                 the pack tracks in the music list\n"
            + "  audio off | on              every row back to the game's clips, or the replacements back";
    }
}
