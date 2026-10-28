using System;
using System.Globalization;
using System.IO;
using CwyLs.ModLoader.Core.Loaders;
using CwyLs.ModLoader.Core.Smoke;
using UnityEngine;

namespace CwyLs.ModLoader.Loaders
{
    /// <summary>
    /// The Phase-1 loader checks. Each one manufactures its own input, loads it back through the
    /// real loader, and deletes it again, so no binary fixture is shipped and no check depends on
    /// a pack being installed.
    /// </summary>
    internal static class LoaderSmokeChecks
    {
        private const string Phase = "P1";
        private const string PngId = "p1.loaders.png";
        private const string WavId = "p1.loaders.wav_sync";
        private const string OggId = "p1.loaders.ogg_async";

        /// <summary>Exactly what this check does and does not prove; the orchestrator reads it.</summary>
        private const string OggCaveat = "ogg path proven via WAV over UnityWebRequest; real OGG verified in P1-4";

        /// <summary>Scratch folder inside <see cref="ModLoaderPaths.DataRoot"/>, emptied as it goes.</summary>
        private const string ScratchFolder = "_smoke";

        private const int ProbeSize = 4;
        private const int ToneHz = 440;
        private const double ToneSeconds = 0.2;
        private const int ToneRate = 44100;
        private const int ToneFrames = 8820;

        /// <summary>Index 6 in a 4x4 texture is (x=2, y=1); Unity's pixel order is bottom-left, row-major.</summary>
        private const int MarkerIndex = 6;

        private static readonly Color32 Marker = new Color32(12, 250, 90, 255);
        private static readonly Color32 Ground = new Color32(255, 0, 128, 255);

        private static readonly object ProbeGate = new object();

        private static bool _probeStarted;
        private static bool _probeFinished;
        private static bool _probeOk;
        private static string _probeDetail = "the async probe has not finished yet";

        /// <summary>
        /// Registers the three checks and starts the async probe. Idempotent - the registry
        /// replaces by id and the probe starts at most once.
        /// </summary>
        internal static void RegisterAll()
        {
            SmokeRegistry.Register(new SmokeCheck(PngId, Phase, Png));
            SmokeRegistry.Register(new SmokeCheck(WavId, Phase, WavSync));
            SmokeRegistry.Register(new SmokeCheck(OggId, Phase, OggAsync));

            // Started here rather than inside the check: a UnityWebRequest needs frames to finish
            // and a smoke check is synchronous, so the probe is armed at Awake and the check, which
            // runs after RoomScene has loaded and settled, reads the outcome.
            StartProbe();
        }

        /// <summary>Encodes a known 4x4 PNG, loads it back, and compares a pixel.</summary>
        private static SmokeCheckResult Png()
        {
            var path = ScratchFile("probe.png");
            Texture2D? loaded = null;

            try
            {
                WriteProbePng(path);

                loaded = TextureLoader.Load(path, new TextureLoadOptions(readableAfterLoad: true));
                if (loaded == null)
                {
                    return SmokeCheckResult.Fail(PngId, "TextureLoader.Load produced nothing; see the warning above");
                }

                // Unity's == above is a user-defined operator, so the compiler's nullable analysis
                // cannot narrow on it; this records that the branch has already proved it is alive.
                var texture = loaded!;

                if (texture.width != ProbeSize || texture.height != ProbeSize)
                {
                    return SmokeCheckResult.Fail(
                        PngId,
                        $"loaded {texture.width}x{texture.height}, expected {ProbeSize}x{ProbeSize}");
                }

                if (!string.Equals(texture.name, "probe", StringComparison.Ordinal))
                {
                    // The texture feature identifies a texture by this name; a wrong one is a silent mis-assignment.
                    return SmokeCheckResult.Fail(PngId, $"texture name is '{texture.name}', expected 'probe'");
                }

                var pixels = texture.GetPixels32();
                if (pixels.Length != ProbeSize * ProbeSize)
                {
                    return SmokeCheckResult.Fail(PngId, $"read back {pixels.Length} pixels, expected {ProbeSize * ProbeSize}");
                }

                if (!Same(pixels[MarkerIndex], Marker))
                {
                    return SmokeCheckResult.Fail(
                        PngId, $"marker pixel is {Describe(pixels[MarkerIndex])}, expected {Describe(Marker)}");
                }

                if (!Same(pixels[0], Ground))
                {
                    return SmokeCheckResult.Fail(
                        PngId, $"background pixel is {Describe(pixels[0])}, expected {Describe(Ground)}");
                }

                return SmokeCheckResult.Pass(
                    PngId,
                    $"{texture.width}x{texture.height} {texture.format} '{texture.name}', marker pixel {Describe(Marker)} round-tripped");
            }
            finally
            {
                Destroy(loaded);
                Discard(path);
            }
        }

