const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

const source = (name) => fs.readFileSync(path.join(__dirname, name), "utf8");

// Shared mock factory for background.js tests. `fire(name, ...args)` invokes every
// listener registered under that event name; `overrides` deep-merges one level into
// the relevant chrome.* namespace (e.g. { tabs: { get: async () => ... } }).
function createMockChrome(overrides = {}) {
  const registry = new Map();
  function evt(name) {
    if (!registry.has(name)) registry.set(name, new Set());
    return {
      addListener(fn) { registry.get(name).add(fn); },
      removeListener(fn) { registry.get(name).delete(fn); }
    };
  }
  function fire(name, ...args) { for (const fn of [...(registry.get(name) ?? [])]) fn(...args); }
  const port = { onMessage: evt("port-message"), onDisconnect: evt("port-disconnect"), postMessage() {} };
  const base = {
    runtime: { id: "extension", connectNative: () => port,
      onStartup: evt("runtime-startup"), onInstalled: evt("runtime-installed") },
    alarms: { onAlarm: evt("alarm"), clear: async () => {}, create() {} },
    action: { onClicked: evt("action-clicked") },
    storage: { session: { get: async () => ({}), set: async () => {} } },
    tabs: {
      onRemoved: evt("tabs-removed"), onUpdated: evt("tabs-updated"), onActivated: evt("tabs-activated"),
      onCreated: evt("tabs-created"),
      create: async () => ({ id: 1, windowId: 1 }),
      get: async (id) => ({ id, windowId: 1, active: true, status: "complete" }),
      update: async (id, properties) => ({ id, windowId: 1, ...properties }),
      query: async () => [],
      sendMessage: async () => ({ ok: true }),
      remove: async () => {},
      goBack: async () => {}
    },
    windows: {
      onFocusChanged: evt("windows-focus"),
      get: async () => ({ state: "normal", focused: true }),
      getLastFocused: async () => ({ id: 1 }),
      update: async (id, properties) => ({ focused: true, ...properties })
    },
    webNavigation: {
      onBeforeNavigate: evt("nav-before"), onCommitted: evt("nav-committed"),
      onCompleted: evt("nav-completed"), onErrorOccurred: evt("nav-error"),
      onHistoryStateUpdated: evt("nav-history"), onDOMContentLoaded: evt("nav-dcl"),
      onReferenceFragmentUpdated: evt("nav-fragment")
    },
    scripting: { executeScript: async () => {} }
  };
  const chrome = { ...base };
  for (const key of Object.keys(overrides)) chrome[key] = { ...base[key], ...overrides[key] };
  return { chrome, fire };
}

function backgroundContext(chrome) {
  const context = vm.createContext({ chrome, console: { info() {}, warn() {} }, setTimeout, clearTimeout, URL, Date });
  vm.runInContext(source("background.js"), context);
  return context;
}

test("surface-only new tab delegates to Chrome default behavior", async () => {
  const event = () => ({ addListener() {}, removeListener() {} });
  const createOptions = [];
  const focusCalls = [];
  const chrome = {
    runtime: { id: "extension", connectNative: () => ({ onMessage: event(), onDisconnect: event(), postMessage() {} }),
      onStartup: event(), onInstalled: event() },
    alarms: { onAlarm: event(), clear: async () => {}, create() {} },
    action: { onClicked: event() },
    storage: { session: { get: async () => ({}), set: async () => {} } },
    tabs: { onRemoved: event(), onUpdated: event(), onActivated: event(),
      create: async options => { createOptions.push(options); return { id: 17, windowId: 2 }; },
      update: async (id, properties) => { focusCalls.push(["tab", id, properties]);
        return { id, windowId: 2, active: true }; },
      get: async id => ({ id, windowId: 2, active: true }) },
    windows: { onFocusChanged: event(), get: async () => ({ state: "normal", focused: true }),
      update: async (id, properties) => { focusCalls.push(["window", id, properties]);
        return { focused: true }; } },
    webNavigation: { onBeforeNavigate: event(), onCommitted: event(), onCompleted: event(),
      onErrorOccurred: event(), onHistoryStateUpdated: event() }
  };
  const context = vm.createContext({ chrome, console: { info() {}, warn() {} }, setTimeout, clearTimeout, URL });
  vm.runInContext(source("background.js"), context);
  const result = await vm.runInContext('createNewTab({ sessionId: "task-0017" })', context);
  assert.equal(result.tab.tabId, 17);
  assert.equal(createOptions.length, 1);
  assert.equal(Object.hasOwn(createOptions[0], "url"), false);
  assert.deepEqual(focusCalls.map(call => [call[0], call[1]]), [["tab", 17], ["window", 2]]);
  assert.equal(focusCalls[1][2].focused, true);
});

