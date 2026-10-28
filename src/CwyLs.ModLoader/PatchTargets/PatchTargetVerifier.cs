using System;
using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader
{
    /// <summary>
    /// Verifies the registered patch targets against the running game and writes the result to the
    /// BepInEx log. This is the startup half of the seam check: the framework must say, out loud and
    /// once, whether the seams it is built on still exist.
    /// </summary>
    public static class PatchTargetVerifier
    {
        /// <summary>
        /// Verifies everything in <see cref="PatchTargetRegistry"/> by reflection.
        /// </summary>
        /// <returns>The report, so a caller can react to <see cref="PatchTargetReport.AllFound"/>.</returns>
        public static PatchTargetReport VerifyAll() =>
            VerifyAll(PatchTargetRegistry.All, new ReflectionMemberResolver());

        /// <summary>
        /// Verifies an explicit table with an explicit resolver, logging the summary at Info and
        /// every missing target at Error.
        /// </summary>
        /// <param name="targets">The table to verify.</param>
        /// <param name="resolver">How to look each target up.</param>
        /// <returns>The report.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="targets"/> or <paramref name="resolver"/> is null.</exception>
        public static PatchTargetReport VerifyAll(IEnumerable<PatchTarget> targets, IMemberResolver resolver)
        {
            var report = PatchTargetChecker.Check(targets, resolver);

            // One SETUP line per owner rather than six identical "targets: N ok" lines with no
            // owner tag.
            CwyLsModLoader.Log.LogDebug(report.Summarize());
            foreach (var line in report.MissingLines())
            {
                CwyLsModLoader.Log.LogError(line);
            }

            return report;
        }

        /// <summary>
        /// Checks the running game's version against <see cref="GameVersionGate.ExpectedGameVersion"/>
        /// and logs the result: Info on a match, Warning on a mismatch. A mismatch is never fatal -
        /// the patch-target report decides whether anything actually broke.
        /// </summary>
        /// <param name="actualVersion">Normally <c>UnityEngine.Application.version</c>.</param>
        /// <returns>The check, so a caller can branch on it.</returns>
        public static GameVersionCheck CheckGameVersion(string? actualVersion) =>
            CheckGameVersion(new GameVersionGate(), actualVersion);

        /// <summary>Checks a version against an explicit gate and logs the result.</summary>
        /// <param name="gate">The gate to apply.</param>
        /// <param name="actualVersion">The running game's version.</param>
        /// <returns>The check.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="gate"/> is null.</exception>
        public static GameVersionCheck CheckGameVersion(GameVersionGate gate, string? actualVersion)
        {
            if (gate == null)
            {
                throw new ArgumentNullException(nameof(gate));
            }

            var check = gate.Check(actualVersion);
            if (check.IsOk)
            {
                CwyLsModLoader.Log.LogDebug(check.Message);
            }
            else
            {
                CwyLsModLoader.Log.LogWarning(check.Message);
            }

            return check;
        }
    }
}
