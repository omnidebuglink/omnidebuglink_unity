using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OmniDebugLink
{
    /// <summary>
    /// scene_traverse — walk the runtime GameObject hierarchy (the whole scene,
    /// not just UI).
    ///
    /// payload:
    ///   path  (string)  path of the node to traverse, "/"-separated from scene root,
    ///                   e.g. "Canvas/Panel". Empty string = all loaded scenes
    ///                   (including the DontDestroyOnLoad scene).
    ///   depth (integer) levels to include. 0 or 1 = top level only.
    ///
    /// Runs on the Unity main thread. Node count is capped to keep the result
    /// under the protocol's ~900KB message limit.
    /// </summary>
    internal static class SceneTraverseTask
    {
        private const int MaxNodes = 3000;

        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "scene_traverse",
                Handle,
                description:
                    "Traverse the runtime GameObject hierarchy of the running app (whole scene, including UI). " +
                    "path selects the starting node (\"/\"-separated from scene root, e.g. \"Canvas/Panel\"); " +
                    "empty path means all loaded scenes including DontDestroyOnLoad. " +
                    "depth is how many levels to include; 0 or 1 returns only the top level. " +
                    "Each node reports name, active state, component type names, childCount and children.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"default\":\"\",\"description\":\"node path from scene root; empty = all loaded scenes\"}," +
                    "\"depth\":{\"type\":\"integer\",\"minimum\":0,\"default\":1,\"description\":\"levels to include; 0 or 1 = top level only\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var path = (string)task.Payload["path"] ?? "";
            var depth = task.Payload["depth"]?.Value<int>() ?? 1;
            if (depth < 1) depth = 1;

            var result = Traverse(path.Trim(), depth);
            return Task.FromResult<object>(result);
        }

        private static JObject Traverse(string path, int depth)
        {
            int nodeCount = 0;
            bool truncated = false;

            var result = new JObject { ["path"] = path, ["depth"] = depth };

            if (path.Length == 0)
            {
                var scenesJson = new JArray();
                foreach (var (name, ddol, roots) in RuntimeScenes())
                {
                    var rootsJson = new JArray();
                    foreach (var root in roots)
                    {
                        var node = WriteNode(root, 1, depth, ref nodeCount, ref truncated);
                        if (node != null) rootsJson.Add(node);
                    }
                    scenesJson.Add(new JObject
                    {
                        ["name"] = name,
                        ["isDontDestroyOnLoad"] = ddol,
                        ["roots"] = rootsJson,
                    });
                }
                result["scenes"] = scenesJson;
            }
            else
            {
                var target = FindByPath(path, RuntimeScenes());
                if (target == null)
                    throw new ArgumentException($"no GameObject found at path \"{path}\" " +
                        "(path is /-separated from scene root; check list_tasks scene_traverse description)");
                var scene = target.gameObject.scene;
                var marker = OmniDebugLinkBehaviour.DontDestroyOnLoadTransform;
                var isDdol = marker != null && marker.gameObject.scene.IsValid() &&
                             marker.gameObject.scene.handle == scene.handle;
                result["scene"] = isDdol ? "DontDestroyOnLoad" : scene.name;
                result["node"] = WriteNode(target, 1, depth, ref nodeCount, ref truncated);
            }

            result["nodeCount"] = nodeCount;
            result["truncated"] = truncated;
            return result;
        }

        /// <summary>All loaded scenes + the DontDestroyOnLoad scene (via our marker object). Shared with other tasks.</summary>
        internal static List<(string name, bool ddol, IEnumerable<Transform> roots)> RuntimeScenes()
        {
            var scenes = new List<(string, bool, IEnumerable<Transform>)>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (!s.isLoaded) continue;
                scenes.Add((string.IsNullOrEmpty(s.name) ? "(untitled)" : s.name, false, RootsOf(s)));
            }

            // DontDestroyOnLoad contents live in a special scene only reachable
            // through an object placed there — our own hidden behaviour.
            var marker = OmniDebugLinkBehaviour.DontDestroyOnLoadTransform;
            if (marker != null && marker.gameObject.scene.IsValid() && marker.gameObject.scene.isLoaded)
            {
                var ds = marker.gameObject.scene;
                scenes.Add(("DontDestroyOnLoad", true, RootsOf(ds)));
            }

            return scenes;
        }

        private static IEnumerable<Transform> RootsOf(Scene s)
        {
            foreach (var go in s.GetRootGameObjects())
                yield return go.transform;
        }

        /// <summary>Find a Transform by "/"-separated path across all runtime scenes. Returns null when not found.</summary>
        internal static Transform FindByPath(
            string path, List<(string name, bool ddol, IEnumerable<Transform> roots)> scenes)
        {
            var parts = path.Split('/');
            foreach (var (_, _, roots) in scenes)
            {
                foreach (var root in roots)
                {
                    if (root.name != parts[0]) continue;
                    var current = root;
                    var ok = true;
                    for (var i = 1; i < parts.Length; i++)
                    {
                        current = FindChild(current, parts[i]);
                        if (current == null) { ok = false; break; }
                    }
                    if (ok) return current;
                }
            }
            return null;
        }

        /// <summary>Reverse of FindByPath: build a "/"-separated scene-root path for a Transform.</summary>
        internal static string BuildPath(Transform t)
        {
            var sb = new System.Text.StringBuilder(t.name);
            var p = t.parent;
            while (p != null)
            {
                sb.Insert(0, p.name + "/");
                p = p.parent;
            }
            return sb.ToString();
        }

        private static Transform FindChild(Transform parent, string name)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name == name) return child;
            }
            return null;
        }

        private static JObject WriteNode(Transform t, int level, int maxLevel, ref int count, ref bool truncated)
        {
            if (count >= MaxNodes)
            {
                truncated = true;
                return null;
            }
            count++;

            var components = new JArray();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c != null) components.Add(c.GetType().Name);
            }

            var node = new JObject
            {
                ["name"] = t.name,
                ["active"] = t.gameObject.activeSelf,
                ["components"] = components,
                ["childCount"] = t.childCount,
            };

            if (level < maxLevel)
            {
                var children = new JArray();
                for (var i = 0; i < t.childCount; i++)
                {
                    var child = WriteNode(t.GetChild(i), level + 1, maxLevel, ref count, ref truncated);
                    if (child != null) children.Add(child);
                }
                node["children"] = children;
            }
            return node;
        }
    }
}
