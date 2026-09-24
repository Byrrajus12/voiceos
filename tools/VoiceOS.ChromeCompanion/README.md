# VoiceOS Chrome Companion — Alpha 0.1 proof

This directory is a thin, unpacked Manifest V3 extension. It has no model, API key,
planner, site-specific workflow, remote-debugging access, or Windows UI Automation
dependency. VoiceOS owns the proof sequence; the extension only creates its task tab,
observes DOM semantics, validates scoped actions, and executes `CLICK` or `TYPE_TEXT`.

The extension action (toolbar icon) is the explicit developer gesture that opens the
Native Messaging port. Chrome requires the extension side to initiate that port. Chrome
then starts `VoiceOS.ChromeNativeHost.exe`, which bridges length-prefixed JSON over a
same-machine named pipe to the already-running VoiceOS process. VoiceOS sends the first
browser command over that established connection.

## One-time developer setup

1. Build the app and the native shim from the repository root:

   ```powershell
   dotnet build src\VoiceOS\VoiceOS.csproj
   dotnet build tools\VoiceOS.ChromeNativeHost\VoiceOS.ChromeNativeHost.csproj
   ```

2. In normal Chrome, open `chrome://extensions`, enable **Developer mode**, choose
   **Load unpacked**, and select `tools\VoiceOS.ChromeCompanion`.
3. Copy the extension ID displayed by Chrome.
4. Register the native host for the current Windows user:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools\VoiceOS.ChromeNativeHost\install-host.ps1 -ExtensionId <32-character-extension-id>
   ```

The script writes the Chrome host manifest to
`%LOCALAPPDATA%\VoiceOS\NativeMessaging\com.voiceos.chrome_companion.json` and sets the
default value of this current-user registry key to that absolute manifest path:

`HKCU\Software\Google\Chrome\NativeMessagingHosts\com.voiceos.chrome_companion`

The manifest contains the absolute native-host executable path and exactly one
`allowed_origins` entry: `chrome-extension://<extension-id>/`. No administrator access
is required. Re-run the script after changing build configuration or extension ID.

To remove only the registry registration:

```powershell
powershell -ExecutionPolicy Bypass -File tools\VoiceOS.ChromeNativeHost\uninstall-host.ps1
```

## Physical validation

1. Start the user's normal Chrome profile and leave two recognizable existing tabs open.
   The profile may stay signed in; do not enable remote debugging.
2. Start VoiceOS from a terminal so proof logs remain visible:

   ```powershell
   dotnet run --project src\VoiceOS\VoiceOS.csproj --no-build
   ```

3. Note the current tab count, pin the VoiceOS extension if desired, then click its
   toolbar action once.
4. Confirm exactly one new Google task tab appears and the pre-existing tabs are
   unchanged.
5. In the VoiceOS console, confirm the initial snapshot logs a real editable DOM control
   and the selected observation-scoped `eN` ref.
6. Watch the literal text `VoiceOS Chrome companion proof alpha 0.1` appear in the search
   control. No Enter key is synthesized.
7. Confirm the post-type snapshot contains that exact current value and has a new
   revision.
8. Watch the extension click the newly observed Google Search control. Confirm the final
   snapshot URL/title/text represents results.
9. Confirm the normal account/cookies remain present in the task tab. Cookie values are
   never requested or logged by the proof.
10. Confirm the VoiceOS log reports proof completion and disconnect. Chrome and the
    result tab must remain open.

Set `VOICEOS_CHROME_PROOF_URL` or `VOICEOS_CHROME_PROOF_TEXT` before starting VoiceOS to
override the two proof literals. The deterministic control selection is intentionally
generic but the completion check expects a visible search/text field and a visible
button named Search, Google Search, or Submit.

## Requested Chrome permissions

- `nativeMessaging`: connect to the allow-listed local VoiceOS host.
- `tabs`: create one task tab and wait for its navigation state.
- `scripting`: inject the isolated-world DOM observer into only the owned task tab.
- `http://*/*`, `https://*/*`: observe and act on ordinary web task pages after the task
  tab navigates. Chrome-internal pages and other restricted schemes are not allowed.

## Protocol

All Native Messaging and internal pipe frames are UTF-8 JSON prefixed by a four-byte
little-endian length. The proof caps every frame at 1 MiB.

Commands are `{type:"command", id, command, payload}`. Responses are
`{type:"response", id, ok, result}` or include `{error:{code,message}}`.

- `OPEN_TASK_TAB {url}` creates exactly one owned HTTP(S) tab and returns `snapshot`.
- `OBSERVE {tabId}` accepts only a tab created on the current extension connection.
- `ACT {tabId,revision,elementRef,action,text?}` accepts only `CLICK` or `TYPE_TEXT` and
  always returns a newly observed `snapshot`. `TYPE_TEXT` never submits or presses Enter.

A snapshot is bounded to 100 visible controls and 3,500 characters of visible body text:

```json
{
  "tabId": 123,
  "revision": "observation nonce",
  "url": "https://www.google.com/",
  "title": "Google",
  "visibleText": "...",
  "truncated": false,
  "viewport": { "width": 1280, "height": 720, "scrollX": 0, "scrollY": 0 },
  "elements": [{
    "ref": "e1",
    "role": "searchbox",
    "name": "Search",
    "enabled": true,
    "editable": true,
    "value": "",
    "href": null,
    "geometry": { "x": 100, "y": 200, "width": 500, "height": 40, "inViewport": true },
    "context": "..."
  }]
}
```

Refs are valid only for the exact `revision` that returned them. The content script keeps
the ref-to-element map privately; VoiceOS cannot supply selectors or JavaScript. Before
acting it rechecks revision, connection, geometry, enabled state, and action compatibility.
Text is literal and capped at 2,000 characters. Navigation creates a new document and a
new injected observer.

## Proof limitations

- It handles only the top frame; cross-origin iframe controls and shadow-root traversal
  are not included.
- Visibility is geometry/style based, not a complete occlusion or hit-test model.
- Accessible-name calculation covers common HTML/ARIA sources but is not the full browser
  accessibility-name algorithm.
- DOM `.click()` and native value setters work on ordinary pages but are not trusted-user
  gestures; file pickers, permission prompts, browser chrome, and gesture-gated controls
  remain out of scope.
- A changed Google locale, consent interstitial, bot check, or page markup can prevent the
  intentionally small deterministic selector in VoiceOS from finding the proof controls.
- The in-memory owned-tab set and refs intentionally disappear when the MV3 worker/native
  connection restarts. Existing tabs remain open and must be deliberately adopted by a
  future product protocol if desired.
- The named pipe is an Alpha transport boundary for the current desktop user, not a
  hardened authenticated IPC design.

No observed limitation in this basic proof requires `chrome.debugger`; ordinary
content-script DOM mechanisms cover its snapshot, typing, and click scope.
