const DEFAULT_CONFIG = {
  relayUrl: "http://127.0.0.1:8787",
  deviceId: "browser",
  deviceToken: "",
  deviceName: "Chrome Browser",
  autoConnect: true,
  allowUnsafeScript: false,
};

const BROWSER_TOOLS = new Set([
  "browser_get_state",
  "browser_click",
  "browser_fill",
  "browser_keypress",
  "browser_navigate",
  "browser_back",
  "browser_forward",
  "browser_reload",
  "browser_screenshot",
  "browser_eval",
]);

let socket = null;
let reconnectTimer = null;
let manualDisconnect = false;
let lastStatus = {
  connected: false,
  state: "disconnected",
  message: "Not connected",
  connectedAt: null,
};

chrome.runtime.onInstalled.addListener(() => {
  void initializeConfig();
  chrome.alarms.create("browser-control-keepalive", { periodInMinutes: 0.5 });
});

chrome.runtime.onStartup.addListener(() => {
  void initializeConfig().then(async () => {
    const config = await getConfig();
    if (config.autoConnect) {
      connect();
    }
  });
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name !== "browser-control-keepalive") {
    return;
  }
  void getConfig().then((config) => {
    if (config.autoConnect && !manualDisconnect && (!socket || socket.readyState === WebSocket.CLOSED)) {
      connect();
    }
  });
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  void (async () => {
    switch (message?.type) {
      case "getStatus":
        return { status: lastStatus, config: await publicConfig() };
      case "saveConfig":
        await chrome.storage.local.set({ browserControlConfig: normalizeConfig(message.config ?? {}) });
        manualDisconnect = false;
        connect();
        return { status: lastStatus, config: await publicConfig() };
      case "connect":
        manualDisconnect = false;
        connect();
        return { status: lastStatus, config: await publicConfig() };
      case "disconnect":
        manualDisconnect = true;
        disconnect("Disconnected by popup");
        return { status: lastStatus, config: await publicConfig() };
      default:
        return { error: "Unknown message" };
    }
  })().then(sendResponse, (error) => {
    sendResponse({ error: error instanceof Error ? error.message : String(error) });
  });
  return true;
});

void initializeConfig().then(async () => {
  const config = await getConfig();
  if (config.autoConnect) {
    connect();
  }
});

async function initializeConfig() {
  const existing = await getConfig();
  await chrome.storage.local.set({ browserControlConfig: normalizeConfig(existing) });
}

async function getConfig() {
  const stored = await chrome.storage.local.get("browserControlConfig");
  return normalizeConfig(stored.browserControlConfig ?? {});
}

function normalizeConfig(value) {
  return {
    relayUrl: typeof value.relayUrl === "string" && value.relayUrl.trim() ? value.relayUrl.trim() : DEFAULT_CONFIG.relayUrl,
    deviceId: typeof value.deviceId === "string" && value.deviceId.trim() ? value.deviceId.trim() : DEFAULT_CONFIG.deviceId,
    deviceToken: typeof value.deviceToken === "string" ? value.deviceToken : DEFAULT_CONFIG.deviceToken,
    deviceName: typeof value.deviceName === "string" && value.deviceName.trim() ? value.deviceName.trim() : DEFAULT_CONFIG.deviceName,
    autoConnect: typeof value.autoConnect === "boolean" ? value.autoConnect : DEFAULT_CONFIG.autoConnect,
    allowUnsafeScript: typeof value.allowUnsafeScript === "boolean" ? value.allowUnsafeScript : DEFAULT_CONFIG.allowUnsafeScript,
  };
}

async function publicConfig() {
  const config = await getConfig();
  return {
    relayUrl: config.relayUrl,
    deviceId: config.deviceId,
    deviceName: config.deviceName,
    autoConnect: config.autoConnect,
    allowUnsafeScript: config.allowUnsafeScript,
    hasDeviceToken: config.deviceToken.length > 0,
  };
}

