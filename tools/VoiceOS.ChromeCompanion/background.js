const NATIVE_HOST = "com.voiceos.chrome_companion";
const PROTOCOL_VERSION = 1;
const RECONNECT_ALARM = "voiceos-native-reconnect";
const ownedTaskTabs = new Map(); // tabId -> opaque VoiceOS session id

let nativePort = null;
let connecting = false;

const ownedTabsReady = restoreOwnedTabs();
void ensureNativeConnection("worker-start");
chrome.runtime.onStartup.addListener(() => void ensureNativeConnection("chrome-startup"));
chrome.runtime.onInstalled.addListener(() => void ensureNativeConnection("extension-installed"));
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === RECONNECT_ALARM && !nativePort) void ensureNativeConnection("alarm");
});
chrome.tabs.onRemoved.addListener((tabId) => {
  if (ownedTaskTabs.has(tabId)) {
    traceTask(tabId, "tab-removed");
    ownedTaskTabs.delete(tabId);
    void persistOwnedTabs();
  }
});
chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
  await ownedTabsReady;
  if (!ownedTaskTabs.has(tabId) || (!changeInfo.url && !changeInfo.status)) return;
  traceTask(tabId, "tab-updated", { status: changeInfo.status,
    origin: safeOrigin(changeInfo.url ?? tab.url) });
});
chrome.tabs.onActivated.addListener(async ({ tabId, windowId }) => {
  await ownedTabsReady;
  if (ownedTaskTabs.has(tabId)) traceTask(tabId, "tab-activated", { windowId });
});
chrome.windows.onFocusChanged.addListener(async (windowId) => {
  await ownedTabsReady;
  if (windowId < 0) return;
  const active = await chrome.tabs.query({ windowId, active: true });
  for (const tab of active) if (ownedTaskTabs.has(tab.id)) traceTask(tab.id, "window-focused", { windowId });
});
for (const [event, source] of [
  [chrome.webNavigation.onBeforeNavigate, "nav-before"],
  [chrome.webNavigation.onCommitted, "nav-committed"],
  [chrome.webNavigation.onCompleted, "nav-completed"],
  [chrome.webNavigation.onErrorOccurred, "nav-error"],
  [chrome.webNavigation.onHistoryStateUpdated, "nav-history-state"]
]) event.addListener(async (details) => {
  await ownedTabsReady;
  if (details.frameId !== 0 || !ownedTaskTabs.has(details.tabId)) return;
  traceTask(details.tabId, source, {
    origin: safeOrigin(details.url), transitionType: details.transitionType,
    transitionQualifiers: details.transitionQualifiers,
    error: details.error, documentId: details.documentId
  });
});

// Optional developer reconnect; normal tasks do not require this gesture.
chrome.action.onClicked.addListener(() => void ensureNativeConnection("toolbar"));

async function ensureNativeConnection(reason) {
  if (nativePort || connecting) return;
  connecting = true;
  try {
    const port = chrome.runtime.connectNative(NATIVE_HOST);
    nativePort = port;
    port.onMessage.addListener((message) => void handleNativeMessage(port, message));
    port.onDisconnect.addListener(() => {
      const detail = chrome.runtime.lastError?.message;
      console.info("VoiceOS native connection closed.", detail ?? "");
      console.info("VoiceOS task trace", new Date().toISOString(), "native-disconnect", { ownedTabs: ownedTaskTabs.size });
      if (nativePort === port) nativePort = null;
      scheduleReconnect();
    });
    port.postMessage({
      type: "extension_hello",
      protocolVersion: PROTOCOL_VERSION,
      extensionId: chrome.runtime.id,
      reason
    });
    await chrome.alarms.clear(RECONNECT_ALARM);
  } finally {
    connecting = false;
  }
}

function scheduleReconnect() {
  chrome.alarms.create(RECONNECT_ALARM, { delayInMinutes: 0.5, periodInMinutes: 0.5 });
  setTimeout(() => void ensureNativeConnection("short-retry"), 2_000);
}

async function restoreOwnedTabs() {
  const stored = await chrome.storage.session.get("ownedTaskTabs");
  for (const [tabId, sessionId] of stored.ownedTaskTabs ?? []) {
    try {
      await chrome.tabs.get(Number(tabId));
      ownedTaskTabs.set(Number(tabId), sessionId);
    } catch { /* Closed while the worker was asleep. */ }
  }
  await persistOwnedTabs();
}

function persistOwnedTabs() {
  return chrome.storage.session.set({ ownedTaskTabs: Array.from(ownedTaskTabs.entries()) });
}

