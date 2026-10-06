# VOS Companion — browser Alpha

VOS Companion is a Manifest V3 extension for VoiceOS browser interaction in the user's normal Chrome profile. It has no model, API key, planner, remote-debugging access, or Windows UI Automation dependency. VoiceOS chooses the scope and executes a bounded semantic plan; the companion supplies DOM observations and validates offered actions.

Chrome initiates a Native Messaging connection to `VoiceOS.ChromeNativeHost.exe`, which bridges length-prefixed JSON over a same-machine named pipe to VoiceOS. The extension reconnects automatically; its toolbar action can request reconnection.

For Chrome Web Store packaging, icons, and native-host origin setup, see [the publishing guide](../../docs/chrome-web-store.md). From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\VoiceOS.ChromeCompanion\package.ps1
```

This packages only runtime files into `artifacts/vos-companion.zip`. Tests, documentation, and packaging tooling are excluded.

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

## Validation

Start VoiceOS and normal Chrome after setup, then check that the companion is connected. Use the configured command activation key to try a scoped search/open task, a section reveal, and voice Back/Forward. Existing user tabs should be preserved unless selected for interaction. Check the choice panel on a genuinely ambiguous result and confirm only the selected item is activated after resume.

The original Alpha 0.1 deterministic search proof is historical; its proof text and environment variables are not the current product workflow. Automated protocol regressions run separately:

```powershell
node --test tools/VoiceOS.ChromeCompanion/companion.test.js
```

Automated tests do not replace microphone, foreground-window, or live-site validation.

## Requested Chrome permissions

- `nativeMessaging`: connect to the allow-listed local VoiceOS host.
- `tabs`: inventory, create, select, focus, and manage task tabs.
- `webNavigation`: observe navigation and history signals.
- `scripting`: inject the DOM observer into the selected task/adopted surface.
- `alarms`: support reconnection.
- `storage`: retain task ownership and opener lineage in Chrome session storage across worker restarts.
- `http://*/*`, `https://*/*`: observe and act on ordinary web pages after scope selection. Chrome-internal and other restricted schemes are excluded.

## Protocol

Native Messaging and internal pipe frames are UTF-8 JSON prefixed by a four-byte little-endian length, bounded to 1 MiB. Commands are `{type:"command", id, command, payload}`; responses are `{type:"response", id, ok, result}` or carry `{error:{code,message}}`.

- `LIST_TABS` supplies lightweight inventory for scope resolution.
- `CREATE_NEW_TAB` and `OPEN_TASK_TAB` create session-owned tabs; `OPEN_TASK_TAB` returns a prepared snapshot.
- `SELECT_TAB` checks the expected URL and active-tab requirement and can adopt an existing user tab for a session.
- `FOCUS_TASK_TAB` and `OBSERVE` operate only within the selected session.
- `ACT` supports `CLICK`, `REPLACE_TEXT`, `INSERT_TEXT`, `SUBMIT`, `SCROLL`, `BACK`, and `FORWARD`. Element actions require a current observation reference. `SUBMIT` applies Enter to an offered editable control; text replacement/insertion alone does not submit.
- `CLOSE_TASK_TAB` closes owned task tabs and refuses to close adopted user tabs.

Snapshots include tab/session identity, revision, URL, title, visible text, viewport/document height, controls, sections, and available navigation facts. Observations are bounded to 100 controls, 40 sections, and 3,500 characters of visible text. Controls expose grounded names, values, addresses, geometry, nearby context, and structural facts such as list position. Section references allow reveal scrolling without activation.

References are valid only for their exact revision. The content script keeps the reference-to-element map privately; VoiceOS does not supply selectors or JavaScript. Actions recheck scope, revision, element connection, visibility, enabled state, and compatibility, then return a new observation. Ownership persists through worker restarts using session storage; DOM references still require fresh observation.

## Alpha limitations

- Only the top document is observed; cross-origin iframe controls and shadow-root traversal are not included.
- Visibility uses geometry/style checks, not a complete occlusion model. Accessible names cover common HTML/ARIA sources, not the full accessibility-name algorithm.
- DOM clicks and value setters are synthetic. File pickers, browser chrome, permission prompts, and trusted-user-gesture controls remain out of scope.
- Authentication, CAPTCHA, consent, changing markup, or model interpretation can prevent completion.
- The named pipe is an Alpha transport boundary for the current desktop user, not a hardened authenticated IPC design.

## Back / Forward and Chrome's skippable history entries

VoiceOS clicks with synthetic `element.click()`, which carries no trusted user activation.
Chrome's history-manipulation intervention can therefore treat the entries those clicks create
as skippable: `chrome.tabs.goBack()` (and possibly the toolbar Back button) may skip or reject
them even though the session history contains them. VoiceOS does not try to change that.
Its own `BACK`/`FORWARD` actions run `history.back()`/`history.forward()` in the page, which
walks the real session history (including VoiceOS-generated entries and the user's earlier
pages in an adopted tab), and succeed only on an observed traversal. `chrome.tabs.goBack()`
is used only when the page cannot be scripted. Only a genuine new child tab (opened by a
VoiceOS action) with no same-tab history falls back to returning to its opener.

## Known Alpha limitation: Chrome's toolbar Back button

VOS "Go back" / "Go forward" call the page's own `history.back()` / `history.forward()`,
which work in adopted user tabs and VOS-owned tabs alike. Chromium's history-manipulation
intervention marks session-history entries created by scripted (untrusted, synthetic)
clicks as skippable for its own Back/Forward UI. After VOS has clicked through a site, the
toolbar Back button can therefore look enabled yet do nothing, while the long-press history
menu still lists the entries. This is Chrome behavior, not lost history; VOS voice Back is
unaffected. The Alpha does not work around it: no `chrome.debugger`, CDP, OS-level mouse
input or OCR is used for this purpose.
