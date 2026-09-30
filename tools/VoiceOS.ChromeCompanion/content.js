(() => {
  if (globalThis.__voiceOSCompanionInstalled) return;
  globalThis.__voiceOSCompanionInstalled = true;

  const MAX_ELEMENTS = 100;
  const MAX_VISIBLE_TEXT = 3_500;
  const MAX_CONTEXT_TEXT = 180;
  const CANDIDATE_SELECTOR = [
    "a[href]", "button", "input", "textarea", "select", "summary",
    "[contenteditable='true']", "[role='button']", "[role='link']",
    "[role='tab']", "[role='menuitem']", "[role='option']",
    "[role='searchbox']", "[role='combobox']", "[role='textbox']"
  ].join(",");
  let observationCounter = 0;
  let currentRevision = null;
  let currentElements = new Map();
  let observationSecrets = [];

  let mutationCount = 0;
  let lastMutationAt = now();
  let loadCompleteAt = (typeof document !== "undefined" && document.readyState === "complete") ? now() : null;

  try {
    if (typeof MutationObserver !== "undefined" && typeof document !== "undefined") {
      new MutationObserver(() => { mutationCount++; lastMutationAt = now(); })
        .observe(document, { childList: true, subtree: true });
    }
  } catch { /* Not observable in this environment; SETTLE degrades to load-based checks. */ }

  try {
    document.addEventListener?.("readystatechange", () => {
      if (document.readyState === "complete" && loadCompleteAt === null) loadCompleteAt = now();
    });
  } catch { /* No document to listen on. */ }

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    try {
      if (message?.type === "VOICEOS_PING") {
        sendResponse({ ok: true });
        return false;
      }
      if (message?.type === "VOICEOS_OBSERVE") {
        sendResponse(observe());
        return false;
      }
      if (message?.type === "VOICEOS_ACT") {
        const navigation = performAction(message);
        sendResponse({ acted: true, navigation });
        return false;
      }
      if (message?.type === "VOICEOS_SETTLE") {
        settle(message).then(sendResponse).catch(() => sendResponse(settleErrorResponse()));
        return true;
      }
    } catch (error) {
      sendResponse({
        __voiceOSError: true,
        code: error?.code ?? "CONTENT_ERROR",
        message: error instanceof Error ? error.message : String(error)
      });
      return false;
    }
    return false;
  });

  function now() {
    return typeof performance !== "undefined" && typeof performance.now === "function" ? performance.now() : Date.now();
  }

  function settleErrorResponse() {
    return { settled: true, reason: "error", waitedMs: 0, mutations: mutationCount, actionable: false };
  }

  function settle(options) {
    const quietMs = Number.isFinite(options?.quietMs) ? options.quietMs : 300;
    const maxMs = Number.isFinite(options?.maxMs) ? options.maxMs : 2_000;
    const requireActionable = Boolean(options?.requireActionable);
    const startedAt = now();

    return new Promise((resolve) => {
      // Evaluated only once a timing condition holds, and sticky once true: the scan
      // touches computed style for every candidate and is too costly to poll blindly.
      let actionableSeen = !requireActionable;
      const actionableNow = () => actionableSeen || (actionableSeen = hasActionable());
      const finish = (reason) => resolve({
        settled: true,
        reason,
        waitedMs: Math.round(now() - startedAt),
        mutations: mutationCount,
        actionable: actionableNow()
      });
      const check = () => {
        try {
          const elapsed = now() - startedAt;
          const quiet = now() - lastMutationAt;
          const complete = typeof document !== "undefined" && document.readyState === "complete";
          const reason = quiet >= quietMs ? "quiet"
            : complete && quiet >= Math.min(100, quietMs) ? "load_quiet"
            : complete && loadCompleteAt !== null && (now() - loadCompleteAt) >= 300 ? "load_hold"
            : null;
          if (reason && actionableNow()) { finish(reason); return; }
          if (elapsed >= maxMs) { finish("max"); return; }
          setTimeout(check, 30);
        } catch {
          resolve(settleErrorResponse());
        }
      };
      check();
    });
  }

  function hasActionable() {
    try {
      const candidates = document.querySelectorAll(CANDIDATE_SELECTOR);
      for (const element of candidates) {
        if (!geometryOf(element)) continue;
        if (isEditable(element) || isClickable(element)) return true;
      }
      return false;
    } catch {
      return false;
    }
  }

  function observe() {
    observationCounter += 1;
    currentRevision = `${Date.now().toString(36)}-${observationCounter}-${randomToken()}`;
    currentElements = new Map();

    const candidates = Array.from(document.querySelectorAll(CANDIDATE_SELECTOR));
    // Only explicit credential controls; this is not a general PII detector.
    observationSecrets = candidates.filter(isCredentialControl)
      .flatMap((element) => [String(element.value ?? ""), String(element.value ?? "").replace(/\s+/g, " ").trim()])
      .filter(Boolean).sort((left, right) => right.length - left.length);
    try {
      return buildObservation(candidates);
    } finally {
      observationSecrets = [];
    }
  }

  function buildObservation(candidates) {
    const visible = candidates
      .map((element, documentOrder) => ({ element, documentOrder, geometry: geometryOf(element) }))
      .filter((item) => item.geometry !== null)
      .sort((left, right) => {
        const leftInViewport = left.geometry.inViewport ? 0 : 1;
        const rightInViewport = right.geometry.inViewport ? 0 : 1;
        return leftInViewport - rightInViewport || left.documentOrder - right.documentOrder;
      })
      .slice(0, MAX_ELEMENTS);

    const elements = visible.map(({ element, geometry }, index) => {
      const ref = `e${index + 1}`;
      currentElements.set(ref, element);
      const role = roleOf(element);
      const editable = isEditable(element);
      const result = {
        ref,
        role,
        name: accessibleName(element),
        enabled: isEnabled(element),
        editable,
        search: isSearchField(element),
        value: currentValue(element),
        geometry,
        context: nearbyContext(element)
      };
      if (element instanceof HTMLAnchorElement && element.href) {
        result.href = redactObservationText(element.href);
      }
      Object.assign(result, structuralFacts(element));
      return result;
    });

    const bodyText = normalizeText(document.body?.innerText ?? "");

    return {
      revision: currentRevision,
      url: redactObservationText(location.href),
      title: redactObservationText(document.title),
      visibleText: bodyText.slice(0, MAX_VISIBLE_TEXT),
      truncated: bodyText.length > MAX_VISIBLE_TEXT,
      pageText: bodyText.slice(0, 12_000),
      viewport: {
        width: window.innerWidth,
        height: window.innerHeight,
        scrollX: Math.round(window.scrollX),
        scrollY: Math.round(window.scrollY),
        documentHeight: Math.round(document.documentElement.scrollHeight)
      },
      elements,
      canGoBack: history.length > 1
    };
  }

  function performAction(message) {
    if (message.revision !== currentRevision) {
      throw actionError("STALE_REVISION", "The element reference belongs to an older observation.");
    }

    if (message.action === "SCROLL") {
      if (!["up", "down"].includes(message.direction)) {
        throw actionError("INVALID_ACTION", "SCROLL requires an offered up/down direction.");
      }
      window.scrollBy({ top: (message.direction === "down" ? 1 : -1) * Math.max(200, innerHeight * 0.8), behavior: "instant" });
      return;
    }

    if (message.action === "BACK" || message.action === "FORWARD") {
      // Only the revision is checked here. The service worker performs the browser-level
      // traversal and confirms it from navigation evidence; a page cannot report it.
      return null;
    }

    const element = currentElements.get(message.elementRef);
    if (!(element instanceof HTMLElement) || !element.isConnected) {
      throw actionError("STALE_ELEMENT", "The referenced element is no longer connected.");
    }
    if (!geometryOf(element)) {
      throw actionError("ELEMENT_NOT_VISIBLE", "The referenced element is no longer visible.");
    }
    if (!isEnabled(element)) {
      throw actionError("ELEMENT_DISABLED", "The referenced element is disabled.");
    }

    if (message.action === "CLICK") {
      if (!isClickable(element)) {
        throw actionError("NOT_CLICKABLE", "CLICK requires a recognized interactive control.");
      }
      element.focus({ preventScroll: false });
      element.click();
      return { method: "element.click", href: element instanceof HTMLAnchorElement ? element.href : null };
    }

    if (message.action === "SUBMIT") {
      // Apply the text in a field as Enter would: key events for script-driven search boxes, then the form itself
      // when nothing handled the key.
      if (!isEditable(element)) {
        throw actionError("NOT_EDITABLE", "SUBMIT requires an editable control.");
      }
      element.focus({ preventScroll: false });
      const init = { key: "Enter", code: "Enter", keyCode: 13, which: 13, bubbles: true, cancelable: true };
      const down = new KeyboardEvent("keydown", init);
      element.dispatchEvent(down);
      element.dispatchEvent(new KeyboardEvent("keypress", init));
      element.dispatchEvent(new KeyboardEvent("keyup", init));
      const form = element.form ?? element.closest?.("form");
      if (!down.defaultPrevented && form && typeof form.requestSubmit === "function") form.requestSubmit();
      return { method: "enter", href: null };
    }

    if (message.action === "REPLACE_TEXT" || message.action === "INSERT_TEXT") {
      if (!isEditable(element)) {
        throw actionError("NOT_EDITABLE", "TYPE_TEXT requires an editable control.");
      }
      element.focus({ preventScroll: false });
      setElementText(element, message.text, message.action === "INSERT_TEXT");
      return;
    }

    throw actionError("INVALID_ACTION", "The requested action is not supported.");
  }

  function setElementText(element, text, insert) {
    if (element instanceof HTMLInputElement) {
      const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")?.set;
      const start = insert ? element.selectionStart ?? element.value.length : 0;
      const end = insert ? element.selectionEnd ?? start : element.value.length;
      setter?.call(element, element.value.slice(0, start) + text + element.value.slice(end));
      element.setSelectionRange(start + text.length, start + text.length);
    } else if (element instanceof HTMLTextAreaElement) {
      const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value")?.set;
      const start = insert ? element.selectionStart : 0;
      const end = insert ? element.selectionEnd : element.value.length;
      setter?.call(element, element.value.slice(0, start) + text + element.value.slice(end));
      element.setSelectionRange(start + text.length, start + text.length);
    } else if (element.isContentEditable) {
      if (insert && document.getSelection()?.rangeCount) {
        const range = document.getSelection().getRangeAt(0);
        range.deleteContents(); range.insertNode(document.createTextNode(text));
      } else {
        element.textContent = text;
      }
    } else {
      throw actionError("NOT_EDITABLE", "The referenced control cannot accept text.");
    }

    element.dispatchEvent(new InputEvent("input", {
      bubbles: true,
      composed: true,
      inputType: "insertText",
      data: text
    }));
    element.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
  }

  function geometryOf(element) {
    const style = getComputedStyle(element);
    if (style.display === "none" || style.visibility === "hidden" || style.visibility === "collapse" || Number(style.opacity) === 0) {
      return null;
    }
    const rect = element.getBoundingClientRect();
    if (rect.width < 1 || rect.height < 1) return null;
    const inViewport = rect.bottom > 0 && rect.right > 0 && rect.top < innerHeight && rect.left < innerWidth;
    return {
      x: Math.round(rect.x),
      y: Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
      inViewport
    };
  }

  function roleOf(element) {
    const explicit = element.getAttribute("role")?.trim().toLowerCase();
    if (explicit) return explicit;
    if (element instanceof HTMLAnchorElement) return "link";
    if (element instanceof HTMLButtonElement) return "button";
    if (element instanceof HTMLTextAreaElement) return "textbox";
    if (element instanceof HTMLSelectElement) return element.multiple ? "listbox" : "combobox";
    if (element instanceof HTMLInputElement) {
      const type = element.type.toLowerCase();
      if (type === "search") return "searchbox";
      if (["button", "submit", "reset", "image"].includes(type)) return "button";
      if (type === "checkbox") return "checkbox";
      if (type === "radio") return "radio";
      if (["text", "email", "url", "tel", "password", "number"].includes(type)) return "textbox";
    }
    if (element.isContentEditable) return "textbox";
    return element.tagName.toLowerCase();
  }

  function isSearchField(element) {
    if (roleOf(element) === "searchbox") return true;
    if (element instanceof HTMLInputElement && element.type === "search") return true;
    return typeof element.closest === "function" && element.closest("[role='search'], search") !== null;
  }

  // Structural facts only (form ownership, landmark, list membership, state); no site knowledge.
  const formIds = new WeakMap();
  const collectionIds = new WeakMap();
  function structuralFacts(element) {
    const facts = {};
    try {
      const form = typeof element.closest === "function" ? element.closest("form") : null;
      if (form) {
        if (!formIds.has(form)) formIds.set(form, `f${formIds.size + 1}`);
        facts.form = formIds.get(form);
        try { facts.formAction = new URL(form.action, location.href).pathname.slice(0, 120); } catch { /* no usable action */ }
        const type = (element.getAttribute("type") ?? "").toLowerCase();
        if ((element instanceof HTMLInputElement && type === "submit")
          || (element instanceof HTMLButtonElement && (type === "submit" || type === ""))) facts.submit = true;
      }
      const landmark = typeof element.closest === "function"
        ? element.closest("nav, [role='navigation'], [role='search'], search, main, [role='main'], header, footer, aside, [role='dialog']") : null;
      if (landmark) {
        const tag = landmark.tagName.toLowerCase();
        const role = landmark.getAttribute("role")?.toLowerCase();
        facts.landmark = role ?? (tag === "nav" ? "navigation" : tag === "header" ? "banner" : tag === "footer" ? "contentinfo"
          : tag === "aside" ? "complementary" : tag);
      }
      const ITEM = "li, [role='listitem'], article, [role='article'], tr, [role='row']";
      const item = typeof element.closest === "function" ? element.closest(ITEM) : null;
      if (item) {
        facts.inList = true;
        // Order within the item's own collection (DOM order), so "the first result" has a structural meaning.
        const container = item.parentElement;
        if (container) {
          if (!collectionIds.has(container)) collectionIds.set(container, `c${collectionIds.size + 1}`);
          const siblings = Array.from(container.children).filter((child) => child.matches(ITEM));
          const index = siblings.indexOf(item);
          if (index >= 0) {
            facts.collection = collectionIds.get(container);
            facts.position = index + 1;
            facts.collectionSize = siblings.length;
          }
        }
      }
      const state = element.getAttribute("aria-selected") ?? element.getAttribute("aria-checked") ?? element.getAttribute("aria-pressed");
      if (state === "true" || state === "false") facts.selected = state === "true";
      else if (element instanceof HTMLInputElement && (element.type === "checkbox" || element.type === "radio")) facts.selected = element.checked;
    } catch { /* facts are best effort */ }
    return facts;
  }

  function accessibleName(element) {
    const ariaLabel = element.getAttribute("aria-label");
    if (ariaLabel?.trim()) return normalizeText(ariaLabel).slice(0, 240);

    const labelledBy = element.getAttribute("aria-labelledby");
    if (labelledBy) {
      const label = labelledBy.split(/\s+/)
        .map((id) => document.getElementById(id)?.innerText ?? "")
        .join(" ");
      if (label.trim()) return normalizeText(label).slice(0, 240);
    }

    if ("labels" in element && element.labels?.length) {
      const label = Array.from(element.labels).map((item) => item.innerText).join(" ");
      if (label.trim()) return normalizeText(label).slice(0, 240);
    }

    const alt = element.getAttribute("alt");
    if (alt?.trim()) return normalizeText(alt).slice(0, 240);
    const title = element.getAttribute("title");
    if (title?.trim()) return normalizeText(title).slice(0, 240);
    const placeholder = element.getAttribute("placeholder");
    if (placeholder?.trim()) return normalizeText(placeholder).slice(0, 240);
    if (element instanceof HTMLInputElement && ["button", "submit", "reset"].includes(element.type) && element.value) {
      return normalizeText(element.value).slice(0, 240);
    }
    return normalizeText(element.innerText ?? element.getAttribute("name") ?? "").slice(0, 240);
  }

  function currentValue(element) {
    if (isCredentialControl(element)) return null;
    if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement) {
      return redactObservationText(String(element.value)).slice(0, 1_000);
    }
    if (element.isContentEditable) return normalizeText(element.innerText).slice(0, 1_000);
    return null;
  }

  function isCredentialControl(element) {
    if (!(element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement)) return false;
    if (element instanceof HTMLInputElement && element.type.toLowerCase() === "password") return true;
    const tokens = (element.getAttribute("autocomplete") ?? "").toLowerCase().split(/\s+/);
    return tokens.some((token) => ["current-password", "new-password", "one-time-code", "cc-csc"].includes(token));
  }

  function redactObservationText(value) {
    let text = String(value ?? "");
    for (const secret of observationSecrets) text = text.split(secret).join("[redacted]");
    return text;
  }

  function nearbyContext(element) {
    const container = element.closest("form, section, main, nav, fieldset, [role='dialog'], [role='region']");
    if (!container) return "";
    const text = normalizeText(container.innerText ?? "");
    const ownName = accessibleName(element);
    return (ownName ? text.replace(ownName, "") : text).trim().slice(0, MAX_CONTEXT_TEXT);
  }

  function isEnabled(element) {
    return !("disabled" in element && element.disabled) && element.getAttribute("aria-disabled") !== "true";
  }

  function isEditable(element) {
    if (element instanceof HTMLTextAreaElement) return !element.readOnly;
    if (element instanceof HTMLInputElement) {
      return !element.readOnly && ["text", "search", "email", "url", "tel", "password", "number"].includes(element.type);
    }
    return element.isContentEditable;
  }

  function isClickable(element) {
    const role = roleOf(element);
    return ["button", "link", "tab", "menuitem", "option", "checkbox", "radio", "summary"].includes(role)
      || element instanceof HTMLButtonElement
      || element instanceof HTMLAnchorElement
      || (element instanceof HTMLInputElement && ["button", "submit", "reset", "image", "checkbox", "radio"].includes(element.type));
  }

  function normalizeText(value) {
    return redactObservationText(redactObservationText(value).replace(/\s+/g, " ").trim());
  }

  function randomToken() {
    const bytes = new Uint8Array(6);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  }

  function actionError(code, message) {
    const error = new Error(message);
    error.code = code;
    return error;
  }
})();
