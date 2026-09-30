const NATIVE_HOST = "com.voiceos.chrome_companion";
const PROTOCOL_VERSION = 1;
const RECONNECT_ALARM = "voiceos-native-reconnect";
const ownedTaskTabs = new Map(); // tabId -> opaque VoiceOS session id
const adoptedUserTabs = new Set();
// Task lineage only: child tabId -> the tab it was opened from. Chrome owns each tab's own
// navigation history; this exists solely so a child with no same-tab history can return to its opener.
const tabLineage = new Map(); // childTabId -> { openerTabId, sessionId }
const taskLastUsed = new Map();
let taskUseSequence = 0;

// TEMPORARY (browser-history investigation): full-URL navigation-chain tracing for one physical run.
// View in chrome://extensions > VoiceOS companion > service worker console; filter "HISTORY-TRACE".
const HISTORY_TRACE = true;
async function historyTrace(tabId, stage, extra = {}) {
  if (!HISTORY_TRACE) return;
  let page = null;
  try {
    const [probe] = await chrome.scripting.executeScript({ target: { tabId }, func: () => ({
      url: location.href, historyLength: history.length,
      navType: performance.getEntriesByType("navigation")[0]?.type ?? null,
      everActivated: navigator.userActivation?.hasBeenActive ?? null }) });
    page = probe?.result ?? null;
  } catch (error) { page = { error: String(error?.message ?? error) }; }
  console.info("VoiceOS HISTORY-TRACE", new Date().toISOString(), `tab=${tabId}`, stage,
    JSON.stringify({ owned: ownedTaskTabs.has(tabId), adopted: adoptedUserTabs.has(tabId), page, ...extra }));
}

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
    adoptedUserTabs.delete(tabId);
    taskLastUsed.delete(tabId);
    tabLineage.delete(tabId);
    void persistOwnedTabs();
  }
});
chrome.tabs.onCreated?.addListener(async (tab) => {
  await ownedTabsReady;
  const opener = tab.openerTabId;
  if (Number.isInteger(tab.id) && Number.isInteger(opener) && ownedTaskTabs.has(opener)) {
    tabLineage.set(tab.id, { openerTabId: opener, sessionId: ownedTaskTabs.get(opener) });
    await persistOwnedTabs();
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
  const stored = await chrome.storage.session.get(["ownedTaskTabs", "adoptedUserTabs", "taskLastUsed", "taskUseSequence", "tabLineage"]);
  taskUseSequence = Number.isSafeInteger(stored.taskUseSequence) ? stored.taskUseSequence : 0;
  for (const [tabId, sessionId] of stored.ownedTaskTabs ?? []) {
    try {
      await chrome.tabs.get(Number(tabId));
      ownedTaskTabs.set(Number(tabId), sessionId);
    } catch { /* Closed while the worker was asleep. */ }
  }
  for (const tabId of stored.adoptedUserTabs ?? [])
    if (ownedTaskTabs.has(Number(tabId))) adoptedUserTabs.add(Number(tabId));
  for (const [childId, lineage] of stored.tabLineage ?? [])
    if (Number.isInteger(lineage?.openerTabId) && typeof lineage?.sessionId === "string") {
      try { await chrome.tabs.get(Number(childId)); tabLineage.set(Number(childId), lineage); } catch { /* closed */ }
    }
  for (const [tabId, sequence] of stored.taskLastUsed ?? [])
    if (ownedTaskTabs.has(Number(tabId)) && Number.isSafeInteger(sequence))
      taskLastUsed.set(Number(tabId), sequence);
  await persistOwnedTabs();
}

function persistOwnedTabs() {
  return chrome.storage.session.set({ ownedTaskTabs: Array.from(ownedTaskTabs.entries()),
    adoptedUserTabs: Array.from(adoptedUserTabs), taskLastUsed: Array.from(taskLastUsed.entries()),
    tabLineage: Array.from(tabLineage.entries()), taskUseSequence });
}

async function markTaskUsed(tabId) {
  if (!ownedTaskTabs.has(tabId) || adoptedUserTabs.has(tabId)) return;
  taskLastUsed.set(tabId, ++taskUseSequence);
  await persistOwnedTabs();
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
      case "CREATE_NEW_TAB": result = await createNewTab(message.payload); break;
      case "OBSERVE": result = { snapshot: await observeOwnedTab(message.payload) }; break;
      case "ACT": result = await actInOwnedTab(message.payload); break;
      case "FOCUS_TASK_TAB": result = { snapshot: await focusTaskTab(message.payload) }; break;
      case "LIST_TABS": result = { tabs: await listTabs() }; break;
      case "SELECT_TAB": result = await selectTab(message.payload); break;
      case "CLOSE_TASK_TAB": result = await closeTaskTab(message.payload); break;
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

async function createNewTab(payload) {
  const sessionId = requireSession(payload?.sessionId);
  const tab = await chrome.tabs.create({ active: true });
  if (!Number.isInteger(tab.id)) throw protocolError("TAB_CREATE_FAILED", "Chrome did not return a tab id.");
  ownedTaskTabs.set(tab.id, sessionId);
  await markTaskUsed(tab.id);
  await persistOwnedTabs();
  await focusOwnedTab(tab.id, sessionId);
  return { tab: { tabId: tab.id, windowId: tab.windowId, active: true,
    url: tab.url ?? null, title: tab.title ?? null, provenance: 1,
    sessionId, lastUsedSequence: taskLastUsed.get(tab.id) ?? null } };
}

async function listTabs() {
  const focused = await chrome.windows.getLastFocused();
  const tabs = await chrome.tabs.query({});
  return tabs.filter(tab => Number.isInteger(tab.id) && Number.isInteger(tab.windowId)
      && (!tab.url || tab.url.length <= 4096))
    .sort((a, b) => Number(Boolean(b.active && b.windowId === focused.id))
      - Number(Boolean(a.active && a.windowId === focused.id)))
    .slice(0, 128)
    .map(tab => ({
      tabId: tab.id, windowId: tab.windowId,
      active: Boolean(tab.active && tab.windowId === focused.id),
      url: tab.url ?? null, title: tab.title?.slice(0, 256) ?? null,
      provenance: ownedTaskTabs.has(tab.id) && !adoptedUserTabs.has(tab.id) ? 1 : 0,
      sessionId: ownedTaskTabs.get(tab.id) ?? null,
      lastUsedSequence: taskLastUsed.get(tab.id) ?? null
    }));
}

async function selectTab(payload) {
  const sessionId = requireSession(payload?.sessionId);
  const tabId = payload?.tabId;
  if (!Number.isInteger(tabId) || typeof payload?.expectedUrl !== "string")
    throw protocolError("INVALID_TAB", "A tab and its observed URL are required.");
  const tab = await chrome.tabs.get(tabId);
  if (tab.url !== payload.expectedUrl || !["http:", "https:"].includes(new URL(tab.url).protocol))
    throw protocolError("STALE_TAB", "The selected tab changed since inventory.");
  if (typeof payload.expectedTitle === "string" && tab.title !== payload.expectedTitle)
    throw protocolError("STALE_TAB", "The selected tab title changed since inventory.");
  if (payload.requireActive) {
    const focused = await chrome.windows.getLastFocused();
    if (!tab.active || tab.windowId !== focused.id)
      throw protocolError("TAB_NOT_ACTIVE", "The selected tab is no longer active.");
  }
  const owner = ownedTaskTabs.get(tabId);
  if (owner && owner !== sessionId)
    throw protocolError("SESSION_MISMATCH", "The tab belongs to another VoiceOS task.");
  if (!owner) {
    ownedTaskTabs.set(tabId, sessionId);
    adoptedUserTabs.add(tabId);
    await persistOwnedTabs();
  }
  await historyTrace(tabId, "select:before-focus", { expectedUrl: payload.expectedUrl });
  await focusOwnedTab(tabId, sessionId);
  await markTaskUsed(tabId);
  await historyTrace(tabId, "select:after-focus");
  const selected = await chrome.tabs.get(tabId);
  if (selected.url !== payload.expectedUrl)
    throw protocolError("STALE_TAB", "The selected tab changed while focusing.");
  if (typeof payload.expectedTitle === "string" && selected.title !== payload.expectedTitle)
    throw protocolError("STALE_TAB", "The selected tab title changed while focusing.");
  return { tabId, sessionId, url: selected.url, title: selected.title ?? null };
}

async function openTaskTab(payload) {
  const sessionId = requireSession(payload?.sessionId);
  const existing = Array.from(ownedTaskTabs).find(([, owner]) => owner === sessionId);
  if (existing) return { snapshot: await focusTaskTab({ tabId: existing[0], sessionId }) };

  const url = parseAllowedUrl(payload?.url);
  const commandAt = Date.now();
  const timings = { created: null, committed: null, domContentLoaded: null, loadComplete: null,
    focused: null, contentReady: null, settled: null, settleReason: null, settleMutations: null,
    actionable: null, observed: null };
  const mark = (key) => { if (timings[key] === null) timings[key] = Date.now() - commandAt; };

  const tab = await chrome.tabs.create({ url: url.href, active: true });
  console.info("VoiceOS HISTORY-TRACE", new Date().toISOString(), `tab=${tab.id}`, "OPEN_TASK_TAB:tabs.create", url.origin);
  if (!Number.isInteger(tab.id)) throw protocolError("TAB_CREATE_FAILED", "Chrome did not return a task tab id.");
  mark("created");
  ownedTaskTabs.set(tab.id, sessionId);
  await markTaskUsed(tab.id);
  traceTask(tab.id, "task-tab-created", { origin: url.origin, windowId: tab.windowId });
  await persistOwnedTabs();

  const onCommitted = (details) => { if (details.tabId === tab.id && details.frameId === 0) mark("committed"); };
  const onDom = (details) => { if (details.tabId === tab.id && details.frameId === 0) mark("domContentLoaded"); };
  const onUpdated = (updatedTabId, changeInfo) => {
    if (updatedTabId === tab.id && changeInfo.status === "complete") mark("loadComplete");
  };
  chrome.webNavigation.onCommitted.addListener(onCommitted);
  chrome.webNavigation.onDOMContentLoaded?.addListener(onDom);
  chrome.tabs.onUpdated.addListener(onUpdated);

  try {
    await Promise.all([
      focusOwnedTab(tab.id, sessionId).then(() => mark("focused")),
      waitForDocumentReady(tab.id, 15_000).then(() => mark("domContentLoaded"))
    ]);
    await ensureContentScript(tab.id);
    mark("contentReady");
    const settleResponse = await sendSettle(tab.id, { quietMs: 300, maxMs: 2_500, requireActionable: true });
    mark("settled");
    timings.settleReason = settleResponse?.reason ?? "legacy";
    timings.settleMutations = Number.isFinite(settleResponse?.mutations) ? settleResponse.mutations : null;
    timings.actionable = typeof settleResponse?.actionable === "boolean" ? settleResponse.actionable : null;
    const snapshot = await observeOwnedTab({ tabId: tab.id, sessionId });
    mark("observed");
    traceTask(tab.id, "startup-readiness", { elapsedMs: Date.now() - commandAt, timings });
    return { snapshot, timings };
  } finally {
    chrome.webNavigation.onCommitted.removeListener(onCommitted);
    chrome.webNavigation.onDOMContentLoaded?.removeListener(onDom);
    chrome.tabs.onUpdated.removeListener(onUpdated);
  }
}

async function focusTaskTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  traceTask(tabId, "task-tab-resume");
  await focusOwnedTab(tabId, sessionId);
  await markTaskUsed(tabId);
  return observeOwnedTab({ tabId, sessionId });
}

async function focusOwnedTab(tabId, sessionId) {
  assertOwnedTab(tabId, sessionId);
  let windowId = null;
  let tabActivation = "failed";
  let windowFocus = "unsupported";
  try {
    const tab = await chrome.tabs.update(tabId, { active: true });
    if (!Number.isInteger(tab?.windowId)) throw protocolError("WINDOW_NOT_FOUND", "Task tab has no Chrome window.");
    windowId = tab.windowId;
    tabActivation = "passed";
    windowFocus = "failed";
    const window = await chrome.windows.get(windowId);
    if (window.state === "minimized") await chrome.windows.update(windowId, { state: "normal" });
    let focused = await chrome.windows.update(windowId, { focused: true });
    if (!focused.focused) {
      await delay(150);
      focused = await chrome.windows.update(windowId, { focused: true });
      if (!focused.focused) throw protocolError("FOCUS_FAILED", "Chrome did not focus the requested window.");
    }
    const confirmedWindow = await chrome.windows.get(windowId);
    const confirmedTab = await chrome.tabs.get(tabId);
    if (!confirmedTab.active) tabActivation = "failed";
    if (!confirmedWindow.focused || !confirmedTab.active)
      throw protocolError("FOCUS_FAILED", "The requested tab or window is not focused.");
    windowFocus = "passed";
  } finally {
    console.info("VoiceOS browser surface preparation", {
      tab: tabId, window: windowId, tab_activation: tabActivation, window_focus: windowFocus
    });
  }
}

async function observeOwnedTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  await ensureContentScript(tabId);
  const snapshot = await sendContentMessage(tabId, { type: "VOICEOS_OBSERVE" });
  return { tabId, sessionId, ...snapshot, ...(await tabFacts(tabId)) };
}

// Grounded navigation facts for an owned tab. Chrome exposes no back/forward entry count to
// extensions, so canGoBack stays the page's own history hint (content.js); the authoritative
// answer to "can this tab go back" is the traversal attempt itself.
async function tabFacts(tabId) {
  const facts = { ownedByVoiceOS: !adoptedUserTabs.has(tabId), adopted: adoptedUserTabs.has(tabId),
    openerTabId: tabLineage.get(tabId)?.openerTabId ?? null, documentId: null };
  try {
    const frame = await chrome.webNavigation.getFrame?.({ tabId, frameId: 0 });
    if (typeof frame?.documentId === "string") facts.documentId = frame.documentId;
  } catch { /* documentId is best-effort */ }
  return facts;
}

async function actInOwnedTab(payload) {
  const { tabId, sessionId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  const action = payload?.action;
  if (!["CLICK", "REPLACE_TEXT", "INSERT_TEXT", "SCROLL", "BACK", "FORWARD"].includes(action))
    throw protocolError("INVALID_ACTION", "The requested browser action is not supported.");
  if (typeof payload?.revision !== "string")
    throw protocolError("INVALID_ACTION", "revision must be a string.");
  if (["CLICK", "REPLACE_TEXT", "INSERT_TEXT"].includes(action) && typeof payload?.elementRef !== "string")
    throw protocolError("INVALID_ACTION", "This action requires an elementRef.");
  if (["REPLACE_TEXT", "INSERT_TEXT"].includes(action)
      && (typeof payload?.text !== "string" || payload.text.length > 2_000))
    throw protocolError("INVALID_TEXT", "Text must be a string of at most 2000 characters.");

  const isTraversal = action === "BACK" || action === "FORWARD";
  const isNavAction = action === "CLICK" || isTraversal;
  if (isNavAction) traceTask(tabId, "navigation-act", { action, elementRef: payload.elementRef });

  if (isNavAction) await historyTrace(tabId, `act:${action}:before`);
  const commandAt = Date.now();
  const timings = { dispatched: null, signal: "none", signalAt: null, committed: null,
    domContentLoaded: null, settled: null, settleReason: null, observed: null };
  const mark = (key) => { timings[key] = Date.now() - commandAt; };

  const beforeTabs = isNavAction ? await chrome.tabs.query({}) : null;
  const sourceTab = beforeTabs?.find(tab => tab.id === tabId);

  const watcher = isNavAction ? watchActionSignals(tabId, commandAt, { traversal: isTraversal }) : null;
  let actionResult;
  let traversalVia = null;
  let noHistory = null;

  try {
    await ensureContentScript(tabId);
    const actionStart = Date.now();
    actionResult = await sendContentMessage(tabId, {
      type: "VOICEOS_ACT", revision: payload.revision, elementRef: payload.elementRef,
      action, text: payload.text, direction: payload.direction
    });
    if (isTraversal) {
      try { traversalVia = await traverseHistory(tabId, action === "BACK" ? "back" : "forward"); }
      catch (error) { if (error?.code !== "NO_HISTORY") throw error; noHistory = error; }
    }
    mark("dispatched");
    traceTask(tabId, "action-dispatch", { action, elapsedMs: Date.now() - actionStart });
    if (actionResult?.navigation)
      traceTask(tabId, "navigation-dispatched", {
        method: actionResult.navigation.method,
        origin: safeOrigin(actionResult.navigation.href)
      });
    const readinessStart = Date.now();
    if (watcher && !noHistory) {
      const outcome = await waitForClickEffects(tabId, watcher, actionStart,
        isTraversal ? BACK_NO_SIGNAL_MS : CLICK_NO_SIGNAL_MS) ?? {};
      timings.signal = outcome.signal ?? "none";
      if (outcome.signalAt != null) mark("signalAt");
      timings.committed = watcher.marks.committed;
      timings.domContentLoaded = watcher.marks.domContentLoaded;
      if (outcome.settled) {
        mark("settled");
        timings.settleReason = outcome.settled.reason ?? null;
      }
    } else if (!noHistory) await delay(100);
    traceTask(tabId, "action-readiness", { action, elapsedMs: Date.now() - readinessStart, signal: timings.signal });
  } finally {
    watcher?.stop();
  }

  if (isTraversal) {
    try {
      if (noHistory) throw noHistory;
      await confirmTraversal(tabId, sourceTab?.url, watcher.marks, traversalVia);
    } catch (error) {
      // Task-level Back: a child tab with no same-tab history returns to the tab it was opened
      // from. Forward is never faked across tabs.
      if (error?.code === "NO_HISTORY" && action === "BACK") {
        const returned = await returnToOpener(tabId, sessionId, timings);
        if (returned) return returned;
      }
      throw error;
    }
  }

  if (beforeTabs) {
    const afterTabs = await chrome.tabs.query({});
    const priorIds = new Set(beforeTabs.map(tab => tab.id));
    const created = afterTabs.filter(tab => !priorIds.has(tab.id));
    const sourceAfter = afterTabs.find(tab => tab.id === tabId);
    if (created.length > 0 || sourceAfter?.active === false) {
      const child = created.length === 1 ? created[0] : null;
      const attributable = child && child.active && Number.isInteger(child.id)
        && (child.openerTabId === tabId
          || (sourceTab?.windowId === child.windowId
            && actionResult?.navigation?.href === child.url));
      if (!attributable)
        throw protocolError("TAB_TOPOLOGY_AMBIGUOUS",
          "The browser action changed tabs without one verifiable task continuation.");
      if (ownedTaskTabs.has(child.id) && ownedTaskTabs.get(child.id) !== sessionId)
        throw protocolError("TAB_TOPOLOGY_AMBIGUOUS", "The new tab belongs to another task.");
      try {
        await waitForTabComplete(child.id, 15_000);
        const finalChild = await chrome.tabs.get(child.id);
        const focusedWindow = await chrome.windows.getLastFocused();
        if (!finalChild.active || finalChild.windowId !== focusedWindow.id
            || typeof finalChild.url !== "string")
          throw protocolError("TAB_TOPOLOGY_AMBIGUOUS", "The new tab is not active or observable.");
        parseAllowedUrl(finalChild.url);
        ownedTaskTabs.set(child.id, sessionId);
        tabLineage.set(child.id, { openerTabId: tabId, sessionId });
        // A child of a user's adopted tab stays conservative: never auto-closable by VoiceOS.
        if (adoptedUserTabs.has(tabId)) adoptedUserTabs.add(child.id);
        await markTaskUsed(child.id);
        await persistOwnedTabs();
        traceTask(child.id, "task-tab-adopted", { fromTabId: tabId });
        const childSnapshot = await observeOwnedTab({ tabId: child.id, sessionId });
        mark("observed");
        return { snapshot: { ...childSnapshot, adoptedFromTabId: tabId }, timings };
      } catch {
        throw protocolError("TAB_TOPOLOGY_AMBIGUOUS",
          "The new tab could not be observed as the task continuation.");
      }
    }
  }
  await markTaskUsed(tabId);
  const snapshot = await observeOwnedTab({ tabId, sessionId });
  mark("observed");
  if (isNavAction) await historyTrace(tabId, `act:${action}:after`, { signal: timings.signal });
  return { snapshot, timings };
}

async function closeTaskTab(payload) {
  const { tabId } = assertOwnedTab(payload?.tabId, payload?.sessionId);
  if (adoptedUserTabs.has(tabId))
    throw protocolError("TAB_NOT_OWNED", "Refusing to close a user's adopted tab.");
  traceTask(tabId, "task-tab-closed");
  await chrome.tabs.remove(tabId);
  ownedTaskTabs.delete(tabId);
  adoptedUserTabs.delete(tabId);
  taskLastUsed.delete(tabId);
  await persistOwnedTabs();
  return { closed: true };
}

async function sendContentMessage(tabId, message) {
  const response = await chrome.tabs.sendMessage(tabId, message);
  if (response?.__voiceOSError)
    throw protocolError(response.code ?? "CONTENT_ERROR", response.message ?? "Content script rejected the request.");
  return response;
}

async function sendSettle(tabId, options) {
  const response = await sendContentMessage(tabId, { type: "VOICEOS_SETTLE", ...options });
  if (!response || typeof response !== "object") return { settled: true, reason: "legacy" };
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
  await chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"], injectImmediately: true });
}

// History traversal is only a request; the caller confirms the effect. Chrome's tab-level
// traversal (chrome.tabs.goBack/goForward) honours the history-manipulation intervention: entries
// created by navigations the user never activated (every VoiceOS element click) are skipped, so it
// reports "no page in history" for tabs that visibly have earlier pages. The page's own
// history.back()/forward() traverses the real session history, so it is the fallback whenever the
// page reports another entry. Returns which mechanism was used.
async function traverseHistory(tabId, direction) {
  try {
    await (direction === "back" ? chrome.tabs.goBack(tabId) : chrome.tabs.goForward(tabId));
    return "chrome";
  } catch (error) {
    if (!/history/i.test(String(error?.message ?? "")))
      throw protocolError("NAVIGATION_FAILED", "The browser could not navigate history.");
  }
  let hint = null;
  try {
    const [probe] = await chrome.scripting.executeScript({ target: { tabId },
      func: (dir) => { const length = history.length; if (length > 1) history[dir](); return length; },
      args: [direction] });
    hint = probe?.result ?? null;
  } catch { /* Not scriptable: Chrome's answer stands. */ }
  if (!(hint > 1))
    throw protocolError("NO_HISTORY", `The tab has no ${direction === "back" ? "previous" : "next"} page.`);
  return "page";
}

// A traversal succeeds only on evidence the tab actually moved: a committed main-frame
// document, a same-document history traversal, or a changed tab URL. When the page-level fallback
// produced no movement there was no entry in that direction (the page's length also counts the
// other direction), which is NO_HISTORY, not an unobserved effect.
async function confirmTraversal(tabId, beforeUrl, marks, via) {
  if (marks.traversed) return;
  const after = await chrome.tabs.get(tabId);
  if (typeof beforeUrl === "string" && typeof after?.url === "string" && after.url !== beforeUrl) return;
  if (marks.navigationError)
    throw protocolError("NAVIGATION_FAILED", "The browser could not navigate history.");
  if (marks.navigationStarted || after?.pendingUrl)
    throw protocolError("NAVIGATION_UNCONFIRMED", "History traversal started, but the page was not confirmed.");
  if (via === "page") throw protocolError("NO_HISTORY", "The tab has no page in that direction.");
  throw protocolError("NAVIGATION_NOT_OBSERVED", "History traversal did not change the page.");
}

// The opener must still exist and belong to this same task session. The child is left open:
// closing it is a separate ownership decision and is never made to simulate history.
async function returnToOpener(childId, sessionId, timings) {
  const lineage = tabLineage.get(childId);
  if (!lineage || lineage.sessionId !== sessionId || ownedTaskTabs.get(lineage.openerTabId) !== sessionId)
    return null;
  try { await chrome.tabs.get(lineage.openerTabId); } catch { return null; }
  await focusOwnedTab(lineage.openerTabId, sessionId);
  await markTaskUsed(lineage.openerTabId);
  traceTask(lineage.openerTabId, "task-tab-returned-to-opener", { fromTabId: childId });
  const snapshot = await observeOwnedTab({ tabId: lineage.openerTabId, sessionId });
  timings.signal = "opener";
  return { snapshot: { ...snapshot, returnedFromTabId: childId }, timings };
}

// Registers navigation/tab listeners for one act(), resolving on the first observed
// signal after a click/back. Also records committed/domContentLoaded marks
// independent of which signal wins, for timing telemetry. In traversal mode (BACK) a
// same-document update counts only when Chrome marks it as a history traversal, so a page's
// own pushState/replaceState is not mistaken for going back.
function watchActionSignals(tabId, dispatchedAt, { traversal = false } = {}) {
  const marks = { committed: null, domContentLoaded: null, traversed: false,
    navigationStarted: false, navigationError: false };
  let settled = false;
  let resolveSignal;
  let resolveNavigationReady;
  const signalPromise = new Promise((resolve) => { resolveSignal = resolve; });
  // Ready only after the new main-frame document committed and reached DOMContentLoaded
  // (or completed, e.g. a bfcache restore), or the navigation failed/was aborted.
  const navigationReady = new Promise((resolve) => { resolveNavigationReady = resolve; });
  const elapsed = () => Date.now() - dispatchedAt;
  const matches = (details) => details.tabId === tabId && details.frameId === 0;
  const emit = (signal) => {
    if (settled) return;
    settled = true;
    resolveSignal({ signal, at: elapsed() });
  };
  const onBeforeNavigate = (details) => {
    if (!matches(details)) return;
    marks.navigationStarted = true;
    emit("cross_document");
  };
  const onCommitted = (details) => {
    if (!matches(details)) return;
    if (marks.committed === null) marks.committed = elapsed();
    marks.traversed = true;
    emit("cross_document");
  };
  const isTraversal = (details) => (details.transitionQualifiers ?? []).includes("forward_back");
  const onSameDocument = (details) => {
    if (!matches(details) || (traversal && !isTraversal(details))) return;
    marks.traversed = true;
    emit("same_document");
  };
  const onDom = (details) => {
    if (!matches(details)) return;
    if (marks.domContentLoaded === null) marks.domContentLoaded = elapsed();
    if (marks.committed !== null) resolveNavigationReady("dom_content_loaded");
  };
  const onCompleted = (details) => {
    if (matches(details) && marks.committed !== null) resolveNavigationReady("completed");
  };
  const onError = (details) => {
    if (!matches(details)) return;
    marks.navigationError = true;
    resolveNavigationReady("error");
  };
  const onHistoryStateUpdated = onSameDocument;
  const onReferenceFragmentUpdated = onSameDocument;
  const onCreated = (tab) => { if (tab.openerTabId === tabId) emit("new_tab"); };

  chrome.webNavigation.onBeforeNavigate.addListener(onBeforeNavigate);
  chrome.webNavigation.onCommitted.addListener(onCommitted);
  chrome.webNavigation.onDOMContentLoaded?.addListener(onDom);
  chrome.webNavigation.onCompleted?.addListener(onCompleted);
  chrome.webNavigation.onErrorOccurred?.addListener(onError);
  chrome.webNavigation.onHistoryStateUpdated.addListener(onHistoryStateUpdated);
  chrome.webNavigation.onReferenceFragmentUpdated?.addListener(onReferenceFragmentUpdated);
  chrome.tabs.onCreated?.addListener(onCreated);

  function stop() {
    chrome.webNavigation.onBeforeNavigate.removeListener(onBeforeNavigate);
    chrome.webNavigation.onCommitted.removeListener(onCommitted);
    chrome.webNavigation.onDOMContentLoaded?.removeListener(onDom);
    chrome.webNavigation.onCompleted?.removeListener(onCompleted);
    chrome.webNavigation.onErrorOccurred?.removeListener(onError);
    chrome.webNavigation.onHistoryStateUpdated.removeListener(onHistoryStateUpdated);
    chrome.webNavigation.onReferenceFragmentUpdated?.removeListener(onReferenceFragmentUpdated);
    chrome.tabs.onCreated?.removeListener(onCreated);
  }

  return { signalPromise, navigationReady, marks, stop };
}

function raceSignal(signalPromise, timeoutMs) {
  if (timeoutMs <= 0) return Promise.resolve(null);
  return new Promise((resolve) => {
    let done = false;
    const timer = setTimeout(() => { if (!done) { done = true; resolve(null); } }, timeoutMs);
    signalPromise.then((value) => { if (!done) { done = true; clearTimeout(timer); resolve(value); } });
  });
}

// A click may legitimately have no navigation effect, so its no-signal budget is short. BACK
// exists only to navigate; its budget only bounds how long an absent traversal is awaited
// before being reported as not observed, and never delays an observed one.
const CLICK_NO_SIGNAL_MS = 800;
const BACK_NO_SIGNAL_MS = 1_500;

// Event-driven settle for CLICK/BACK. Never waits longer than the no-signal
// budget when nothing observable happens; otherwise resolves as soon as the new
// document (cross_document) or SPA update (same_document) is ready, without
// waiting for full page load (images/ads/late resources).
async function waitForClickEffects(tabId, watcher, dispatchedAt, noSignalMs = CLICK_NO_SIGNAL_MS) {
  const signalPromise = watcher.signalPromise;
  const phase1Remaining = Math.max(0, 350 - (Date.now() - dispatchedAt));
  let signal = await raceSignal(signalPromise, phase1Remaining);
  if (!signal) {
    const phase2Remaining = Math.max(0, (dispatchedAt + noSignalMs) - Date.now());
    signal = await raceSignal(signalPromise, phase2Remaining);
  }
  if (!signal) return { signal: "none" };

  if (signal.signal === "new_tab") {
    await delay(450);
    return { signal: "new_tab", signalAt: signal.at };
  }

  if (signal.signal === "cross_document") {
    await waitForNavigationReady(watcher.navigationReady, 15_000);
    await ensureContentScript(tabId);
    const settled = await sendSettle(tabId, { quietMs: 200, maxMs: 1_500, requireActionable: true });
    return { signal: "cross_document", signalAt: signal.at, settled };
  }

  const elapsedSinceDispatch = Date.now() - dispatchedAt;
  const maxMs = Math.max(250, 800 - elapsedSinceDispatch);
  const settled = await sendSettle(tabId, { quietMs: 150, maxMs, requireActionable: false });
  return { signal: "same_document", signalAt: signal.at, settled };
}

// Waits for the watcher's post-commit document readiness. It never falls back to the
// tab's current status: right after onBeforeNavigate that status still describes the
// old document, and observing it would hand Jev the pre-navigation DOM.
function waitForNavigationReady(navigationReady, timeoutMs) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(protocolError("TAB_TIMEOUT", "The navigation did not become ready.")), timeoutMs);
    navigationReady.then((reason) => { clearTimeout(timer); resolve(reason); });
  });
}

