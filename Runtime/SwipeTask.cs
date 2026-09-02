using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OmniDebugLink
{
    /// <summary>
    /// swipe / long_press — gesture-level input through the real UGUI event
    /// pipeline. Coordinates follow tap_screen conventions: floats in [0,1],
    /// origin BOTTOM-LEFT. Swipe dispatches pointerDown → per-frame drag (with
    /// delta, so ScrollRect/list inertia works) → pointerUp/endDrag, spread
    /// across frames over the requested duration via a coroutine.
    /// </summary>
    internal static class SwipeTask
    {
        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "swipe",
                HandleSwipe,
                description:
                    "Simulate a swipe/drag on the screen through the UGUI event pipeline (scroll lists, " +
                    "sliders, drag-and-drop). Coordinates are floats 0..1, origin BOTTOM-LEFT (same as " +
                    "tap_screen; screenshot pixel (px,py) in W×H → x=(px+0.5)/W, y=1-(py+0.5)/H). " +
                    "duration_ms controls the gesture speed. Returns the hit object and both endpoints.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"x1\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"y1\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
                    "\"x2\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"y2\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
                    "\"duration_ms\":{\"type\":\"integer\",\"minimum\":50,\"maximum\":3000,\"default\":300}" +
                    "},\"required\":[\"x1\",\"y1\",\"x2\",\"y2\"],\"additionalProperties\":false}");

            registry.Register(
                "long_press",
                HandleLongPress,
                description:
                    "Simulate a long press at one screen point (pointer down, hold duration_ms, pointer up; " +
                    "no click event). Coordinates are floats 0..1, origin BOTTOM-LEFT, like tap_screen.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"x\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"y\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1}," +
                    "\"duration_ms\":{\"type\":\"integer\",\"minimum\":200,\"maximum\":5000,\"default\":800}" +
                    "},\"required\":[\"x\",\"y\"],\"additionalProperties\":false}");
        }

        private static Task<object> HandleSwipe(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var p = task.Payload;
            var x1 = p["x1"]?.Value<float>() ?? -1f; var y1 = p["y1"]?.Value<float>() ?? -1f;
            var x2 = p["x2"]?.Value<float>() ?? -1f; var y2 = p["y2"]?.Value<float>() ?? -1f;
            if (!In01(x1) || !In01(y1) || !In01(x2) || !In01(y2))
                throw new ArgumentException("x1/y1/x2/y2 must be floats in [0,1] (origin bottom-left)");
            var durationMs = Math.Max(50, Math.Min(3000, p["duration_ms"]?.Value<int>() ?? 300));

            var es = RequireEventSystem();
            var start = ToPixels(x1, y1);
            var end = ToPixels(x2, y2);
            var (hitGo, hitNames) = RaycastAt(es, start);

            var result = new JObject
            {
                ["x1"] = x1, ["y1"] = y1, ["x2"] = x2, ["y2"] = y2,
                ["duration_ms"] = durationMs,
                ["hit"] = hitGo != null,
                ["raycastHits"] = hitNames,
            };
            if (hitGo == null)
            {
                result["executed"] = false;
                return Task.FromResult<object>(result);
            }

            result["dragPath"] = SceneTraverseTask.BuildPath(DragTarget(hitGo).transform);
            return RunCoroutine(result, SwipeRoutine(es, hitGo, start, end, durationMs));
        }

        private static Task<object> HandleLongPress(OmniDebugLinkTask task)
        {
            OmniDebugLink.EnsureActionsEnabled();
            var p = task.Payload;
            var x = p["x"]?.Value<float>() ?? -1f;
            var y = p["y"]?.Value<float>() ?? -1f;
            if (!In01(x) || !In01(y))
                throw new ArgumentException("x and y must be floats in [0,1] (origin bottom-left)");
            var durationMs = Math.Max(200, Math.Min(5000, p["duration_ms"]?.Value<int>() ?? 800));

            var es = RequireEventSystem();
            var pos = ToPixels(x, y);
            var (hitGo, hitNames) = RaycastAt(es, pos);

            var result = new JObject
            {
                ["x"] = x, ["y"] = y, ["duration_ms"] = durationMs,
                ["hit"] = hitGo != null,
                ["raycastHits"] = hitNames,
            };
            if (hitGo == null)
            {
                result["executed"] = false;
                return Task.FromResult<object>(result);
            }

            result["pressPath"] = SceneTraverseTask.BuildPath(hitGo.transform);
            return RunCoroutine(result, LongPressRoutine(es, hitGo, pos, durationMs));
        }

        // ---- gesture coroutines -------------------------------------------------

        private static IEnumerator SwipeRoutine(
            EventSystem es, GameObject hitGo, Vector2 start, Vector2 end, float durationMs)
        {
            var dragTarget = DragTarget(hitGo);
            var ped = NewPointer(es, start);
            SafeExecute(hitGo, ped, ExecuteEvents.pointerDownHandler);

            // At least one intermediate frame even for short swipes, so
            // velocity-based receivers (ScrollRect inertia) see real movement.
            var elapsed = 0f;
            Vector2 last = start;
            do
            {
                yield return null;
                elapsed += FrameDeltaMs();
                var t = Math.Min(1f, elapsed / durationMs);
                var pos = Vector2.Lerp(start, end, t);
                ped.delta = pos - last;
                ped.position = pos;
                last = pos;
                SafeExecute(dragTarget, ped, ExecuteEvents.dragHandler);
            } while (elapsed < durationMs);

            ped.delta = end - last;
            ped.position = end;
            SafeExecute(dragTarget, ped, ExecuteEvents.dragHandler);
            SafeExecute(dragTarget, ped, ExecuteEvents.endDragHandler);
            SafeExecute(hitGo, ped, ExecuteEvents.pointerUpHandler);
        }

        private static IEnumerator LongPressRoutine(
            EventSystem es, GameObject hitGo, Vector2 pos, float durationMs)
        {
            var ped = NewPointer(es, pos);
            SafeExecute(hitGo, ped, ExecuteEvents.pointerDownHandler);
            var elapsed = 0f;
            do
            {
                yield return null;
                elapsed += FrameDeltaMs();
            } while (elapsed < durationMs);
            // Deliberately NO pointerClickHandler: a click after a long hold is
            // not what long-press UIs expect.
            SafeExecute(hitGo, ped, ExecuteEvents.pointerUpHandler);
        }

        // ---- helpers --------------------------------------------------------------

        private static GameObject DragTarget(GameObject hitGo) =>
            ExecuteEvents.GetEventHandler<IDragHandler>(hitGo) ?? hitGo;

        /// <summary>Frame delta floored at 1ms so time-based coroutines also terminate
        /// when drained synchronously (no player loop) or on a zero-delta frame.</summary>
        private static float FrameDeltaMs() => Math.Max(1f, Time.unscaledDeltaTime * 1000f);

        private static PointerEventData NewPointer(EventSystem es, Vector2 pos) => new(es)
        {
            position = pos,
            button = PointerEventData.InputButton.Left,
        };

        /// <summary>ExecuteHierarchy so parent handlers (ScrollRect on a root) receive the event.</summary>
        private static void SafeExecute(GameObject target, PointerEventData ped,
            ExecuteEvents.EventFunction<IPointerDownHandler> func)
        { try { ExecuteEvents.ExecuteHierarchy(target, ped, func); } catch { } }

        private static void SafeExecute(GameObject target, PointerEventData ped,
            ExecuteEvents.EventFunction<IPointerUpHandler> func)
        { try { ExecuteEvents.ExecuteHierarchy(target, ped, func); } catch { } }

        private static void SafeExecute(GameObject target, PointerEventData ped,
            ExecuteEvents.EventFunction<IDragHandler> func)
        { try { ExecuteEvents.Execute(target, ped, func); } catch { } }

        private static void SafeExecute(GameObject target, PointerEventData ped,
            ExecuteEvents.EventFunction<IEndDragHandler> func)
        { try { ExecuteEvents.Execute(target, ped, func); } catch { } }

        private static (GameObject go, JArray hitNames) RaycastAt(EventSystem es, Vector2 pos)
        {
            var ped = new PointerEventData(es) { position = pos, button = PointerEventData.InputButton.Left };
            var hits = new List<RaycastResult>();
            es.RaycastAll(ped, hits);
            var names = new JArray();
            for (var i = 0; i < hits.Count && i < 5; i++)
                if (hits[i].gameObject != null) names.Add(hits[i].gameObject.name);
            return (hits.Count > 0 ? hits[0].gameObject : null, names);
        }

        private static EventSystem RequireEventSystem()
        {
            var es = EventSystem.current;
            if (es == null)
                throw new InvalidOperationException("no active EventSystem in this scene (UGUI required)");
            return es;
        }

        private static bool In01(float v) => v >= 0f && v <= 1f;

        private static Vector2 ToPixels(float x, float y) => new(x * Screen.width, y * Screen.height);

        /// <summary>Run the gesture coroutine on the behaviour; mark executed=true when done.</summary>
        internal static Task<object> RunCoroutine(JObject result, IEnumerator routine)
        {
            var runner = OmniDebugLinkBehaviour.Current;
            if (runner == null)
            {
                // No player loop to spread frames over: run synchronously.
                while (routine.MoveNext()) { }
                result["executed"] = true;
                return Task.FromResult<object>(result);
            }
            var tcs = new TaskCompletionSource<object>();
            runner.StartCoroutine(WrapRoutine(routine, result, tcs));
            return tcs.Task;
        }

        private static IEnumerator WrapRoutine(IEnumerator routine, JObject result, TaskCompletionSource<object> tcs)
        {
            while (true)
            {
                bool moved;
                try { moved = routine.MoveNext(); }
                catch (Exception e) { tcs.SetException(e); yield break; }
                if (!moved) break;
                yield return routine.Current;
            }
            result["executed"] = true;
            tcs.SetResult(result);
        }
    }
}
