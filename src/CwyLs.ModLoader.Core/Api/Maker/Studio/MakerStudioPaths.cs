using System;
using CwyLs.ModLoader.Core.Packs;

namespace CwyLs.ModLoader.Core.Api.Maker.Studio
{
    /// <summary>Constructs every Studio-owned pack-relative path from validated identifiers.</summary>
    public static class MakerStudioPaths
    {
        public const string Root = ".maker";
        public const string Project = Root + "/project.json";
        public const string Transactions = Root + "/transactions";
        public const string Takes = Root + "/takes";
        /// <summary>v2.4: the pack's voice origin and consent default (absent = none set).</summary>
        public const string VoiceDefault = Root + "/voice.json";

        /// <summary>
        /// True when <paramref name="id"/> can name Studio's per-line files on macOS and Windows: a line id that is not a Windows
        /// device name and has no trailing dot (<see cref="SafeFileNames.ProblemWith"/>). Other lines are read-only in Studio.
        /// </summary>
        public static bool CanNameFile(string? id) => MakerSegments.IsId(id) && SafeFileNames.ProblemWith(id) == null;

        public static string Line(string lang, string id)
        {
            RequireLang(lang);
            RequireId(id, "line id");
            return Root + "/lines/" + lang + "/" + id + ".json";
        }

        public static string TakeDir(string lang, string id, string takeId)
        {
            RequireLang(lang);
            RequireId(id, "line id");
            return Takes + "/" + lang + "/" + id + "/" + RequireTakeId(takeId);
        }

        public static string TakeAudio(string lang, string id, string takeId, string kind)
        {
            if (!IsTakeAudioKind(kind))
            {
                throw new ArgumentException("take audio kind must be source or render", nameof(kind));
            }

            return TakeDir(lang, id, takeId) + "/" + kind + ".wav";
        }

        public static bool IsTakeId(string? value) => IsOperationId(value);

        public static string RequireTakeId(string? value)
        {
            if (!IsTakeId(value))
            {
                throw new ArgumentException("takeId must be 32 lowercase hexadecimal characters", nameof(value));
            }

            return value!;
        }

        public static bool IsTakeAudioKind(string? kind) =>
            string.Equals(kind, "source", StringComparison.Ordinal)
            || string.Equals(kind, "render", StringComparison.Ordinal);

        public static bool IsTakeAudioPath(string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var parts = path!.Split('/');
            if (parts.Length != 6 || parts[0] != Root || parts[1] != "takes"
                || (parts[5] != "source.wav" && parts[5] != "render.wav"))
            {
                return false;
            }

            try
            {
                var kind = parts[5].Substring(0, parts[5].Length - 4);
                return TakeAudio(parts[2], parts[3], parts[4], kind) == path;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static string Glossary(string lang)
        {
            RequireLang(lang);
            return Root + "/glossary/" + lang + ".json";
        }

        public static string Receipt(string operationId) => Transactions + "/" + RequireOperation(operationId) + ".json";

        public static string Pending(string operationId) => Transactions + "/" + RequireOperation(operationId) + ".pending.json";

        public static string Stage(string operationId, int index)
        {
            if (index < 0 || index > 255)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return Transactions + "/" + RequireOperation(operationId) + "." + index + ".stage";
        }

        public static bool IsOperationId(string? value)
        {
            if (value is null || value.Length != 32)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        public static string RequireOperation(string? value)
        {
            if (!IsOperationId(value))
            {
                throw new ArgumentException("operationId must be 32 lowercase hexadecimal characters", nameof(value));
            }

            return value!;
        }

        // ---- M5d publication (W2b-03): per-(lang, clip) records, the portable provenance file, runtime voice paths.

        /// <summary>The pack-root disclosure of published clips (CONTRACT-v2 §2).</summary>
        public const string Provenance = "voice-provenance.json";

        /// <summary>Per-(language, clip) publication records.</summary>
        public const string Clips = Root + "/clips";

        public static string Clip(string lang, string clip)
        {
            RequireLang(lang);
            if (!Editor.MakerEditRules.IsClip(clip) || SafeFileNames.ProblemWith(clip) != null)
                throw new ArgumentException("invalid voice clip name", nameof(clip));
            return Clips + "/" + lang + "/" + clip + ".json";
        }

        public static bool IsClipRecordPath(string? path)
        {
            var parts = path?.Split('/');
            if (parts is null || parts.Length != 4 || parts[0] + "/" + parts[1] != Clips || !parts[3].EndsWith(".json", StringComparison.Ordinal))
                return false;
            try { return Clip(parts[2], parts[3].Substring(0, parts[3].Length - 5)) == path; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// A runtime voice file Studio publication may write, delete or restore (<c>audio/voice/&lt;folder&gt;/&lt;clip&gt;.wav|.ogg</c>),
        /// or its moved-aside copy under <c>_maker_backup/</c> (optionally dated, as <see cref="Editor.MakerPackFiles.MoveAside"/> names it).
        /// </summary>
        public static bool IsVoicePublicationPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var backup = path!.StartsWith(Editor.MakerEditRules.BackupFolder + "/", StringComparison.Ordinal);
            var parts = (backup ? path.Substring(Editor.MakerEditRules.BackupFolder.Length + 1) : path).Split('/');
            if (parts.Length != 4 || parts[0] != "audio" || parts[1] != "voice"
                || SafeFileNames.ProblemWith(parts[2]) != null || SafeFileNames.ProblemWith(parts[3]) != null
                || !CwyLs.ModLoader.Core.Voice.LanguageVoicePlan.TryNormalizeCode(parts[2], out _, out _))
                return false;
            var match = VoiceFileName.Match(parts[3]);
            return match.Success && Editor.MakerEditRules.IsClip(match.Groups[1].Value) && (backup || match.Groups[2].Length == 0);
        }

        private static readonly System.Text.RegularExpressions.Regex VoiceFileName =
            new System.Text.RegularExpressions.Regex(@"^([A-Za-z0-9_()-]{1,128})(\.\d{8}-\d{6}(?:-\d+)?)?\.(?:wav|ogg)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void RequireLang(string value)
        {
            if (!MakerSegments.IsLang(value) || SafeFileNames.ProblemWith(value) != null)
            {
                throw new ArgumentException("invalid language code", nameof(value));
            }
        }

        private static void RequireId(string value, string name)
        {
            if (!MakerSegments.IsId(value) || SafeFileNames.ProblemWith(value) != null)
            {
                throw new ArgumentException("invalid " + name, nameof(value));
            }
        }
    }
}
