using System;
using System.IO;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Smoke;

namespace CwyLs.ModLoader.Packs
{
    /// <summary>
    /// The Phase-1 pack checks, registered by <see cref="PackManager.DiscoverAndLoad"/> so they
    /// exist whether or not the smoke plugin is installed; the runner in CwyLs.ModLoader.Smoke picks
    /// them up from the shared <see cref="SmokeRegistry"/>.
    /// <para>
    /// They answer the three questions a launch has to settle: did discovery run at all, did a
    /// real deployed pack load, and is a broken pack refused rather than half-loaded.
    /// </para>
    /// </summary>
    internal static class P1PackChecks
    {
        /// <summary>Smoke phase label of these checks.</summary>
        internal const string Phase = "P1";

        /// <summary>Id of the pack shipped in the repo as <c>packs/cwy.example</c>.</summary>
        internal const string ExamplePackId = "cwy.example";

        /// <summary>Folder the bad-manifest check creates and removes inside <c>CWYLS Mods/</c>.</summary>
        internal const string BadPackFolder = "_smoke_bad";

        private const string ScanOkId = "p1.packs.scan_ok";
        private const string ExampleLoadedId = "p1.packs.example_loaded";
        private const string BadManifestId = "p1.packs.bad_manifest_skipped";

        /// <summary>Registers every P1 pack check. Idempotent - the registry replaces by id.</summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(ScanOkId, Phase, ScanOk));
            SmokeRegistry.Register(new SmokeCheck(ExampleLoadedId, Phase, ExampleLoaded));
            SmokeRegistry.Register(new SmokeCheck(BadManifestId, Phase, BadManifestSkipped));
        }

        private static SmokeCheckResult ScanOk()
        {
            if (!PackManager.HasScanned)
            {
                return SmokeCheckResult.Fail(ScanOkId, "pack discovery never ran");
            }

            if (PackManager.LastScanError != null)
            {
                return SmokeCheckResult.Fail(ScanOkId, "pack discovery failed: " + PackManager.LastScanError);
            }

            return SmokeCheckResult.Pass(
                ScanOkId,
                $"{PackManager.Loaded.Count} loaded, {PackManager.Skipped.Count} skipped from {ModLoaderPaths.PacksRoot}");
        }

        private static SmokeCheckResult ExampleLoaded()
        {
            var pack = PackManager.Find(ExamplePackId);
            if (pack != null)
            {
                return SmokeCheckResult.Pass(
                    ExampleLoadedId, $"{pack.Manifest} ({pack.Index.Count} file(s)) at {pack.RootPath}");
            }

            // The pack lives in the repo and is copied in by ./deploy-packs.sh, so "not deployed"
            // is the expected failure on a fresh install and the detail says exactly what to do.
            var skipped = PackManager.FindSkipped(ExamplePackId);
            return SmokeCheckResult.Fail(
                ExampleLoadedId,
                skipped is null
                    ? "deploy packs/" + ExamplePackId
                    : $"deploy packs/{ExamplePackId} (found but skipped: {skipped.Reason} - {skipped.Detail})");
        }

        private static SmokeCheckResult BadManifestSkipped()
        {
            var folder = Path.Combine(ModLoaderPaths.PacksRoot, BadPackFolder);
            var manifest = Path.Combine(folder, PackContentIndex.ManifestFileName);

            try
            {
                Directory.CreateDirectory(folder);

                // Deliberately truncated: the reader must refuse it with a line:column, and the
                // resolver must turn that into an InvalidManifest skip rather than an exception.
                File.WriteAllText(manifest, "{ \"id\": \"smoke.bad\", \"name\": ");

                // Quiet: this check IS the broken pack, so the skip it provokes is the pass
                // condition, not a warning the user should act on.
                PackManager.Rescan(quiet: true);

                var skipped = PackManager.FindSkipped(BadPackFolder);
                if (skipped is null)
                {
                    return SmokeCheckResult.Fail(
                        BadManifestId, $"'{BadPackFolder}' was not reported as skipped after a rescan");
                }

                if (skipped.Reason != PackSkipReason.InvalidManifest)
                {
                    return SmokeCheckResult.Fail(
                        BadManifestId,
                        $"'{BadPackFolder}' was skipped as {skipped.Reason}, expected {PackSkipReason.InvalidManifest}");
                }

                if (PackManager.Find(BadPackFolder) != null || PackManager.Find("smoke.bad") != null)
                {
                    return SmokeCheckResult.Fail(BadManifestId, $"'{BadPackFolder}' loaded despite a broken manifest");
                }

                return SmokeCheckResult.Pass(BadManifestId, skipped.Reason + " - " + skipped.Detail);
            }
            catch (Exception ex)
            {
                // Reported rather than rethrown so the detail names the path that could not be
                // written; the runner would otherwise only record the exception type.
                return SmokeCheckResult.Fail(
                    BadManifestId, $"could not exercise {folder}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Cleanup(folder);
            }
        }

        private static void Cleanup(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"smoke: could not remove {folder}: {ex.GetType().Name}: {ex.Message}; delete it by hand");
            }

            // Always rescan, even if the delete failed: leaving the process believing in a pack
            // that this check invented would poison every later check and the report itself.
            // Quiet for the same reason as above - if the delete failed, the skip is still ours.
            PackManager.Rescan(quiet: true);
        }
    }
}
