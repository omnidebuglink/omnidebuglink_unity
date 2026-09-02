using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// set_component — write field/property values on a component (view_component's mirror).
    ///
    /// values maps member paths (same dotted syntax view_component reads, e.g.
    /// "color" or "targetGraphic.color") to JSON values. A conversion layer maps
    /// JSON to the target type: enums by name, colors from "#RRGGBBAA" or
    /// {r,g,b,a} (0-1), vectors from {x,y,(z,w)}, numbers/strings/bools widened
    /// as needed. Each member reports before/after so the AI can verify.
    /// Structural members (parent/transform/gameObject/tag/...) are rejected.
    /// </summary>
    internal static class SetComponentTask
    {
        private const int MaxValues = 20;

        private static readonly HashSet<string> BlockedMembers = new HashSet<string>(
            new[] { "parent", "root", "transform", "gameObject", "tag", "childcount", "hierarchycount" },
            StringComparer.OrdinalIgnoreCase);

        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "set_component",
                Handle,
                description:
                    "Set field/property values on one component (the write-side counterpart of view_component). " +
                    "values maps member names (dotted paths allowed, e.g. \"targetGraphic.color\") to JSON values; " +
                    "enums accept names, colors accept \"#RRGGBBAA\" or {r,g,b,a} with 0-1 floats, vectors accept {x,y(,z,w)}. " +
                    "Each member is reported with before/after values. Structural members (parent/transform/...) are rejected. " +
                    "component selection works like view_component (full/short/base-class name).",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root\"}," +
                    "\"component\":{\"type\":\"string\",\"description\":\"component type name (full or short); omit for the single non-Transform component\"}," +
                    "\"values\":{\"type\":\"object\",\"additionalProperties\":true,\"description\":\"member path → new value\"}" +
                    "},\"required\":[\"path\",\"values\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var path = ((string)task.Payload["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");
            var values = task.Payload["values"] as JObject
                ?? throw new ArgumentException("values must be an object of {member: value}");
            if (values.Count == 0) throw new ArgumentException("values is empty");
            if (values.Count > MaxValues) throw new ArgumentException($"at most {MaxValues} members per call");

            var target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");
            var comps = target.GetComponents<Component>();
            var want = ((string)task.Payload["component"])?.Trim();
            var wantDisplay = string.IsNullOrEmpty(want) ? "(the single non-Transform component)" : want;
            var chosen = ViewComponentTask.ResolveComponent(comps, want)
                ?? throw new ArgumentException(
                    $"no component matching \"{wantDisplay}\" on {target.name}; see list_component");

            var results = new JObject();
            foreach (var prop in values.Properties())
            {
                results[prop.Name] = SetMember(chosen, prop.Name, prop.Value);
            }

            return Task.FromResult<object>(new JObject
            {
                ["path"] = path,
                ["type"] = chosen.GetType().FullName,
                ["results"] = results,
            });
        }

        private static JObject SetMember(object root, string memberPath, JToken value)
        {
            var firstSegment = memberPath.Split('.')[0];
            if (BlockedMembers.Contains(firstSegment))
            {
                return Error($"member \"{firstSegment}\" is structural and cannot be set");
            }

            try
            {
                var before = ViewComponentTask.Normalize(ViewComponentTask.ReflectGet(root, memberPath));

                // Walk to the container of the final segment.
                var parts = memberPath.Split('.');
                var container = root;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    container = ViewComponentTask.ReflectGet(container, parts[i]);
                    if (container == null) return Error($"null while walking to \"{memberPath}\"");
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                var t = container.GetType();
                var p = t.GetProperty(parts[parts.Length - 1], flags);
                FieldInfo f = null;
                if (p == null) f = t.GetField(parts[parts.Length - 1], flags);
                if (p == null && f == null) return Error($"member \"{memberPath}\" not found");
                if (p != null && (!p.CanWrite || p.GetIndexParameters().Length > 0))
                    return Error($"property \"{memberPath}\" has no public setter");
                if (p == null && (f == null || f.IsInitOnly || f.IsLiteral))
                    return Error($"field \"{memberPath}\" is readonly");

                var memberType = p?.PropertyType ?? f.FieldType;
                object converted;
                try
                {
                    converted = ConvertValue(value, memberType);
                }
                catch (Exception e)
                {
                    return Error($"{e.Message} (target type {memberType.Name})");
                }

                try
                {
                    if (p != null) p.SetValue(container, converted, null);
                    else f.SetValue(container, converted);
                }
                catch (Exception e)
                {
                    return Error($"setter threw: {e.InnerException?.Message ?? e.Message}");
                }

                var after = ViewComponentTask.Normalize(ViewComponentTask.ReflectGet(root, memberPath));
                return new JObject { ["ok"] = true, ["before"] = before, ["after"] = after };
            }
            catch (Exception e)
            {
                return Error(e.Message);
            }
        }

        private static JObject Error(string message) =>
            new JObject { ["ok"] = false, ["error"] = message };

        // ---- JSON → target type conversion -----------------------------------------

        internal static object ConvertValue(JToken v, Type t)
        {
            if (t.IsEnum)
            {
                if (v.Type == JTokenType.String)
                    return Enum.Parse(t, v.Value<string>(), true);
                return Enum.ToObject(t, Convert.ToInt64((JValue)v));
            }
            if (t == typeof(string)) return v.Type == JTokenType.Null ? null : v.ToString();
            if (t == typeof(bool)) return Convert.ToBoolean((JValue)v);
            if (t == typeof(char)) return v.Value<string>()[0];

            if (t == typeof(Color) || t == typeof(Color32))
            {
                var c = ParseColor(v);
                return t == typeof(Color) ? (object)c : (object)(Color32)c;
            }
            if (t == typeof(Vector2)) { var o = (JObject)v; return new Vector2(F(o, "x"), F(o, "y")); }
            if (t == typeof(Vector3)) { var o = (JObject)v; return new Vector3(F(o, "x"), F(o, "y"), F(o, "z")); }
            if (t == typeof(Vector4)) { var o = (JObject)v; return new Vector4(F(o, "x"), F(o, "y"), F(o, "z"), F(o, "w")); }
            if (t == typeof(Rect)) { var o = (JObject)v; return new Rect(F(o, "x"), F(o, "y"), F(o, "width"), F(o, "height")); }
            if (t == typeof(RectOffset))
            {
                var o = (JObject)v;
                return new RectOffset(I(o, "left"), I(o, "right"), I(o, "top"), I(o, "bottom"));
            }

            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
                t == typeof(float) || t == typeof(double) || t == typeof(uint) || t == typeof(ulong))
            {
                return Convert.ChangeType(((JValue)v).Value, t);
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
                throw new InvalidOperationException("cannot assign Unity object references over the debug relay");

            // last resort: string-based conversion
            return Convert.ChangeType(v.ToString(), t);
        }

        private static Color ParseColor(JToken v)
        {
            if (v.Type == JTokenType.String)
            {
                var s = v.Value<string>().TrimStart('#');
                if (s.Length != 6 && s.Length != 8)
                    throw new FormatException("color strings must be #RRGGBB or #RRGGBBAA hex");
                var r = Convert.ToInt32(s.Substring(0, 2), 16) / 255f;
                var g = Convert.ToInt32(s.Substring(2, 2), 16) / 255f;
                var b = Convert.ToInt32(s.Substring(4, 2), 16) / 255f;
                var a = s.Length == 8 ? Convert.ToInt32(s.Substring(6, 2), 16) / 255f : 1f;
                return new Color(r, g, b, a);
            }
            var o = (JObject)v;
            return new Color(F(o, "r"), F(o, "g"), F(o, "b"), o["a"] != null ? F(o, "a") : 1f);
        }

        private static float F(JObject o, string key) => Convert.ToSingle(((JValue)o[key]).Value);

        private static int I(JObject o, string key) => Convert.ToInt32(((JValue)o[key]).Value);
    }

    /// <summary>set_active — activate/deactivate a GameObject.</summary>
    internal static class SetActiveTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "set_active",
                Handle,
                description: "Activate or deactivate a GameObject (SetActive). Returns before/after state.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root\"}," +
                    "\"active\":{\"type\":\"boolean\",\"default\":true}" +
                    "},\"required\":[\"path\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var path = ((string)task.Payload["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");
            var active = task.Payload["active"]?.Value<bool>() ?? true;

            var target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");

            var before = target.gameObject.activeSelf;
            target.gameObject.SetActive(active);
            return Task.FromResult<object>(new JObject
            {
                ["path"] = path,
                ["before"] = before,
                ["after"] = target.gameObject.activeSelf,
            });
        }
    }
}