        /// <summary>Synthesises a tone, loads it through the managed WAV path, and checks the numbers.</summary>
        private static SmokeCheckResult WavSync()
        {
            var path = ScratchFile("probe.wav");
            AudioClip? loaded = null;

            try
            {
                File.WriteAllBytes(path, WavWriter.Tone(ToneHz, ToneSeconds, ToneRate, channels: 1));

                loaded = AudioLoader.LoadWav(path);
                if (loaded == null)
                {
                    return SmokeCheckResult.Fail(WavId, "AudioLoader.LoadWav produced nothing; see the warning above");
                }

                var clip = loaded!;

                // Exact, because every number here is produced by our own code: WavWriter ->
                // WavParser -> AudioClip.Create. Nothing is left to a decoder's discretion.
                if (clip.samples != ToneFrames || clip.channels != 1 || clip.frequency != ToneRate)
                {
                    return SmokeCheckResult.Fail(
                        WavId,
                        $"loaded {clip.samples} frames x{clip.channels} @{clip.frequency}Hz, " +
                        $"expected {ToneFrames} x1 @{ToneRate}Hz");
                }

                if (!string.Equals(clip.name, "probe", StringComparison.Ordinal))
                {
                    // The voice feature registers clips into the game's voice cache under exactly this name.
                    return SmokeCheckResult.Fail(WavId, $"clip name is '{clip.name}', expected 'probe'");
                }

                var samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0))
                {
                    return SmokeCheckResult.Fail(WavId, "AudioClip.GetData refused to read the clip back");
                }

                var peak = 0f;
                foreach (var sample in samples)
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }

                // WavWriter.Tone defaults to amplitude 0.5; silence here would mean the samples
                // never actually reached the clip.
                if (peak < 0.45f || peak > 0.55f)
                {
                    return SmokeCheckResult.Fail(
                        WavId, $"peak amplitude is {peak.ToString("0.000", CultureInfo.InvariantCulture)}, expected ~0.5");
                }

