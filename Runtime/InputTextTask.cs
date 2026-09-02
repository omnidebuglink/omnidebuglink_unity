using System;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace OmniDebugLink
{
    /// <summary>
    /// input_text — put text into a UI input field AND fire its change/end-edit
    /// events, so game logic listening on them reacts (a raw .text assignment
    /// via set_component skips validation and often the listeners).
    ///
    /// Primary path: UGUI InputField (SetTextWithoutNotify → invoke onValueChanged
    /// + onEndEdit exactly once). Fallback for TMP_InputField and friends: set the
    /// "text" property by reflection and invoke the UnityEvent&lt;string&gt; found in
    /// a field named onValueChanged/onEndEdit.
    /// </summary>
    internal static class InputTextTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "input_text",
                Handle,
                description:
                    "Type text into a UI input field (UGUI InputField or TMP) and fire its " +
                    "onValueChanged/onEndEdit events so game logic reacts. path points at the node " +
                    "holding the input field. Returns the applied text and which events fired.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root, e.g. \\\"Canvas/Login/InputField\\\"\"}," +
                    "\"text\":{\"type\":\"string\",\"description\":\"text to enter\"}" +
                    "},\"required\":[\"path\",\"text\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var p = task.Payload;
            var path = ((string)p["path"])?.Trim();
            var text = (string)p["text"] ?? "";
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");

            var t = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");
            var comps = t.GetComponents<Component>();
            var result = new JObject { ["path"] = path, ["applied"] = false };

            // UGUI fast path
            foreach (var c in comps)
            {
                if (c is not InputField field) continue;
                field.SetTextWithoutNotify(text);
                field.onValueChanged?.Invoke(text);
                field.onEndEdit?.Invoke(text);
                result["applied"] = true;
                result["component"] = "InputField";
                result["text"] = field.text;
                result["events"] = new JArray("onValueChanged", "onEndEdit");
                return Task.FromResult<object>(result);
            }

            // Reflection fallback (TMP_InputField etc.)
            foreach (var c in comps)
            {
                if (c == null) continue;
                var type = c.GetType();
                var textProp = type.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (textProp == null || !textProp.CanWrite || textProp.PropertyType != typeof(string)) continue;
                textProp.SetValue(c, text, null);
                var fired = new JArray();
                InvokeStringEvent(c, "onValueChanged", text, fired);
                InvokeStringEvent(c, "onEndEdit", text, fired);
                result["applied"] = true;
                result["component"] = type.Name;
                result["text"] = JToken.FromObject(textProp.GetValue(c, null) ?? "");
                result["events"] = fired;
                return Task.FromResult<object>(result);
            }

            throw new ArgumentException($"no input field component on \"{path}\" (UGUI InputField or a type with a string text property)");
        }

        private static void InvokeStringEvent(Component c, string eventName, string arg, JArray fired)
        {
            var f = c.GetType().GetField(eventName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var evt = f?.GetValue(c);
            if (evt == null) return;
            var invoke = evt.GetType().GetMethod("Invoke", new[] { typeof(string) });
            if (invoke == null) return;
            try
            {
                invoke.Invoke(evt, new object[] { arg });
                fired.Add(eventName);
            }
            catch { /* listener threw; still report the other events */ }
        }
    }
}
