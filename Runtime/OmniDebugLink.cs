using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

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
        /// <summary>Default heartbeat interval in milliseconds.</summary>
        public const int HeartbeatMs = 15_000;

        /// <summary>Client library version, reported in the capability hello.</summary>
        public const string LibVersion = "0.2.0";

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
        /// <param name="host">Relay origin, e.g. "wss://omnidebuglinkapi.tomfox.cc".</param>
        /// <param name="clientToken">Client (device-side) token minted via POST /register.</param>
        /// <param name="reconnectMaxMs">Reconnect backoff cap in ms (default 30000).</param>
        public static void Start(string host, string clientToken, int reconnectMaxMs = 30_000)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentNullException(nameof(host));
            if (string.IsNullOrEmpty(clientToken)) throw new ArgumentNullException(nameof(clientToken));
            Stop();

            var url = $"{host.TrimEnd('/')}/ws?token={Uri.EscapeDataString(clientToken)}";
            var go = new GameObject("OmniDebugLink (debug)");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.DontSave;
            _behaviour = go.AddComponent<OmniDebugLinkBehaviour>();
            _behaviour.StartClient(url, reconnectMaxMs);
            BuiltinTasks.RegisterAll(Tasks);
        }

        /// <summary>Disconnect and tear down. Safe to call repeatedly.</summary>
        public static void Stop()
        {
            if (_behaviour == null) return;
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

        internal void StartClient(string url, int reconnectMaxMs)
        {
            // Captured on the main thread so background state changes can notify subscribers there.
            Client = new OmniDebugLinkClient(url, reconnectMaxMs, SynchronizationContext.Current);
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
                    ["platform"] = Application.platform.ToString(),
                    ["version"] = Application.version,
                    ["unityVersion"] = Application.unityVersion,
                    ["libVersion"] = OmniDebugLink.LibVersion,
                },
                ["tasks"] = tasks,
            };
            client.SendRaw(hello.ToString(Newtonsoft.Json.Formatting.None));
        }

        private void Update()
        {
            if (Client == null) return;
            while (Client.TryDequeueTask(out var task))
            {
                ExecuteTask(task);
            }
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
        }

        private void OnApplicationQuit() => StopClient();

        private void OnDestroy() => Client?.Dispose();
    }

    /// <summary>WebSocket client. All network I/O lives on background threads; task execution is marshalled to the main thread via a queue read by <see cref="OmniDebugLinkBehaviour.Update"/>.</summary>
    internal sealed class OmniDebugLinkClient : IDisposable
    {
        private const int ReceiveChunk = 16 * 1024;

        private readonly string _url;
        private readonly int _reconnectMaxMs;
        private readonly SynchronizationContext _mainThread;
        private readonly ConcurrentQueue<OmniDebugLinkTask> _incoming = new();
        private readonly ConcurrentQueue<string> _outgoing = new();
        private readonly SemaphoreSlim _outgoingSignal = new(0, int.MaxValue);

        private CancellationTokenSource _cts = new();
        private ClientWebSocket _ws;
        private long _lastServerMessage;

        internal LinkState State { get; private set; }
        private int _mainThreadStateQueued;

        /// <summary>Raised on a background thread each time the WebSocket connects.</summary>
        internal event Action Connected;

        public OmniDebugLinkClient(
            string url, int reconnectMaxMs, SynchronizationContext mainThread)
        {
            _url = url;
            _reconnectMaxMs = reconnectMaxMs;
            _mainThread = mainThread;
        }

        public void Start()
        {
            Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispose()
        {
            if (_cts == null) return;
            try
            {
                _cts.Cancel();
                _ws?.Abort();
            }
            catch { /* shutting down */ }
            SetState(LinkState.Stopped);
            _cts = null;
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

        private void EnqueueOutgoing(string json)
        {
            _outgoing.Enqueue(json);
            try { _outgoingSignal.Release(); } catch (SemaphoreFullException) { }
        }

        private void SetState(LinkState state)
        {
            State = state;
            // Marshal the notification to the main thread on the next player loop tick.
            if (Interlocked.Exchange(ref _mainThreadStateQueued, 1) == 0)
            {
                var ctx = _mainThread;
                if (ctx == null)
                {
                    Interlocked.Exchange(ref _mainThreadStateQueued, 0);
                    return;
                }
                ctx.Post(_ =>
                {
                    Interlocked.Exchange(ref _mainThreadStateQueued, 0);
                    OmniDebugLink.RaiseStateChanged(State);
                }, null);
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var backoffMs = 1_000;
            while (!ct.IsCancellationRequested)
            {
                SetState(LinkState.Connecting);
                ClientWebSocket ws = null;
                try
                {
                    ws = new ClientWebSocket();
                    _ws = ws;
                    await ws.ConnectAsync(new Uri(_url), ct);
                    _lastServerMessage = Environment.TickCount64;
                    SetState(LinkState.Connected);
                    Connected?.Invoke();
                    backoffMs = 1_000;

                    using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var receive = ReceiveLoopAsync(ws, connectionCts.Token);
                    var send = SendLoopAsync(ws, connectionCts.Token);
                    var heartbeat = HeartbeatLoopAsync(ws, connectionCts.Token);
                    // Wait for the first loop to finish (normally by throwing), then
                    // cancel the siblings so they don't linger as unobserved tasks.
                    var first = await Task.WhenAny(receive, send, heartbeat);
                    connectionCts.Cancel();
                    try { await Task.WhenAll(receive, send, heartbeat); }
                    catch { /* already surfaced via first, or sibling cancellation */ }
                    await first;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Debug.Log($"[OmniDebugLink] connection error: {e.Message}");
                }
                finally
                {
                    _ws = null;
                    try { ws?.Dispose(); } catch { }
                }

                if (ct.IsCancellationRequested) break;
                SetState(LinkState.Connecting);
                try { await Task.Delay(backoffMs, ct); } catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, _reconnectMaxMs);
            }
            SetState(LinkState.Stopped);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[ReceiveChunk];
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var sb = new StringBuilder();
                WebSocketReceiveResult received;
                do
                {
                    received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (received.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException($"server closed connection ({received.CloseStatus})");
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                } while (!received.EndOfMessage);

                HandleServerMessage(sb.ToString());
            }
        }

        private void HandleServerMessage(string json)
        {
            _lastServerMessage = Environment.TickCount64;
            JObject msg;
            try { msg = JObject.Parse(json); } catch { return; }
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

        private async Task SendLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await _outgoingSignal.WaitAsync(ct);
                while (_outgoing.TryDequeue(out var json))
                {
                    await ws.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                        WebSocketMessageType.Text, true, ct);
                }
            }
        }

        private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(OmniDebugLink.HeartbeatMs, ct);
                // Watchdog: no traffic at all for ~3 heartbeat intervals → the connection
                // is presumed dead; drop it so the run loop reconnects.
                if (Environment.TickCount64 - _lastServerMessage > OmniDebugLink.HeartbeatMs * 3)
                    throw new WebSocketException("heartbeat watchdog: server went silent");
                await ws.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"ping\"}")),
                    WebSocketMessageType.Text, true, ct);
            }
        }
    }
}