function connect() {
  clearTimeout(reconnectTimer);
  void (async () => {
    const config = await getConfig();
    if (!config.deviceToken) {
      setStatus(false, "missing-token", "Set a browser device token first");
      return;
    }

    disconnect("Reconnecting", false);
    const wsUrl = toWebSocketUrl(config.relayUrl);
    setStatus(false, "connecting", `Connecting to ${wsUrl.origin}`);

    socket = new WebSocket(wsUrl.toString());
    socket.addEventListener("open", () => {
      socket?.send(JSON.stringify({
        type: "device:hello",
        deviceId: config.deviceId,
        deviceName: config.deviceName,
        token: config.deviceToken,
      }));
      setStatus(true, "connected", `Connected as ${config.deviceId}`, new Date().toISOString());
    });

    socket.addEventListener("message", (event) => {
      void handleRelayMessage(event.data, config);
    });

    socket.addEventListener("close", (event) => {
      setStatus(false, "disconnected", event.reason || `Socket closed (${event.code})`);
      socket = null;
      if (!manualDisconnect && config.autoConnect) {
        reconnectTimer = setTimeout(connect, 2500);
      }
    });

    socket.addEventListener("error", () => {
      setStatus(false, "error", "WebSocket error");
    });
  })().catch((error) => {
    setStatus(false, "error", error instanceof Error ? error.message : String(error));
  });
}

function disconnect(message = "Disconnected", updateStatus = true) {
  clearTimeout(reconnectTimer);
  reconnectTimer = null;
  if (socket) {
    socket.close(1000, message);
    socket = null;
  }
  if (updateStatus) {
    setStatus(false, "disconnected", message);
  }
}

function toWebSocketUrl(relayUrl) {
  const url = new URL(relayUrl);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  url.pathname = "/ws/device";
  url.search = "";
  url.hash = "";
  return url;
}

function setStatus(connected, state, message, connectedAt = null) {
  lastStatus = {
    connected,
    state,
    message,
    connectedAt: connectedAt ?? (connected ? lastStatus.connectedAt : null),
  };
  chrome.runtime.sendMessage({ type: "statusChanged", status: lastStatus }).catch(() => undefined);
}

async function handleRelayMessage(rawData, config) {
  let message;
  try {
    message = JSON.parse(String(rawData));
  } catch {
    return;
  }

  if (message.type === "relay:ping") {
    send({ type: "device:pong", sentAt: new Date().toISOString() });
    return;
  }

  if (message.type !== "tool:request" || !message.request) {
    return;
  }

  const response = await handleToolRequest(message.request, config);
  send({ type: "tool:response", response });
}

function send(payload) {
  if (socket?.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify(payload));
  }
}

async function handleToolRequest(request, config) {
  const requestId = typeof request.requestId === "string" ? request.requestId : crypto.randomUUID();
  try {
    if (!BROWSER_TOOLS.has(request.tool)) {
      return errorResponse(requestId, "BROWSER_TOOL_UNSUPPORTED", `Unsupported browser tool: ${request.tool}`);
    }

    switch (request.tool) {
      case "browser_get_state":
        return okResponse(requestId, await browserGetState(request));
      case "browser_click":
        return okResponse(requestId, await browserClick(request));
      case "browser_fill":
        return okResponse(requestId, await browserFill(request));
      case "browser_keypress":
        return okResponse(requestId, await browserKeypress(request));
      case "browser_navigate":
        return okResponse(requestId, await browserNavigate(request));
      case "browser_back":
        return okResponse(requestId, await browserHistory(request, "back"));
      case "browser_forward":
        return okResponse(requestId, await browserHistory(request, "forward"));
      case "browser_reload":
        return okResponse(requestId, await browserReload(request));
      case "browser_screenshot":
        return okResponse(requestId, await browserScreenshot(request));
      case "browser_eval":
        return okResponse(requestId, await browserEval(request, config));
      default:
        return errorResponse(requestId, "BROWSER_TOOL_UNSUPPORTED", `Unsupported browser tool: ${request.tool}`);
    }
  } catch (error) {
    return errorResponse(requestId, "BROWSER_TOOL_FAILED", error instanceof Error ? error.message : String(error));
  }
}

function okResponse(requestId, result) {
  return { requestId, status: "ok", result };
}

function errorResponse(requestId, code, message) {
  return { requestId, status: "error", error: { code, message } };
}

async function browserGetState(request) {
  const tab = await getTargetTab(request.tabId);
  assertInjectableTab(tab);
  const result = await executeInTab(tab.id, collectPageState, [{
    selector: stringOrUndefined(request.selector),
    includeHtml: request.includeHtml !== false,
    includeText: request.includeText === true,
    maxHtmlBytes: clampInteger(request.maxHtmlBytes, 200000, 1000, 2000000),
    maxElements: clampInteger(request.maxElements, 120, 1, 500),
    redact: request.redact !== false,
  }]);
  return { tab: tabSummary(tab), ...result };
}

