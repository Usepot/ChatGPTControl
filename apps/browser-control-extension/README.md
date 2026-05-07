# Portable Codex Browser Control Extension

Chrome Manifest V3 extension that connects to the Portable Codex relay as a browser device. It handles `browser_*` tool requests over the existing `/ws/device` WebSocket channel.

## What it can do

- `browser_get_state` returns the active tab URL/title, viewport, redacted HTML, optional visible text, and an actionable element list with selectors and rectangles.
- `browser_click` clicks by CSS selector, visible text/label, or viewport coordinates.
- `browser_fill` fills inputs, textareas, selects, and contenteditable elements.
- `browser_keypress` dispatches keyboard events and handles simple printable text insertion.
- `browser_navigate`, `browser_back`, `browser_forward`, and `browser_reload` control tab navigation.
- `browser_screenshot` captures the visible tab as inline image data.
- `browser_eval` runs page JavaScript only when enabled in the extension and acknowledged per call with `allowUnsafeScript: true`.

## Relay setup

Add a browser device credential to your relay environment. Use a distinct browser device ID so the extension does not replace the desktop companion WebSocket connection.

```powershell
$env:RELAY_DEVICE_TOKENS="desktop-device:desktop-device-token,browser:browser-device-token"
```

Keep your existing `RELAY_API_KEYS` default device pointed at the desktop companion if you still want workspace tools to default there. For browser tools, pass `deviceId: "browser"`, or configure a separate API principal whose default device is `browser`.

## Install locally

1. Open Chrome and go to `chrome://extensions`.
2. Enable **Developer mode**.
3. Click **Load unpacked**.
4. Select `apps/browser-control-extension`.
5. Open the extension popup and set:
   - Relay URL, for example `http://127.0.0.1:8787`
   - Browser device ID, for example `browser`
   - Device token matching the relay's `RELAY_DEVICE_TOKENS` entry
6. Click **Save & Connect**.
7. Check the relay `/health` endpoint; the browser device should appear in `connectedDevices`.

## Safety notes

The extension can read and interact with web pages where Chrome allows extension scripting. By default, form values are redacted from returned HTML. The extension refuses to inspect browser-internal pages such as `chrome://` and does not expose stored configuration secrets in the popup or logs. `browser_eval` is intentionally gated by both a persistent extension setting and a per-request flag.
