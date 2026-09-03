using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using OmniDebugLink.UnityWebSocket;

namespace OmniDebugLink
{
    /// <summary>Connection state of the debug link.</summary>
    public enum LinkState
    {
        Stopped,
        Connecting,
        Connected,
    }

    /// <summary>A task dispatched from the MCP relay. Must be answered via the handler's return value.</summary>
    public sealed class OmniDebugLinkTask
    {
        /// <summary>Unique id linking this task to its result.</summary>
        public string RequestId { get; }

        /// <summary>Task type, as registered on the tool side (e.g. "echo").</summary>
        public string Type { get; }

        /// <summary>Task payload as a JSON object (empty object when omitted).</summary>
        public JObject Payload { get; }

        public OmniDebugLinkTask(string requestId, string type, JObject payload)
        {
            RequestId = requestId;
            Type = type;
            Payload = payload ?? new JObject();
        }
    }

    /// <summary>
    /// Handles one task type. Runs on the Unity main thread inside the player loop
    /// (safe to touch Unity APIs). Return value is JSON-serialized back to the caller;
    /// return a <see cref="JToken"/> for exact control of the JSON shape.
    /// Throw to report failure.
    /// </summary>
    /// <returns>Result object, or null for an empty result.</returns>
    public delegate Task<object> TaskHandler(OmniDebugLinkTask task);

    /// <summary>Self-describing capability declaration for one task type.</summary>
    public sealed class TaskSpec
    {
        public string Type;
        /// <summary>One or two sentences for the AI consuming list_tasks.</summary>
        public string Description;
        /// <summary>JSON Schema (draft-07) string describing the payload. Optional.</summary>
        public string PayloadSchema;
    }

    /// <summary>Registry mapping task types to handlers.</summary>
    public sealed class TaskRegistry
    {
        private readonly ConcurrentDictionary<string, TaskSpec> _specs =
            new ConcurrentDictionary<string, TaskSpec>();

        /// <summary>Raised (possibly from any thread) whenever the registry changes, so the client can re-announce capabilities.</summary>
        public event Action Changed;

        /// <summary>Register a handler with an optional self-description used for capability discovery.</summary>
        public void Register(string type, TaskHandler handler, string description = null, string payloadSchema = null)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentNullException(nameof(type));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _specs[type] = new TaskSpec { Type = type, Description = description, PayloadSchema = payloadSchema };
            HandlerTable[type] = handler;
            Changed?.Invoke();
        }

        public bool Unregister(string type)
        {
            var removed = _specs.TryRemove(type, out _) | HandlerTable.TryRemove(type, out _);
            if (removed) Changed?.Invoke();
            return removed;
        }

        // Handlers live in a separate map: Register/Unregister keep both in sync.
        private ConcurrentDictionary<string, TaskHandler> HandlerTable { get; } =
            new ConcurrentDictionary<string, TaskHandler>();

        internal bool TryGet(string type, out TaskHandler handler) =>
            HandlerTable.TryGetValue(type, out handler!);

