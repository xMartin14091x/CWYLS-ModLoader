using System;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// A pack file or <c>_rules.json</c> entry that could not become a <see cref="TextureRule"/>.
    /// <para>
    /// Kept rather than thrown: a misspelled slot in one file must not cost a pack its other forty
    /// textures. The Unity side logs these once each, so a pack author sees the typo without the
    /// game stopping.
    /// </para>
    /// </summary>
    public sealed class TextureRuleProblem
    {
        /// <summary>Creates a problem record.</summary>
        /// <param name="packId">Pack the file belongs to.</param>
        /// <param name="path">What was being read: a pack-relative path, or a <c>_rules.json</c> entry position.</param>
        /// <param name="reason">Why it was rejected, in the words a pack author needs.</param>
        /// <exception cref="ArgumentException"><paramref name="packId"/> or <paramref name="path"/> is blank.</exception>
        public TextureRuleProblem(string packId, string path, string reason)
        {
            if (string.IsNullOrWhiteSpace(packId))
            {
                throw new ArgumentException("a pack id is required", nameof(packId));
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("a path is required", nameof(path));
            }

            PackId = packId;
            Path = path;
            Reason = reason ?? string.Empty;
        }

        /// <summary>The pack the rejected file belongs to.</summary>
        public string PackId { get; }

        /// <summary>The pack-relative path, or <c>textures/_rules.json[2]</c> for a rules entry.</summary>
        public string Path { get; }

        /// <summary>Why it was rejected. Never null.</summary>
        public string Reason { get; }

        /// <summary><c>cwy.example/textures/Body/glow.png: "glow" is not a known slot alias...</c>.</summary>
        public override string ToString() => PackId + "/" + Path + ": " + Reason;
    }
}