// Resolves once the tab's main-frame DOMContentLoaded fires, or its status
// reaches "complete", whichever is first. Listeners are registered before the
// final chrome.tabs.get status check to avoid a race.
async function waitForDocumentReady(tabId, timeoutMs) {
  return new Promise((resolve, reject) => {
    let finished = false;
    const timer = setTimeout(() => finish(false), timeoutMs);
    const onDom = (details) => { if (details.tabId === tabId && details.frameId === 0) finish(true); };
    const onUpdated = (updatedTabId, changeInfo, tab) => {
      if (updatedTabId === tabId && changeInfo.status === "complete" && isNavigatedTab(tab)) finish(true);
    };
    const onCompleted = (details) => { if (details.tabId === tabId && details.frameId === 0) finish(true); };
    function cleanup() {
      chrome.webNavigation.onDOMContentLoaded?.removeListener(onDom);
      chrome.tabs.onUpdated.removeListener(onUpdated);
      chrome.webNavigation.onCompleted?.removeListener(onCompleted);
    }
    function finish(success) {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      cleanup();
      if (success) resolve(); else reject(protocolError("TAB_TIMEOUT", `Tab ${tabId} did not become ready.`));
    }
    chrome.webNavigation.onDOMContentLoaded?.addListener(onDom);
    chrome.tabs.onUpdated.addListener(onUpdated);
    chrome.webNavigation.onCompleted?.addListener(onCompleted);
    chrome.tabs.get(tabId).then((tab) => { if (tab.status === "complete" && isNavigatedTab(tab)) finish(true); }).catch(() => {});
  });
}

// The initial about:blank of a just-created tab can report "complete" before the
// requested navigation commits; only a settled, non-blank URL counts as ready.
function isNavigatedTab(tab) {
  return !tab || (!tab.pendingUrl && typeof tab.url === "string" && tab.url !== "" && tab.url !== "about:blank");
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
