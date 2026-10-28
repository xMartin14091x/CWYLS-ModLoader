using System;
using System.Diagnostics;
using System.Reflection;
using CwyLs.ModLoader.Core.Trace;
using UnityEngine;
using UnityEngine.Profiling;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// Samples how the process itself is doing, once per snapshot interval: frame rate, memory,
    /// audio load, tweens, window state and whether Unity has been logging errors.
    /// </summary>
    /// <remarks>
    /// Every reading is independently guarded. The expensive ones — counting playing AudioSources
    /// needs a scene-wide find, and reading the process working set is a syscall — are only ever
    /// taken on the interval tick, never per frame.
    /// </remarks>
    internal sealed class HealthProbe
    {
        private const double BytesPerMB = 1024d * 1024d;

        private readonly HealthStats _frames = new HealthStats();
        private readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();

        private Process? _process;
        private MethodInfo? _totalPlayingTweens;
        private bool _tweenLookupDone;
        private bool _warnedProcess;

        /// <summary>Records one frame. Called from the host's Update, so it must stay allocation-free.</summary>
        internal void AddFrame(double milliseconds) => _frames.AddFrame(milliseconds);

        /// <summary>Frames seen so far this interval.</summary>
        internal int FrameCount => _frames.Count;

        /// <summary>
        /// Takes a reading and starts the next interval.
        /// </summary>
        /// <param name="warnings">Unity warnings counted since the last reading.</param>
        /// <param name="errors">Unity errors counted since the last reading.</param>
        /// <param name="steam">Steam initialisation state, resolved by the game-state probe.</param>
        internal TraceHealth Capture(int warnings, int errors, bool? steam)
        {
            var health = new TraceHealth
            {
                Fps = new FpsHealth { Average = _frames.AverageFps, Min = _frames.MinFps, Max = _frames.MaxFps },
                FrameMs = new FrameMsHealth { Average = _frames.AverageMs, P95 = _frames.P95Ms, Max = _frames.MaxMs },
                Spikes = _frames.Spikes,
                UptimeSec = _uptime.Elapsed.TotalSeconds,
                Steam = steam,
                WarningsInInterval = warnings,
                ErrorsInInterval = errors,
            };

            _frames.Reset();

            ReadMemory(health);
            ReadProcess(health);
            health.Audio = ReadAudio();
            health.Tweens = ReadTweens();
            ReadDisplay(health);

            return health;
        }

        private static void ReadMemory(TraceHealth health)
        {
            try
            {
                health.GcMB = GC.GetTotalMemory(forceFullCollection: false) / BytesPerMB;
                health.MonoUsedMB = Profiler.GetMonoUsedSizeLong() / BytesPerMB;
                health.MonoHeapMB = Profiler.GetMonoHeapSizeLong() / BytesPerMB;
                health.AllocatedMB = Profiler.GetTotalAllocatedMemoryLong() / BytesPerMB;
                health.ReservedMB = Profiler.GetTotalReservedMemoryLong() / BytesPerMB;
            }
            catch (Exception ex)
            {
                // The Profiler API is stripped in some player builds; memory then reads as null.
                TraceDiagnostics.Report("TRACE: memory counters are unavailable", ex);
            }
        }

        private void ReadProcess(TraceHealth health)
        {
            try
            {
                _process ??= Process.GetCurrentProcess();
                _process.Refresh();
                health.RssMB = _process.WorkingSet64 / BytesPerMB;
                health.Threads = _process.Threads.Count;
            }
            catch (Exception ex)
            {
                if (!_warnedProcess)
                {
                    _warnedProcess = true;
                    TraceDiagnostics.Report("TRACE: process counters are unavailable", ex);
                }
            }
        }

        private static AudioHealth? ReadAudio()
        {
            try
            {
                var configuration = AudioSettings.GetConfiguration();
                var audio = new AudioHealth
                {
                    SampleRate = configuration.sampleRate,
                    DspBufferSize = configuration.dspBufferSize,
                    SpeakerMode = configuration.speakerMode.ToString(),
                    ListenerVolume = AudioListener.volume,
                };

                // Scene-wide find: acceptable once every few seconds, never per frame.
                var playing = 0;
                foreach (var source in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                {
                    if (source != null && source.isPlaying)
                    {
                        playing++;
                    }
                }

                audio.PlayingSources = playing;
                return audio;
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: audio counters are unavailable", ex);
                return null;
            }
        }

        /// <summary>DOTween by reflection: the game ships it, but the trace must not depend on it.</summary>
        private int? ReadTweens()
        {
            try
            {
                if (!_tweenLookupDone)
                {
                    _tweenLookupDone = true;
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = assembly.GetType("DG.Tweening.DOTween", throwOnError: false);
                        if (type is null)
                        {
                            continue;
                        }

                        _totalPlayingTweens = type.GetMethod(
                            "TotalPlayingTweens", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                        break;
                    }
                }

                return _totalPlayingTweens?.Invoke(null, null) as int?;
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: DOTween tween count is unavailable", ex);
                return null;
            }
        }

        private static void ReadDisplay(TraceHealth health)
        {
            try
            {
                health.TargetFps = Application.targetFrameRate;
                health.Vsync = QualitySettings.vSyncCount;
                health.Focused = Application.isFocused;

                // The game lowers its own frame rate in the background (observed), so
                // a snapshot taken there says why its numbers look bad instead of implying a bug.
                health.FpsCapReason = Application.isFocused ? null : TraceHealth.UnfocusedCapReason;
                health.TimeScale = Time.timeScale;
                health.Screen = new ScreenHealth
                {
                    Width = UnityEngine.Screen.width,
                    Height = UnityEngine.Screen.height,
                    FullScreen = UnityEngine.Screen.fullScreen,
                    Dpi = UnityEngine.Screen.dpi,
                };
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: display counters are unavailable", ex);
            }
        }
    }
}