test("one attributable active child tab is adopted; ambiguous topology stops", async () => {
  async function run(createdTabs) {
    const event = () => ({ addListener() {}, removeListener() {} });
    const sourceTab = { id: 7, windowId: 2, active: true, status: "complete",
      url: "https://source.example/" };
    const tabs = new Map([[7, sourceTab]]);
    const chrome = {
      runtime: { id: "extension", connectNative: () => ({ onMessage: event(), onDisconnect: event(), postMessage() {} }),
        onStartup: event(), onInstalled: event() },
      alarms: { onAlarm: event(), clear: async () => {}, create() {} },
      action: { onClicked: event() },
      storage: { session: { get: async () => ({}), set: async () => {} } },
      tabs: { onRemoved: event(), onUpdated: event(), onActivated: event(),
        get: async id => tabs.get(id), query: async () => [...tabs.values()],
        sendMessage: async (id, message) => {
          if (message.type === "VOICEOS_ACT") {
            sourceTab.active = false;
            for (const child of createdTabs) tabs.set(child.id, child);
            return { navigation: { method: "element.click", href: "https://child.example/" } };
          }
          if (message.type === "VOICEOS_PING") return { ok: true };
          return { revision: "r1", url: tabs.get(id).url, title: "Child",
            visibleText: "destination", truncated: false, viewport: {}, elements: [] };
        } },
      windows: { onFocusChanged: event(), getLastFocused: async () => ({ id: 2 }) },
      webNavigation: { onBeforeNavigate: event(), onCommitted: event(), onCompleted: event(),
        onErrorOccurred: event(), onHistoryStateUpdated: event() },
      scripting: { executeScript: async () => {} }
    };
    const context = vm.createContext({ chrome, console: { info() {}, warn() {} }, setTimeout, clearTimeout, URL });
    vm.runInContext(source("background.js"), context);
    vm.runInContext('ownedTaskTabs.set(7, "session-0007"); waitForClickEffects = async () => {}', context);
    const action = 'actInOwnedTab({ tabId: 7, sessionId: "session-0007", revision: "r1", action: "CLICK", elementRef: "e1" })';
    return { context, action };
  }
  const one = await run([{ id: 9, windowId: 2, openerTabId: 7, active: true,
    status: "complete", url: "https://child.example/" }]);
  const adopted = await vm.runInContext(one.action, one.context);
  assert.equal(adopted.snapshot.tabId, 9);
  assert.equal(adopted.snapshot.adoptedFromTabId, 7);
  const many = await run([
    { id: 9, windowId: 2, openerTabId: 7, active: true, url: "https://child.example/" },
    { id: 10, windowId: 2, openerTabId: 7, active: false, url: "https://other.example/" }
  ]);
  await assert.rejects(vm.runInContext(many.action, many.context),
    error => error.code === "TAB_TOPOLOGY_AMBIGUOUS");
});

test("tab inventory is metadata only and selection checks the observed URL and activity", async () => {
  const listeners = new Map();
  let domMessages = 0;
  const focusCalls = [];
  const event = (name) => ({ addListener(fn) { listeners.set(name, fn); }, removeListener() {} });
  const port = { onMessage: event(), onDisconnect: event(), postMessage() {} };
  const tabs = new Map([
    [1, { id: 1, windowId: 3, active: true, url: "https://www.reddit.com/", title: "Reddit" }],
    [2, { id: 2, windowId: 3, active: false, url: "https://www.youtube.com/", title: "YouTube" }]
  ]);
  const chrome = {
    runtime: { id: "extension", connectNative: () => port, onStartup: event(), onInstalled: event() },
    alarms: { onAlarm: event(), clear: async () => {}, create() {} },
    action: { onClicked: event() },
    storage: { session: { get: async () => ({}), set: async () => {} } },
    tabs: { onRemoved: event(), onUpdated: event(), onActivated: event(),
      get: async id => tabs.get(id), query: async () => [...tabs.values()],
      update: async (id, value) => { focusCalls.push(["tab", id]);
        const next = { ...tabs.get(id), ...value }; tabs.set(id, next); return next; },
      sendMessage: async () => { domMessages++; return { revision: "r", url: "https://www.youtube.com/", title: "YouTube", elements: [] }; } },
    windows: { onFocusChanged: event(), getLastFocused: async () => ({ id: 3 }),
      get: async () => ({ state: "normal", focused: true }),
      update: async id => { focusCalls.push(["window", id]); return { focused: true }; } },
    webNavigation: { onBeforeNavigate: event(), onCommitted: event(), onCompleted: event(),
      onErrorOccurred: event(), onHistoryStateUpdated: event() },
    scripting: { executeScript: async () => {} }
  };
  const context = vm.createContext({ chrome, console: { info() {}, warn() {} }, setTimeout, clearTimeout, URL });
  vm.runInContext(source("background.js"), context);
  const inventory = await vm.runInContext("listTabs()", context);
  assert.equal(inventory.length, 2);
  assert.equal(inventory[0].active, true);
  assert.equal(inventory[1].provenance, 0);
  assert.equal(Object.hasOwn(inventory[0], "elements"), false);
  await assert.rejects(vm.runInContext(`selectTab({ sessionId: "test-session", tabId: 2,
    expectedUrl: "https://www.youtube.com/", requireActive: true })`, context));
  await assert.rejects(vm.runInContext(`selectTab({ sessionId: "test-session", tabId: 2,
    expectedUrl: "https://www.youtube.com/watch", requireActive: false })`, context));
  await assert.rejects(vm.runInContext(`selectTab({ sessionId: "test-session", tabId: 2,
    expectedUrl: "https://www.youtube.com/", expectedTitle: "Outdated title", requireActive: false })`, context));
  assert.equal((await vm.runInContext("listTabs()", context))[1].provenance, 0);
  await vm.runInContext(`selectTab({ sessionId: "test-session", tabId: 2,
    expectedUrl: "https://www.youtube.com/", expectedTitle: "YouTube", requireActive: false })`, context);
  assert.equal((await vm.runInContext("listTabs()", context))[1].provenance, 0);
  assert.deepEqual(focusCalls, [["tab", 2], ["window", 3]]);
  await vm.runInContext(`selectTab({ sessionId: "test-session", tabId: 1,
    expectedUrl: "https://www.reddit.com/", requireActive: true })`, context);
  assert.deepEqual(focusCalls.slice(2), [["tab", 1], ["window", 3]]);
  assert.equal(domMessages, 0);
});

