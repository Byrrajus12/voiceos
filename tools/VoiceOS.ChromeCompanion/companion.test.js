const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

const source = (name) => fs.readFileSync(path.join(__dirname, name), "utf8");

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
