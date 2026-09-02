using System;
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
                    "Click a GameObject by path, going through the real event pipeline (UGUI ExecuteEvents " +
                    "pointerDown/Up/Click, NGUI UICamera.Notify OnClick, or FairyGUI onClick dispatch — " +
                    "auto-detected). Use tap_screen to click by normalized screen coordinates instead.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"path\":{\"type\":\"string\",\"description\":\"node path from scene root, e.g. \\\"Canvas/Panel/Button\\\"\"}" +
                    "},\"required\":[\"path\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var path = ((string)task.Payload["path"])?.Trim();
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("path is required (\"/\"-separated from scene root)");

            var target = SceneTraverseTask.FindByPath(path, SceneTraverseTask.RuntimeScenes())
                ?? throw new ArgumentException($"no GameObject found at path \"{path}\"");
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
                    return Task.FromResult<object>(Result(path, "fairygui", true,
                        "GObject " + gOwner.GetType().Name));
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
                return Task.FromResult<object>(Result(path, "ugui", true, handler.name));
            }

            // NGUI: classic notify.
            var uiCameraType = ViewComponentTask.FindType("UICamera");
            var notify = uiCameraType?.GetMethod("Notify",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (notify != null)
            {
                SafeInvoke(() => notify.Invoke(null, new object[] { go, "OnClick", null }));
                return Task.FromResult<object>(Result(path, "ngui", true, go.name));
            }

            return Task.FromResult<object>(Result(path, "none", false,
                "no click handler found (no IPointerClickHandler up the chain, no FairyGUI owner, no UICamera); " +
                "check list_component"));
        }

        private static PointerEventData NewPointerData() =>
            new PointerEventData(EventSystem.current);

        private static JObject Result(string path, string framework, bool executed, string targetName) => new JObject
        {
            ["path"] = path,
            ["framework"] = framework,
            ["executed"] = executed,
            ["target"] = targetName,
        };

        private static void SafeInvoke(Action a)
        {
            try { a(); }
            catch { /* listener exceptions are the game's business, not ours */ }
        }
    }
}
