using System.Collections.Generic;
using System.Globalization;

namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// A stable short string over the packs' master-data files, used to answer one question: has
    /// anything a pack ships actually changed since the last injection?
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because <c>PackManager.PacksResolved</c> fires on every rescan - the smoke runner
    /// and the texture feature both rescan - and re-injecting identical rows is wasted work and a
    /// repeated log line. Comparing fingerprints turns that into one Debug line.
    /// </para>
    /// <para>
    /// The hash is FNV-1a 64 over the file contents themselves rather than over file timestamps and
    /// sizes: it is the only form that cannot be fooled by an edit that keeps the length, needs no
    /// filesystem access (so it is testable off-engine), and the files are a few kilobytes at most.
    /// The pack count and the total length are carried alongside the hash, so an accidental
    /// collision would also have to match both.
    /// </para>
    /// <para>
    /// This is a change detector, not a security primitive. It answers "did the bytes move?", and
    /// nothing trusts it with anything else.
    /// </para>
    /// </remarks>
    public static class MasterDataFingerprint
    {
        /// <summary>The fingerprint of no packs at all.</summary>
        public const string Empty = "p0:l0:cbf29ce484222325";

        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        /// <summary>Marker hashed in place of a file the pack does not ship.</summary>
        private const char Absent = '\u0000';

        /// <summary>Separator hashed between fields, so concatenations cannot collide.</summary>
        private const char Separator = '\u001F';

        /// <summary>
        /// The fingerprint of a set of packs, in the order given. Order matters: the same files in
        /// a different pack order are a different merge, because a later pack overrides an earlier
        /// one.
        /// </summary>
        /// <param name="packs">The packs' files; null entries are skipped, null itself is <see cref="Empty"/>.</param>
        /// <returns>A short, stable, printable string, e.g. <c>p1:l842:3f2a1c9d0e5b7a64</c>.</returns>
        public static string Compute(IEnumerable<MasterDataPackInput?>? packs)
        {
            var hash = FnvOffsetBasis;
            var packCount = 0;
            var totalLength = 0L;

            if (packs != null)
            {
                foreach (var pack in packs)
                {
                    if (pack is null)
                    {
                        continue;
                    }

                    packCount++;
                    Feed(ref hash, pack.PackId, ref totalLength);
                    Feed(ref hash, pack.LocalizationJson, ref totalLength);
                    Feed(ref hash, pack.NovelJson, ref totalLength);
                    Feed(ref hash, pack.ScenarioGroupsJson, ref totalLength);

                    foreach (var subtitles in pack.Subtitles)
                    {
                        Feed(ref hash, subtitles.RelativePath, ref totalLength);
                        Feed(ref hash, subtitles.Json, ref totalLength);
                    }
                }
            }

            return "p" + packCount.ToString(CultureInfo.InvariantCulture)
                   + ":l" + totalLength.ToString(CultureInfo.InvariantCulture)
                   + ":" + hash.ToString("x16", CultureInfo.InvariantCulture);
        }

        /// <summary>Folds one field in, distinguishing an absent file from an empty one.</summary>
        private static void Feed(ref ulong hash, string? value, ref long totalLength)
        {
            if (value is null)
            {
                Mix(ref hash, Absent);
            }
            else
            {
                totalLength += value.Length;

                foreach (var c in value)
                {
                    Mix(ref hash, c);
                }
            }

            Mix(ref hash, Separator);
        }

        private static void Mix(ref ulong hash, char c)
        {
            // Both bytes of the UTF-16 unit, so a change in the high byte cannot be lost.
            hash = (hash ^ (byte)(c & 0xFF)) * FnvPrime;
            hash = (hash ^ (byte)((c >> 8) & 0xFF)) * FnvPrime;
        }
    }
}
