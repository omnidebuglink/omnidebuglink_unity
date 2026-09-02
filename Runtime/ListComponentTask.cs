using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// list_component — list every component on a GameObject with its type fullname.
    /// Cheap companion to view_component (which inspects one component in depth).
    /// </summary>
    internal static class ListComponentTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "list_component",
                Handle,
                description:
                    "List all components on a GameObject with their full type names (and enabled state). " +
                    "Use before view_component to pick the right component name.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root, e.g. \\\"Canvas/Panel/Button\\\"\"}" +
                    "},\"required\":[\"path\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var path = ((string)task.Payload["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");

            var target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");

            var components = new JArray();
            foreach (var c in target.GetComponents<Component>())
            {
                if (c == null) continue;
                components.Add(new JObject
                {
                    ["fullName"] = c.GetType().FullName,
                    ["name"] = c.GetType().Name,
                    ["enabled"] = c is Behaviour b ? (JToken)b.enabled : JValue.CreateNull(),
                });
            }

            return Task.FromResult<object>(new JObject
            {
                ["path"] = path,
                ["componentCount"] = components.Count,
                ["components"] = components,
            });
        }
    }
}
