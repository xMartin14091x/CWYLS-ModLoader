using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CwyLs.ModLoader.Bulbul.Events;
using CwyLs.ModLoader.Bulbul.Language;
using CwyLs.ModLoader.Bulbul.MasterData;
using CwyLs.ModLoader.Core.Api;
using CwyLs.ModLoader.Core.Api.Maker;
using CwyLs.ModLoader.Core.Api.Maker.Catalog;
using CwyLs.ModLoader.Core.Events;
using CwyLs.ModLoader.Core.Logging;
using CwyLs.ModLoader.Core.Packs;
using CwyLs.ModLoader.Core.Threading;
using CwyLs.ModLoader.Core.Voice;
using CwyLs.ModLoader.Packs;
using CwyLs.ModLoader.Threading;
using UnityEngine;

namespace CwyLs.ModLoader.Bulbul.Maker
{
    /// <summary>
    /// The live half of Pack Maker's read side (DS-02): publishes <c>line.spoken</c>,
    /// <c>game.language</c> and <c>room.ready</c> through <see cref="MakerBackends.Publish"/>, keeps
    /// the status the <c>/v1/maker/status</c> route answers with, and tells the catalog when the
    /// game's lines or the packs changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every handler runs on the main thread (the event bus delivers there). The status is read once
    /// a second (<see cref="GameClockTick"/>), at <see cref="RoomReady"/> and when the room scene
    /// loads or unloads; <c>game.language</c> and <c>room.ready</c> are published only when the value
    /// read differs from the last one published. <c>line.spoken</c> carries the fields of the
    /// <c>/v1/events</c> event of the same name (<see cref="ApiEventMapper"/>) plus <c>lineId</c>
    /// (<see cref="MakerPlayedLines.LineId"/>).
    /// </para>
    /// <para>Initialised once from <c>BulbulPlugin.Awake</c>; unwound on <c>Application.quitting</c>.</para>
    /// </remarks>
    internal static class MakerEvents
    {
        /// <summary>Event type: the game's language changed. Data: <c>{code, isPack}</c>.</summary>
        internal const string GameLanguage = "game.language";

        /// <summary>Event type: the room became ready or stopped being ready. Data: <c>{ready}</c>.</summary>
        internal const string RoomReadyType = "room.ready";

        /// <summary>A status older than this is read again on the main thread before it is served.</summary>
        private const double StatusMaxAgeSeconds = 3d;

        private static readonly object Gate = new object();
        private static readonly List<IDisposable> Subscriptions = new List<IDisposable>();

        private static bool _initialized;
        private static string _language = string.Empty;
        private static bool _languageIsPack;
        private static bool _roomReady;
        private static string? _gameVersion;
        private static MakerStatus? _status;
        private static double _statusAt;
        private static int _failures;

        /// <summary>Subscribes to the game events and the master-data and pack notifications. Idempotent.</summary>
        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                Subscriptions.Add(CWYEvents.Subscribe<LineSpoken>(OnLineSpoken));
                Subscriptions.Add(CWYEvents.Subscribe<RoomReady>(_ => OnRoomReady()));
                Subscriptions.Add(CWYEvents.Subscribe<SceneChanged>(OnSceneChanged));
                Subscriptions.Add(CWYEvents.Subscribe<GameClockTick>(_ => Refresh()));
                MasterDataFeature.Injected += OnInjected;
                PackManager.PacksResolved += OnPacksResolved;
                Application.quitting += Shutdown;

