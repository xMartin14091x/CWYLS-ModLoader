using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core
{
    /// <summary>Runs a patch-target table through a resolver and collects a <see cref="PatchTargetReport"/>.</summary>
    public static class PatchTargetChecker
    {
        /// <summary>
        /// Checks every target. A resolver that throws is treated as a missing target rather than
        /// being allowed to abort the sweep: one broken seam must not hide the other twenty-five.
        /// The exception text lands in <see cref="PatchTargetResult.Detail"/>, which the plugin-side
        /// verifier logs at Error - this layer has no logger of its own by design (no BepInEx here).
        /// </summary>
        /// <param name="targets">The table to check.</param>
        /// <param name="resolver">How to look each target up.</param>
        /// <returns>A report over all targets, in table order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="targets"/> or <paramref name="resolver"/> is null.</exception>
        public static PatchTargetReport Check(IEnumerable<PatchTarget> targets, IMemberResolver resolver)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            if (resolver == null)
            {
                throw new ArgumentNullException(nameof(resolver));
            }

            var results = new List<PatchTargetResult>();
            foreach (var target in targets)
            {
                if (target == null)
                {
                    continue;
                }

                PatchTargetResult result;
                try
                {
                    result = resolver.Resolve(target)
                        ?? PatchTargetResult.Missing(target, "resolver returned no result");
                }
                catch (Exception ex)
                {
                    result = PatchTargetResult.Missing(
                        target,
                        "resolver threw " + ex.GetType().Name + ": " + ex.Message);
                }

                results.Add(result);
            }

            return new PatchTargetReport(results);
        }
    }
}