                return SmokeCheckResult.Pass(
                    WavId,
                    $"'{clip.name}' {clip.samples} frames x{clip.channels} @{clip.frequency}Hz, " +
                    $"peak {peak.ToString("0.000", CultureInfo.InvariantCulture)}");
            }
            finally
            {
                Destroy(loaded);
                Discard(path);
            }
        }

        /// <summary>Reports the outcome of the async <c>UnityWebRequestMultimedia</c> probe.</summary>
        private static SmokeCheckResult OggAsync()
        {
            // No-op unless the probe never got off the ground (no coroutine host at Awake, say).
            StartProbe();

            lock (ProbeGate)
            {
                if (!_probeFinished)
                {
                    return SmokeCheckResult.Fail(OggId, _probeDetail);
                }

                return _probeOk
                    ? SmokeCheckResult.Pass(OggId, _probeDetail)
                    : SmokeCheckResult.Fail(OggId, _probeDetail);
            }
        }

        private static void StartProbe()
        {
            lock (ProbeGate)
            {
                if (_probeStarted)
                {
                    return;
                }

                _probeStarted = true;
            }

            var path = ScratchFile("probe_async.wav");

            try
            {
                File.WriteAllBytes(path, WavWriter.Tone(ToneHz, ToneSeconds, ToneRate, channels: 1));
                AudioLoader.LoadAsync(path, clip => FinishProbe(path, clip));
            }
            catch (Exception ex)
            {
                var detail = $"the probe could not be started: {ex.GetType().Name}: {ex.Message}";
                CwyLsModLoader.Log.LogWarning($"{OggId}: {detail}");
                RecordProbe(false, detail);
                Discard(path);
            }
        }

        private static void FinishProbe(string path, AudioClip? loaded)
        {
            var ok = false;
            string detail;

            try
            {
                if (loaded == null)
                {
                    detail = "the async load produced no clip; see the warning above";
                }
                else
                {
                    var clip = loaded!;
                    var seconds = clip.frequency > 0 ? clip.samples / (double)clip.frequency : 0d;

                    if (clip.samples <= 0 || clip.channels < 1 || clip.frequency <= 0)
                    {
                        detail = $"clip is degenerate: {clip.samples} frames x{clip.channels} @{clip.frequency}Hz";
                    }
                    else if (seconds < ToneSeconds * 0.75 || seconds > ToneSeconds * 1.25)
                    {
                        // Deliberately loose: the engine's decoder, not our code, decides the rate
                        // and may resample. What is being proved here is the coroutine path.
                        detail = string.Format(
                            CultureInfo.InvariantCulture,
                            "clip is {0:0.000}s, expected about {1:0.000}s",
                            seconds,
                            ToneSeconds);
                    }
                    else if (!string.Equals(clip.name, "probe_async", StringComparison.Ordinal))
                    {
                        detail = $"clip name is '{clip.name}', expected 'probe_async'";
                    }
                    else
                    {
                        ok = true;
                        detail = string.Format(
                            CultureInfo.InvariantCulture,
                            "'{0}' {1} frames x{2} @{3}Hz ({4:0.000}s) over UnityWebRequest; {5}",
                            clip.name,
                            clip.samples,
                            clip.channels,
                            clip.frequency,
                            seconds,
                            OggCaveat);
                    }
                }

                Destroy(loaded);
            }
            catch (Exception ex)
            {
                detail = $"{ex.GetType().Name}: {ex.Message}";
                ok = false;
                CwyLsModLoader.Log.LogWarning($"{OggId}: {detail}");
            }

            Discard(path);
            RecordProbe(ok, detail);

            CwyLsModLoader.Log.LogDebug($"{OggId}: {(ok ? "ok" : "FAILED")} - {detail}");
        }

        private static void RecordProbe(bool ok, string detail)
        {
            lock (ProbeGate)
            {
                _probeFinished = true;
                _probeOk = ok;
                _probeDetail = detail;
            }
        }

        private static void WriteProbePng(string path)
        {
            var source = new Texture2D(ProbeSize, ProbeSize, TextureFormat.RGBA32, mipChain: false, linear: false);
            try
            {
                var pixels = new Color32[ProbeSize * ProbeSize];
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Ground;
                }

                pixels[MarkerIndex] = Marker;

                source.SetPixels32(pixels);
                source.Apply();

                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(source));
            }
            finally
            {
                Destroy(source);
            }
        }

        /// <summary>
        /// A file under <see cref="ModLoaderPaths.DataRoot"/>/<see cref="ScratchFolder"/>. The folder
        /// is created on demand and removed again by <see cref="Discard"/> once its last file is
        /// gone.
        /// </summary>
        private static string ScratchFile(string fileName)
        {
            var directory = Path.Combine(ModLoaderPaths.DataRoot, ScratchFolder);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, fileName);
        }

        private static void Discard(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) &&
                    Directory.Exists(directory) &&
                    Directory.GetFileSystemEntries(directory!).Length == 0)
                {
                    Directory.Delete(directory!);
                }
            }
            catch (Exception ex)
            {
                // Leftover scratch files are untidy, not dangerous; never fail a check over one.
                CwyLsModLoader.Log.LogWarning(
                    $"smoke scratch cleanup failed ({path}): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Destroy(UnityEngine.Object? asset)
        {
            if (asset == null)
            {
                return;
            }

            try
            {
                UnityEngine.Object.Destroy(asset);
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"smoke probe asset could not be destroyed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool Same(Color32 left, Color32 right) =>
            left.r == right.r && left.g == right.g && left.b == right.b && left.a == right.a;

        private static string Describe(Color32 colour) =>
            $"rgba({colour.r},{colour.g},{colour.b},{colour.a})";
    }
}
