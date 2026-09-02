using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OmniDebugLink
{
    /// <summary>
    /// find_objects — query the runtime hierarchy by node name (substring or regex),
    /// by the text a node renders (UGUI Text / TMP / InputField value), and/or by
    /// component type, returning "/"-separated paths. The cheap, iterative
    /// alternative to dumping the whole scene via scene_traverse.
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
                    "Find GameObjects by node name (substring, or full regex when regex=true), by the " +
                    "text they render (UGUI Text / TextMeshPro / InputField value, substring — this is " +
                    "how you find a button by its label), and/or by component type. Returns scene-root " +
                    "paths; nodes matched by text also report click_target (the nearest clickable " +
                    "ancestor) so ui_click(click_target) presses the button the label belongs to. " +
                    "active_only=false includes inactive objects. Much cheaper than scene_traverse when " +
                    "you know roughly what you are looking for.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"name\":{\"type\":\"string\",\"description\":\"GameObject name to match (substring, case-insensitive; regex when regex=true)\"}," +
                    "\"text\":{\"type\":\"string\",\"description\":\"display text to match (substring, case-insensitive) — the label a Text/TMP node renders or an InputField's current value\"}," +
                    "\"regex\":{\"type\":\"boolean\",\"default\":false,\"description\":\"treat name as a regular expression\"}," +
                    "\"component\":{\"type\":\"string\",\"description\":\"also require this component type (full or short name)\"}," +
                    "\"active_only\":{\"type\":\"boolean\",\"default\":true,\"description\":\"skip inactiveInHierarchy objects\"}" +
                    "},\"additionalProperties\":false}");
        }

        /// <summary>One hierarchy hit, shared by find_objects and ui_click's text locating.</summary>
        internal sealed class Match
        {
            public Transform Node;
            /// <summary>The text the node renders; null when it renders none (name/component hit).</summary>
            public string DisplayText;
            public string Scene;
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            var name = ((string)p["name"])?.Trim();
            var text = ((string)p["text"])?.Trim();
            var useRegex = p["regex"]?.Value<bool>() ?? false;
            var component = ((string)p["component"])?.Trim();
            var activeOnly = p["active_only"]?.Value<bool>() ?? true;
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(text) && string.IsNullOrEmpty(component))
                throw new ArgumentException(
                    "provide 'name' (node-name substring, or a regex with regex=true), 'text' " +
                    "(the text the node renders, e.g. a button label) and/or 'component' to match" +
                    DescribePayloadKeys(p) + "; to dump the whole scene use scene_traverse instead");

            Regex regex = null;
            if (useRegex && !string.IsNullOrEmpty(name))
            {
                try { regex = new Regex(name, RegexOptions.IgnoreCase); }
                catch (ArgumentException e) { throw new ArgumentException($"invalid regex: {e.Message}"); }
            }

            var matches = Query(name, regex, text, component, activeOnly,
                SceneTraverseTask.RuntimeScenes(), out var truncated);

            var objects = new JArray();
            foreach (var m in matches)
            {
                var o = new JObject
                {
                    ["path"] = SceneTraverseTask.BuildPath(m.Node),
                    ["name"] = m.Node.name,
                    ["active"] = m.Node.gameObject.activeSelf,
                    ["scene"] = m.Scene,
                };
                if (m.DisplayText != null) o["text"] = m.DisplayText;
                var clickable = ClickTargetOf(m.Node);
                if (clickable != null) o["click_target"] = clickable;
                objects.Add(o);
            }

            return Task.FromResult<object>(new JObject
            {
                ["count"] = objects.Count,
                ["truncated"] = truncated,
                ["objects"] = objects,
            });
        }

        /// <summary>
        /// Shared hierarchy query (find_objects, and ui_click's text locating). All given
        /// conditions must hold. Scans at most <see cref="MaxScanNodes"/> nodes and returns
        /// at most <see cref="MaxResults"/> matches.
        /// </summary>
        internal static List<Match> Query(string name, Regex regex, string text, string component,
            bool activeOnly, List<(string name, bool ddol, IEnumerable<Transform> roots)> scenes,
            out bool truncated)
        {
            var matches = new List<Match>();
            var scanned = 0;
            truncated = false;

            foreach (var (sceneName, ddol, roots) in scenes)
            {
                foreach (var root in roots)
                    Scan(root, ddol ? "DontDestroyOnLoad" : sceneName, name, regex, text, component,
                        activeOnly, matches, ref scanned, ref truncated);
                if (truncated) break;
            }
            return matches;
        }

        private static void Scan(Transform t, string sceneName, string name, Regex regex, string text,
            string component, bool activeOnly, List<Match> matches, ref int scanned, ref bool truncated)
        {
            if (truncated) return;
            if (++scanned > MaxScanNodes) { truncated = true; return; }
            if (matches.Count >= MaxResults) { truncated = true; return; }

            var go = t.gameObject;
            var comps = go.GetComponents<Component>();
            if ((!activeOnly || go.activeInHierarchy)
                && NameMatches(go.name, name, regex)
                && TextMatches(comps, text)
                && HasComponent(comps, component))
            {
                matches.Add(new Match
                {
                    Node = t,
                    DisplayText = SceneTraverseTask.DisplayTextOf(comps),
                    Scene = sceneName,
                });
            }

            for (var i = 0; i < t.childCount; i++)
                Scan(t.GetChild(i), sceneName, name, regex, text, component, activeOnly,
                    matches, ref scanned, ref truncated);
        }

        private static bool NameMatches(string nodeName, string name, Regex regex)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return regex != null
                ? regex.IsMatch(nodeName)
                : nodeName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TextMatches(Component[] comps, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            var rendered = SceneTraverseTask.DisplayTextOf(comps);
            return rendered != null &&
                   rendered.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool HasComponent(Component[] comps, string want)
        {
            if (string.IsNullOrEmpty(want)) return true;
            foreach (var c in comps)
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

        /// <summary>Path of the nearest ancestor (or self) that handles pointer clicks —
        /// what ui_click should be pointed at when this node is just a label. Null when
        /// nothing up the chain handles clicks (UGUI semantics; NGUI/FairyGUI users
        /// should resolve paths via view_component instead).</summary>
        internal static string ClickTargetOf(Transform t)
        {
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(t.gameObject);
            return handler == null ? null : SceneTraverseTask.BuildPath(handler.transform);
        }

        /// <summary>Append the payload's actual keys to a validation error, so an AI
        /// sending a wrongly-named field can correct itself in one round-trip.</summary>
        internal static string DescribePayloadKeys(JObject p)
        {
            var keys = new List<string>();
            foreach (var prop in p.Properties()) keys.Add(prop.Name);
            return keys.Count == 0
                ? " (payload was empty)"
                : $" (payload keys sent: {string.Join(", ", keys)})";
        }
    }
}
