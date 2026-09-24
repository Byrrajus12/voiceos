const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");

const source = (name) => fs.readFileSync(path.join(__dirname, name), "utf8");

test("owned task focus activates its tab and containing window; passive observe does not", async () => {
  const calls = [];
  const traces = [];
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
      get: async () => ({ id: 7, windowId: 3 }), query: async () => [{ id: 7 }],
      update: async (id, properties) => { calls.push(["tab", id, properties]); return { id, windowId: 3 }; },
      sendMessage: async () => ({ revision: "r", url: "https://example.com/", title: "Example", elements: [] })
    },
    windows: {
      onFocusChanged: event("window-focused"),
      get: async () => ({ state: "normal" }),
      update: async (id, properties) => { calls.push(["window", id, properties]); return { focused: true }; }
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
