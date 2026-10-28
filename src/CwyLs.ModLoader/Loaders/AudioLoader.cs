using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CwyLs.ModLoader.Core.Loaders;
using UnityEngine;
using UnityEngine.Networking;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// Turns a pack sound into an <c>AudioClip</c>. Two paths, because the two formats are not
    /// alike:
    /// <list type="bullet">
    /// <item><see cref="LoadWav"/> decodes 16-bit PCM WAV in managed code and is synchronous, so a
    /// caller that needs a clip <em>now</em> (a smoke check, a UI beep) gets one.</item>
    /// <item><see cref="LoadAsync"/> hands the file to the engine's own decoder over
    /// <c>UnityWebRequestMultimedia</c>. This is the only path for Ogg Vorbis - which is what the
    /// game's own 1381 voice clips are, and therefore what the voice feature loads - because decoding
    /// Vorbis ourselves would mean shipping a decoder.</item>
    /// </list>
    /// <para>
    /// A clip's <c>name</c> is the file name without its extension, and that is load-bearing:
    /// KanKikuchi's <c>VoiceManager</c> resolves clips by name, so
    /// <c>audio/voice/Voice_Tutorial_1_001.ogg</c> must arrive as <c>Voice_Tutorial_1_001</c>.
    /// </para>
    /// </summary>
    public static class AudioLoader
    {
        private static readonly object InFlightGate = new object();

        /// <summary>
        /// Callbacks waiting on an async load, keyed by path. A pack with a hundred clips will ask
        /// for the same file twice sooner or later; without this, the second request would build a
        /// second clip and the cache entry the first one is already registered under would be
        /// replaced by a different object.
        /// </summary>
        private static readonly Dictionary<string, List<Action<AudioClip?>>> InFlight =
            new Dictionary<string, List<Action<AudioClip?>>>(StringComparer.Ordinal);

        /// <summary>How many async loads have been started and not yet finished.</summary>
        public static int PendingCount
        {
            get
            {
                lock (InFlightGate)
                {
                    return InFlight.Count;
                }
            }
        }

        /// <summary>
        /// Loads a 16-bit PCM WAV synchronously.
        /// </summary>
        /// <param name="absolutePath">Full path to the file.</param>
        /// <param name="report">
        /// Report to record the attempt in; null means <see cref="LoaderDiagnostics.Session"/>.
        /// </param>
        /// <returns>
        /// The clip, or null when the file is missing, is not a WAV we decode, or would not build.
        /// Every failure is a recorded <see cref="LoadEntry"/> and a warning, never an exception.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="absolutePath"/> is blank.</exception>
        public static AudioClip? LoadWav(string absolutePath, LoadReport? report = null)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                throw new ArgumentException("a path is required", nameof(absolutePath));
            }

            var watch = Stopwatch.StartNew();

            try
            {
                if (!File.Exists(absolutePath))
                {
                    return Fail(report, absolutePath, "file not found", watch);
                }

                var stamp = AssetCache.StampOf(absolutePath);
                if (AssetCache.TryGet<AudioClip>(absolutePath, stamp, out var cached) && cached is not null)
                {
                    LoaderDiagnostics.Record(
                        report,
                        LoadEntry.Success(
                            absolutePath, AssetKind.Audio, $"cached '{cached.name}'", watch.ElapsedMilliseconds));
                    return cached;
                }

                var bytes = File.ReadAllBytes(absolutePath);

                var detection = FileKindDetector.Detect(absolutePath, bytes);
                if (detection.Format != AssetFormat.Wav)
                {
                    return Fail(
                        report,
                        absolutePath,
                        detection.Format == AssetFormat.OggVorbis
                            ? detection.Reason + "; Ogg has to go through LoadAsync"
                            : detection.Reason,
                        watch,
                        bytes.Length);
                }

                if (!WavParser.TryParse(bytes, out var wav, out var error) || wav is null)
                {
                    return Fail(report, absolutePath, error ?? "the WAV could not be decoded", watch, bytes.Length);
                }

                var name = AssetKey.FromPath(absolutePath).Name;

                // lengthSamples is per channel - WavData.Frames, not Samples.Length.
                var clip = AudioClip.Create(name, wav.Frames, wav.Channels, wav.SampleRate, stream: false);
                if (clip == null)
                {
                    return Fail(report, absolutePath, "AudioClip.Create returned nothing", watch, bytes.Length);
                }

                if (!clip.SetData(wav.Samples, 0))
                {
                    UnityEngine.Object.Destroy(clip);
                    return Fail(report, absolutePath, "AudioClip.SetData refused the samples", watch, bytes.Length);
                }

                AssetCache.Store(absolutePath, stamp, clip);

                LoaderDiagnostics.Record(
                    report,
                    LoadEntry.Success(
                        absolutePath,
                        AssetKind.Audio,
                        $"{wav} as '{name}'",
                        watch.ElapsedMilliseconds,
                        bytes.Length));

                return clip;
            }
            catch (Exception ex)
            {
                return Fail(report, absolutePath, $"{ex.GetType().Name}: {ex.Message}", watch);
            }
        }

        /// <summary>
        /// Loads an Ogg Vorbis or WAV file through the engine's decoder, over several frames.
        /// </summary>
        /// <param name="absolutePath">Full path to the file.</param>
        /// <param name="onDone">
        /// Called exactly once, on the main thread, with the clip or with null on failure. It is
        /// called before this method returns whenever the answer needs no frames - a cached clip, a
        /// missing or unloadable file, a request that could not be built - and a few frames later
        /// otherwise. An exception thrown by this callback is logged and swallowed, so one bad
        /// consumer cannot stop the rest of a pack from loading.
        /// </param>
        /// <param name="report">
        /// Report to record the attempt in; null means <see cref="LoaderDiagnostics.Session"/>.
        /// </param>
        /// <exception cref="ArgumentException"><paramref name="absolutePath"/> is blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="onDone"/> is null.</exception>
        public static void LoadAsync(string absolutePath, Action<AudioClip?> onDone, LoadReport? report = null)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                throw new ArgumentException("a path is required", nameof(absolutePath));
            }

            if (onDone is null)
            {
                throw new ArgumentNullException(nameof(onDone));
            }

            var watch = Stopwatch.StartNew();

            // The three cheap, non-throwing answers come first, so that everything after the
            // in-flight registration below has exactly one owner and one way to finish.
            if (!File.Exists(absolutePath))
            {
                Fail(report, absolutePath, "file not found", watch);
                Invoke(onDone, null);
                return;
            }

            var stamp = AssetCache.StampOf(absolutePath);
            if (AssetCache.TryGet<AudioClip>(absolutePath, stamp, out var cached) && cached is not null)
            {
                LoaderDiagnostics.Record(
                    report,
                    LoadEntry.Success(
                        absolutePath, AssetKind.Audio, $"cached '{cached.name}'", watch.ElapsedMilliseconds));
                Invoke(onDone, cached);
                return;
            }

            if (!TryStartOrJoin(absolutePath, onDone))
            {
                // Someone else is already loading this file; their Complete will call us too.
                return;
            }

            // From here on this call owns the load, and every exit must go through Complete.
            try
            {
                var header = ReadHeader(absolutePath, out var length);

                if (length == 0)
                {
                    // An empty file reaches the decoder as a plausible request and comes back as
                    // an opaque transport error; say what is actually wrong instead.
                    Fail(report, absolutePath, "file is empty", watch);
                    Complete(absolutePath, null);
                    return;
                }

                var detection = FileKindDetector.Detect(absolutePath, header);

                if (!detection.Supported || detection.Kind != AssetKind.Audio)
                {
                    Fail(report, absolutePath, detection.Reason, watch, length);
                    Complete(absolutePath, null);
                    return;
                }

                var audioType = detection.Format == AssetFormat.Wav ? AudioType.WAV : AudioType.OGGVORBIS;

                if (LoaderHost.Run(LoadRoutine(absolutePath, audioType, stamp, length, report, watch)) is null)
                {
                    Fail(report, absolutePath, "no coroutine host is available", watch, length);
                    Complete(absolutePath, null);
                }
            }
            catch (Exception ex)
            {
                Fail(report, absolutePath, $"{ex.GetType().Name}: {ex.Message}", watch);
                Complete(absolutePath, null);
            }
        }

        private static IEnumerator LoadRoutine(
            string path, AudioType audioType, long stamp, long bytes, LoadReport? report, Stopwatch watch)
        {
            AudioClip? clip = null;

            // Built outside the try/finally below because a coroutine may not yield inside a
            // try that has a catch clause; CreateRequest does its own error handling instead.
            var request = CreateRequest(path, audioType, report, watch, bytes);

            if (request != null)
            {
                try
                {
                    yield return request.SendWebRequest();
                    clip = Finish(request, path, stamp, bytes, report, watch);
                }
                finally
                {
                    // Safe to dispose with the clip still in hand: streamAudio is false, so the
                    // clip owns fully decoded samples rather than reading from this handler.
                    request.Dispose();
                }
            }

            Complete(path, clip);
        }

        private static UnityWebRequest? CreateRequest(
            string path, AudioType audioType, LoadReport? report, Stopwatch watch, long bytes)
        {
            try
            {
                // Uri.AbsoluteUri, not "file://" + path: the game lives under
                // "Chill with You Lo-Fi Story", and an unescaped space is a request that fails
                // with a useless error.
                var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, audioType);

                if (!(request.downloadHandler is DownloadHandlerAudioClip handler))
                {
                    request.Dispose();
                    Fail(report, path, "the request did not come with an audio download handler", watch, bytes);
                    return null;
                }

                // Decode the whole clip up front and keep it uncompressed in memory. A streamed or
                // still-compressed clip cannot be handed to the game's voice cache and played
                // immediately, which is exactly what the voice feature needs to do.
                handler.streamAudio = false;
                handler.compressed = false;

                return request;
            }
            catch (Exception ex)
            {
                Fail(report, path, $"{ex.GetType().Name}: {ex.Message}", watch, bytes);
                return null;
            }
        }

        private static AudioClip? Finish(
            UnityWebRequest request, string path, long stamp, long bytes, LoadReport? report, Stopwatch watch)
        {
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    return Fail(report, path, $"{request.result}: {request.error}", watch, bytes);
                }

                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    return Fail(report, path, "the decoder produced no clip", watch, bytes);
                }

                clip.name = AssetKey.FromPath(path).Name;
                AssetCache.Store(path, stamp, clip);

                LoaderDiagnostics.Record(
                    report,
                    LoadEntry.Success(
                        path,
                        AssetKind.Audio,
                        $"{clip.samples} frames x{clip.channels} @{clip.frequency}Hz via UnityWebRequest as '{clip.name}'",
                        watch.ElapsedMilliseconds,
                        bytes));

                return clip;
            }
            catch (Exception ex)
            {
                return Fail(report, path, $"{ex.GetType().Name}: {ex.Message}", watch, bytes);
            }
        }

        /// <summary>
        /// Registers this call as the owner of the load, or attaches <paramref name="onDone"/> to
        /// the one already running.
        /// </summary>
        /// <returns>True when the caller owns the load and must finish it through <see cref="Complete"/>.</returns>
        private static bool TryStartOrJoin(string path, Action<AudioClip?> onDone)
        {
            lock (InFlightGate)
            {
                if (InFlight.TryGetValue(path, out var waiting))
                {
                    waiting.Add(onDone);
                    return false;
                }

                InFlight[path] = new List<Action<AudioClip?>> { onDone };
                return true;
            }
        }

        /// <summary>Hands the result to everyone waiting on this path and clears the entry.</summary>
        private static void Complete(string path, AudioClip? clip)
        {
            List<Action<AudioClip?>>? waiting;
            lock (InFlightGate)
            {
                InFlight.TryGetValue(path, out waiting);
                InFlight.Remove(path);
            }

            if (waiting is null)
            {
                return;
            }

            foreach (var callback in waiting)
            {
                Invoke(callback, clip);
            }
        }

        private static void Invoke(Action<AudioClip?> onDone, AudioClip? clip)
        {
            try
            {
                onDone(clip);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogError(
                    $"audio load callback threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Reads just enough of the file for <see cref="FileKindDetector"/>, plus its size.</summary>
        private static byte[] ReadHeader(string path, out long length)
        {
            using (var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: FileKindDetector.HeaderBytes))
            {
                length = stream.Length;

                var header = new byte[(int)Math.Min(FileKindDetector.HeaderBytes, length)];
                var read = 0;
                while (read < header.Length)
                {
                    var got = stream.Read(header, read, header.Length - read);
                    if (got <= 0)
                    {
                        break;
                    }

                    read += got;
                }

                if (read == header.Length)
                {
                    return header;
                }

                var partial = new byte[read];
                Array.Copy(header, partial, read);
                return partial;
            }
        }

        private static AudioClip? Fail(LoadReport? report, string path, string detail, Stopwatch watch, long bytes = 0)
        {
            LoaderDiagnostics.Record(
                report, LoadEntry.Failure(path, AssetKind.Audio, detail, watch.ElapsedMilliseconds, bytes));
            return null;
        }
    }
}
