using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace OmniDebugLink
{
    /// <summary>
    /// get_perf — real-device performance snapshot: Unity memory (mono heap,
    /// engine total), GC counters, N-frame sampling with frame-time
    /// percentiles (p50/p95/p99 — "why does it occasionally stutter"), CPU/GPU
    /// frame ms via FrameTimingManager (when the platform reports it), battery,
    /// optional device snapshot, and Android system-memory extras via JNI.
    ///
    /// Draw calls / batching stats are NOT available in a release player —
    /// those need the editor or a profiler-connected development build.
    /// </summary>
    internal static class PerfTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "get_perf",
                Handle,
                description:
                    "Measure device performance: memory (mono heap used/total, engine allocated/reserved), " +
                    "GC counters, and frame times sampled over N frames (fps avg/min/max, frame ms p50/p95/p99, " +
                    "cpu/gpu frame ms when the platform reports them), plus battery level and optional device " +
                    "snapshot; on Android also system available memory and native heap. " +
                    "Use for lag spikes, memory leaks and device-thermal investigations.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"samples\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":300,\"default\":30,\"description\":\"frames to sample\"}," +
                    "\"include_device\":{\"type\":\"boolean\",\"default\":false,\"description\":\"add a one-time hardware snapshot (SoC, RAM, VRAM, dpi)\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            var samples = Math.Max(1, Math.Min(300, p["samples"]?.Value<int>() ?? 30));
            var includeDevice = p["include_device"]?.Value<bool>() ?? false;

            var runner = OmniDebugLinkBehaviour.Current;
            if (runner == null || samples <= 1)
                return Task.FromResult<object>(BuildResult(null, includeDevice));

            var tcs = new TaskCompletionSource<object>();
            runner.StartCoroutine(SampleRoutine(samples, includeDevice, tcs));
            return tcs.Task;
        }

        private static IEnumerator SampleRoutine(int samples, bool includeDevice, TaskCompletionSource<object> tcs)
        {
            var frameMs = new List<float>(samples);
            var cpuMs = new List<double>();
            var gpuMs = new List<double>();
            for (var i = 0; i < samples; i++)
            {
                yield return null;
                frameMs.Add(Time.unscaledDeltaTime * 1000f);
                // CaptureFrameTimings is void; GetLatestTimings is the widely
                // available reader (GetTimings only exists on Unity 6+). When
                // the platform reports nothing the lists stay empty and cpu/gpu
                // come back null in the result.
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    var timings = new FrameTiming[1];
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                    {
                        cpuMs.Add(timings[0].cpuFrameTime * 1000.0);
                        gpuMs.Add(timings[0].gpuFrameTime * 1000.0);
                    }
                }
                catch { /* frame timing unsupported here */ }
            }

            try
            {
                tcs.SetResult(BuildResult(FrameStats(frameMs, cpuMs, gpuMs), includeDevice));
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        }

        // ---- result assembly ---------------------------------------------------

        private static JObject BuildResult(JObject frame, bool includeDevice)
        {
            var result = new JObject
            {
                ["memory"] = MemorySnapshot(),
                ["gc"] = new JObject
                {
                    ["gen0"] = GC.CollectionCount(0),
                    ["gen1"] = GC.CollectionCount(1),
                    ["managedAllocatedBytes"] = GC.GetTotalMemory(false),
                },
                ["battery"] = new JObject
                {
                    ["level"] = SystemInfo.batteryLevel < 0 ? null : (JToken)Math.Round(SystemInfo.batteryLevel * 100f),
                    ["status"] = SystemInfo.batteryStatus.ToString(),
                },
                ["frame"] = frame,
                ["unityVersion"] = Application.unityVersion,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

#if UNITY_ANDROID && !UNITY_EDITOR
            var android = AndroidMemory();
            if (android != null) result["android"] = android;
#endif

            if (includeDevice)
            {
                result["device"] = new JObject
                {
                    ["model"] = SystemInfo.deviceModel,
                    ["os"] = SystemInfo.operatingSystem,
                    ["processorCount"] = SystemInfo.processorCount,
                    ["systemMemoryMb"] = SystemInfo.systemMemorySize,
                    ["graphicsMemoryMb"] = SystemInfo.graphicsMemorySize,
                    ["gpu"] = SystemInfo.graphicsDeviceName,
                    ["screen"] = new JObject
                    {
                        ["width"] = Screen.width,
                        ["height"] = Screen.height,
                        ["dpi"] = Math.Round(Screen.dpi),
                        ["refreshRate"] = Screen.currentResolution.refreshRate,
                    },
                };
            }
            return result;
        }

        private static JObject MemorySnapshot() => new()
        {
            ["mono"] = new JObject
            {
                ["heapBytes"] = Profiler.GetMonoHeapSizeLong(),
                ["usedBytes"] = Profiler.GetMonoUsedSizeLong(),
            },
            ["total"] = new JObject
            {
                ["allocatedBytes"] = Profiler.GetTotalAllocatedMemoryLong(),
                ["reservedBytes"] = Profiler.GetTotalReservedMemoryLong(),
                ["unusedReservedBytes"] = Profiler.GetTotalUnusedReservedMemoryLong(),
            },
        };

        private static JObject FrameStats(List<float> frameMs, List<double> cpuMs, List<double> gpuMs)
        {
            if (frameMs.Count == 0) return null;
            var sorted = new List<float>(frameMs);
            sorted.Sort();
            float Percentile(float pct) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(pct * (sorted.Count - 1)))];
            var avgMs = 0f;
            var maxMs = 0f;
            var minMs = float.MaxValue;
            foreach (var v in frameMs)
            {
                avgMs += v;
                maxMs = Math.Max(maxMs, v);
                minMs = Math.Min(minMs, v);
            }
            avgMs /= frameMs.Count;

            var stats = new JObject
            {
                ["sampledFrames"] = frameMs.Count,
                ["fps"] = new JObject
                {
                    ["avg"] = Math.Round(1000f / avgMs, 1),
                    ["min"] = Math.Round(1000f / maxMs, 1),
                    ["max"] = Math.Round(1000f / minMs, 1),
                },
                ["frameMs"] = new JObject
                {
                    ["avg"] = Math.Round(avgMs, 2),
                    ["min"] = Math.Round(minMs, 2),
                    ["max"] = Math.Round(maxMs, 2),
                    ["p50"] = Math.Round(Percentile(0.50f), 2),
                    ["p95"] = Math.Round(Percentile(0.95f), 2),
                    ["p99"] = Math.Round(Percentile(0.99f), 2),
                },
            };
            if (cpuMs.Count > 0) stats["cpuFrameMs"] = new JObject { ["avg"] = Math.Round(Avg(cpuMs), 2), ["max"] = Math.Round(MaxOf(cpuMs), 2) };
            if (gpuMs.Count > 0) stats["gpuFrameMs"] = new JObject { ["avg"] = Math.Round(Avg(gpuMs), 2), ["max"] = Math.Round(MaxOf(gpuMs), 2) };
            return stats;
        }

        private static double Avg(List<double> v) { var s = 0.0; foreach (var x in v) s += x; return s / v.Count; }
        private static double MaxOf(List<double> v) { var m = double.MinValue; foreach (var x in v) m = Math.Max(m, x); return m; }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>System-level memory via JNI (no plugin needed). Null on any failure.</summary>
        private static JObject AndroidMemory()
        {
            try
            {
                var dbg = new AndroidJavaClass("android.os.Debug");
                var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                    .GetStatic<AndroidJavaObject>("currentActivity");
                var am = activity.Call<AndroidJavaObject>("getSystemService", "activity");
                var info = new AndroidJavaObject("android.os.ActivityManager$MemoryInfo");
                am.Call("getMemoryInfo", info);
                return new JObject
                {
                    ["nativeHeapAllocatedBytes"] = dbg.CallStatic<long>("getNativeHeapAllocatedSize"),
                    ["nativeHeapSizeBytes"] = dbg.CallStatic<long>("getNativeHeapSize"),
                    ["systemAvailBytes"] = info.Get<long>("availMem"),
                    ["systemTotalBytes"] = info.Get<long>("totalMem"),
                    ["systemLowMemory"] = info.Get<bool>("lowMemory"),
                };
            }
            catch
            {
                return null;
            }
        }
#endif
    }
}
