# Chrome Web Store developer preparation

- Extension name: **VOS Companion**; short name: **VOS**.
- Description: Browser companion for VOS, enabling voice-driven navigation and interaction with Chrome.
- Runtime source directory: `tools/VoiceOS.ChromeCompanion` (Manifest V3, version `0.2.0`).
- Native Messaging is required. The extension connects to `com.voiceos.chrome_companion`,
  which bridges to the locally running VoiceOS app. The ZIP does not install the native host.

## Create the upload ZIP

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File tools\VoiceOS.ChromeCompanion\package.ps1
```

Output: `artifacts/vos-companion.zip`. The script uses a fixed file order and ZIP
timestamps, replacing that output on each run. It includes only `manifest.json` at
the ZIP root, `background.js`, `content.js`, and the three `icons/icon*.png` files.
Tests, docs, tooling, native-host binaries, and other repository files are excluded.
Do not ZIP the entire source directory directly.

## Temporary icons

Canonical copies are `docs/branding/vos-companion-icon-16.png`,
`vos-companion-icon-48.png`, and `vos-companion-icon-128.png`. These are unchanged
copies of the supplied `bubble-letter-v-blue-icon-{16,48,128}.png` assets.
Runtime copies are `tools/VoiceOS.ChromeCompanion/icons/icon{16,48,128}.png`.
Both manifest icons and toolbar action icons use these runtime files.
The V artwork is temporary branding; verify its source and license before public release.

## Native host origins after the store ID is assigned

`tools/VoiceOS.ChromeNativeHost/install-host.ps1` accepts a development extension ID
via `-ExtensionId` and writes exactly one origin in its `$manifest.allowed_origins`
array. It registers this generated file:

`%LOCALAPPDATA%\VoiceOS\NativeMessaging\com.voiceos.chrome_companion.json`

The current local registration at preparation time allows
`chrome-extension://ipgenidfgpoekleeadohjinjbgoeghln/`.
No production ID has been assigned or hardcoded. After assignment, add
`chrome-extension://<WEB_STORE_EXTENSION_ID>/` to the generated manifest's
`allowed_origins` array, keeping the development origin for local testing.
Chrome supports multiple entries in this array. For durable registration, update
the `$manifest` object's `allowed_origins` assignment in `install-host.ps1` to
emit both actual origins; the current installer overwrites the file with one entry
each time it runs. Keep the host name, registry key, executable, and protocol unchanged.
The placeholder above is documentation only; do not put it in an installed manifest.

## Verify locally before upload

1. Run `node --test tools/VoiceOS.ChromeCompanion/companion.test.js` and create the ZIP.
   If process spawning is restricted, Node 24 can run the same tests with
   `node --test --test-isolation=none tools/VoiceOS.ChromeCompanion/companion.test.js`.
2. Extract the ZIP to a fresh directory. Confirm `manifest.json` is directly inside
   it and only the six runtime files listed above are present.
3. Open `chrome://extensions`, enable Developer mode, and load that extracted
   directory with **Load unpacked**. Check the VOS Companion name, description,
   icons, and absence of manifest/service-worker errors.
4. Copy this loaded extension's ID. It may differ from the existing development
   ID because the unpacked path changed. Ensure its origin is in the installed
   native manifest's `allowed_origins`, preserving other required origins. The
   existing installer can register it with `-ExtensionId`, but replaces the array.
5. With the native host built/registered and VoiceOS running, click **Reconnect
   VOS Companion** and verify a normal browser task and native connection. Inspect
   the extension service-worker console for errors. See the extension README for
   native-host build and registration commands.
6. After store assignment, verify Native Messaging with the store-installed
   extension and its actual production origin as well.
