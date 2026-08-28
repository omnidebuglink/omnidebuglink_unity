using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace OmniDebugLink
{
    /// <summary>
    /// Task types every OmniDebugLink device answers out of the box.
    /// They double as the reference for writing custom handlers.
    /// </summary>
    internal static class BuiltinTasks
    {
        public static void RegisterAll(TaskRegistry registry)
        {
            // Reference round-trip used by the MCP `device_ping` tool.
            registry.Register("ping",
                task => Task.FromResult<object>(new JObject
                {
                    ["pong"] = true,
                    ["sentAt"] = task.Payload["sentAt"],
                }),
                description: "Round-trip liveness probe; echoes sentAt back with pong=true.",
                payloadSchema: "{\"type\":\"object\",\"properties\":{\"sentAt\":{\"type\":\"integer\"}},\"additionalProperties\":false}");

            // Returns the payload unchanged — proves the full loop and is handy
            // for inspecting arbitrary state via run_task(type:"echo", payload:...).
            registry.Register("echo",
                task => Task.FromResult<object>(task.Payload),
                description: "Returns the payload unchanged. Useful for smoke-testing the relay loop.",
                payloadSchema: "{\"type\":\"object\",\"additionalProperties\":true}");

            // Read back basic runtime state from the device.
            registry.Register("get_stats",
                task => Task.FromResult<object>(new JObject
                {
                    ["unityVersion"] = UnityEngine.Application.unityVersion,
                    ["platform"] = UnityEngine.Application.platform.ToString(),
                    ["version"] = UnityEngine.Application.version,
                    ["isEditor"] = UnityEngine.Application.isEditor,
                    ["fps"] = 1f / UnityEngine.Time.unscaledDeltaTime,
                    ["frame"] = UnityEngine.Time.frameCount,
                }),
                description: "Basic runtime stats: unity/platform version, fps, frame count.");

            // Runtime GameObject hierarchy traversal (whole scene, including UI).
            SceneTraverseTask.Register(registry);
        }
    }
}