async function handleNativeMessage(port, message) {
  await ownedTabsReady;
  const id = typeof message?.id === "string" ? message.id : null;
  if (message?.type !== "command" || !id) {
    port.postMessage({ type: "response", id, ok: false,
      error: { code: "INVALID_ENVELOPE", message: "Expected a command with a string id." } });
    return;
  }

  const tabId = message.payload?.tabId;
  if (Number.isInteger(tabId) && ownedTaskTabs.has(tabId))
    traceTask(tabId, "native-command", { command: message.command, action: message.payload?.action });
  else if (message.command === "OPEN_TASK_TAB")
    console.info("VoiceOS task trace", new Date().toISOString(), "native-command", { command: message.command });

  try {
    let result;
    switch (message.command) {
      case "OPEN_TASK_TAB": result = await openTaskTab(message.payload); break;
      case "OBSERVE": result = { snapshot: await observeOwnedTab(message.payload) }; break;
      case "ACT": result = await actInOwnedTab(message.payload); break;
      case "FOCUS_TASK_TAB": result = { snapshot: await focusTaskTab(message.payload) }; break;
      default: throw protocolError("UNKNOWN_COMMAND", `Unsupported command: ${String(message.command)}`);
    }
    port.postMessage({ type: "response", id, ok: true, result });
  } catch (error) {
    if (Number.isInteger(tabId) && ownedTaskTabs.has(tabId))
      traceTask(tabId, "native-command-error", { command: message.command, code: error?.code });
    port.postMessage({ type: "response", id, ok: false, error: {
      code: error?.code ?? "EXTENSION_ERROR",
      message: error instanceof Error ? error.message : String(error)
    }});
  }
}

async function openTaskTab(payload) {
  const sessionId = requireSession(payload?.sessionId);
  const existing = Array.from(ownedTaskTabs).find(([, owner]) => owner === sessionId);
  if (existing) return { snapshot: await focusTaskTab({ tabId: existing[0], sessionId }) };

  const url = parseAllowedUrl(payload?.url);
  const tab = await chrome.tabs.create({ url: url.href, active: true });
  if (!Number.isInteger(tab.id)) throw protocolError("TAB_CREATE_FAILED", "Chrome did not return a task tab id.");
  ownedTaskTabs.set(tab.id, sessionId);
  traceTask(tab.id, "task-tab-created", { origin: url.origin, windowId: tab.windowId });
  await persistOwnedTabs();
  const readyStart = Date.now();
  await waitForTabComplete(tab.id, 15_000);
  traceTask(tab.id, "startup-readiness", { elapsedMs: Date.now() - readyStart });
  await focusOwnedTab(tab.id, sessionId);
  return { snapshot: await observeOwnedTab({ tabId: tab.id, sessionId }) };
}

async function focusTaskTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  traceTask(tabId, "task-tab-resume");
  await focusOwnedTab(tabId, sessionId);
  return observeOwnedTab({ tabId, sessionId });
}

async function focusOwnedTab(tabId, sessionId) {
  assertOwnedTab(tabId, sessionId);
  traceTask(tabId, "tabs.update", { reason: "interactive-task-focus", active: true });
  const tab = await chrome.tabs.update(tabId, { active: true });
  if (!Number.isInteger(tab?.windowId)) throw protocolError("WINDOW_NOT_FOUND", "Task tab has no Chrome window.");
  const window = await chrome.windows.get(tab.windowId);
  if (window.state === "minimized") {
    traceTask(tabId, "windows.update", { reason: "interactive-task-restore", windowId: tab.windowId, state: "normal" });
    await chrome.windows.update(tab.windowId, { state: "normal" });
  }
  traceTask(tabId, "windows.update", { reason: "interactive-task-focus", windowId: tab.windowId, focused: true });
  let focused = await chrome.windows.update(tab.windowId, { focused: true });
  if (!focused.focused) {
    await delay(150);
    traceTask(tabId, "windows.update", { reason: "interactive-task-focus-retry", windowId: tab.windowId, focused: true });
    focused = await chrome.windows.update(tab.windowId, { focused: true });
    if (!focused.focused) console.warn("VoiceOS task window did not report focus.", tab.windowId);
  }
}

async function observeOwnedTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  await ensureContentScript(tabId);
  const snapshot = await sendContentMessage(tabId, { type: "VOICEOS_OBSERVE" });
  return { tabId, sessionId, ...snapshot };
}

