using System;
using UnityEngine;
using Game = global::Bulbul;

namespace CwyLs.ModLoader.Bulbul.MasterData
{
    /// <summary>
    /// Owns the hidden <c>DontDestroyOnLoad</c> object that watches for the moment the game's
    /// master data has finished loading, and asks <see cref="MasterDataFeature"/> to inject then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A host rather than a Harmony postfix because there is no synchronous game method that means
    /// "the master data is now loaded". <c>MasterDataLoader.Load</c> is <c>async UniTask</c>, so a
    /// postfix on it fires at its first <c>await</c>, before the three collections are assigned;
    /// <c>LoadCompletionAsync</c> is async too, so a postfix there has the same problem; and
    /// patching the auto-property setter of
    /// <c>IsLoaded</c> would be at the mercy of Mono inlining that one-line setter into the
    /// compiler-generated <c>MoveNext</c>, which would make the patch silently never fire.
    /// </para>
    /// <para>
    /// What is reliable is the flag itself: <c>IsLoaded</c> is set true after all three
    /// assignments and before the load task completes. Reading it
    /// costs a property call and three null checks per frame, and it cannot be inlined away from
    /// <em>our</em> side. So the host reads it every frame once it has the loader, and after
    /// injecting drops to one check every <see cref="WatchIntervalFrames"/> frames, watching for
    /// <c>Dispose</c> nulling the collections - which is how a re-load announces itself,
    /// since <c>Dispose</c> leaves <c>IsLoaded</c> true.
    /// </para>
    /// <para>
    /// Same pattern as <c>TextureHost</c> and <c>SmokeHost</c>: the game destroys BepInEx plugin
    /// components about 0.2 s after boot, so nothing that needs an
    /// <c>Update</c> may live on the plugin.
    /// </para>
    /// </remarks>
    public static class MasterDataHost
    {
        /// <summary>Name of the hidden host GameObject, for anyone reading a scene inspector.</summary>
        public const string RunnerObjectName = "CWY.MasterDataRunner";

        /// <summary>How many frames apart the post-injection re-load watch runs.</summary>
        public const int WatchIntervalFrames = 30;

        /// <summary>
        /// How many frames apart the loader is looked for while it does not exist yet, and after a
        /// failed injection.
        /// </summary>
        /// <remarks>
        /// Before <c>BootBehavior</c> has called <c>ProjectLifetimeScope.SetInstance</c>, the
        /// scope's <c>TryResolve</c> dereferences a null static and throws;
        /// <c>GameScopes.TryProject</c> catches and logs that, and
        /// doing it every frame would cost an exception a frame for the first seconds of a session.
        /// Once the <c>Load</c> postfix has captured the loader the lookup is a field read, and the
        /// cheap per-frame <see cref="IsReady"/> check takes over.
        /// </remarks>
        public const int SearchIntervalFrames = 10;

        private static readonly object Gate = new object();

        private static MasterDataRunner? _runner;
        private static int _nextWatchFrame;
        private static int _nextSearchFrame;

        /// <summary>True once the hidden runner exists.</summary>
        public static bool HasRunner => _runner != null;

        /// <summary>How many times <see cref="Pump"/> has run. For the smoke detail.</summary>
        public static long Ticks { get; private set; }

