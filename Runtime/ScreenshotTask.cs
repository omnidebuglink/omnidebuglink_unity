using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OmniDebugLink
{
    /// <summary>
    /// screenshot — capture the game screen as a JPEG.
    ///
    /// payload:
    ///   maxSize (integer) cap for the longest edge, default 1280. Captured at the
    ///                    game resolution first, then downscaled only if larger.
    ///   quality  (integer) JPEG quality 10..100, default 75.
    ///   inline   (bool)   when true, keep the raw base64 in the text result
    ///                    (legacy path). Default false: the image travels in the
    ///                    __odl_file envelope and the MCP layer returns it as a
    ///                    native image content block (vision input, ~1.6k image
    ///                    tokens instead of ~30k base64 text tokens).
    ///
    /// All-Unity-built-in pipeline (no extra dependencies):
    ///   ScreenCapture.CaptureScreenshotAsTexture → Graphics.Blit downscale →
    ///   ImageConversion.EncodeToJPG → Convert.ToBase64String.
    /// Quality auto-degrades if the base64 payload would exceed the relay's
    /// ~900KB message limit.
    /// </summary>
    internal static class ScreenshotTask
    {
        private const int DefaultMaxSize = 1280;
        private const int DefaultQuality = 75;
        private const int MinQuality = 40;
        // Stay under the protocol's ~900KB single-message limit (base64 + JSON wrapper).
        private const int MaxBase64Length = 850_000;

        public static void Register(TaskRegistry registry)
        {
            registry.Register(
                "screenshot",
                Handle,
                description:
                    "Capture the game screen and return it as a JPEG image (reflects the most recent rendered frame). " +
                    "The image is delivered as a native image content block so you can see it directly. " +
                    "maxSize caps the longest edge in pixels (default 1280, captured at game resolution then downscaled proportionally); " +
                    "quality is JPEG quality 10-100 (default 75, auto-lowered if the result would be too large). " +
                    "The text part of the result reports width/height, original resolution and byte size.",
                payloadSchema:
                    "{\"type\":\"object\",\"properties\":{" +
                    "\"maxSize\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096,\"default\":1280,\"description\":\"cap for the longest edge in pixels\"}," +
                    "\"quality\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":100,\"default\":75,\"description\":\"JPEG quality\"}," +
                    "\"inline\":{\"type\":\"boolean\",\"default\":false,\"description\":\"legacy: return raw base64 in the text result instead of an image block\"}" +
                    "},\"additionalProperties\":false}");
        }

        private static async Task<object> Handle(OmniDebugLinkTask task)
        {
            var maxSize = Clamp(task.Payload["maxSize"]?.Value<int>() ?? DefaultMaxSize, 64, 4096);
            var quality = Clamp(task.Payload["quality"]?.Value<int>() ?? DefaultQuality, 10, 100);
            var inline = task.Payload["inline"]?.Value<bool>() ?? false;

            // CaptureScreenshotAsTexture only works after the frame has rendered;
            // our task runs in Update(), so hop to end-of-frame via a coroutine.
            var src = await OmniDebugLinkBehaviour.AtEndOfFrame(
                () => ScreenCapture.CaptureScreenshotAsTexture());
            if (src == null)
                throw new InvalidOperationException(
                    "CaptureScreenshotAsTexture returned null; screen capture may be unsupported in this context");
            try
            {
                var ow = src.width;
                var oh = src.height;
                var scale = Mathf.Min(1f, (float)maxSize / Mathf.Max(ow, oh));
                var w = Mathf.Max(1, Mathf.RoundToInt(ow * scale));
                var h = Mathf.Max(1, Mathf.RoundToInt(oh * scale));

                var ownsDst = false;
                Texture2D dst;
                if (scale >= 1f && src.format == TextureFormat.RGB24)
                {
                    dst = src;
                }
                else
                {
                    dst = DownscaleToRgb(src, w, h);
                    ownsDst = true;
                }
                try
                {
                    var bytes = ImageConversion.EncodeToJPG(dst, quality);
                    var b64 = Convert.ToBase64String(bytes);
                    while (b64.Length > MaxBase64Length && quality > MinQuality)
                    {
                        quality = Mathf.Max(MinQuality, quality - 15);
                        bytes = ImageConversion.EncodeToJPG(dst, quality);
                        b64 = Convert.ToBase64String(bytes);
                    }
                    if (b64.Length > MaxBase64Length)
                        throw new InvalidOperationException(
                            $"screenshot is {b64.Length} base64 chars even at quality {MinQuality}; " +
                            "retry with a lower maxSize");

                    var result = new JObject
                    {
                        ["format"] = "jpg",
                        ["quality"] = quality,
                        ["width"] = w,
                        ["height"] = h,
                        ["originalWidth"] = ow,
                        ["originalHeight"] = oh,
                        ["bytes"] = bytes.Length,
                    };
                    if (inline)
                    {
                        result["data"] = b64;
                    }
                    else
                    {
                        // File envelope: the MCP layer turns this into a native
                        // image content block (and keeps it out of the text).
                        result["__odl_file"] = new JObject
                        {
                            ["mime"] = "image/jpeg",
                            ["data"] = b64,
                        };
                    }
                    // NOTE: plain return — this is an async Task<object> method now.
                    // Returning Task.FromResult(result) here would compile but ship a
                    // nested Task as the value (serialized as {"Result": ...}), breaking
                    // the __odl_file envelope extraction on the server.
                    return result;
                }
                finally
                {
                    if (ownsDst) Destroy(dst);
                }
            }
            finally
            {
                Destroy(src);
            }
        }

        private static Texture2D DownscaleToRgb(Texture2D src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt); // bilinear filtering by default
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        private static void Destroy(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }
    }
}
