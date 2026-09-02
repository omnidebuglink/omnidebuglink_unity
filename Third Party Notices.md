# Third Party Notices

This package bundles third-party open source software. Each entry lists the
upstream project, what was modified, and the full text of its license.

## UnityWebSocket

- Upstream: https://github.com/psygames/UnityWebSocket
- Vendored version: 2.7.0 (runtime portion only)
- Location: `Runtime/UnityWebSocket/`
- Modifications: trimmed to the runtime sources (Core, the WebGL and NoWebGL
  implementations, and the WebGL `WebSocket.jslib`). The editor settings
  window, `Settings.cs`, and the standalone assembly definition were removed;
  the remaining sources compile into the `OmniDebugLink` assembly directly.
  To avoid clashing with a copy of the upstream library already present in a
  host project, the C# namespace was renamed `UnityWebSocket` →
  `OmniDebugLink.UnityWebSocket`, the jslib file is
  `OmniDebugLinkWebSocket.jslib`, and every jslib entry point (and its
  `DllImport`) was prefixed with `Odl` (e.g. `OdlWebSocketAllocate`), along
  with the library/internal-manager JS objects. No behavioral changes were
  made to the library.
- License: MIT

```text
MIT License

Copyright (c) 2020 psy

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