async function browserClick(request) {
  const tab = await getTargetTab(request.tabId);
  assertInjectableTab(tab);
  const result = await executeInTab(tab.id, performClick, [{
    selector: stringOrUndefined(request.selector),
    text: stringOrUndefined(request.text),
    x: finiteNumberOrUndefined(request.x),
    y: finiteNumberOrUndefined(request.y),
    button: ["left", "right", "middle"].includes(request.button) ? request.button : "left",
    clicks: clampInteger(request.clicks, 1, 1, 10),
    scrollIntoView: request.scrollIntoView !== false,
  }]);
  const waitAfterMs = clampInteger(request.waitAfterMs, 0, 0, 10000);
  if (waitAfterMs > 0) {
    await sleep(waitAfterMs);
  }
  const updatedTab = await chrome.tabs.get(tab.id);
  return { tab: tabSummary(updatedTab), ...result };
}

async function browserFill(request) {
  if (typeof request.value !== "string") {
    throw new Error("browser_fill requires a string value.");
  }
  const tab = await getTargetTab(request.tabId);
  assertInjectableTab(tab);
  const result = await executeInTab(tab.id, performFill, [{
    selector: stringOrUndefined(request.selector),
    text: stringOrUndefined(request.text),
    value: request.value,
    clear: request.clear !== false,
    submit: request.submit === true,
  }]);
  return { tab: tabSummary(await chrome.tabs.get(tab.id)), ...result };
}

async function browserKeypress(request) {
  if (typeof request.key !== "string" || request.key.length === 0) {
    throw new Error("browser_keypress requires key.");
  }
  const tab = await getTargetTab(request.tabId);
  assertInjectableTab(tab);
  const result = await executeInTab(tab.id, performKeypress, [{
    selector: stringOrUndefined(request.selector),
    key: request.key,
    ctrlKey: request.ctrlKey === true,
    altKey: request.altKey === true,
    shiftKey: request.shiftKey === true,
    metaKey: request.metaKey === true,
  }]);
  return { tab: tabSummary(await chrome.tabs.get(tab.id)), ...result };
}

async function browserNavigate(request) {
  if (typeof request.url !== "string" || request.url.trim().length === 0) {
    throw new Error("browser_navigate requires url.");
  }
  const url = normalizeNavigationUrl(request.url);
  const tab = request.newTab === true
    ? await chrome.tabs.create({ url, active: true })
    : await chrome.tabs.update((await getTargetTab(request.tabId)).id, { url, active: true });
  return { tab: tabSummary(tab) };
}

async function browserHistory(request, direction) {
  const tab = await getTargetTab(request.tabId);
  if (direction === "back") {
    await chrome.tabs.goBack(tab.id);
  } else {
    await chrome.tabs.goForward(tab.id);
  }
  return { tab: tabSummary(await chrome.tabs.get(tab.id)), direction };
}

async function browserReload(request) {
  const tab = await getTargetTab(request.tabId);
  await chrome.tabs.reload(tab.id, { bypassCache: request.bypassCache === true });
  return { tab: tabSummary(await chrome.tabs.get(tab.id)), bypassCache: request.bypassCache === true };
}

async function browserScreenshot(request) {
  const tab = await getTargetTab(request.tabId);
  if (typeof tab.windowId === "number") {
    await chrome.windows.update(tab.windowId, { focused: true }).catch(() => undefined);
  }
  await chrome.tabs.update(tab.id, { active: true });
  const format = request.format === "jpeg" ? "jpeg" : "png";
  const options = format === "jpeg"
    ? { format, quality: clampInteger(request.quality, 90, 1, 100) }
    : { format };
  const dataUrl = await chrome.tabs.captureVisibleTab(tab.windowId, options);
  const [header, data = ""] = dataUrl.split(",", 2);
  const mimeType = header.includes("image/jpeg") ? "image/jpeg" : "image/png";
  return {
    tab: tabSummary(await chrome.tabs.get(tab.id)),
    mimeType,
    dataUrl,
    data,
    bytes: Math.floor((data.length * 3) / 4),
  };
}

async function browserEval(request, config) {
  if (typeof request.code !== "string" || request.code.trim().length === 0) {
    throw new Error("browser_eval requires code.");
  }
  if (!config.allowUnsafeScript || request.allowUnsafeScript !== true) {
    throw new Error("browser_eval is disabled. Enable unsafe script in the extension and pass allowUnsafeScript=true on the request.");
  }
  const tab = await getTargetTab(request.tabId);
  assertInjectableTab(tab);
  const result = await executeInTab(tab.id, runUserScript, [{ code: request.code, args: Array.isArray(request.args) ? request.args : [] }]);
  return { tab: tabSummary(await chrome.tabs.get(tab.id)), result };
}