                ConsoleEvents.Setup(
                    "maker.catalog",
                    "Pack Maker catalog: lines, packs, status; events line.spoken, game.language, room.ready",
                    ("snapshot", "main thread, " + MakerCatalog.MainThreadTimeout.TotalSeconds + " s"),
                    ("invalidate", "MakerCatalog.Invalidate"));
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(MakerCatalog.LogPrefix + " catalog events could not be wired: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// The status for <c>/v1/maker/status</c>: the one read on the last tick, or (when that is
        /// missing or older than 3 s) a fresh read on the main thread with the 2 s timeout.
        /// </summary>
        /// <param name="cancel">Stops the wait.</param>
        internal static async Task<MakerResult<MakerStatus>> StatusAsync(CancellationToken cancel)
        {
            MakerStatus? cached;
            double at;
            lock (Gate)
            {
                cached = _status;
                at = _statusAt;
            }

            if (cached != null && MainThread.Now - at <= StatusMaxAgeSeconds)
            {
                return MakerResult<MakerStatus>.Success(Copy(cached));
            }

            string failure;
            try
            {
                var fresh = await MainThread.InvokeAsync(
                    "maker.catalog.status", ReadAndPublish, MakerCatalog.MainThreadTimeout, cancel, MakerCatalog.Owner).ConfigureAwait(false);
                return MakerResult<MakerStatus>.Success(Copy(fresh));
            }
            catch (TimeoutException)
            {
                failure = "The game did not answer within " + MakerCatalog.MainThreadTimeout.TotalSeconds + " s; try again in a moment.";
            }
            catch (MainThreadRejectedException ex)
            {
                failure = "The game could not take the request now (" + ex.Message + ").";
            }
            catch (OperationCanceledException)
            {
                return MakerResult<MakerStatus>.Fail(MakerErrorCodes.Unavailable, "The request was cancelled.");
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(MakerCatalog.LogPrefix + " status failed: " + ex.GetType().Name + ": " + ex.Message);
                return MakerResult<MakerStatus>.Fail(MakerErrorCodes.Internal, "The status could not be read (" + ex.GetType().Name + "); see the log.");
            }

            return cached != null
                ? MakerResult<MakerStatus>.Success(Copy(cached))
                : MakerResult<MakerStatus>.Fail(MakerErrorCodes.Unavailable, failure);
        }

        /// <summary>
        /// Last main-thread status without a refresh or wait. Studio v2 combines it with the independent activity
        /// snapshot so an unfocused/paused game can still answer HTTP from worker threads.
        /// </summary>
        internal static MakerStatus? CachedStatus()
        {
            lock (Gate)
            {
                return _status is null ? null : Copy(_status);
            }
        }

        private static void OnLineSpoken(LineSpoken line)
        {
            try
            {
                var voice = VoiceNameRules.KeyFromAudioPath(line.VoiceName) ?? line.VoiceName;

                // Same frame as the play, so the same answer the lookup gave: no override means the
                // game's own clip played, and its length is the original's.
                var gameClip = !VoiceFeature.TryGetEffective(voice, out _);
                var group = !string.IsNullOrEmpty(line.ScenarioGroupId)
                    ? line.ScenarioGroupId
                    : line.Source == LineSource.Other ? null : ApiJson.Camel(line.Source.ToString());
                MakerCatalog.Instance.Spoken.Note(voice, line.LocalizationId, group, line.DurationSec, gameClip);

                if (ApiEventMapper.TryMap(nameof(LineSpoken), line, out var type, out var data))
                {
                    // lineId: the catalog line, also for a Pack Maker Play (spoken as a runtime cwy_say_* row).
                    MakerBackends.Publish(type, new Dictionary<string, object?>(data, StringComparer.Ordinal)
                    {
                        ["lineId"] = MakerCatalog.Instance.Played.LineId(line.LocalizationId),
                    });
                }
            }
            catch (Exception ex)
            {
                Failed("line.spoken", ex);
            }
        }

        private static void OnRoomReady()
        {
            // The voice cache and the mods' runtime clips are certainly in place now.
            MakerCatalog.InvalidateGame("room ready");
            Refresh();
        }

        private static void OnSceneChanged(SceneChanged change)
        {
            if (string.Equals(change.Scene, EventHost.SceneName, StringComparison.Ordinal))
            {
                Refresh();
            }
        }

        private static void OnInjected(string trigger) => MakerCatalog.InvalidateGame("masterdata " + trigger);

        private static void OnPacksResolved(PackResolution resolution)
        {
            MakerCatalog.InvalidateGame("packs resolved");
            MakerCatalog.Invalidate("packs resolved");
        }

        private static void Refresh()
        {
            try
            {
                ReadAndPublish();
            }
            catch (Exception ex)
            {
                Failed("status", ex);
            }
        }

        /// <summary>Reads the status, stores it, and publishes what changed. Main thread only.</summary>
        private static MakerStatus ReadAndPublish()
        {
            _gameVersion ??= Application.version ?? string.Empty;
            var code = LanguageFeature.CurrentCode;
            var isPack = LanguageFeature.TryCurrentValue(out var value) && LanguageFeature.Registry.IsPackLanguage(value);
            var status = new MakerStatus
            {
                GameVersion = _gameVersion,
                LoaderVersion = CwyLsModLoader.PluginVersion,
                GameLanguage = code,
                GameLanguageIsPack = isPack,
                RoomReady = EventHost.RoomReadyRaised,
            };

            lock (Gate)
            {
                _status = status;
                _statusAt = MainThread.Now;
            }

            // Before the save has loaded the language reads as empty: not a change worth an event.
            if (code.Length > 0 && (!string.Equals(code, _language, StringComparison.Ordinal) || isPack != _languageIsPack))
            {
                _language = code;
                _languageIsPack = isPack;
                MakerBackends.Publish(GameLanguage, new Dictionary<string, object?> { ["code"] = code, ["isPack"] = isPack });
            }

            if (status.RoomReady != _roomReady)
            {
                _roomReady = status.RoomReady;
                MakerBackends.Publish(RoomReadyType, new Dictionary<string, object?> { ["ready"] = status.RoomReady });
            }

            return status;
        }

        private static MakerStatus Copy(MakerStatus status) => new MakerStatus
        {
            GameVersion = status.GameVersion,
            LoaderVersion = status.LoaderVersion,
            GameLanguage = status.GameLanguage,
            GameLanguageIsPack = status.GameLanguageIsPack,
            RoomReady = status.RoomReady,
        };

        /// <summary>One warning for the first few failures of a handler, then debug lines only: a handler must never spam the console.</summary>
        private static void Failed(string what, Exception ex)
        {
            var line = MakerCatalog.LogPrefix + " " + what + " handler failed: " + ex.GetType().Name + ": " + ex.Message;
            if (Interlocked.Increment(ref _failures) <= 5)
            {
                CwyLsModLoader.Log.LogWarning(line);
            }
            else
            {
                CwyLsModLoader.Log.LogDebug(line);
            }
        }

        private static void Shutdown()
        {
            MasterDataFeature.Injected -= OnInjected;
            PackManager.PacksResolved -= OnPacksResolved;
            foreach (var subscription in Subscriptions)
            {
                try
                {
                    subscription.Dispose();
                }
                catch (Exception ex)
                {
                    CwyLsModLoader.Log.LogDebug(MakerCatalog.LogPrefix + " a catalog subscription could not be disposed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            Subscriptions.Clear();
        }
    }
}