        internal IEnumerable<TaskSpec> Snapshot() => _specs.Values;
    }

    /// <summary>
    /// Entry point. Call <see cref="Start"/> once at app start (debug builds only!),
    /// register task handlers on <see cref="Tasks"/>, and <see cref="Stop"/> on teardown.
    /// </summary>
    public static class OmniDebugLink
    {
        /// <summary>
        /// Heartbeat interval. Long enough (55s) that an idle relay DO can hibernate
        /// between beats; the connection itself stays alive without app-level pings.
        /// </summary>
        public const int HeartbeatMs = 55_000;

        /// <summary>No-traffic watchdog: presume the connection dead after this.</summary>
        public const int WatchdogMs = 180_000;

        /// <summary>Client library version, reported in the capability hello.</summary>
        public const string LibVersion = "0.6.0";

        /// <summary>Relay origin, baked in so callers only supply a token.
        /// Self-hosted relays: point this at your deployment and rebuild.</summary>
        public const string Host = "wss://api.omnidebuglink.dev";

        /// <summary>
        /// Master switch for write/action tasks (ui_click, set_component, set_active, tap_screen...).
        /// Read tasks always work. Reported in the capability hello; set false to make the
        /// device observation-only.
        /// </summary>
        public static bool ActionsEnabled = true;

        /// <summary>Throws when write/action tasks are disabled via ActionsEnabled.</summary>
        internal static void EnsureActionsEnabled()
        {
            if (!ActionsEnabled)
                throw new InvalidOperationException(
                    "write/action tasks are disabled on this device (OmniDebugLink.ActionsEnabled = false)");
        }

        private static OmniDebugLinkBehaviour _behaviour;

        /// <summary>Task handlers. Register yours before or after Start; discovery is dynamic.</summary>
        public static TaskRegistry Tasks { get; } = new TaskRegistry();

        public static LinkState State =>
            _behaviour != null ? _behaviour.ClientState : LinkState.Stopped;

        /// <summary>Raised on the main thread whenever the connection state changes.</summary>
        public static event Action<LinkState> StateChanged
        {
            add { _stateChanged += value; }
            remove { _stateChanged -= value; }
        }

        private static event Action<LinkState> _stateChanged;

        internal static void RaiseStateChanged(LinkState state) => _stateChanged?.Invoke(state);

        /// <summary>
        /// Connect to the OmniDebugLink relay.
        /// </summary>
        /// <param name="clientToken">Client (device-side) token minted via POST /register.</param>
        /// <param name="reconnectMaxMs">Reconnect backoff cap in ms (default 30000).</param>
        public static void Start(string clientToken, int reconnectMaxMs = 30_000)
        {
            if (string.IsNullOrEmpty(clientToken)) throw new ArgumentNullException(nameof(clientToken));
            Stop();

            var url = $"{Host.TrimEnd('/')}/ws?token={Uri.EscapeDataString(clientToken)}";
            var go = new GameObject("OmniDebugLink (debug)");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            _behaviour = go.AddComponent<OmniDebugLinkBehaviour>();
            _behaviour.StartClient(url, reconnectMaxMs);
            BuiltinTasks.RegisterAll(Tasks);
            DeviceLogBuffer.Attach(); // game-log capture for read_logs
        }

        /// <summary>Disconnect and tear down. Safe to call repeatedly.</summary>
        public static void Stop()
        {
            if (_behaviour == null) return;
            DeviceLogBuffer.Detach();
            _behaviour.StopClient();
            _behaviour = null;
        }
    }

    /// <summary>Hidden singleton pumping tasks on the main thread. Created by <see cref="OmniDebugLink.Start"/>.</summary>
    internal sealed class OmniDebugLinkBehaviour : MonoBehaviour
    {
        internal OmniDebugLinkClient Client { get; private set; }

        /// <summary>Marker living in the DontDestroyOnLoad scene; used to enumerate that scene's roots.</summary>
        internal static Transform DontDestroyOnLoadTransform { get; private set; }

        internal LinkState ClientState => Client?.State ?? LinkState.Stopped;

        // Application.* is main-thread-only; captured once on Start so the capability
        // hello can be built no matter which thread a registry change fires from.
        private string _platform;
        private string _appVersion;
        private string _unityVersion;

        internal void StartClient(string url, int reconnectMaxMs)
        {
            _platform = Application.platform.ToString();
            _appVersion = Application.version;
            _unityVersion = Application.unityVersion;
            // All transport events arrive on the main thread, so the client needs no marshalling.
            Client = new OmniDebugLinkClient(url, reconnectMaxMs);
            Client.Connected += SendCapabilityHello;
            OmniDebugLink.Tasks.Changed += OnTasksChanged;
            Client.Start();
        }

        internal void StopClient()
        {
            OmniDebugLink.Tasks.Changed -= OnTasksChanged;
            if (Client != null)
            {
                Client.Connected -= SendCapabilityHello;
                Client.Dispose();
                Client = null;
            }
            if (gameObject != null) Destroy(gameObject);
        }

        private void OnTasksChanged()
        {
            // Registry changed (maybe from another thread); hello is safe to send from any thread.
            if (Client != null && Client.State == LinkState.Connected) SendCapabilityHello();
        }

        /// <summary>Announce the current task manifest to the relay. Thread-safe.</summary>
        private void SendCapabilityHello()
        {
            var client = Client;
            if (client == null) return;
            var tasks = new JArray();
            foreach (var spec in OmniDebugLink.Tasks.Snapshot())
            {
                var o = new JObject { ["type"] = spec.Type };
                if (!string.IsNullOrEmpty(spec.Description)) o["description"] = spec.Description;
                if (!string.IsNullOrEmpty(spec.PayloadSchema))
                {
                    try { o["payloadSchema"] = JToken.Parse(spec.PayloadSchema); }
                    catch { o["payloadSchema"] = spec.PayloadSchema; }
                }
                tasks.Add(o);
            }
            var hello = new JObject
            {
                ["v"] = 1,
                ["type"] = "hello",
                ["client"] = new JObject
                {
                    ["platform"] = _platform,
                    ["version"] = _appVersion,
                    ["unityVersion"] = _unityVersion,
                    ["libVersion"] = OmniDebugLink.LibVersion,
                    ["actionsEnabled"] = OmniDebugLink.ActionsEnabled, // plain bool, thread-safe to read
                },
                ["tasks"] = tasks,
            };
            client.SendRaw(hello.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void Update()
        {
            var client = Client;
            if (client == null) return;
            client.Tick(); // outgoing flush + reconnect backoff + heartbeat
            while (client.TryDequeueTask(out var task))
            {
                ExecuteTask(task);
            }
        }

        /// <summary>Current behaviour instance; lets tasks run coroutines.</summary>
        internal static OmniDebugLinkBehaviour Current { get; private set; }

        /// <summary>
        /// Run func at the end of the current frame (after rendering). Required for
        /// APIs like ScreenCapture.CaptureScreenshotAsTexture that only work once
        /// the frame's rendering has finished. Continues on the main thread.
        /// </summary>
        internal static Task<T> AtEndOfFrame<T>(Func<T> func)
        {
            var runner = Current;
            if (runner == null) return Task.FromResult(func()); // no runner: best effort now
            var tcs = new TaskCompletionSource<T>();
            runner.StartCoroutine(EndOfFrameRoutine(func, tcs));
            return tcs.Task;
        }

        private static System.Collections.IEnumerator EndOfFrameRoutine<T>(Func<T> func, TaskCompletionSource<T> tcs)
        {
            yield return new WaitForEndOfFrame();
            try { tcs.SetResult(func()); }
            catch (Exception e) { tcs.SetException(e); }
        }

        private async void ExecuteTask(OmniDebugLinkTask task)
        {
            var client = Client;
            if (client == null) return; // stopped while queued
            try
            {
                if (!OmniDebugLink.Tasks.TryGet(task.Type, out var handler))
                {
                    client.SendResult(task.RequestId, "UNKNOWN_TASK",
                        $"no handler registered for task type \"{task.Type}\"");
                    return;
                }
                var result = await handler(task);
                client.SendResult(task.RequestId, result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                client.SendResult(task.RequestId, "TASK_FAILED", e.Message);
            }
        }

        private void Awake()
        {
            DontDestroyOnLoadTransform = transform;
            Current = this;
        }

        private void OnApplicationQuit() => StopClient();

        private void OnDestroy() => Client?.Dispose();
    }

    /// <summary>
    /// WebSocket client on top of the vendored UnityWebSocket transport (Runtime/UnityWebSocket):
    /// browser WebSocket via jslib on WebGL, ClientWebSocket on every other platform (editor
    /// included). All transport events arrive on the main thread — manager Update natively, jslib
    /// callbacks on WebGL — so this class runs no loops of its own; sends, reconnect backoff and
    /// the heartbeat are driven by <see cref="Tick"/> from <see cref="OmniDebugLinkBehaviour.Update"/>.
    /// </summary>
    internal sealed class OmniDebugLinkClient : IDisposable
    {
        /// <summary>Monotonic milliseconds (Environment.TickCount64 is unavailable on Unity Mono).</summary>
        private static long MonotonicMs() =>
            System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;

        private readonly string _url;
        private readonly int _reconnectMaxMs;
        private readonly ConcurrentQueue<OmniDebugLinkTask> _incoming = new();
        // Sends are funneled through a queue drained on the main thread so any thread may call
        // SendRaw/SendResult (e.g. Tasks.Register from a worker) even on WebGL, where nothing
        // outside the main thread may touch the browser socket.
        private readonly ConcurrentQueue<string> _outgoing = new();

        private WebSocket _ws;
        private bool _disposed;
        /// <summary>Set when the server closed us with 4000 (same token used by a newer
        /// connection). Stops the reconnect loop instead of fighting for the slot.</summary>
        private bool _replacedByNewerConnection;
        private int _backoffMs = 1_000;
        /// <summary>Monotonic deadline for the next reconnect attempt; 0 = none pending.</summary>
        private long _reconnectAtMs;
        private long _lastServerMessage;
        private long _lastPingMs;

        internal LinkState State { get; private set; }

        /// <summary>Raised on the main thread each time the WebSocket connects.</summary>
        internal event Action Connected;

        public OmniDebugLinkClient(string url, int reconnectMaxMs)
        {
            _url = url;
            _reconnectMaxMs = reconnectMaxMs;
        }

        public void Start() => Connect();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _reconnectAtMs = 0;
            try { _ws?.CloseAsync(); } catch { /* shutting down */ }
            _ws = null;
            SetState(LinkState.Stopped);
        }

        internal bool TryDequeueTask(out OmniDebugLinkTask task) => _incoming.TryDequeue(out task!);

        /// <summary>Thread-safe raw send of a protocol message (used for the capability hello).</summary>
        internal void SendRaw(string json) => EnqueueOutgoing(json);

        /// <summary>Thread-safe. Serialize result object (JToken passthrough) and enqueue for send.</summary>
        internal void SendResult(string requestId, object result)
        {
            var msg = new JObject
            {
                ["v"] = 1,
                ["type"] = "result",
                ["requestId"] = requestId,
                ["ok"] = true,
            };
            msg["result"] = result as JToken ?? (result == null ? JValue.CreateNull() : JToken.FromObject(result));
            EnqueueOutgoing(msg.ToString(Newtonsoft.Json.Formatting.None));
        }

        internal void SendResult(string requestId, string errorCode, string errorMessage)
        {
            var msg = new JObject
            {
                ["v"] = 1,
                ["type"] = "result",
                ["requestId"] = requestId,
                ["ok"] = false,
                ["error"] = new JObject { ["code"] = errorCode, ["message"] = errorMessage },
            };
            EnqueueOutgoing(msg.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void EnqueueOutgoing(string json) => _outgoing.Enqueue(json);

        /// <summary>Main-thread pump called every frame: flush queued sends, fire due
        /// reconnects, send heartbeats and run the no-traffic watchdog.</summary>
        internal void Tick()
        {
            var ws = _ws;
            if (ws != null)
            {
                while (_outgoing.TryDequeue(out var json))
                    ws.SendAsync(json);
            }

            if (_disposed || _replacedByNewerConnection) return;
            var now = MonotonicMs();
            if (_reconnectAtMs > 0 && now >= _reconnectAtMs)
            {
                _reconnectAtMs = 0;
                Connect();
                return;
            }

            ws = _ws;
            if (State != LinkState.Connected || ws == null) return;
            if (now - _lastPingMs < OmniDebugLink.HeartbeatMs) return;
            _lastPingMs = now;
            // Watchdog: no traffic at all for ~3 heartbeat intervals → the connection
            // is presumed dead; abandon it and reconnect without waiting for the close
            // handshake, which may never complete on a silently dropped connection.
            if (now - _lastServerMessage > OmniDebugLink.WatchdogMs)
            {
                Debug.LogWarning("[OmniDebugLink] heartbeat watchdog: server went silent, reconnecting");
                _ws = null; // its close event (whenever it lands) is ignored
                try { ws.CloseAsync(); } catch { }
                ScheduleReconnect();
                return;
            }
            SendRaw("{\"v\":1,\"type\":\"ping\"}");
        }

        private void Connect()
        {
            SetState(LinkState.Connecting);
            var ws = new WebSocket(_url);
            ws.OnOpen += HandleOpen;
            ws.OnMessage += HandleMessage;
            ws.OnError += HandleError;
            ws.OnClose += HandleClose;
            _ws = ws;
            ws.ConnectAsync();
        }

        private void ScheduleReconnect()
        {
            SetState(LinkState.Connecting);
            _reconnectAtMs = MonotonicMs() + _backoffMs;
            _backoffMs = Math.Min(_backoffMs * 2, _reconnectMaxMs);
        }

        // ----- transport events (main thread: manager Update natively, jslib callback on WebGL) -----

        private void HandleOpen(object sender, OpenEventArgs e)
        {
            var ws = (WebSocket)sender;
            if (!ReferenceEquals(sender, _ws) || _disposed)
            {
                try { ws.CloseAsync(); } catch { }
                return;
            }
            _lastServerMessage = MonotonicMs();
            _lastPingMs = _lastServerMessage;
            _backoffMs = 1_000;
            SetState(LinkState.Connected);
            Connected?.Invoke(); // capability hello goes out on the next Tick
        }

        private void HandleMessage(object sender, MessageEventArgs e)
        {
            if (_disposed || !e.IsText) return;
            _lastServerMessage = MonotonicMs();

            JObject msg;
            try { msg = JObject.Parse(e.Data); } catch { return; }
            if ((int?)msg["v"] != 1) return;

            switch ((string)msg["type"])
            {
                case "pong":
                    return;
                case "task":
                {
                    var requestId = (string)msg["requestId"];
                    var taskType = (string)msg["task"]?["type"];
                    if (requestId == null || taskType == null) return;
                    var payload = msg["task"]?["payload"] as JObject ?? new JObject();
                    _incoming.Enqueue(new OmniDebugLinkTask(requestId, taskType, payload));
                    return;
                }
            }
        }

        private void HandleError(object sender, ErrorEventArgs e) =>
            Debug.Log($"[OmniDebugLink] connection error: {e.Message}");

        private void HandleClose(object sender, CloseEventArgs e)
        {
            // Events from a socket we already abandoned (watchdog reset) are irrelevant —
            // they must not schedule extra reconnects.
            if (!ReferenceEquals(sender, _ws)) return;
            _ws = null;

            if (_disposed)
            {
                SetState(LinkState.Stopped);
                return;
            }
            if (e.Code == 4000)
            {
                // 4000 = another connection took over this token's slot (relay-side
                // "replaced by new connection"). Reconnecting would just kick the other
                // side and ping-pong forever.
                _replacedByNewerConnection = true;
                _reconnectAtMs = 0;
                Debug.LogWarning(
                    "[OmniDebugLink] token slot taken over by another connection (close 4000) — " +
                    "stopping reconnects. One token pair belongs to ONE device; mint a separate " +
                    "pair per device (console → 新建 token 对) and Stop()/Start() this client with it.");
                SetState(LinkState.Stopped);
                return;
            }

            Debug.Log($"[OmniDebugLink] connection closed (code {e.Code}), reconnecting in {_backoffMs} ms");
            ScheduleReconnect();
        }

        private void SetState(LinkState state)
        {
            State = state;
            OmniDebugLink.RaiseStateChanged(state);
        }
    }
}