async function actInOwnedTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  const action = payload?.action;
  if (!["CLICK", "REPLACE_TEXT", "INSERT_TEXT", "SCROLL", "BACK"].includes(action))
    throw protocolError("INVALID_ACTION", "The requested browser action is not supported.");
  if (typeof payload?.revision !== "string")
    throw protocolError("INVALID_ACTION", "revision must be a string.");
  if (["CLICK", "REPLACE_TEXT", "INSERT_TEXT"].includes(action) && typeof payload?.elementRef !== "string")
    throw protocolError("INVALID_ACTION", "This action requires an elementRef.");
  if (["REPLACE_TEXT", "INSERT_TEXT"].includes(action)
      && (typeof payload?.text !== "string" || payload.text.length > 2_000))
    throw protocolError("INVALID_TEXT", "Text must be a string of at most 2000 characters.");

  if (action === "CLICK" || action === "BACK")
    traceTask(tabId, "navigation-act", { action, elementRef: payload.elementRef });

  await ensureContentScript(tabId);
  const actionStart = Date.now();
  const actionResult = await sendContentMessage(tabId, {
    type: "VOICEOS_ACT", revision: payload.revision, elementRef: payload.elementRef,
    action, text: payload.text, direction: payload.direction
  });
  traceTask(tabId, "action-dispatch", { action, elapsedMs: Date.now() - actionStart });
  if (actionResult?.navigation)
    traceTask(tabId, "navigation-dispatched", {
      method: actionResult.navigation.method,
      origin: safeOrigin(actionResult.navigation.href)
    });
  const readinessStart = Date.now();
  if (action === "CLICK" || action === "BACK") await waitForClickEffects(tabId);
  else await delay(100);
  traceTask(tabId, "action-readiness", { action, elapsedMs: Date.now() - readinessStart });
  return { snapshot: await observeOwnedTab({ tabId, sessionId }) };
}

async function sendContentMessage(tabId, message) {
  const response = await chrome.tabs.sendMessage(tabId, message);
  if (response?.__voiceOSError)
    throw protocolError(response.code ?? "CONTENT_ERROR", response.message ?? "Content script rejected the request.");
  return response;
}

function assertOwnedTab(tabId, sessionValue) {
  const sessionId = requireSession(sessionValue);
  if (!Number.isInteger(tabId) || !ownedTaskTabs.has(tabId))
    throw protocolError("TAB_NOT_OWNED", "The tab was not created by VoiceOS.");
  if (ownedTaskTabs.get(tabId) !== sessionId)
    throw protocolError("SESSION_MISMATCH", "The tab belongs to a different VoiceOS browser session.");
  return { tabId, sessionId };
}

function requireSession(value) {
  if (typeof value !== "string" || !/^[a-zA-Z0-9_-]{8,80}$/.test(value))
    throw protocolError("INVALID_SESSION", "A bounded opaque session id is required.");
  return value;
}

function parseAllowedUrl(value) {
  let url;
  try { url = new URL(value); } catch { throw protocolError("INVALID_URL", "An absolute URL is required."); }
  if (!["http:", "https:"].includes(url.protocol))
    throw protocolError("INVALID_URL", "Only HTTP and HTTPS task URLs are allowed.");
  return url;
}

async function ensureContentScript(tabId) {
  try { await chrome.tabs.sendMessage(tabId, { type: "VOICEOS_PING" }); return; } catch {}
  await chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"] });
}

async function waitForClickEffects(tabId) {
  await delay(350);
  const tab = await chrome.tabs.get(tabId);
  if (tab.status === "loading") await waitForTabComplete(tabId, 15_000);
  else await delay(450);
}

async function waitForTabComplete(tabId, timeoutMs) {
  const existing = await chrome.tabs.get(tabId);
  if (existing.status === "complete") return;
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(protocolError("TAB_TIMEOUT", `Tab ${tabId} did not finish loading.`));
    }, timeoutMs);
    function listener(updatedTabId, changeInfo) {
      if (updatedTabId === tabId && changeInfo.status === "complete") {
        clearTimeout(timeout); chrome.tabs.onUpdated.removeListener(listener); resolve();
      }
    }
    chrome.tabs.onUpdated.addListener(listener);
  });
}

function protocolError(code, message) { const error = new Error(message); error.code = code; return error; }
function delay(milliseconds) { return new Promise((resolve) => setTimeout(resolve, milliseconds)); }
function traceTask(tabId, event, detail = {}) {
  console.info("VoiceOS task trace", new Date().toISOString(),
    `activation=${ownedTaskTabs.get(tabId) ?? "unknown"}`, `tab=${tabId}`, event, detail);
}
function safeOrigin(value) {
  try { return value ? new URL(value).origin : null; } catch { return null; }
}
