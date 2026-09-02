using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// In-memory ring buffer of Unity logs (Debug.Log / warnings / errors /
    /// uncaught exceptions with stack traces), captured from the moment the
    /// link starts. Unity offers no API to read PAST logs, so subscription
    /// begins in OmniDebugLink.Start().
    /// </summary>
    internal static class DeviceLogBuffer
    {
        private const int Capacity = 1000;
        private const int MaxMessageChars = 4096;
        private const int MaxStackChars = 8192;

        private static readonly ConcurrentQueue<JObject> Queue = new();
        private static int _attached;

        internal static void Attach()
        {
            if (Interlocked.Exchange(ref _attached, 1) == 1) return;
            Application.logMessageReceived += OnLog;
            // logMessageReceivedThreadsafe (background-thread logs) is bound by
            // reflection: some compile environments don't expose the event even
            // though the runtime raises it. Missing → main-thread logs only.
            try
            {
                _threadsafeEvent = typeof(Application).GetEvent("logMessageReceivedThreadsafe",
                    BindingFlags.Static | BindingFlags.Public);
                if (_threadsafeEvent != null)
                {
                    var method = typeof(DeviceLogBuffer).GetMethod(nameof(OnLog),
                        BindingFlags.Static | BindingFlags.NonPublic);
                    _threadsafeDelegate = Delegate.CreateDelegate(_threadsafeEvent.EventHandlerType, null, method);
                    _threadsafeEvent.AddEventHandler(null, _threadsafeDelegate);
                }
            }
            catch { /* degrade to main-thread capture */ }
        }

        private static EventInfo _threadsafeEvent;
        private static Delegate _threadsafeDelegate;

        internal static void Detach()
        {
            if (Interlocked.Exchange(ref _attached, 0) == 0) return;
            Application.logMessageReceived -= OnLog;
            try
            {
                if (_threadsafeEvent != null && _threadsafeDelegate != null)
                    _threadsafeEvent.RemoveEventHandler(null, _threadsafeDelegate);
            }
            catch { }
            _threadsafeEvent = null;
            _threadsafeDelegate = null;
        }

        // Handler must never log and never touch Unity APIs other than the
        // capture (threadsafe variant may fire on any thread).
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            var entry = new JObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["level"] = type.ToString(),
                ["message"] = Truncate(condition, MaxMessageChars),
            };
            if (!string.IsNullOrEmpty(stackTrace) && type != LogType.Log)
                entry["stack"] = Truncate(stackTrace, MaxStackChars);
            Queue.Enqueue(entry);
            while (Queue.Count > Capacity) Queue.TryDequeue(out _);
        }

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

        internal static JArray Read(string level, string contains, int limit, long sinceMs)
        {
            var result = new JArray();
            var wantSeverity = SeverityOf(level);
            foreach (var e in Queue) // enqueue order = chronological
            {
                if (result.Count >= limit) break;
                if (sinceMs > 0 && (long)e["ts"] < sinceMs) continue;
                if (contains != null && !((string)e["message"]).Contains(contains, StringComparison.OrdinalIgnoreCase)) continue;
                var sev = SeverityOf((string)e["level"]);
                if (wantSeverity >= 0 && sev < wantSeverity) continue;
                result.AddFirst(e); // newest first in the output
            }
            return result;
        }

        // -1 = no level filter. Otherwise: 0 Log, 1 Warning, 2+ everything worse.
        private static int SeverityOf(string level)
        {
            if (string.IsNullOrEmpty(level)) return -1;
            switch (level.ToLowerInvariant())
            {
                case "log": return 0;
                case "warning": return 1;
                case "error": return 2; // matches Error / Exception / Assert
                default: return -1;     // unknown filter → no level filtering
            }
        }
    }

    /// <summary>read_logs task — game-side log tail (see DeviceLogBuffer).</summary>
    internal static class DeviceLogTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "read_logs",
                Handle,
                description:
                    "Read game-side Unity logs (Debug.Log, warnings, errors, uncaught exceptions with stack " +
                    "traces) captured since the debug link started, newest first. level filters by minimum " +
                    "severity (log|warning|error), contains matches message text. " +
                    "Use this after a failed step to see what the game logged.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"level\":{\"type\":\"string\",\"enum\":[\"log\",\"warning\",\"error\"],\"description\":\"minimum severity filter; omit for everything\"}," +
                    "\"contains\":{\"type\":\"string\",\"description\":\"only messages containing this text (case-insensitive)\"}," +
                    "\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"default\":50}," +
                    "\"since_ms\":{\"type\":\"integer\",\"description\":\"only entries with ts >= this epoch ms\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            var limit = Clamp(p["limit"]?.Value<int>() ?? 50, 1, 500);
            var since = p["since_ms"]?.Value<long>() ?? 0;
            var logs = DeviceLogBuffer.Read((string)p["level"], (string)p["contains"], limit, since);
            return Task.FromResult<object>(new JObject { ["count"] = logs.Count, ["logs"] = logs });
        }

        private static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));
    }
}
