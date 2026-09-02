using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OmniDebugLink
{
    /// <summary>
    /// Small single-purpose tasks: set_time_scale, prefs (PlayerPrefs),
    /// send_key (UGUI submit/cancel events on the selected object).
    ///
    /// send_key deliberately does NOT try to inject hardware keycodes
    /// (Escape/Android back): Unity's Input is read-only at player runtime, so
    /// games polling Input.GetKeyDown cannot be driven from managed code.
    /// </summary>
    internal static class MiscTasks
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "set_time_scale",
                HandleSetTimeScale,
                description:
                    "Set Time.timeScale (e.g. 5 to fast-forward timers, cooldowns and animations; " +
                    "1 to restore). Returns before/after values.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"value\":{\"type\":\"number\",\"minimum\":0,\"maximum\":100}" +
                    "},\"required\":[\"value\"],\"additionalProperties\":false}");

            registry.Register(
                "prefs",
                HandlePrefs,
                description:
                    "Read/write/delete a PlayerPrefs entry. action is get|set|delete; value_type " +
                    "(string|int|float) selects the stored type, default string. set requires value.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"action\":{\"type\":\"string\",\"enum\":[\"get\",\"set\",\"delete\"]}," +
                    "\"key\":{\"type\":\"string\"}," +
                    "\"value\":{\"type\":\"string\",\"description\":\"value to set (parsed per value_type)\"}," +
                    "\"value_type\":{\"type\":\"string\",\"enum\":[\"string\",\"int\",\"float\"],\"default\":\"string\"}" +
                    "},\"required\":[\"action\",\"key\"],\"additionalProperties\":false}");

            registry.Register(
                "send_key",
                HandleSendKey,
                description:
                    "Fire a UGUI key event (submit or cancel) on a GameObject, defaulting to the " +
                    "currently selected one. Suits confirming dialogs and keyboard-driven UI. " +
                    "Raw hardware keys (Escape/Android back) are not injectable in a built player.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"key\":{\"type\":\"string\",\"enum\":[\"submit\",\"cancel\"],\"description\":\"enter/escape map to submit/cancel\"}," +
                    "\"path\":{\"type\":\"string\",\"description\":\"optional node path; default = current selected object\"}" +
                    "},\"required\":[\"key\"],\"additionalProperties\":false}");
        }

        // ---- set_time_scale -------------------------------------------------------

        private static Task<object> HandleSetTimeScale(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var value = task.Payload["value"]?.Value<float>() ?? -1f;
            if (value < 0f || value > 100f)
                throw new ArgumentException("value must be in [0,100]");
            var before = Time.timeScale;
            Time.timeScale = value;
            return Task.FromResult<object>(new JObject { ["before"] = before, ["after"] = Time.timeScale });
        }

        // ---- prefs ------------------------------------------------------------------

        private static Task<object> HandlePrefs(OmniDebugLinkTask task)
        {
            var p = task.Payload;
            var action = ((string)p["action"])?.Trim().ToLowerInvariant();
            var key = (string)p["key"];
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key is required");
            var type = ((string)p["type"] ?? (string)p["value_type"] ?? "string").Trim().ToLowerInvariant();

            switch (action)
            {
                case "get":
                {
                    if (!PlayerPrefs.HasKey(key))
                        return Task.FromResult<object>(new JObject { ["key"] = key, ["exists"] = false });
                    object result = type switch
                    {
                        "int" => PlayerPrefs.GetInt(key),
                        "float" => PlayerPrefs.GetFloat(key),
                        _ => PlayerPrefs.GetString(key),
                    };
                    return Task.FromResult<object>(new JObject
                    {
                        ["key"] = key, ["exists"] = true, ["type"] = type, ["value"] = JToken.FromObject(result),
                    });
                }
                case "set":
                {
                    OmniDebugLink.EnsureActionsEnabled();
                    var raw = (string)p["value"];
                    if (raw == null) throw new ArgumentException("value is required for set");
                    switch (type)
                    {
                        case "int":
                        {
                            if (!int.TryParse(raw, out var i))
                                throw new ArgumentException($"\"{raw}\" is not an int (use value_type to pick the type)");
                            PlayerPrefs.SetInt(key, i);
                            break;
                        }
                        case "float":
                        {
                            if (!float.TryParse(raw, out var f))
                                throw new ArgumentException($"\"{raw}\" is not a float (use value_type to pick the type)");
                            PlayerPrefs.SetFloat(key, f);
                            break;
                        }
                        default:
                            PlayerPrefs.SetString(key, raw);
                            break;
                    }
                    PlayerPrefs.Save();
                    return Task.FromResult<object>(new JObject { ["key"] = key, ["set"] = true, ["type"] = type });
                }
                case "delete":
                {
                    OmniDebugLink.EnsureActionsEnabled();
                    var existed = PlayerPrefs.HasKey(key);
                    PlayerPrefs.DeleteKey(key);
                    PlayerPrefs.Save();
                    return Task.FromResult<object>(new JObject { ["key"] = key, ["deleted"] = existed });
                }
                default:
                    throw new ArgumentException("action must be get|set|delete");
            }
        }

        // ---- send_key ---------------------------------------------------------------

        private static Task<object> HandleSendKey(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var p = task.Payload;
            var key = (((string)p["key"])?.Trim().ToLowerInvariant()) ?? "";
            key = key switch
            {
                "enter" or "return" => "submit",
                "escape" or "back" => "cancel",
                _ => key,
            };
            if (key != "submit" && key != "cancel")
                throw new ArgumentException("key must be submit or cancel (enter→submit, escape→cancel)");

            var es = EventSystem.current
                ?? throw new InvalidOperationException("no active EventSystem in this scene (UGUI required)");

            Transform target = null;
            var path = ((string)p["path"])?.Trim();
            if (!string.IsNullOrEmpty(path))
            {
                target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                    ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");
            }
            var go = target != null ? target.gameObject : es.currentSelectedGameObject;
            if (go == null)
                throw new InvalidOperationException("no target: pass path or select an object first");

            var data = new BaseEventData(es);
            var sent = key == "submit"
                ? ExecuteEvents.Execute(go, data, ExecuteEvents.submitHandler)
                : ExecuteEvents.Execute(go, data, ExecuteEvents.cancelHandler);

            return Task.FromResult<object>(new JObject
            {
                ["key"] = key,
                ["path"] = SceneTraverseTask.BuildPath(go.transform),
                ["handled"] = sent,
            });
        }
    }
}
