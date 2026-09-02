using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OmniDebugLink
{
    /// <summary>
    /// ui_click — trigger a click on a GameObject through the real event pipeline.
    ///
    /// Framework dispatch (auto-detected from the target's components):
    ///   UGUI:     PointerEventData + ExecuteEvents (pointerDown → pointerUp → pointerClick),
    ///             handler resolved up the parent chain like a real tap would.
    ///   NGUI:     UICamera.Notify(go, "OnClick", null) via reflection.
    ///   FairyGUI: gOwner.DispatchEvent("onClick") via the DisplayObject wrapper.
    /// </summary>
    internal static class UiClickTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "ui_click",
                Handle,
                description:
                    "Click a UI element through the real event pipeline (UGUI ExecuteEvents " +
                    "pointerDown/Up/Click, NGUI UICamera.Notify OnClick, or FairyGUI onClick dispatch — " +
                    "auto-detected). Locate the target either by its scene path or by the text it " +
                    "renders (text=\"Start\" finds the button whose label says Start and clicks it in " +
                    "one call; when several nodes match, pick with index). Use tap_screen to click by " +
                    "normalized screen coordinates instead.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root, e.g. \\\"Canvas/Panel/Button\\\"\"}," +
                    "\"text\":{\"type\":\"string\",\"description\":\"click the node rendering this text (substring, case-insensitive); its clickable ancestor is pressed\"}," +
                    "\"index\":{\"type\":\"integer\",\"default\":0,\"description\":\"which match to use when several nodes render the same text\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var p = task.Payload;
            var path = ((string)p["path"])?.Trim();
            var text = ((string)p["text"])?.Trim();
            var index = p["index"]?.Value<int>() ?? 0;

            Transform target;
            string locatedBy;
            if (!string.IsNullOrEmpty(path))
            {
                target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                    ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");
                locatedBy = "path";
            }
            else if (!string.IsNullOrEmpty(text))
            {
                var matches = FindObjectsTask.Query(null, null, text, null, true,
                    SceneTraverseTask.RuntimeScenes(), out _);
                if (matches.Count == 0)
                    throw new ArgumentException(
                        $"no active node renders text like \"{text}\" (matched against UGUI Text / " +
                        "TextMeshPro / InputField values); run find_objects with text to inspect " +
                        "what is on screen, or screenshot to look at it");
                if (index < 0 || index >= matches.Count)
                    throw new ArgumentException(
                        $"{matches.Count} nodes match text \"{text}\", index {index} is out of range. " +
                        "Paths: " + string.Join(", ", PathsOf(matches, 5)));
                target = matches[index].Node;
                path = SceneTraverseTask.BuildPath(target);
                locatedBy = "text";
            }
            else
            {
                throw new ArgumentException(
                    "provide 'path' or 'text' to locate the node to click" +
                    FindObjectsTask.DescribePayloadKeys(p));
            }

            var go = target.gameObject;
            var comps = go.GetComponents<Component>();

            // FairyGUI: dispatch on the logical G-object when present.
            var gOwner = ViewComponentTask.FindFairyGuiOwner(comps);
            if (gOwner != null)
            {
                var dispatch = gOwner.GetType().GetMethod("DispatchEvent",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (dispatch != null)
                {
                    SafeInvoke(() => dispatch.Invoke(gOwner, new object[] { "onClick", null }));
                    return Task.FromResult<object>(Result(path, locatedBy, "fairygui", true,
                        "GObject " + gOwner.GetType().Name, path));
                }
            }

            // UGUI: full pointer sequence on the resolved handler.
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
            if (handler != null)
            {
                var ped = NewPointerData();
                SafeInvoke(() => ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerDownHandler));
                SafeInvoke(() => ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerUpHandler));
                SafeInvoke(() => ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerClickHandler));
                // When text matched a label child, handler is the clickable ancestor (the
                // Button); report its path so follow-up tasks operate on the right node.
                return Task.FromResult<object>(Result(path, locatedBy, "ugui", true, handler.name,
                    SceneTraverseTask.BuildPath(handler.transform)));
            }

            // NGUI: classic notify.
            var uiCameraType = ViewComponentTask.FindType("UICamera");
            var notify = uiCameraType?.GetMethod("Notify",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (notify != null)
            {
                SafeInvoke(() => notify.Invoke(null, new object[] { go, "OnClick", null }));
                return Task.FromResult<object>(Result(path, locatedBy, "ngui", true, go.name, path));
            }

            return Task.FromResult<object>(Result(path, locatedBy, "none", false,
                "no click handler found (no IPointerClickHandler up the chain, no FairyGUI owner, no UICamera); " +
                "check list_component", null));
        }

        private static PointerEventData NewPointerData() =>
            new PointerEventData(EventSystem.current);

        private static IEnumerable<string> PathsOf(List<FindObjectsTask.Match> matches, int max)
        {
            var paths = new List<string>();
            for (var i = 0; i < matches.Count && i < max; i++)
                paths.Add(SceneTraverseTask.BuildPath(matches[i].Node));
            return paths;
        }

        private static JObject Result(string path, string locatedBy, string framework, bool executed,
            string targetName, string clicked)
        {
            var o = new JObject
            {
                ["path"] = path,
                ["located_by"] = locatedBy,
                ["framework"] = framework,
                ["executed"] = executed,
                ["target"] = targetName,
            };
            if (clicked != null) o["clicked"] = clicked;
            return o;
        }

        private static void SafeInvoke(Action a)
        {
            try { a(); }
            catch { /* listener exceptions are the game's business, not ours */ }
        }
    }
}
