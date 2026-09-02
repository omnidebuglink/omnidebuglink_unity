using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OmniDebugLink
{
    /// <summary>
    /// tap_screen — simulated tap at normalized screen coordinates, through the
    /// real UGUI raycast pipeline.
    ///
    /// Coordinates are floats in [0,1], origin at the BOTTOM-LEFT of the screen
    /// (Unity convention), so they are independent of resolution and of the
    /// screenshot's maxSize downscaling (which is always proportional).
    ///
    /// Mapping from a returned screenshot (images have origin at the TOP-left):
    ///   pixel (px, py) in an image of W×H  →  x = (px + 0.5) / W,  y = 1 - (py + 0.5) / H
    /// </summary>
    internal static class TapScreenTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "tap_screen",
                Handle,
                description:
                    "Simulate a tap on the screen (UGUI raycast pipeline, like a real touch). " +
                    "x and y are floats 0..1, origin at the BOTTOM-LEFT corner of the screen. " +
                    "For a pixel (px,py) in a returned screenshot of size W×H (image origin TOP-left): " +
                    "x=(px+0.5)/W, y=1-(py+0.5)/H. " +
                    "Returns the hit object, resolved pixel coords and the clicked node path.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"x\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1,\"description\":\"horizontal position, 0=left, 1=right\"}," +
                    "\"y\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1,\"description\":\"vertical position, 0=bottom, 1=top (Unity screen convention)\"}" +
                    "},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}");
        }

        private static Task<object> Handle(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var x = task.Payload["x"]?.Value<float>() ?? -1f;
            var y = task.Payload["y"]?.Value<float>() ?? -1f;
            if (x < 0f || x > 1f || y < 0f || y > 1f)
                throw new ArgumentException("x and y must be floats in [0,1] (origin bottom-left)");

            var es = EventSystem.current;
            if (es == null)
                throw new InvalidOperationException("no active EventSystem in this scene (UGUI required)");

            var screenPos = new Vector2(x * Screen.width, y * Screen.height);
            var ped = new PointerEventData(es)
            {
                position = screenPos,
                button = PointerEventData.InputButton.Left,
            };
            var hits = new List<RaycastResult>();
            es.RaycastAll(ped, hits);

            var result = new JObject
            {
                ["x"] = x,
                ["y"] = y,
                // Echo the exact pixel coordinates used, so callers can reconcile
                // against RectTransform math / screenshots and pinpoint whether a
                // miss comes from coordinate conversion or from the raycast itself.
                ["px"] = screenPos.x,
                ["py"] = screenPos.y,
                ["screen"] = new JObject { ["width"] = Screen.width, ["height"] = Screen.height },
            };

            if (hits.Count == 0)
            {
                result["hit"] = false;
                result["executed"] = false;
                return Task.FromResult<object>(result);
            }

            var hitNames = new JArray();
            for (var i = 0; i < hits.Count && i < 5; i++)
            {
                if (hits[i].gameObject != null) hitNames.Add(hits[i].gameObject.name);
            }
            result["hit"] = true;
            result["raycastHits"] = hitNames;

            var hitGo = hits[0].gameObject;
            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitGo) ?? hitGo;

            try { ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerDownHandler); } catch { }
            try { ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerUpHandler); } catch { }
            try { ExecuteEvents.Execute(handler, ped, ExecuteEvents.pointerClickHandler); } catch { }

            result["executed"] = true;
            result["clickedPath"] = SceneTraverseTask.BuildPath(handler.transform);
            result["clickedName"] = handler.name;
            return Task.FromResult<object>(result);
        }
    }
}