test("owned task focus activates its tab and containing window; passive observe does not", async () => {
  const calls = [];
  const traces = [];
  let focusConfirmed = true;
  const listeners = new Map();
  const event = (name) => ({ addListener(fn) { listeners.set(name, fn); }, removeListener() {} });
  const port = { onMessage: event(), onDisconnect: event(), postMessage() {} };
  const chrome = {
    runtime: { id: "extension", connectNative: () => port, onStartup: event(), onInstalled: event() },
    alarms: { onAlarm: event(), clear: async () => {}, create() {} },
    action: { onClicked: event() },
    storage: { session: { get: async () => ({}), set: async () => {} } },
    tabs: {
      onRemoved: event(), onUpdated: event("tab-updated"), onActivated: event("tab-activated"),
      get: async () => ({ id: 7, windowId: 3, active: true }), query: async () => [{ id: 7 }],
      update: async (id, properties) => { calls.push(["tab", id, properties]); return { id, windowId: 3 }; },
      sendMessage: async () => ({ revision: "r", url: "https://example.com/", title: "Example", elements: [] })
    },
    windows: {
      onFocusChanged: event("window-focused"),
      get: async () => ({ state: "normal", focused: focusConfirmed }),
      update: async (id, properties) => { calls.push(["window", id, properties]); return { focused: focusConfirmed }; }
    },
    webNavigation: { onBeforeNavigate: event(), onCommitted: event("nav-committed"),
      onCompleted: event(), onErrorOccurred: event(), onHistoryStateUpdated: event() },
    scripting: { executeScript: async () => {} }
  };
  const context = vm.createContext({ chrome, console: { info: (...args) => traces.push(args), warn() {} }, setTimeout, clearTimeout, URL });
  vm.runInContext(source("background.js"), context);
  vm.runInContext('ownedTaskTabs.set(7, "owned-session")', context);
  await vm.runInContext('observeOwnedTab({ tabId: 7, sessionId: "owned-session" })', context);
  assert.equal(calls.length, 0);
  await listeners.get("tab-updated")(7, { status: "loading", url: "https://example.com/previous" }, { url: "https://example.com/previous" });
  await listeners.get("nav-committed")({ tabId: 7, frameId: 0, url: "https://example.com/previous", transitionQualifiers: ["forward_back"] });
  assert.equal(calls.length, 0);
  assert.ok(traces.some(entry => entry.includes("nav-committed")));
  await vm.runInContext('focusTaskTab({ tabId: 7, sessionId: "owned-session" })', context);
  assert.deepEqual(calls.map(x => x[0]), ["tab", "window"]);
  assert.equal(calls[0][2].active, true);
  assert.equal(calls[1][2].focused, true);
  await assert.rejects(vm.runInContext('focusTaskTab({ tabId: 8, sessionId: "owned-session" })', context));
  assert.equal(calls.length, 2);
  focusConfirmed = false;
  await assert.rejects(vm.runInContext('focusTaskTab({ tabId: 7, sessionId: "owned-session" })', context),
    error => error.code === "FOCUS_FAILED");
  assert.deepEqual(calls.filter(call => call[0] === "window").map(call => call[1]), [3, 3, 3]);
  assert.ok(traces.some(entry => entry[0] === "VoiceOS browser surface preparation"
    && entry[1].tab === 7 && entry[1].window === 3 && entry[1].window_focus === "failed"));
});

