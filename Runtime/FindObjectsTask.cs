using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// find_objects — query the runtime hierarchy by name (substring or regex)
    /// and/or component type, returning "/"-separated paths. The cheap,
    /// iterative alternative to dumping the whole scene via scene_traverse.
    /// </summary>
    internal static class FindObjectsTask
    {
        private const int MaxResults = 200;
        private const int MaxScanNodes = 20000;

        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "find_objects",
                Handle,
                description:
                    "Find GameObjects by name (substring, or full regex when regex=true) and optional " +
                    "component type, returning their scene-root paths for use with view_component/ui_click. " +
                    "active_only=false includes inactive objects. Much cheaper than scene_traverse when " +
                    "you already know roughly what you are looking for.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"name\":{\"type\":\"string\",\"description\":\"name to match (substring, case-insensitive; regex when regex=true)\"}," +
                    "\"regex\":{\"type\":\"boolean\",\"default\":false,\"description\":\"treat name as a regular expression\"}," +
                    "\"component\":{\"type\":\"string\",\"description\":\"also require this component type (full or short name)\"}," +
                    "\"active_only\":{\"type\":\"boolean\",\"default\":true,\"description\":\"skip inactiveInHierarchy objects\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            // Cross-platform habit: the other clients' find_objects match by "text" —
            // accept it as an alias so AI tools moving between platforms don't trip.
            var name = (((string)p["name"]) ?? (string)p["text"])?.Trim();
            var useRegex = p["regex"]?.Value<bool>() ?? false;
            var component = ((string)p["component"])?.Trim();
            var activeOnly = p["active_only"]?.Value<bool>() ?? true;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(component))
                throw new ArgumentException(
                    "provide 'name' (substring, case-insensitive, or a regex with regex=true) " +
                    "and/or 'component' to match" + DescribePayloadKeys(p) +
                    "; to dump the whole scene use scene_traverse instead");

            Regex regex = null;
            if (useRegex && !string.IsNullOrEmpty(name))
            {
                try { regex = new Regex(name, RegexOptions.IgnoreCase); }
                catch (ArgumentException e) { throw new ArgumentException($"invalid regex: {e.Message}"); }
            }

            var matches = new JArray();
            var scanned = 0;
            var truncated = false;

            foreach (var (sceneName, ddol, roots) in SceneTraverseTask.RuntimeScenes())
            {
                foreach (var root in roots)
                    Scan(root, sceneName, ddol, name, regex, component, activeOnly,
                        matches, ref scanned, ref truncated);
                if (truncated) break;
            }

            return Task.FromResult<object>(new JObject
            {
                ["count"] = matches.Count,
                ["truncated"] = truncated,
                ["objects"] = matches,
            });
        }

        /// <summary>Append the payload's actual keys to a validation error, so an AI
        /// sending a wrongly-named field can correct itself in one round-trip.</summary>
        private static string DescribePayloadKeys(JObject p)
        {
            var keys = new List<string>();
            foreach (var prop in p.Properties()) keys.Add(prop.Name);
            return keys.Count == 0
                ? " (payload was empty)"
                : $" (payload keys sent: {string.Join(", ", keys)})";
        }

        private static void Scan(Transform t, string sceneName, bool ddol, string name, Regex regex,
            string component, bool activeOnly, JArray matches, ref int scanned, ref bool truncated)
        {
            if (truncated) return;
            if (++scanned > MaxScanNodes) { truncated = true; return; }
            if (matches.Count >= MaxResults) { truncated = true; return; }

            var go = t.gameObject;
            if ((!activeOnly || go.activeInHierarchy) && NameMatches(go.name, name, regex) && HasComponent(go, component))
            {
                matches.Add(new JObject
                {
                    ["path"] = SceneTraverseTask.BuildPath(t),
                    ["name"] = go.name,
                    ["active"] = go.activeSelf,
                    ["scene"] = ddol ? "DontDestroyOnLoad" : sceneName,
                });
            }

            for (var i = 0; i < t.childCount; i++)
                Scan(t.GetChild(i), sceneName, ddol, name, regex, component, activeOnly, matches, ref scanned, ref truncated);
        }

        private static bool NameMatches(string nodeName, string name, Regex regex)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return regex != null
                ? regex.IsMatch(nodeName)
                : nodeName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasComponent(GameObject go, string want)
        {
            if (string.IsNullOrEmpty(want)) return true;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                var type = c.GetType();
                if (string.Equals(type.Name, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.FullName, want, StringComparison.OrdinalIgnoreCase) ||
                    // base-class / interface short-name match, e.g. "Button" for a MyButton
                    InheritsOrImplements(type, want))
                    return true;
            }
            return false;
        }

        private static bool InheritsOrImplements(Type type, string want)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (string.Equals(t.Name, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.FullName, want, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            foreach (var i in type.GetInterfaces())
            {
                if (string.Equals(i.Name, want, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.FullName, want, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
