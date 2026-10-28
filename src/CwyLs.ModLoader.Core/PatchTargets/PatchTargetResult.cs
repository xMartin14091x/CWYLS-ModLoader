using System;

namespace CwyLs.ModLoader.Core
{
    /// <summary>The outcome of resolving one <see cref="PatchTarget"/> against the running game.</summary>
    public sealed class PatchTargetResult
    {
        private PatchTargetResult(PatchTarget target, bool found, string detail)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            Found = found;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The target that was checked.</summary>
        public PatchTarget Target { get; }

        /// <summary>
        /// True when the member exists <em>as specified</em>. A member that exists but whose
        /// signature differs from <see cref="PatchTarget.ExpectedSignature"/> is not found:
        /// the seam moved, even if the name survived.
        /// </summary>
        public bool Found { get; }

        /// <summary>
        /// On success, the canonical signature that was actually read. On failure, why the
        /// target could not be resolved. Never null.
        /// </summary>
        public string Detail { get; }

        /// <summary>Records a target that resolved.</summary>
        /// <param name="target">The target that was checked.</param>
        /// <param name="detail">The canonical signature that was read.</param>
        public static PatchTargetResult Ok(PatchTarget target, string detail) =>
            new PatchTargetResult(target, true, detail);

        /// <summary>Records a target that did not resolve.</summary>
        /// <param name="target">The target that was checked.</param>
        /// <param name="detail">Why it did not resolve; this text is what reaches the log.</param>
        public static PatchTargetResult Missing(PatchTarget target, string detail) =>
            new PatchTargetResult(target, false, detail);

        /// <inheritdoc />
        public override string ToString() => (Found ? "ok " : "missing ") + Target.Id + ": " + Detail;
    }
}