test("observed cross-origin anchor uses its real DOM activation", () => {
  let listener;
  let assigned;
  let clicked = 0;
  class HTMLElement {
    isConnected = true;
    focus() {}
    getBoundingClientRect() { return { x: 0, y: 0, width: 100, height: 30, bottom: 30, right: 100, top: 0, left: 0 }; }
    getAttribute() { return null; }
    closest() { return null; }
  }
  class HTMLAnchorElement extends HTMLElement {
    href = "https://github.com/example/repo";
    target = "";
    download = "";
    innerText = "Example repository";
    click() { clicked++; }
  }
  const anchor = new HTMLAnchorElement();
  const document = {
    querySelectorAll: () => [anchor], body: { innerText: "Example repository" },
    documentElement: { scrollHeight: 800 }, title: "Results"
  };
  const location = { href: "https://www.google.com/search?q=repo", origin: "https://www.google.com",
    assign: (url) => { assigned = url; } };
  const chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  const context = vm.createContext({ chrome, document, location, HTMLElement, HTMLAnchorElement,
    HTMLButtonElement: class {}, HTMLInputElement: class {}, HTMLTextAreaElement: class {}, HTMLSelectElement: class {},
    window: { innerWidth: 1280, innerHeight: 800, scrollX: 0, scrollY: 0 }, innerWidth: 1280, innerHeight: 800,
    history: { length: 2 }, URL, Date, crypto: require("node:crypto").webcrypto,
    getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1" }) });
  vm.runInContext(source("content.js"), context);
  let observation;
  listener({ type: "VOICEOS_OBSERVE" }, null, value => { observation = value; });
  listener({ type: "VOICEOS_ACT", action: "CLICK", revision: observation.revision, elementRef: "e1" }, null, () => {});
  assert.equal(assigned, undefined);
  assert.equal(clicked, 1);
});

test("openTaskTab resolves after document-ready + SETTLE, focusing while still loading, without waiting for full load", async () => {
  const focusCalls = [];
  const { chrome, fire } = createMockChrome({
    tabs: {
      create: async () => ({ id: 5, windowId: 1 }),
      get: async (id) => ({ id, windowId: 1, active: true, status: "loading" }),
      update: async (id, properties) => { focusCalls.push(properties); return { id, windowId: 1, ...properties }; },
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 10, mutations: 0, actionable: true };
        return { revision: "r1", url: "https://example.com/", title: "Example",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  const promise = vm.runInContext('openTaskTab({ sessionId: "task-open-01", url: "https://example.com/" })', context);
  await new Promise((resolve) => setTimeout(resolve, 15));
  assert.ok(focusCalls.some((properties) => properties.active === true), "focus happens while the tab is still loading");
  fire("nav-dcl", { tabId: 5, frameId: 0 });
  const result = await promise;
  assert.equal(result.snapshot.url, "https://example.com/");
  assert.ok(result.timings);
  assert.ok(Number.isInteger(result.timings.created));
  assert.ok(Number.isInteger(result.timings.domContentLoaded));
  assert.ok(Number.isInteger(result.timings.focused));
  assert.ok(Number.isInteger(result.timings.settled));
  assert.ok(Number.isInteger(result.timings.observed));
});

test("waitForDocumentReady rejects with TAB_TIMEOUT when neither DOMContentLoaded nor status complete arrives", async () => {
  const { chrome } = createMockChrome({ tabs: { get: async (id) => ({ id, windowId: 1, status: "loading" }) } });
  const context = backgroundContext(chrome);
  await assert.rejects(
    vm.runInContext("waitForDocumentReady(42, 30)", context),
    (error) => error.code === "TAB_TIMEOUT"
  );
});

test("ACT cross_document: settles on the new document without waiting for status complete", async () => {
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 7, windowId: 1, active: true, status: "complete", url: "https://source.example/" }],
      get: async (id) => ({ id, windowId: 1, active: true, status: "loading" }),
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return { navigation: { method: "element.click" } };
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        return { revision: "r1", url: "https://dest.example/", title: "Dest",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(7, "session-cross-doc")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 7, sessionId: "session-cross-doc", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-before", { tabId: 7, frameId: 0 });
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-committed", { tabId: 7, frameId: 0 });
  fire("nav-dcl", { tabId: 7, frameId: 0 });
  const started = Date.now();
  const result = await promise;
  assert.ok(Date.now() - started < 1_000, "should not wait for full page load");
  assert.equal(result.timings.signal, "cross_document");
  assert.equal(result.snapshot.url, "https://dest.example/");
});

test("ACT cross_document never settles on the old document before the new one commits", async () => {
  const sent = [];
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 12, windowId: 1, active: true, status: "complete", url: "https://old.example/" }],
      // Right after onBeforeNavigate Chrome still reports the old document as complete.
      get: async (id) => ({ id, windowId: 1, active: true, status: "complete", url: "https://old.example/" }),
      sendMessage: async (id, message) => {
        sent.push(message.type);
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return { navigation: { method: "element.click" } };
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        return { revision: "r2", url: "https://new.example/", title: "New",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(12, "session-old-doc")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 12, sessionId: "session-old-doc", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-before", { tabId: 12, frameId: 0 });
  await new Promise((resolve) => setTimeout(resolve, 200));
  assert.equal(sent.includes("VOICEOS_SETTLE"), false, "must not settle before the new document commits");
  assert.equal(sent.includes("VOICEOS_OBSERVE"), false, "must not observe the pre-navigation DOM");
  fire("nav-committed", { tabId: 12, frameId: 0 });
  fire("nav-dcl", { tabId: 12, frameId: 0 });
  const result = await promise;
  assert.equal(result.timings.signal, "cross_document");
  assert.equal(result.snapshot.url, "https://new.example/");
});

test("ACT same_document: settles with requireActionable false and a bounded maxMs", async () => {
  const seenSettleOptions = [];
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 8, windowId: 1, active: true, status: "complete", url: "https://spa.example/" }],
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return { navigation: { method: "element.click" } };
        if (message.type === "VOICEOS_SETTLE") {
          seenSettleOptions.push(message);
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 1, actionable: false };
        }
        return { revision: "r1", url: "https://spa.example/#tab2", title: "SPA",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(8, "session-same-doc")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 8, sessionId: "session-same-doc", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-history", { tabId: 8, frameId: 0 });
  const result = await promise;
  assert.equal(result.timings.signal, "same_document");
  assert.equal(seenSettleOptions.length, 1);
  assert.equal(seenSettleOptions[0].requireActionable, false);
  assert.ok(seenSettleOptions[0].maxMs <= 800);
});

test("ACT with no navigation signal waits the ~800ms no-signal budget and reports signal none", async () => {
  const { chrome } = createMockChrome({
    tabs: {
      query: async () => [{ id: 9, windowId: 1, active: true, status: "complete", url: "https://static.example/" }],
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return {};
        return { revision: "r1", url: "https://static.example/", title: "Static",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(9, "session-no-signal")', context);
  const started = Date.now();
  const result = await vm.runInContext(
    'actInOwnedTab({ tabId: 9, sessionId: "session-no-signal", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  const elapsed = Date.now() - started;
  assert.ok(elapsed >= 750, `expected >= 750ms, got ${elapsed}`);
  assert.equal(result.timings.signal, "none");
});

test("ACT late navigation signal (after 350ms) still switches to the cross_document path", async () => {
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 10, windowId: 1, active: true, status: "complete", url: "https://late.example/" }],
      get: async (id) => ({ id, windowId: 1, active: true, status: "loading" }),
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return {};
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        return { revision: "r1", url: "https://late.example/next", title: "Next",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(10, "session-late-nav")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 10, sessionId: "session-late-nav", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 500));
  fire("nav-before", { tabId: 10, frameId: 0 });
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-committed", { tabId: 10, frameId: 0 });
  fire("nav-dcl", { tabId: 10, frameId: 0 });
  const result = await promise;
  assert.equal(result.timings.signal, "cross_document");
});

test("BACK: commit + onCompleted (bfcache) resolves cross_document without waiting for status complete", async () => {
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 11, windowId: 1, active: true, status: "complete", url: "https://back.example/two" }],
      get: async (id) => ({ id, windowId: 1, active: true, status: "loading" }),
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return { navigation: { method: "history.back" } };
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        return { revision: "r1", url: "https://back.example/one", title: "One",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(11, "session-back-nav")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 11, sessionId: "session-back-nav", revision: "r1", action: "BACK" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-committed", { tabId: 11, frameId: 0 });
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-completed", { tabId: 11, frameId: 0 });
  const result = await promise;
  assert.equal(result.timings.signal, "cross_document");
  assert.equal(result.snapshot.url, "https://back.example/one");
});

// BACK harness: the tab starts at `before`; `tabs.get` reports `after()` once the action ran.
function backChrome({ before = "https://back.example/two", after = () => before, goBack } = {}) {
  const calls = { goBack: 0, settle: 0 };
  const created = createMockChrome({
    tabs: {
      query: async () => [{ id: 12, windowId: 1, active: true, status: "complete", url: before }],
      get: async (id) => ({ id, windowId: 1, active: true, status: "complete", url: after() }),
      goBack: async (id) => { calls.goBack++; if (goBack) await goBack(id); },
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return { acted: true, navigation: null };
        if (message.type === "VOICEOS_SETTLE") {
          calls.settle++;
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        }
        return { revision: "r2", url: after(), title: "Page", visibleText: "", truncated: false,
          viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(created.chrome);
  vm.runInContext('ownedTaskTabs.set(12, "session-back-12")', context);
  const act = () => vm.runInContext(
    'actInOwnedTab({ tabId: 12, sessionId: "session-back-12", revision: "r1", action: "BACK" })', context);
  return { ...created, calls, act };
}

test("BACK: cross-document traversal via the browser succeeds with the previous page's snapshot", async () => {
  let navigated = false;
  const { fire, calls, act } = backChrome({ after: () => navigated ? "https://back.example/one" : "https://back.example/two" });
  const promise = act();
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-before", { tabId: 12, frameId: 0 });
  navigated = true;
  fire("nav-committed", { tabId: 12, frameId: 0, transitionQualifiers: ["forward_back"] });
  fire("nav-dcl", { tabId: 12, frameId: 0 });
  const result = await promise;
  assert.equal(calls.goBack, 1);
  assert.equal(result.timings.signal, "cross_document");
  assert.equal(result.snapshot.url, "https://back.example/one");
});

test("BACK: same-document history traversal succeeds", async () => {
  let navigated = false;
  const { fire, act } = backChrome({ before: "https://spa.example/#/b",
    after: () => navigated ? "https://spa.example/#/a" : "https://spa.example/#/b" });
  const promise = act();
  await new Promise((resolve) => setTimeout(resolve, 20));
  navigated = true;
  fire("nav-history", { tabId: 12, frameId: 0, transitionQualifiers: ["forward_back"] });
  const result = await promise;
  assert.equal(result.timings.signal, "same_document");
  assert.equal(result.snapshot.url, "https://spa.example/#/a");
});

test("BACK: a same-document traversal that keeps the URL still succeeds", async () => {
  const { fire, act } = backChrome({ before: "https://spa.example/feed" });
  const promise = act();
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-history", { tabId: 12, frameId: 0, transitionQualifiers: ["forward_back"] });
  const result = await promise;
  assert.equal(result.timings.signal, "same_document");
});

test("BACK: no prior history entry is NO_HISTORY, never success", async () => {
  const { calls, act } = backChrome({
    goBack: async () => { throw new Error("Cannot find a next page in history."); }
  });
  await assert.rejects(act(), error => error.code === "NO_HISTORY");
  assert.equal(calls.goBack, 1);
  assert.equal(calls.settle, 0);
});

test("BACK: no navigation signal and unchanged URL is NAVIGATION_NOT_OBSERVED", async () => {
  const { act } = backChrome();
  const started = Date.now();
  await assert.rejects(act(), error => error.code === "NAVIGATION_NOT_OBSERVED");
  assert.ok(Date.now() - started >= 1_400, "BACK waits its full no-signal budget before failing");
});

test("BACK: a page's own pushState is not evidence of going back", async () => {
  const { fire, act } = backChrome();
  const promise = act();
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-history", { tabId: 12, frameId: 0, transitionQualifiers: [] });
  await assert.rejects(promise, error => error.code === "NAVIGATION_NOT_OBSERVED");
});

test("BACK: a URL change without a traversal event is accepted as evidence", async () => {
  let navigated = false;
  const { act } = backChrome({ after: () => navigated ? "https://back.example/one" : "https://back.example/two",
    goBack: async () => { navigated = true; } });
  const result = await act();
  assert.equal(result.timings.signal, "none");
  assert.equal(result.snapshot.url, "https://back.example/one");
});

test("BACK: a started navigation that errors is NAVIGATION_FAILED", async () => {
  const { fire, act } = backChrome();
  const promise = act();
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-before", { tabId: 12, frameId: 0 });
  fire("nav-error", { tabId: 12, frameId: 0 });
  await assert.rejects(promise, error => error.code === "NAVIGATION_FAILED");
});

test("CLICK: a page's own pushState still counts as a same-document effect", async () => {
  const { chrome, fire } = createMockChrome({
    tabs: {
      query: async () => [{ id: 13, windowId: 1, active: true, status: "complete", url: "https://spa.example/" }],
      sendMessage: async (id, message) => {
        if (message.type === "VOICEOS_PING") return { ok: true };
        if (message.type === "VOICEOS_ACT") return {};
        if (message.type === "VOICEOS_SETTLE")
          return { settled: true, reason: "quiet", waitedMs: 5, mutations: 0, actionable: true };
        return { revision: "r2", url: "https://spa.example/next", title: "Next",
          visibleText: "", truncated: false, viewport: {}, elements: [] };
      }
    }
  });
  const context = backgroundContext(chrome);
  vm.runInContext('ownedTaskTabs.set(13, "session-click-13")', context);
  const promise = vm.runInContext(
    'actInOwnedTab({ tabId: 13, sessionId: "session-click-13", revision: "r1", action: "CLICK", elementRef: "e1" })',
    context);
  await new Promise((resolve) => setTimeout(resolve, 20));
  fire("nav-history", { tabId: 13, frameId: 0, transitionQualifiers: [] });
  const result = await promise;
  assert.equal(result.timings.signal, "same_document");
});

test("CLOSE_TASK_TAB closes an owned task tab but refuses an adopted tab and a tab owned by another session", async () => {
  const removed = [];
  const { chrome } = createMockChrome({ tabs: { remove: async (id) => { removed.push(id); } } });
  const context = backgroundContext(chrome);
  vm.runInContext(`
    ownedTaskTabs.set(20, "session-close-01");
    ownedTaskTabs.set(21, "session-close-01");
    adoptedUserTabs.add(21);
    ownedTaskTabs.set(22, "session-other-02");
  `, context);
  const result = await vm.runInContext('closeTaskTab({ tabId: 20, sessionId: "session-close-01" })', context);
  assert.equal(result.closed, true);
  assert.deepEqual(removed, [20]);
  await assert.rejects(vm.runInContext('closeTaskTab({ tabId: 21, sessionId: "session-close-01" })', context),
    (error) => error.code === "TAB_NOT_OWNED");
  await assert.rejects(vm.runInContext('closeTaskTab({ tabId: 22, sessionId: "session-close-01" })', context),
    (error) => error.code === "SESSION_MISMATCH");
  assert.deepEqual(removed, [20]);
});

test("content SETTLE resolves quiet after the quiet window with no mutations", async () => {
  let listener;
  let observedTarget;
  let observedOptions;
  class FakeMutationObserver {
    constructor(callback) { this.callback = callback; }
    observe(target, options) { observedTarget = target; observedOptions = options; }
  }
  const document = { readyState: "loading", querySelectorAll: () => [], addEventListener() {} };
  const chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  const context = vm.createContext({
    chrome, document, MutationObserver: FakeMutationObserver,
    performance: { now: () => Date.now() }, setTimeout, clearTimeout, Date,
    HTMLElement: class {}, HTMLAnchorElement: class {}, HTMLButtonElement: class {},
    HTMLInputElement: class {}, HTMLTextAreaElement: class {}, HTMLSelectElement: class {}
  });
  vm.runInContext(source("content.js"), context);
  assert.equal(observedTarget, document);
  assert.equal(observedOptions.childList, true);
  assert.equal(observedOptions.subtree, true);
  const response = await new Promise((resolve) => {
    listener({ type: "VOICEOS_SETTLE", quietMs: 40, maxMs: 500, requireActionable: false }, null, resolve);
  });
  assert.equal(response.settled, true);
  assert.equal(response.reason, "quiet");
  assert.equal(response.mutations, 0);
});

test("content SETTLE resolves max when mutations keep arriving", async () => {
  let listener;
  let observerCallback;
  class FakeMutationObserver {
    constructor(callback) { observerCallback = callback; }
    observe() {}
  }
  const document = { readyState: "loading", querySelectorAll: () => [], addEventListener() {} };
  const chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  const context = vm.createContext({
    chrome, document, MutationObserver: FakeMutationObserver,
    performance: { now: () => Date.now() }, setTimeout, clearTimeout, Date,
    HTMLElement: class {}, HTMLAnchorElement: class {}, HTMLButtonElement: class {},
    HTMLInputElement: class {}, HTMLTextAreaElement: class {}, HTMLSelectElement: class {}
  });
  vm.runInContext(source("content.js"), context);
  const interval = setInterval(() => observerCallback([]), 20);
  const response = await new Promise((resolve) => {
    listener({ type: "VOICEOS_SETTLE", quietMs: 40, maxMs: 150, requireActionable: false }, null, resolve);
  });
  clearInterval(interval);
  assert.equal(response.reason, "max");
  assert.ok(response.mutations > 0);
});

test("content SETTLE with requireActionable true falls back to max when nothing is actionable", async () => {
  let listener;
  class FakeMutationObserver { observe() {} }
  const document = { readyState: "complete", querySelectorAll: () => [], addEventListener() {} };
  const chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  const context = vm.createContext({
    chrome, document, MutationObserver: FakeMutationObserver,
    performance: { now: () => Date.now() }, setTimeout, clearTimeout, Date,
    HTMLElement: class {}, HTMLAnchorElement: class {}, HTMLButtonElement: class {},
    HTMLInputElement: class {}, HTMLTextAreaElement: class {}, HTMLSelectElement: class {}
  });
  vm.runInContext(source("content.js"), context);
  const response = await new Promise((resolve) => {
    listener({ type: "VOICEOS_SETTLE", quietMs: 20, maxMs: 80, requireActionable: true }, null, resolve);
  });
  assert.equal(response.reason, "max");
  assert.equal(response.actionable, false);
});

function observeFormControls(specs, pageText = "") {
  let listener;
  class HTMLElement {
    isConnected = true;
    getBoundingClientRect() { return { x: 0, y: 0, width: 100, height: 30, bottom: 30, right: 100, top: 0, left: 0 }; }
    getAttribute(name) { return this.attributes?.[name] ?? null; }
    closest(selector) { return selector.includes("form") ? { innerText: pageText } : null; }
  }
  class HTMLInputElement extends HTMLElement { type = "text"; value = ""; }
  class HTMLTextAreaElement extends HTMLElement { value = ""; }
  const controls = specs.map(({ textarea, ...spec }) => Object.assign(
    textarea ? new HTMLTextAreaElement() : new HTMLInputElement(), spec));
  const document = {
    querySelectorAll: () => controls, body: { innerText: pageText },
    documentElement: { scrollHeight: 0 }, title: pageText
  };
  const context = vm.createContext({
    chrome: { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } },
    document, location: { href: "https://example.com/login" }, HTMLElement, HTMLInputElement, HTMLTextAreaElement,
    HTMLAnchorElement: class extends HTMLElement {}, HTMLButtonElement: class {}, HTMLSelectElement: class {},
    window: { innerWidth: 1280, innerHeight: 800, scrollX: 0, scrollY: 0 }, innerWidth: 1280, innerHeight: 800,
    history: { length: 1 }, Date, crypto: require("node:crypto").webcrypto,
    getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1" })
  });
  vm.runInContext(source("content.js"), context);
  let observation;
  listener({ type: "VOICEOS_OBSERVE" }, null, (value) => { observation = value; });
  assert.equal(observation.__voiceOSError, undefined);
  return observation;
}

test("content observation omits password values while preserving editable login controls", () => {
  for (const value of ["hunter2", "", "a much longer password"]) {
    const observation = observeFormControls([{ type: "password", value, attributes: { "aria-label": "Password" } }]);
    const control = observation.elements[0];
    assert.equal(control.role, "textbox");
    assert.equal(control.name, "Password");
    assert.equal(control.editable, true);
    assert.equal(control.enabled, true);
    assert.equal(control.ref, "e1");
    assert.equal(control.value, null);
    if (value) assert.equal(JSON.stringify(observation).includes(value), false);
  }
});

test("content observation removes echoed credentials from labels, context, page text and other values", () => {
  const observation = observeFormControls([
    { type: "password", value: "hunter2", attributes: { "aria-label": "Password hunter2" } },
    { type: "text", value: "echo hunter2", attributes: { placeholder: "hunter2" } }
  ], "Login hunter2");
  assert.equal(JSON.stringify(observation).includes("hunter2"), false);
  assert.equal(observation.elements[0].value, null);
  assert.equal(observation.elements[1].value, "echo [redacted]");
});

test("content observation omits explicit credential autocomplete values generically", () => {
  for (const token of ["current-password", "new-password", "one-time-code", "cc-csc"]) {
    for (const textarea of [false, true]) {
      const observation = observeFormControls([{ textarea, type: "text", value: "hunter2",
        attributes: { autocomplete: `section-login ${token.toUpperCase()}` } }], "Code hunter2");
      assert.equal(observation.elements[0].value, null);
      assert.equal(observation.elements[0].editable, true);
      assert.equal(JSON.stringify(observation).includes("hunter2"), false);
    }
  }
});

test("content observation preserves ordinary form values", () => {
  for (const type of ["text", "email", "tel", "search", "number"]) {
    const observation = observeFormControls([{ type, value: "ordinary value" }]);
    assert.equal(observation.elements[0].value, "ordinary value");
  }
  assert.equal(observeFormControls([{ textarea: true, value: "ordinary notes" }]).elements[0].value, "ordinary notes");
});

test("content observe marks the search field via a [role=search] ancestor, independent of type=search", () => {
  let listener;
  class HTMLElement {
    isConnected = true;
    focus() {}
    getBoundingClientRect() { return { x: 0, y: 0, width: 100, height: 30, bottom: 30, right: 100, top: 0, left: 0 }; }
    getAttribute() { return null; }
    closest() { return this._searchContainer ? {} : null; }
  }
  class HTMLInputElement extends HTMLElement { type = "text"; value = ""; }
  const searchInput = new HTMLInputElement();
  searchInput._searchContainer = true;
  const plainInput = new HTMLInputElement();
  const document = {
    querySelectorAll: () => [searchInput, plainInput],
    body: { innerText: "" }, documentElement: { scrollHeight: 0 }, title: "Page"
  };
  const location = { href: "https://example.com/" };
  const chrome = { runtime: { onMessage: { addListener: (fn) => { listener = fn; } } } };
  const context = vm.createContext({
    chrome, document, location, HTMLElement, HTMLAnchorElement: class extends HTMLElement {},
    HTMLButtonElement: class {}, HTMLInputElement, HTMLTextAreaElement: class {}, HTMLSelectElement: class {},
    window: { innerWidth: 1280, innerHeight: 800, scrollX: 0, scrollY: 0 }, innerWidth: 1280, innerHeight: 800,
    history: { length: 1 }, URL, Date, crypto: require("node:crypto").webcrypto,
    getComputedStyle: () => ({ display: "block", visibility: "visible", opacity: "1" })
  });
  vm.runInContext(source("content.js"), context);
  let observation;
  listener({ type: "VOICEOS_OBSERVE" }, null, (value) => { observation = value; });
  assert.equal(observation.elements[0].search, true);
  assert.equal(observation.elements[1].search, false);
});