        /// <summary>
        /// Creates the hidden, scene-surviving runner once and returns it; later calls return the
        /// same instance. Returns null only when the GameObject could not be created, which is
        /// logged - the feature then has no automatic injection and depends on an explicit
        /// <see cref="MasterDataFeature.ApplyNow"/>.
        /// </summary>
        public static MasterDataRunner? EnsureRunner()
        {
            lock (Gate)
            {
                // Unity's == also catches a runner whose GameObject was destroyed, so a lost runner
                // is rebuilt rather than handed back dead.
                if (_runner != null)
                {
                    return _runner;
                }

                try
                {
                    var host = new GameObject(RunnerObjectName);
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.hideFlags = HideFlags.HideAndDontSave;

                    _runner = host.AddComponent<MasterDataRunner>();
                    CwyLsModLoader.Log.LogDebug(
                        "MASTERDATA runner armed (" + RunnerObjectName
                        + ", watching MasterDataLoader.IsLoaded)");
                    return _runner;
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogError(
                        "MASTERDATA runner could not be created; pack rows will only be injected "
                        + "when something calls ApplyNow: " + ex.GetType().Name + ": " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// One frame of the watch. Called from <see cref="MasterDataRunner"/>'s <c>Update</c>, on
        /// the main thread, and nowhere else.
        /// </summary>
        internal static void Pump()
        {
            Ticks++;

            try
            {
                if (!MasterDataFeature.Enabled)
                {
                    return;
                }

                if (MasterDataFeature.IsInjected)
                {
                    WatchForReload();
                    return;
                }

                if (Time.frameCount < _nextSearchFrame)
                {
                    return;
                }

                var loader = MasterDataFeature.ResolveLoader();
                if (loader is null)
                {
                    _nextSearchFrame = Time.frameCount + SearchIntervalFrames;
                    return;
                }

                // From here the loader is a cached reference, so this check is three null reads and
                // a bool: cheap enough to run every frame and land on the exact frame it flips.
                if (!IsReady(loader))
                {
                    return;
                }

                if (!MasterDataFeature.ApplyNow("loaded"))
                {
                    // Do not retry a failed injection every frame; the reason is already logged.
                    _nextSearchFrame = Time.frameCount + WatchIntervalFrames;
                }
            }
            catch (Exception ex)
            {
                // Update runs every frame; a repeating stack trace would drown the log, so the
                // watch is disarmed after reporting once and ApplyNow stays available by hand.
                CwyLsModLoader.Log.LogError(
                    "MASTERDATA watch failed and was disarmed: " + ex.GetType().Name + ": " + ex.Message);
                Disarm();
            }
        }

        /// <summary>
        /// True when the loader has finished: <c>IsLoaded</c> is set and all three collections this
        /// feature replaces are present.
        /// </summary>
        /// <param name="loader">The game's master-data loader.</param>
        internal static bool IsReady(Game.MasterDataLoader? loader) =>
            loader != null
            && loader!.IsLoaded
            && loader.LocalizationList != null
            && loader.NovelMasterList != null
            && loader.ScenarioGroupMasterList != null;

        /// <summary>Destroys the runner. Used when the watch has failed and must not repeat.</summary>
        private static void Disarm()
        {
            lock (Gate)
            {
                if (_runner == null)
                {
                    return;
                }

                try
                {
                    UnityEngine.Object.Destroy(_runner.gameObject);
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogWarning(
                        "MASTERDATA runner could not be destroyed: " + ex.GetType().Name + ": " + ex.Message);
                }

                _runner = null;
            }
        }

        /// <summary>
        /// After a successful injection, looks every <see cref="WatchIntervalFrames"/> frames for
        /// the loader having been disposed and re-loaded, and re-arms the feature when it has.
        /// </summary>
        private static void WatchForReload()
        {
            if (Time.frameCount < _nextWatchFrame)
            {
                return;
            }

            _nextWatchFrame = Time.frameCount + WatchIntervalFrames;

            var loader = MasterDataFeature.ResolveLoader();
            if (loader is null)
            {
                return;
            }

            // Dispose() nulls the collections but leaves IsLoaded true,
            // so a null collection is the only honest signal that our rows are gone.
            if (loader!.LocalizationList is null
                || loader.NovelMasterList is null
                || loader.ScenarioGroupMasterList is null)
            {
                CwyLsModLoader.Log.LogDebug(
                    "MASTERDATA the game released its master data; waiting for the next load to re-inject");
                _nextSearchFrame = 0;
                MasterDataFeature.Rearm("loader disposed");
            }
        }
    }
}
