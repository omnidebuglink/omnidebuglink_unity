using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// view_component — inspect one component on a GameObject in detail.
    ///
    /// payload:
    ///   path      (string, required) "/"-separated node path from scene root.
    ///   component (string, optional) which component to inspect: full type name,
    ///             short type name, or any registered short name (resolved via the
    ///             fullname map, including base-class matching). Default: the single
    ///             non-Transform component (error + component list when ambiguous).
    ///
    /// Extraction is dictionary-driven: fullname → field specs. Unity built-ins use
    /// member paths; NGUI / FairyGUI (third-party, not referenced at compile time)
    /// use the same reflection mechanism against property/field names — missing
    /// members across framework versions degrade to null, never throw.
    /// Unknown fullnames (custom MonoBehaviours) fall back to reflecting declared
    /// public/[SerializeField] members of basic types.
    /// </summary>
    internal static class ViewComponentTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "view_component",
                Handle,
                description:
                    "Inspect one component on a GameObject in detail. path is the \"/\"-separated node path; " +
                    "component optionally selects which (full or short type name; default = the only non-Transform component). " +
                    "Returns the component's type fullname plus extracted fields (value + declared type), covering " +
                    "UGUI, layout components, NGUI, FairyGUI, and common Unity components; unknown types are reflected " +
                    "automatically. On FairyGUI display objects the wrapped G-object is included as fairyguiOwner. " +
                    "Use list_component first when unsure which components exist.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root, e.g. \\\"Canvas/Panel/Button\\\"\"}," +
                    "\"component\":{\"type\":\"string\",\"description\":\"component type name (full or short); omit for the single non-Transform component\"}" +
                    "},\"required\":[\"path\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            var path = ((string)task.Payload["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");
            var want = ((string)task.Payload["component"])?.Trim();

            var target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");

            var go = target.gameObject;
            var comps = go.GetComponents<Component>();
            var chosen = ResolveComponent(comps, want)
                ?? throw new ArgumentException(BuildAmbiguityError(go, comps, want));

            var result = new JObject
            {
                ["path"] = path,
                ["type"] = chosen.GetType().FullName,
                ["typeName"] = chosen.GetType().Name,
                ["fields"] = Extract(chosen),
            };

            // FairyGUI hook: scene GameObjects carry a DisplayObject whose gOwner
            // references the logical G-object (GButton/GList/...) that holds the
            // real UI state. Surface it when reachable.
            var owner = FindFairyGuiOwner(comps);
            if (owner != null)
            {
                result["fairyguiOwner"] = new JObject
                {
                    ["type"] = owner.GetType().FullName,
                    ["typeName"] = owner.GetType().Name,
                    ["fields"] = Extract(owner),
                };
            }

            return Task.FromResult<object>(result);
        }

        // ---- component selection -------------------------------------------------

        internal static Component ResolveComponent(Component[] comps, string want)
        {
            if (string.IsNullOrEmpty(want))
            {
                Component only = null;
                foreach (var c in comps)
                {
                    if (c == null || c is Transform) continue;
                    if (only != null) return null; // ambiguous
                    only = c;
                }
                return only;
            }

            // 1) exact fullname, 2) short name
            foreach (var c in comps)
            {
                if (c != null && string.Equals(c.GetType().FullName, want, StringComparison.OrdinalIgnoreCase)) return c;
            }
            foreach (var c in comps)
            {
                if (c != null && string.Equals(c.GetType().Name, want, StringComparison.OrdinalIgnoreCase)) return c;
            }

            // 3) registered short-name map → resolve fullname → subclass match
            if (ShortNameMap.TryGetValue(want, out var fullnames))
            {
                foreach (var fullname in fullnames)
                {
                    var t = FindType(fullname);
                    if (t == null) continue;
                    foreach (var c in comps)
                    {
                        if (c != null && t.IsInstanceOfType(c)) return c;
                    }
                }
            }

            // 4) treat `want` itself as a fullname anywhere in the app
            var direct = FindType(want);
            if (direct != null)
            {
                foreach (var c in comps)
                {
                    if (c != null && direct.IsInstanceOfType(c)) return c;
                }
            }
            return null;
        }

        private static string BuildAmbiguityError(GameObject go, Component[] comps, string want)
        {
            var names = new List<string>();
            foreach (var c in comps)
            {
                if (c == null || c is Transform) continue;
                names.Add(c.GetType().FullName);
            }
            return string.IsNullOrEmpty(want)
                ? $"{go.name} has {names.Count} non-Transform components [{string.Join(", ", names)}]; pass \"component\" to choose one (see list_component)"
                : $"no component matching \"{want}\" on {go.name}; available [{string.Join(", ", names)}]";
        }

        // ---- field extraction -----------------------------------------------------

        /// <summary>fullname → field specs. Lookup walks the type's base-class chain,
        /// so entries on base types (UIWidget, GObject, Collider…) cover subclasses.</summary>
        private static readonly Dictionary<string, FieldSpec[]> Inspectors = BuildInspectors();

        /// <summary>short class name → registered fullnames (e.g. "Image" → ["UnityEngine.UI.Image"]).</summary>
        private static readonly Dictionary<string, List<string>> ShortNameMap = BuildShortNameMap();

        private sealed class FieldSpec
        {
            public string Out;   // output field name
            public string Type;  // declared type label
            public string Member; // dotted reflection path (property or field at each step)
            public Func<object, object> Custom; // special getter
        }

        private static FieldSpec F(string name, string type, string member) =>
            new FieldSpec { Out = name, Type = type, Member = member };

        private static FieldSpec C(string name, string type, Func<object, object> get) =>
            new FieldSpec { Out = name, Type = type, Custom = get };

        private static Dictionary<string, FieldSpec[]> BuildInspectors() => new Dictionary<string, FieldSpec[]>
        {
            // ---------- UGUI: display ----------
            ["UnityEngine.UI.Image"] = new[] {
                F("sprite", "string", "sprite"), F("color", "Color32", "color"),
                F("type", "enum", "type"), F("fillAmount", "float", "fillAmount"),
                F("raycastTarget", "bool", "raycastTarget"), F("material", "string", "material"),
            },
            ["UnityEngine.UI.RawImage"] = new[] {
                F("texture", "string", "mainTexture"), F("color", "Color32", "color"), F("uvRect", "Rect", "uvRect"),
            },
            ["UnityEngine.UI.Text"] = new[] {
                F("text", "string", "text"), F("fontSize", "int", "fontSize"), F("color", "Color32", "color"),
                F("font", "string", "font"), F("alignment", "enum", "alignment"), F("richText", "bool", "richText"),
            },
            ["UnityEngine.SpriteRenderer"] = new[] {
                F("sprite", "string", "sprite"), F("color", "Color32", "color"),
                F("sortingOrder", "int", "sortingOrder"), F("flipX", "bool", "flipX"), F("flipY", "bool", "flipY"),
            },
            // ---------- UGUI: interaction ----------
            ["UnityEngine.UI.Button"] = new[] {
                F("interactable", "bool", "interactable"), F("transition", "enum", "transition"),
                C("targetGraphicType", "string", o => ReflectGet(o, "targetGraphic")?.GetType().Name),
                F("targetGraphic", "string", "targetGraphic"),
                F("targetSprite", "string", "targetGraphic.sprite"),
                C("onClickListeners", "int", o => ReflectCall(ReflectGet(o, "onClick"), "GetPersistentEventCount")),
            },
            ["UnityEngine.UI.InputField"] = new[] {
                F("text", "string", "text"), F("characterLimit", "int", "characterLimit"),
                F("contentType", "enum", "contentType"), F("readOnly", "bool", "readOnly"),
                F("isFocused", "bool", "isFocused"), F("placeholderText", "string", "placeholder.text"),
            },
            ["UnityEngine.UI.Toggle"] = new[] {
                F("isOn", "bool", "isOn"), F("interactable", "bool", "interactable"), F("group", "string", "group"),
            },
            ["UnityEngine.UI.Slider"] = new[] {
                F("value", "float", "value"), F("minValue", "float", "minValue"), F("maxValue", "float", "maxValue"),
                F("wholeNumbers", "bool", "wholeNumbers"), F("interactable", "bool", "interactable"),
            },
            ["UnityEngine.UI.Dropdown"] = new[] {
                F("value", "int", "value"), F("interactable", "bool", "interactable"),
                C("options", "list", o => CollectTexts(ReflectGet(o, "options"), "text")),
            },
            ["UnityEngine.UI.ScrollRect"] = new[] {
                F("horizontal", "bool", "horizontal"), F("vertical", "bool", "vertical"),
                F("normalizedPosition", "Vector2", "normalizedPosition"), F("velocity", "Vector2", "velocity"),
                F("content", "string", "content"),
            },
            ["UnityEngine.CanvasGroup"] = new[] {
                F("alpha", "float", "alpha"), F("interactable", "bool", "interactable"),
                F("blocksRaycasts", "bool", "blocksRaycasts"), F("ignoreParentGroups", "bool", "ignoreParentGroups"),
            },
            // ---------- layout ----------
            ["UnityEngine.RectTransform"] = new[] {
                F("anchoredPosition3D", "Vector3", "anchoredPosition3D"), F("sizeDelta", "Vector2", "sizeDelta"),
                F("rect", "Rect", "rect"), F("offsetMin", "Vector2", "offsetMin"), F("offsetMax", "Vector2", "offsetMax"),
                F("anchorMin", "Vector2", "anchorMin"), F("anchorMax", "Vector2", "anchorMax"),
                F("pivot", "Vector2", "pivot"), F("localScale", "Vector3", "localScale"),
                F("localEulerAngles", "Vector3", "localEulerAngles"),
                // Unambiguous coordinates for cross-checking with tap_screen px/py —
                // anchoredPosition means different things under stretch anchors.
                F("worldPosition", "Vector3", "position"),
                C("screenPosition", "Vector2", o =>
                    RectTransformUtility.WorldToScreenPoint(null, ((RectTransform)o).position)),
                C("anchorMode", "string", o => {
                    var rt = (RectTransform)o;
                    string X(float a, float b) => Mathf.Approximately(a, b) ? "point" : "stretch";
                    return X(rt.anchorMin.x, rt.anchorMax.x) + "-" + X(rt.anchorMin.y, rt.anchorMax.y);
                }),
            },
            ["UnityEngine.Canvas"] = new[] {
                F("renderMode", "enum", "renderMode"), F("sortingOrder", "int", "sortingOrder"),
                F("overrideSorting", "bool", "overrideSorting"), F("worldCamera", "string", "worldCamera"),
                F("planeDistance", "float", "planeDistance"), F("referencePixelsPerUnit", "float", "referencePixelsPerUnit"),
            },
            ["UnityEngine.UI.CanvasScaler"] = new[] {
                F("uiScaleMode", "enum", "uiScaleMode"), F("referenceResolution", "Vector2", "referenceResolution"),
                F("matchWidthOrHeight", "float", "matchWidthOrHeight"), F("scaleFactor", "float", "scaleFactor"),
            },
            ["UnityEngine.UI.HorizontalOrVerticalLayoutGroup"] = new[] {
                F("padding", "RectOffset", "padding"), F("spacing", "float", "spacing"),
                F("childAlignment", "enum", "childAlignment"),
                F("childControlWidth", "bool", "childControlWidth"), F("childControlHeight", "bool", "childControlHeight"),
                F("childForceExpandWidth", "bool", "childForceExpandWidth"),
                F("childForceExpandHeight", "bool", "childForceExpandHeight"),
                F("reverseArrangement", "bool", "reverseArrangement"),
            },
            ["UnityEngine.UI.GridLayoutGroup"] = new[] {
                F("padding", "RectOffset", "padding"), F("spacing", "Vector2", "spacing"),
                F("childAlignment", "enum", "childAlignment"), F("cellSize", "Vector2", "cellSize"),
                F("constraint", "enum", "constraint"), F("startCorner", "enum", "startCorner"),
                F("startAxis", "enum", "startAxis"),
            },
            ["UnityEngine.UI.LayoutElement"] = new[] {
                F("ignoreLayout", "bool", "ignoreLayout"), F("minWidth", "float", "minWidth"),
                F("preferredWidth", "float", "preferredWidth"), F("flexibleWidth", "float", "flexibleWidth"),
                F("minHeight", "float", "minHeight"), F("preferredHeight", "float", "preferredHeight"),
                F("flexibleHeight", "float", "flexibleHeight"), F("layoutPriority", "int", "layoutPriority"),
            },
            ["UnityEngine.UI.ContentSizeFitter"] = new[] {
                F("horizontalFit", "enum", "horizontalFit"), F("verticalFit", "enum", "verticalFit"),
            },
            ["UnityEngine.UI.AspectRatioFitter"] = new[] {
                F("aspectMode", "enum", "aspectMode"), F("aspectRatio", "float", "aspectRatio"),
            },
            // ---------- 3D / animation / audio / physics ----------
            ["UnityEngine.Camera"] = new[] {
                F("fieldOfView", "float", "fieldOfView"), F("orthographic", "bool", "orthographic"),
                F("orthographicSize", "float", "orthographicSize"), F("depth", "float", "depth"),
                F("cullingMask", "int", "cullingMask"),
            },
            ["UnityEngine.Animator"] = new[] {
                F("controller", "string", "runtimeAnimatorController"), F("speed", "float", "speed"),
                F("layerCount", "int", "layerCount"),
                C("state0", "string", o => {
                    var info = ReflectCall(o, "GetCurrentAnimatorStateInfo", 0);
                    return info == null ? null : ReflectGet(info, "shortName");
                }),
            },
            ["UnityEngine.AudioSource"] = new[] {
                F("clip", "string", "clip"), F("volume", "float", "volume"),
                F("isPlaying", "bool", "isPlaying"), F("loop", "bool", "loop"),
                F("mute", "bool", "mute"), F("pitch", "float", "pitch"),
            },
            ["UnityEngine.Rigidbody"] = new[] {
                F("velocity", "Vector3", "velocity"), F("mass", "float", "mass"),
                F("isKinematic", "bool", "isKinematic"), F("useGravity", "bool", "useGravity"),
            },
            ["UnityEngine.Collider"] = new[] {
                F("isTrigger", "bool", "isTrigger"),
            },
            // ---------- NGUI (global namespace — bare fullnames, reflection-only) ----------
            ["UIWidget"] = new[] {
                F("width", "int", "width"), F("height", "int", "height"), F("depth", "int", "depth"),
                F("alpha", "float", "alpha"), F("color", "Color32", "color"), F("pivot", "enum", "pivot"),
                F("isVisible", "bool", "isVisible"),
            },
            ["UISprite"] = new[] {
                F("spriteName", "string", "spriteName"), F("atlas", "string", "atlas"),
                F("type", "enum", "type"), F("fillAmount", "float", "fillAmount"), F("flip", "enum", "flip"),
            },
            ["UILabel"] = new[] {
                F("text", "string", "text"), F("fontSize", "int", "fontSize"),
                F("overflowMethod", "enum", "overflowMethod"), F("alignment", "enum", "alignment"),
                F("effectStyle", "enum", "effectStyle"), F("effectColor", "Color32", "effectColor"),
                F("maxLineCount", "int", "maxLineCount"), F("trueTypeFont", "string", "trueTypeFont"),
                F("bitmapFont", "string", "bitmapFont"),
            },
            ["UITexture"] = new[] {
                F("mainTexture", "string", "mainTexture"), F("color", "Color32", "color"), F("uvRect", "Rect", "uvRect"),
            },
            ["UIButton"] = new[] {
                F("isEnabled", "bool", "isEnabled"), F("tweenTarget", "string", "tweenTarget"),
            },
            ["UIToggle"] = new[] {
                F("value", "bool", "value"), F("group", "string", "group"),
                F("optionCanBeNone", "bool", "optionCanBeNone"),
            },
            ["UISlider"] = new[] {
                F("value", "float", "value"), F("numberOfSteps", "int", "numberOfSteps"),
                F("fillDirection", "enum", "fillDirection"),
            },
            ["UIPanel"] = new[] {
                F("depth", "int", "depth"), F("alpha", "float", "alpha"), F("clipping", "enum", "clipping"),
                F("clipRange", "Vector4", "clipRange"),
                C("widgetCount", "int", o => CountEnumerable(ReflectGet(o, "widgets"))),
            },
            ["UIGrid"] = new[] {
                F("cellSize", "Vector2", "cellSize"), F("maxPerLine", "int", "maxPerLine"),
                F("arrangement", "enum", "arrangement"), F("sorted", "bool", "sorted"),
                F("hideInactive", "bool", "hideInactive"),
            },
            ["UITable"] = new[] {
                F("columns", "int", "columns"), F("padding", "Vector2", "padding"), F("direction", "enum", "direction"),
            },
            ["UIAnchor"] = new[] {
                F("side", "enum", "side"), F("relativeOffset", "Vector2", "relativeOffset"),
                F("pixelOffset", "Vector2", "pixelOffset"), F("runOnlyOnce", "bool", "runOnlyOnce"),
            },
            ["UIRoot"] = new[] {
                F("scalingStyle", "enum", "scalingStyle"), F("activeHeight", "int", "activeHeight"),
                F("manualHeight", "int", "manualHeight"), F("minimumHeight", "int", "minimumHeight"),
                F("maximumHeight", "int", "maximumHeight"),
            },
            // ---------- FairyGUI (namespace FairyGUI; G-objects are plain C#, reached
            // via DisplayObject.gOwner — reflection-only, no compile-time reference) ----------
            ["FairyGUI.GObject"] = new[] {
                F("name", "string", "name"), F("xy", "Vector2", "xy"), F("width", "float", "width"),
                F("height", "float", "height"), F("scale", "Vector2", "scale"), F("visible", "bool", "visible"),
                F("touchable", "bool", "touchable"), F("sortingOrder", "int", "sortingOrder"),
            },
            ["FairyGUI.GImage"] = new[] {
                F("flip", "enum", "flip"), F("fillMethod", "enum", "fillMethod"),
                F("fillAmount", "float", "fillAmount"), F("textureSize", "Vector2", "textureSize"),
            },
            ["FairyGUI.GTextField"] = new[] {
                F("text", "string", "text"), F("color", "Color32", "color"),
                F("fontSize", "int", "textFormat.size"), F("align", "enum", "textFormat.align"),
            },
            ["FairyGUI.GLabel"] = new[] {
                F("title", "string", "title"), F("text", "string", "text"), F("icon", "string", "icon"),
            },
            ["FairyGUI.GButton"] = new[] {
                F("title", "string", "title"), F("selected", "bool", "selected"), F("mode", "enum", "mode"),
                F("changeStateOnClick", "bool", "changeStateOnClick"), F("icon", "string", "icon"),
            },
            ["FairyGUI.GList"] = new[] {
                F("itemCount", "int", "itemCount"), F("selectedIndex", "int", "selectedIndex"),
                F("layout", "enum", "layout"), F("lineGap", "int", "lineGap"), F("columnGap", "int", "columnGap"),
                F("defaultItem", "string", "defaultItem"),
            },
            ["FairyGUI.GSlider"] = new[] {
                F("value", "float", "value"), F("max", "float", "max"),
            },
            ["FairyGUI.GProgressBar"] = new[] {
                F("value", "float", "value"), F("max", "float", "max"), F("titleType", "enum", "titleType"),
            },
            ["FairyGUI.GLoader"] = new[] {
                F("url", "string", "url"),
            },
            ["FairyGUI.GComponent"] = new[] {
                F("numChildren", "int", "numChildren"), F("touchable", "bool", "touchable"),
                C("scrollable", "bool", o => ReflectGet(o, "scrollPane") != null),
            },
            ["FairyGUI.GGraph"] = new[] {
                F("color", "Color32", "color"),
            },
        };

        private static Dictionary<string, List<string>> BuildShortNameMap()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullname in Inspectors.Keys)
            {
                var shortName = fullname.Substring(fullname.LastIndexOf('.') + 1);
                if (!map.TryGetValue(shortName, out var list))
                {
                    list = new List<string>();
                    map[shortName] = list;
                }
                list.Add(fullname);
            }
            return map;
        }

        /// <summary>Extract fields for any object (Component or FairyGUI G-object).</summary>
        private static JObject Extract(object target)
        {
            var fields = new JObject
            {
                ["enabled"] = new JObject { ["type"] = "bool", ["value"] = Normalize(ReflectGet(target, "enabled")) },
                ["activeInHierarchy"] = new JObject { ["type"] = "bool", ["value"] = Normalize(ReflectGet(target, "gameObject.activeInHierarchy")) },
            };

            FieldSpec[] specs = null;
            string matchedBy = null;
            for (var t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                if (Inspectors.TryGetValue(t.FullName, out specs))
                {
                    matchedBy = t.FullName;
                    break;
                }
            }

            if (specs != null)
            {
                foreach (var spec in specs)
                {
                    object raw;
                    try
                    {
                        raw = spec.Custom != null ? spec.Custom(target) : ReflectGet(target, spec.Member);
                    }
                    catch { raw = null; }
                    fields[spec.Out] = new JObject { ["type"] = spec.Type, ["value"] = Normalize(raw) };
                }
            }
            else
            {
                // Reflection fallback for unknown types (custom MonoBehaviours):
                // declared public fields/properties of basic types, capped.
                fields.Remove("enabled"); // may not exist on plain objects
                fields.Remove("activeInHierarchy");
                foreach (var kv in ReflectFallback(target))
                    fields[kv.Key] = kv.Value;
            }
            if (specs != null && matchedBy != target.GetType().FullName)
            {
                fields["~matchedBy"] = matchedBy; // base-class entry used
            }
            return fields;
        }

        private const int FallbackMaxFields = 20;

        private static IEnumerable<KeyValuePair<string, JToken>> ReflectFallback(object target)
        {
            var t = target.GetType();
            var count = 0;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (count >= FallbackMaxFields) yield break;
                if (!IsFallbackType(f.FieldType)) continue;
                yield return FallbackEntry(f.Name, f.FieldType, SafeGet(() => f.GetValue(target)));
                count++;
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (count >= FallbackMaxFields) yield break;
                if (p.GetIndexParameters().Length > 0 || !IsFallbackType(p.PropertyType)) continue;
                yield return FallbackEntry(p.Name, p.PropertyType, SafeGet(() => p.GetValue(target, null)));
                count++;
            }
        }

        private static KeyValuePair<string, JToken> FallbackEntry(string name, Type type, object raw) =>
            new KeyValuePair<string, JToken>(name, new JObject
            {
                ["type"] = FallbackLabel(type),
                ["value"] = Normalize(raw),
            });

        private static object SafeGet(Func<object> f)
        {
            try { return f(); }
            catch { return null; }
        }

        private static bool IsFallbackType(Type t)
        {
            if (t.IsEnum) return true;
            if (t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) ||
                t == typeof(bool) || t == typeof(string) || t == typeof(byte)) return true;
            if (t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4) ||
                t == typeof(Color) || t == typeof(Color32) || t == typeof(Rect)) return true;
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return true; // reported as its name
            return false;
        }

        private static string FallbackLabel(Type t)
        {
            if (t.IsEnum) return "enum";
            if (typeof(UnityEngine.Object).IsAssignableFrom(t)) return "string";
            if (t == typeof(int) || t == typeof(long) || t == typeof(byte)) return "int";
            if (t == typeof(float) || t == typeof(double)) return "float";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "string";
            return t.Name;
        }

        // ---- reflection helpers ---------------------------------------------------

        private const BindingFlags InstancePublic = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>Walk a dotted member path (property or field at each step). Null-safe, exception-safe.</summary>
        internal static object ReflectGet(object o, string path)
        {
            var current = o;
            foreach (var part in path.Split('.'))
            {
                if (current == null) return null;
                var t = current.GetType();
                var p = t.GetProperty(part, InstancePublic);
                if (p != null)
                {
                    current = SafeGet(() => p.GetValue(current, null));
                    continue;
                }
                var f = t.GetField(part, InstancePublic);
                current = f != null ? SafeGet(() => f.GetValue(current)) : null;
            }
            return current;
        }

        internal static object ReflectCall(object o, string method, params object[] args)
        {
            if (o == null) return null;
            try
            {
                return o.GetType().GetMethod(method, InstancePublic)?.Invoke(o, args);
            }
            catch { return null; }
        }

        private static int CountEnumerable(object enumerable) =>
            enumerable is IEnumerable en ? CountEnumerableInner(en) : 0;

        private static int CountEnumerableInner(IEnumerable en)
        {
            var n = 0;
            foreach (var _ in en) n++;
            return n;
        }

        private static List<string> CollectTexts(object list, string member)
        {
            var result = new List<string>();
            if (list is IEnumerable en)
            {
                foreach (var item in en)
                {
                    if (item == null) continue;
                    result.Add(ReflectGet(item, member) as string);
                    if (result.Count >= 30) break;
                }
            }
            return result;
        }

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>Resolve a fullname across all loaded assemblies (null when unknown).</summary>
        internal static Type FindType(string fullname)
        {
            if (string.IsNullOrEmpty(fullname)) return null;
            if (TypeCache.TryGetValue(fullname, out var cached)) return cached;
            Type found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullname, false); }
                catch { /* skip unloadable assemblies */ }
                if (t != null) { found = t; break; }
            }
            TypeCache[fullname] = found;
            return found;
        }

        internal static object FindFairyGuiOwner(Component[] comps)
        {
            foreach (var c in comps)
            {
                if (c == null) continue;
                var fullname = c.GetType().FullName ?? "";
                if (!fullname.StartsWith("FairyGUI.", StringComparison.Ordinal)) continue;
                var owner = ReflectGet(c, "gOwner");
                if (owner != null && (owner.GetType().FullName ?? "").StartsWith("FairyGUI.G", StringComparison.Ordinal))
                    return owner;
            }
            return null;
        }

        // ---- value normalization --------------------------------------------------

        internal static JToken Normalize(object v)
        {
            try
            {
                switch (v)
                {
                    case null: return JValue.CreateNull();
                    case bool b: return b;
                    case string s: return Truncate(s, 500);
                    case byte _: case sbyte _: case short _: case ushort _:
                    case int i: case uint _: case long l: case ulong _:
                        return (long)Convert.ToInt64(v);
                    case float _: case double _: return Math.Round(Convert.ToDouble(v), 3);
                    case Enum e: return e.ToString();
                    case Color c: return ColorToHex(c);
                    case Color32 c32: return ColorToHex(c32);
                    case Vector2 v2: return new JObject { ["x"] = R(v2.x), ["y"] = R(v2.y) };
                    case Vector3 v3: return new JObject { ["x"] = R(v3.x), ["y"] = R(v3.y), ["z"] = R(v3.z) };
                    case Vector4 v4: return new JObject { ["x"] = R(v4.x), ["y"] = R(v4.y), ["z"] = R(v4.z), ["w"] = R(v4.w) };
                    case Rect r: return new JObject { ["x"] = R(r.x), ["y"] = R(r.y), ["width"] = R(r.width), ["height"] = R(r.height) };
                    case RectOffset ro: return new JObject { ["left"] = ro.left, ["right"] = ro.right, ["top"] = ro.top, ["bottom"] = ro.bottom };
                    case UnityEngine.Object uo: return uo ? uo.name : "(null)";
                }
                if (v is IEnumerable en)
                {
                    var arr = new JArray();
                    foreach (var item in en)
                    {
                        if (item == null) continue;
                        if (item is string || !(item is IEnumerable)) arr.Add(Normalize(item));
                        if (arr.Count >= 30) break;
                    }
                    return arr;
                }
                return Truncate(v.ToString(), 200);
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static string ColorToHex(Color32 c) =>
            $"#{c.r:X2}{c.g:X2}{c.b:X2}{c.a:X2}";

        private static double R(float f) => Math.Round(f, 3);

        private static string Truncate(string s, int max) =>
            string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
