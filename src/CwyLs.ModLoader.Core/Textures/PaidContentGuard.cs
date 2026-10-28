using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>
    /// Project rule: paid content is never modified, replaced, re-skinned
    /// or unlocked. This is the check every texture and material-property rule passes before it
    /// touches a material.
    /// <para>
    /// What the game marks as paid, as observed in game 1.17.3:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Steam DLC: <c>NestopiSystem.Steam.BulbulSteamDLC.DLC_1 = 3718890</c>
    ///   - the original soundtrack, audio only;
    ///   <c>NestopiSystem.Steam.SteamManager.IsInstalledDLC</c> has no caller.</description></item>
    ///   <item><description>Decorations and environments unlock through
    ///   <c>UnlockItemService.ConditionsType</c> <c>None</c>/<c>Scenario</c>/<c>PointPurchase</c>:
    ///   in-game points, not money (collaborations and exclusives are treated as free).</description></item>
    ///   <item><description>Mobile-only store items (<c>DecorationSkinListItemModelForMobile.IsPurchased</c>)
    ///   have no PC materials.</description></item>
    /// </list>
    /// <para>
    /// So no material of the PC build is paid today. The guard is the defence for a later DLC:
    /// a material whose name carries a store marker is refused, with one warning naming the rule.
    /// No material in game 1.17.3 matches these patterns (all 235 materials checked).
    /// </para>
    /// </summary>
    public static class PaidContentGuard
    {
        /// <summary>Material/renderer name patterns treated as paid content.</summary>
        public static IReadOnlyList<string> DeniedPatterns { get; } = new[] { "*dlc*", "*shop*", "*purchase*", "*premium*" };

        /// <summary>True when the name looks like paid content and must not be touched.</summary>
        /// <param name="name">A material or renderer name, or a rule's material pattern.</param>
        /// <param name="reason">The sentence for the warning, or null.</param>
        public static bool IsRefused(string? name, out string? reason)
        {
            reason = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            foreach (var pattern in DeniedPatterns)
            {
                if (MaterialNamePattern.Matches(pattern, name!.Trim()))
                {
                    reason = "'" + name + "' looks like paid content (matches " + pattern +
                             "); CWY:LS Mod Loader never modifies paid content";
                    return true;
                }
            }

            return false;
        }
    }
}