async function getTargetTab(tabId) {
  if (Number.isInteger(tabId)) {
    return await chrome.tabs.get(tabId);
  }
  const [lastFocused] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (lastFocused) {
    return lastFocused;
  }
  const [current] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (current) {
    return current;
  }
  throw new Error("No active Chrome tab found.");
}

function assertInjectableTab(tab) {
  const url = tab.url ?? "";
  if (!/^https?:|^file:/i.test(url)) {
    throw new Error(`Chrome extensions cannot inspect or control this tab URL: ${url || "unknown"}`);
  }
}

async function executeInTab(tabId, func, args) {
  const frames = await chrome.scripting.executeScript({ target: { tabId }, func, args });
  if (!frames || frames.length === 0) {
    throw new Error("No script result returned from tab.");
  }
  return frames[0].result;
}

function normalizeNavigationUrl(rawUrl) {
  const withScheme = /^[a-z][a-z0-9+.-]*:/i.test(rawUrl) ? rawUrl : `https://${rawUrl}`;
  const url = new URL(withScheme);
  if (!/^https?:$|^file:$/i.test(url.protocol)) {
    throw new Error(`Refusing to navigate to unsupported URL scheme: ${url.protocol}`);
  }
  return url.toString();
}

function tabSummary(tab) {
  return {
    id: tab.id,
    windowId: tab.windowId,
    title: tab.title,
    url: tab.url,
    status: tab.status,
    active: tab.active,
  };
}

