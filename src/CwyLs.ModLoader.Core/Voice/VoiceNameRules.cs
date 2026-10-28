using System;
using System.IO;

namespace CwyLs.ModLoader.Core.Voice
{
    /// <summary>
    /// What counts as a voice name, and how one is derived from a file name or from the path the
    /// game hands to its audio manager.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The character rule is deliberately narrow. A voice name ends up as a dictionary key that the
    /// game, our packs and a mod all spell independently, and the game's own names are plain ASCII
    /// (<c>Voice_Tutorial_1_001</c>). Allowing spaces, dots or non-ASCII would mean a clip that
    /// silently never resolves because one side normalised it and the other did not.
    /// </para>
    /// <para>
    /// <see cref="KeyFromAudioPath"/> mirrors <c>AudioManager&lt;T&gt;.PathToName</c>
    /// (KanKikuchi.AudioManager), which is
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> - the same call, so the two cannot
    /// drift apart.
    /// </para>
    /// </remarks>
    public static class VoiceNameRules
    {
        /// <summary>The rule, as a regular expression, for documentation and error messages.</summary>
        /// <remarks>
        /// <see cref="IsValid"/> implements this as a character loop rather than a
        /// <c>Regex</c>: it runs inside a Harmony prefix on every clip lookup the game makes.
        /// </remarks>
        /// <remarks>Parentheses are allowed because the game itself uses them (1.17.3:
        /// <c>Voice_MainScenario_12(5)_001</c>…<c>_008</c>); refusing them silently dropped those Thai clips.</remarks>
        public const string Pattern = @"^[A-Za-z0-9_\-()]+$";

        /// <summary>Why a name was refused, when it is refused for its characters.</summary>
        public const string CharacterRuleReason =
            "a voice name may only contain letters, digits, '_', '-' and parentheses (" + Pattern + ")";

        /// <summary>True when <paramref name="name"/> is a usable voice name.</summary>
        /// <param name="name">The candidate name; null and blank are never valid.</param>
        public static bool IsValid(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (var c in name!)
            {
                var ok = (c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '_'
                    || c == '-'
                    || c == '('
                    || c == ')';

                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Derives the voice name from a pack file name: the file name without its extension.
        /// </summary>
        /// <param name="fileName">
        /// A file name such as <c>Voice_CWY_Test.wav</c>. A leading directory is tolerated and
        /// dropped, so a pack-relative path works too.
        /// </param>
        /// <param name="name">The derived name, or null when there is none.</param>
        /// <param name="reason">Why it was refused, or null on success.</param>
        /// <returns>True when a valid name was derived.</returns>
        public static bool TryFromFileName(string? fileName, out string? name, out string? reason)
        {
            name = null;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                reason = "the file name is blank";
                return false;
            }

            var trimmed = fileName!.Trim();
            var slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            var bare = slash < 0 ? trimmed : trimmed.Substring(slash + 1);

            // Only the last extension is dropped, and a leading dot is not an extension:
            // ".keep" stays ".keep" and is then refused by the character rule, which is the
            // message we want rather than "the file name is blank".
            var dot = bare.LastIndexOf('.');
            var candidate = dot <= 0 ? bare : bare.Substring(0, dot);

            if (candidate.Length == 0)
            {
                reason = "the file name is blank";
                return false;
            }

            if (!IsValid(candidate))
            {
                reason = "'" + candidate + "' is not a usable voice name: " + CharacterRuleReason;
                return false;
            }

            name = candidate;
            reason = null;
            return true;
        }

        /// <summary>
        /// The cache key the game would compute for an audio path, so a lookup can be answered
        /// with exactly the name the game itself would have used.
        /// </summary>
        /// <param name="audioPath">What the game passed to <c>Play</c> - usually a bare name.</param>
        /// <returns>The key, or null when the path yields none.</returns>
        public static string? KeyFromAudioPath(string? audioPath)
        {
            if (string.IsNullOrEmpty(audioPath))
            {
                return null;
            }

            try
            {
                var key = Path.GetFileNameWithoutExtension(audioPath);
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch (ArgumentException ex)
            {
                // Old Mono throws on a path containing characters it considers invalid. A lookup
                // key we cannot compute simply means "not ours"; the caller falls back to the game.
                VoiceDiagnostics.Warn(
                    "voice key could not be derived from '" + audioPath + "': " + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
