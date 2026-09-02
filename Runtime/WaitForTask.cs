using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// wait_for — block until a node exists (and optionally a component field
    /// equals a value), polling on the main thread. The synchronous counterpart
    /// of "click then wait for the panel": saves the AI from screenshot polling.
    /// </summary>
    internal static class WaitForTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "wait_for",
                Handle,
                description:
                    "Wait until a GameObject exists at path (optionally until a component field equals a " +
                    "value), polling every 0.2s on the main thread. Returns found=true with waitedMs, or " +
                    "found=false on timeout. Typical use: ui_click then wait_for the target panel's path.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root\"}," +
                    "\"include_inactive\":{\"type\":\"boolean\",\"default\":false,\"description\":\"also match inactive objects\"}," +
                    "\"component\":{\"type\":\"string\",\"description\":\"optional: also require this component on the node\"}," +
                    "\"field\":{\"type\":\"string\",\"description\":\"optional: component field/property to compare\"}," +
                    "\"equals\":{\"type\":\"string\",\"description\":\"expected string form of the field value\"}," +
                    "\"timeout_ms\":{\"type\":\"integer\",\"minimum\":100,\"maximum\":60000,\"default\":10000}" +
                    "},\"required\":[\"path\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            var path = ((string)p["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");
            var includeInactive = p["include_inactive"]?.Value<bool>() ?? false;
            var component = ((string)p["component"])?.Trim();
            var field = ((string)p["field"])?.Trim();
            var equals = (string)p["equals"];
            var timeoutMs = Math.Max(100, Math.Min(60000, p["timeout_ms"]?.Value<int>() ?? 10000));
            if (field != null && component == null)
                throw new ArgumentException("field requires component");

            var runner = OmniDebugLinkBehaviour.Current;
            var tcs = new TaskCompletionSource<object>();
            var started = Time.realtimeSinceStartup;
            if (runner == null)
            {
                // No player loop: a single immediate check.
                tcs.SetResult(Evaluate(path, includeInactive, component, field, equals, 0f));
                return tcs.Task;
            }
            runner.StartCoroutine(PollRoutine(path, includeInactive, component, field, equals, timeoutMs, started, tcs));
            return tcs.Task;
        }

        private static IEnumerator PollRoutine(string path, bool includeInactive, string component,
            string field, string equals, float timeoutMs, float started, TaskCompletionSource<object> tcs)
        {
            while (true)
            {
                var waitedMs = (Time.realtimeSinceStartup - started) * 1000f;
                JObject result;
                try
                {
                    result = Evaluate(path, includeInactive, component, field, equals, waitedMs);
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                    yield break;
                }
                if ((bool)result["found"] || waitedMs >= timeoutMs)
                {
                    tcs.SetResult(result);
                    yield break;
                }
                yield return new WaitForSeconds(0.2f);
            }
        }

        private static JObject Evaluate(string path, bool includeInactive, string component,
            string field, string equals, float waitedMs)
        {
            var t = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes());
            var result = new JObject { ["path"] = path, ["waitedMs"] = Mathf.RoundToInt(waitedMs) };
            if (t == null || (!includeInactive && !t.gameObject.activeInHierarchy))
                return result; // found = false (default)

            result["found"] = true;
            result["active"] = t.gameObject.activeInHierarchy;
            if (component == null) return result;

            var chosen = ViewComponentTask.ResolveComponent(t.GetComponents<Component>(), component);
            if (chosen == null)
            {
                result["found"] = false;
                return result;
            }
            result["component"] = chosen.GetType().Name;
            if (field == null) return result;

            var value = ReadMember(chosen, field);
            result["field"] = field;
            result["value"] = value?.ToString();
            if (equals != null) result["matches"] = string.Equals(value?.ToString(), equals, StringComparison.Ordinal);
            if (equals != null && !(bool)result["matches"]) result["found"] = false;
            return result;
        }

        private static object ReadMember(Component c, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = c.GetType();
            var f = type.GetField(name, flags);
            if (f != null) return f.GetValue(c);
            var prop = type.GetProperty(name, flags);
            return prop != null && prop.CanRead ? prop.GetValue(c) : null;
        }
    }
}
