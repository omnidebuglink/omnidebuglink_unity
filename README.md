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

## Built-in tasks (20)

Read: `scene_traverse` (scene hierarchy dump) / `find_objects` /
`view_component` (reflection-based field inspection) / `wait_for` /
`screenshot` / `read_logs` / `get_perf` / `get_state` / `prefs`
(PlayerPrefs)
Write: `ui_click` / `tap_screen` / `swipe` / `long_press` / `input_text` /
`set_component` (reflection write) / `send_key` (soft dispatch) /
`prefs` (set/delete)
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