function stringOrUndefined(value) {
  return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

function finiteNumberOrUndefined(value) {
  return Number.isFinite(value) ? Number(value) : undefined;
}

function clampInteger(value, fallback, min, max) {
  const parsed = typeof value === "number" ? value : Number.parseInt(String(value), 10);
  if (!Number.isFinite(parsed)) {
    return fallback;
  }
  return Math.min(max, Math.max(min, Math.trunc(parsed)));
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function collectPageState(options) {
  const root = options.selector ? document.querySelector(options.selector) : document.documentElement;
  const selectorFound = Boolean(root);
  const scopedRoot = root ?? document.documentElement;
  const htmlSource = scopedRoot.cloneNode(true);
  sanitizeHtmlClone(htmlSource, options.redact);
  const html = options.includeHtml ? truncateUtf8(htmlSource.outerHTML, options.maxHtmlBytes) : undefined;
  const visibleText = options.includeText ? truncateUtf8((scopedRoot.innerText || "").trim(), 100000) : undefined;
  const elements = collectActionableElements(options.maxElements);

  return {
    url: location.href,
    title: document.title,
    selector: options.selector || null,
    selectorFound,
    viewport: {
      width: window.innerWidth,
      height: window.innerHeight,
      devicePixelRatio: window.devicePixelRatio,
      scrollX: window.scrollX,
      scrollY: window.scrollY,
    },
    activeElement: document.activeElement ? describeElement(document.activeElement) : null,
    elements,
    html,
    htmlTruncated: Boolean(html?.endsWith("\n<!-- truncated -->")),
    text: visibleText,
  };

  function sanitizeHtmlClone(clone, redact) {
    clone.querySelectorAll?.("script,noscript").forEach((node) => node.remove());
    if (!redact) {
      return;
    }
    clone.querySelectorAll?.("input,textarea,select").forEach((node) => {
      node.removeAttribute("value");
      node.setAttribute("data-browser-control-redacted", "value");
      if (node.tagName.toLowerCase() === "textarea") {
        node.textContent = "";
      }
    });
    clone.querySelectorAll?.("[autocomplete],[data-token],[data-secret],[data-password],[aria-keyshortcuts]").forEach((node) => {
      for (const attr of Array.from(node.attributes)) {
        if (/token|secret|password|credential|auth/i.test(attr.name)) {
          node.removeAttribute(attr.name);
        }
      }
    });
  }

  function collectActionableElements(maxElements) {
    const selector = [
      "a[href]",
      "button",
      "input",
      "textarea",
      "select",
      "summary",
      "[role='button']",
      "[role='link']",
      "[role='menuitem']",
      "[role='checkbox']",
      "[role='radio']",
      "[role='tab']",
      "[contenteditable='true']",
      "[tabindex]:not([tabindex='-1'])",
    ].join(",");
    return Array.from(document.querySelectorAll(selector))
      .filter(isVisible)
      .slice(0, maxElements)
      .map(describeElement);
  }
}

function performClick(input) {
  const target = findTarget(input);
  if (!target) {
    throw new Error("No clickable target found. Provide selector, text, or x/y viewport coordinates.");
  }
  if (target.disabled || target.getAttribute("aria-disabled") === "true") {
    throw new Error("Target is disabled.");
  }
  if (input.scrollIntoView) {
    target.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
  }
  target.focus?.({ preventScroll: true });
  const rect = target.getBoundingClientRect();
  const clientX = Number.isFinite(input.x) ? input.x : rect.left + rect.width / 2;
  const clientY = Number.isFinite(input.y) ? input.y : rect.top + rect.height / 2;
  const buttonCode = input.button === "middle" ? 1 : input.button === "right" ? 2 : 0;
  const eventOptions = { bubbles: true, cancelable: true, view: window, clientX, clientY, button: buttonCode };

  for (let index = 0; index < input.clicks; index += 1) {
    target.dispatchEvent(new PointerEvent("pointerdown", eventOptions));
    target.dispatchEvent(new MouseEvent("mousedown", eventOptions));
    target.dispatchEvent(new PointerEvent("pointerup", eventOptions));
    target.dispatchEvent(new MouseEvent("mouseup", eventOptions));
    if (input.button === "right") {
      target.dispatchEvent(new MouseEvent("contextmenu", eventOptions));
    } else {
      target.dispatchEvent(new MouseEvent("click", eventOptions));
      target.click?.();
    }
  }

  return {
    clicked: true,
    target: describeElement(target),
    coordinates: { x: clientX, y: clientY },
    button: input.button,
    clicks: input.clicks,
  };
}

function performFill(input) {
  const target = findTarget(input);
  if (!target) {
    throw new Error("No fill target found. Provide selector or text.");
  }
  target.scrollIntoView?.({ block: "center", inline: "center", behavior: "instant" });
  target.focus?.({ preventScroll: true });

  const tag = target.tagName.toLowerCase();
  if (target.isContentEditable) {
    target.textContent = input.clear === false ? `${target.textContent ?? ""}${input.value}` : input.value;
  } else if (tag === "select") {
    const option = Array.from(target.options).find((item) => item.value === input.value || item.text.trim() === input.value);
    target.value = option?.value ?? input.value;
  } else if ("value" in target) {
    target.value = input.clear === false ? `${target.value ?? ""}${input.value}` : input.value;
  } else {
    throw new Error(`Element cannot be filled: ${tag}`);
  }

  target.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true, inputType: "insertText", data: input.value }));
  target.dispatchEvent(new Event("change", { bubbles: true }));

  if (input.submit) {
    const form = target.form ?? target.closest?.("form");
    if (form?.requestSubmit) {
      form.requestSubmit();
    } else if (form?.submit) {
      form.submit();
    }
  }

  return {
    filled: true,
    submitted: input.submit === true,
    target: describeElement(target),
  };
}

function performKeypress(input) {
  const target = input.selector ? document.querySelector(input.selector) : document.activeElement || document.body;
  if (!target) {
    throw new Error("No keyboard target found.");
  }
  target.focus?.({ preventScroll: true });
  const eventOptions = {
    bubbles: true,
    cancelable: true,
    key: input.key,
    ctrlKey: input.ctrlKey,
    altKey: input.altKey,
    shiftKey: input.shiftKey,
    metaKey: input.metaKey,
  };
  target.dispatchEvent(new KeyboardEvent("keydown", eventOptions));
  if (input.key.length === 1 && isEditableElement(target)) {
    insertText(target, input.key);
  } else if (input.key === "Enter" && target.form?.requestSubmit) {
    target.form.requestSubmit();
  }
  target.dispatchEvent(new KeyboardEvent("keypress", eventOptions));
  target.dispatchEvent(new KeyboardEvent("keyup", eventOptions));
  return { pressed: true, key: input.key, target: describeElement(target) };
}

function runUserScript(input) {
  const fn = new Function("args", `"use strict"; return (async () => {\n${input.code}\n})();`);
  return Promise.resolve(fn(input.args)).then((value) => serializeForBridge(value));
}

