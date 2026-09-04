# OmniDebugLink Unity SDK

OmniDebugLink client SDK for Unity (UPM package, `com.omnidebuglink.unity`):
connect a running Unity player (editor, device, standalone or WebGL) to the
OmniDebugLink relay so AI coding tools (via MCP) can inspect and drive the
scene — traverse the scene hierarchy, click/tap UI and world objects,
capture screenshots, read logs, inspect performance, and edit component
fields via reflection.

Works on WebGL builds as well as native platforms: the bundled UnityWebSocket
transport picks the browser WebSocket (jslib) on WebGL and
`ClientWebSocket` everywhere else, with all events dispatched on the main
thread.

## Install (UPM git URL)

Package Manager → Add package from git URL:

```
https://github.com/omnidebuglink/omnidebuglink_unity.git
```

Or pin a released tag:

```
https://github.com/omnidebuglink/omnidebuglink_unity.git#v0.7.0  # pin a tag for reproducibility; drop `#vX.Y.Z` to track main (bleeding edge)
```

## Quick start

```csharp
using OmniDebugLink;

public class Boot : MonoBehaviour
{
    void Start()
    {
        OmniDebugLink.Start("<clientToken>");
    }
}
```

- `OmniDebugLink.ActionsEnabled` (default true): master switch for write
  operations — false = read-only observation mode, announced with hello
- Custom tasks: `OmniDebugLink.Tasks.Register("my_task", handler,
  description, payloadSchema)` — registry changes re-announce capabilities
  automatically, zero server changes

**One token pair per device seat** — on close code 4000 (replaced by a
newer connection with the same token) the SDK stops reconnecting
permanently and logs a warning.

## Built-in tasks (21)

Read tasks:

| Task | What it does |
|---|---|
| `scene_traverse` | Full scene hierarchy dump (3000-node cap). Nodes that render text carry the live `text` value (UGUI Text / TextMeshPro / InputField), so the AI reads the labels straight from the tree |
| `find_objects` | Search by node name (substring or regex), **displayed text**, or component type; hits include center coordinates and `click_target` (nearest clickable ancestor) that feeds `ui_click` directly |
| `view_component` | One node in depth: UGUI + layout properties, NGUI / FairyGUI dictionaries, plus reflection-based field inspection for anything else |
| `list_component` | List the components attached to a node |
| `wait_for` | Poll until a path appears or a component field reaches a value (200 ms interval); timeouts return `found: false` instead of an error |
| `screenshot` | JPEG capture at end of frame via the `__odl_file` envelope; auto-compresses to fit the relay frame budget |
| `read_logs` | 1000-entry ring buffer of `Debug.Log` / warnings / exceptions with stack traces; level / contains / limit / since filters |
| `get_perf` | Mono + total memory, GC counts, fps and frame-time percentiles (p50/p95/p99), FrameTiming cpu/gpu times where available, battery |
| `prefs` | Read PlayerPrefs (get / list) |

Write tasks (all gated by `ActionsEnabled`):

| Task | What it does |
|---|---|
| `ui_click` | Delivered physically since v0.7.0: raycasts from the target's center through the EventSystem and executes pointerDown/Up/Click on the topmost clickable object — tutorial overlays and intercept layers receive the click exactly like a real tap. Locates by path or by the text it renders (`text="Start"` clicks the button whose label says Start), `index` disambiguates multiple matches |
| `tap_screen` | Tap at normalized 0-1 coordinates (bottom-left origin), through the same raycast pipeline |
| `swipe` | Drag between two points over a duration, per-frame deltas — ScrollRect inertia works |
| `long_press` | Press, hold (default 800 ms), release |
| `input_text` | Type into UGUI InputField / TMP, firing the change events |
| `set_component` | Reflection-based write of any component field |
| `set_active` | Toggle a GameObject's active state |
| `set_time_scale` | Set `Time.timeScale` (slow-mo / freeze for inspection) |
| `send_key` | Soft-dispatch UGUI submit/cancel events (hardware keys cannot be injected in a running player) |
| `prefs` | Write / delete PlayerPrefs |

Basics: `echo` / `ping` / `get_stats`

Coordinate convention: **normalized 0-1, bottom-left origin** (Unity's own
native convention — note this differs from every other client, which is
top-left; task descriptions state it explicitly).

## License

Released under the [MIT License](LICENSE). This package bundles a trimmed
copy of [UnityWebSocket](https://github.com/psygames/UnityWebSocket) (MIT) in
`Runtime/UnityWebSocket` — see
[Third Party Notices.md](Third%20Party%20Notices.md) for attribution and the
full license text.
