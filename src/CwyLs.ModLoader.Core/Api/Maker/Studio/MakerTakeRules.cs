using System;
using System.Globalization;
using CwyLs.ModLoader.Core.Api.Maker.Editor;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Validation and WAV canonicalisation rules for Studio takes.</summary>
    public static class MakerTakeRules
    {
        public const string OwnOrigin = "own";
        public const string AiAssistedOrigin = "ai-assisted";
        public const string AiGeneratedOrigin = "ai-generated";
        public const string SourceKind = "source";
        public const string RenderKind = "render";

        public static bool IsOrigin(string? origin) =>
            string.Equals(origin, OwnOrigin, StringComparison.Ordinal)
            || string.Equals(origin, AiAssistedOrigin, StringComparison.Ordinal)
            || string.Equals(origin, AiGeneratedOrigin, StringComparison.Ordinal);

        public static bool ValidateOrigin(string? origin, out string? problem)
        {
            if (!IsOrigin(origin))
            {
                problem = "origin must be own, ai-assisted or ai-generated";
                return false;
            }

            problem = null;
            return true;
        }

        public static bool ValidateConsent(MakerStudioConsent? consent, out string? problem)
        {
            if (consent is null)
            {
                problem = "consent is required";
                return false;
            }

            if (!string.Equals(consent.StatementVersion, MakerStudioContract.ConsentVersion, StringComparison.Ordinal))
            {
                problem = "consent.statementVersion must be " + MakerStudioContract.ConsentVersion;
                return false;
            }

            problem = null;
            return true;
        }

        public static bool ValidateTrim(double? trimStartSec, double? trimEndSec, double? sourceDurationSec, out string? problem)
        {
            if (trimStartSec.HasValue)
            {
                var start = trimStartSec.Value;
                if (double.IsNaN(start) || double.IsInfinity(start) || start < 0d || start > MakerEditRules.MaxVoiceSeconds)
                {
                    problem = "trimStartSec must be a finite number between 0 and 60";
                    return false;
                }

                if (sourceDurationSec.HasValue && start >= sourceDurationSec.Value)
                {
                    problem = "trimStartSec must be less than sourceDurationSec";
                    return false;
                }
            }

            if (trimEndSec.HasValue)
            {
                var end = trimEndSec.Value;
                if (double.IsNaN(end) || double.IsInfinity(end) || end <= 0d || end > MakerEditRules.MaxVoiceSeconds)
                {
                    problem = "trimEndSec must be a finite number greater than 0 and at most 60";
                    return false;
                }

                if (sourceDurationSec.HasValue && end > sourceDurationSec.Value + 0.001d)
                {
                    problem = "trimEndSec must not exceed sourceDurationSec";
                    return false;
                }
            }

            if (trimStartSec.HasValue && trimEndSec.HasValue && trimEndSec.Value <= trimStartSec.Value)
            {
                problem = "trimEndSec must be greater than trimStartSec";
                return false;
            }

            problem = null;
            return true;
        }

        public static bool InspectAudio(
            string? kind,
            byte[]? wav,
            out byte[] canonical,
            out double durationSec,
            out string? errorCode,
            out string? problem)
        {
            canonical = new byte[0];
            durationSec = 0d;
            if (!MakerStudioPaths.IsTakeAudioKind(kind))
            {
                errorCode = MakerErrorCodes.BadRequest;
                problem = "kind must be source or render";
                return false;
            }

            var report = MakerWav.Inspect(wav);
            if (!report.Ok)
            {
                errorCode = report.Error ?? MakerErrorCodes.UnsupportedMedia;
                problem = report.Message;
                return false;
            }

            if (string.Equals(kind, RenderKind, StringComparison.Ordinal)
                && (report.Channels != 1 || report.SampleRate != MakerEditRules.MaxSampleRate))
            {
                errorCode = MakerErrorCodes.UnsupportedMedia;
                problem = string.Format(
                    CultureInfo.InvariantCulture,
                    "a render WAV must be 48000 Hz mono PCM16 (this one is {0} Hz, {1}-channel)",
                    report.SampleRate,
                    report.Channels);
                return false;
            }

            canonical = MakerWav.Canonical(wav!, report);
            durationSec = Math.Round(report.DurationSec, 3);
            errorCode = null;
            problem = null;
            return true;
        }
    }
}