function findTarget(input) {
  if (input.selector) {
    return document.querySelector(input.selector);
  }
  if (Number.isFinite(input.x) && Number.isFinite(input.y)) {
    return document.elementFromPoint(input.x, input.y);
  }
  if (input.text) {
    return findByText(input.text);
  }
  return null;
}

function findByText(text) {
  const needle = normalizeText(text);
  const actionable = Array.from(document.querySelectorAll("button,a,input,textarea,select,summary,[role],[tabindex],[contenteditable='true'],label"));
  return actionable.find((element) => isVisible(element) && normalizeText(getElementText(element)).includes(needle)) ?? null;
}

function describeElement(element) {
  const rect = element.getBoundingClientRect();
  return {
    selector: uniqueSelector(element),
    tag: element.tagName.toLowerCase(),
    id: element.id || undefined,
    name: element.getAttribute("name") || undefined,
    role: element.getAttribute("role") || undefined,
    type: element.getAttribute("type") || undefined,
    text: truncateUtf8(getElementText(element), 300),
    href: element instanceof HTMLAnchorElement ? element.href : undefined,
    disabled: Boolean(element.disabled || element.getAttribute("aria-disabled") === "true"),
    checked: "checked" in element ? Boolean(element.checked) : undefined,
    rect: {
      x: Math.round(rect.x),
      y: Math.round(rect.y),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
    },
  };
}

function getElementText(element) {
  const aria = element.getAttribute?.("aria-label");
  const title = element.getAttribute?.("title");
  const placeholder = element.getAttribute?.("placeholder");
  const valueText = element instanceof HTMLInputElement && ["button", "submit", "reset"].includes(element.type) ? element.value : "";
  const labelText = element.id ? document.querySelector(`label[for='${cssEscape(element.id)}']`)?.innerText : "";
  return [aria, valueText, placeholder, title, labelText, element.innerText, element.textContent]
    .filter(Boolean)
    .join(" ")
    .replace(/\s+/g, " ")
    .trim();
}

function isVisible(element) {
  const style = window.getComputedStyle(element);
  if (style.visibility === "hidden" || style.display === "none" || Number(style.opacity) === 0) {
    return false;
  }
  const rect = element.getBoundingClientRect();
  return rect.width > 0 && rect.height > 0;
}

function uniqueSelector(element) {
  if (!(element instanceof Element)) {
    return "";
  }
  if (element.id) {
    return `#${cssEscape(element.id)}`;
  }
  const parts = [];
  let current = element;
  while (current && current.nodeType === Node.ELEMENT_NODE && current !== document.documentElement) {
    let part = current.tagName.toLowerCase();
    const name = current.getAttribute("name");
    if (name) {
      part += `[name='${cssEscape(name)}']`;
    }
    const parent = current.parentElement;
    if (parent) {
      const siblings = Array.from(parent.children).filter((item) => item.tagName === current.tagName);
      if (siblings.length > 1) {
        part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
      }
    }
    parts.unshift(part);
    current = parent;
  }
  return parts.join(" > ");
}

function cssEscape(value) {
  if (window.CSS?.escape) {
    return window.CSS.escape(value);
  }
  return String(value).replace(/['"\\#.:\[\]>+~*^$|=\s]/g, "\\$&");
}

function normalizeText(value) {
  return String(value ?? "").replace(/\s+/g, " ").trim().toLowerCase();
}

function isEditableElement(element) {
  return element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element.isContentEditable;
}

function insertText(element, value) {
  if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement) {
    const start = element.selectionStart ?? element.value.length;
    const end = element.selectionEnd ?? element.value.length;
    element.setRangeText(value, start, end, "end");
  } else if (element.isContentEditable) {
    document.execCommand("insertText", false, value);
  }
  element.dispatchEvent(new InputEvent("input", { bubbles: true, cancelable: true, inputType: "insertText", data: value }));
}

function serializeForBridge(value) {
  if (value === undefined) {
    return null;
  }
  try {
    return JSON.parse(JSON.stringify(value));
  } catch {
    return String(value);
  }
}

function truncateUtf8(value, maxBytes) {
  const text = String(value ?? "");
  if (new Blob([text]).size <= maxBytes) {
    return text;
  }
  let low = 0;
  let high = text.length;
  while (low < high) {
    const mid = Math.floor((low + high + 1) / 2);
    if (new Blob([text.slice(0, mid)]).size <= maxBytes) {
      low = mid;
    } else {
      high = mid - 1;
    }
  }
  return `${text.slice(0, low)}\n<!-- truncated -->`;
}
