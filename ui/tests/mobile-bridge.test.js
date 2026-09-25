import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { runInNewContext } from "node:vm";

const source = readFileSync(new URL("../../src/TerminalV.Mobile/wwwroot/js/mobile-bridge.js", import.meta.url), "utf8");
function load() {
  const native = { postMessage() { throw new Error("native Blazor transport must not receive terminal messages"); } };
  const elements = new Map(), handlers = new Map(), intervals = [];
  const window = { chrome: { webview: native }, setInterval(callback, delay) { intervals.push({ callback, delay }); }, addEventListener(type, handler) {
    const list = handlers.get(type) || []; list.push(handler); handlers.set(type, list);
  } };
  const document = { readyState: "loading", documentElement: {}, activeElement: null,
    getElementById: id => elements.get(id),
    addEventListener(type, handler) { const list = handlers.get(type) || []; list.push(handler); handlers.set(type, list); }
  };
  const mount = () => {
    for (const id of ["app", "sidebar-swipe-handle", "mobile-connection", "mobile-connection-name", "mobile-connection-status", "connection-close"]) {
      const attributes = new Map(), events = new Map();
      elements.set(id, { id, dataset: {}, textContent: "", isConnected: true,
        setAttribute: (name, value) => attributes.set(name, value), getAttribute: name => attributes.get(name),
        addEventListener: (name, handler) => events.set(name, handler), click() { events.get("click")?.(); },
        getClientRects: () => [{}], focus() { document.activeElement = this; }, blur() { document.activeElement = null; }
      });
    }
    for (const handler of handlers.get("DOMContentLoaded") || []) handler();
  };
  const context = {
    window, console,
    document,
    MutationObserver: class { observe() {} }
  };
  runInNewContext(source, context);
  return { window, native, document, elements, handlers, intervals, mount };
}

test("catalog polling runs only for a configured foreground connection and resumes immediately", async () => {
  const { window, document, handlers, intervals } = load();
  const received = [];
  window.__tvSetBridge({ async invokeMethodAsync(method, json) { received.push(JSON.parse(json)); } });
  assert.equal(intervals.length, 1);
  assert.equal(intervals[0].delay, 15000);
  const refresh = intervals[0].callback;
  refresh();
  assert.equal(received.length, 0);
  window.__tvDispatch(JSON.stringify({ type: "connection-state", state: "connected", name: "PC" }));
  refresh();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(received, [{ type: "connection-check" }]);
  document.hidden = true;
  refresh();
  assert.equal(received.length, 1);
  document.hidden = false;
  for (const handler of handlers.get("visibilitychange")) handler();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(received.length, 2);
  window.__tvDispatch(JSON.stringify({ type: "connection-state", state: "idle", name: "PC" }));
  refresh();
  assert.equal(received.length, 2);
});

test("mobile messages wait for the component and preserve input order without replacing native transport", async () => {
  const { window, native } = load();
  const received = [];
  window.__terminalvHost.postMessage({ type: "ready" });
  window.__terminalvHost.postMessage({ type: "write", data: "first" });
  assert.deepEqual(received, []);
  let concurrent = 0;
  window.__tvSetBridge({ async invokeMethodAsync(method, json) {
    assert.equal(method, "HandleMessage");
    assert.equal(concurrent++, 0);
    await Promise.resolve();
    received.push(JSON.parse(json));
    concurrent--;
  } });
  window.__terminalvHost.postMessage({ type: "write", data: "second" });
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(received, [{ type: "ready" }, { type: "write", data: "first" }, { type: "write", data: "second" }]);
  assert.equal(window.chrome.webview, native);
});

test("host output arriving before the UI listener is retained and delivered once", () => {
  const { window } = load();
  window.__tvDispatch(JSON.stringify({ type: "init" }));
  window.__tvDispatch(JSON.stringify({ type: "data", data: "snapshot" }));
  const messages = [];
  const listener = event => messages.push(event.data);
  window.__terminalvHost.addEventListener("message", listener);
  assert.deepEqual(JSON.parse(JSON.stringify(messages)), [{ type: "init" }, { type: "data", data: "snapshot" }]);
  window.__terminalvHost.removeEventListener("message", listener);
  window.__terminalvHost.addEventListener("message", listener);
  assert.equal(messages.length, 2);
});

test("connection controls bypass a pending terminal write without reordering terminal input", async () => {
  const { window } = load();
  const received = [];
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  window.__tvSetBridge({ async invokeMethodAsync(method, json) {
    const message = JSON.parse(json); received.push(message.type);
    if (message.type === "write") await pending;
  } });
  window.__terminalvHost.postMessage({ type: "write", data: "a" });
  window.__terminalvHost.postMessage({ type: "resize" });
  window.__terminalvHost.postMessage({ type: "open-connection" });
  window.__terminalvHost.postMessage(JSON.stringify({ type: "close-connection" }));
  assert.deepEqual(received, ["write", "open-connection", "close-connection"]);
  release();
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(received, ["write", "open-connection", "close-connection", "resize"]);
});

test("connection state arriving before DOM ready is retained and host labels stay plain text", () => {
  const { window, elements, mount } = load();
  const name = '<img src=x onerror="alert(1)">';
  window.__tvDispatch(JSON.stringify({ type: "connection-state", state: "connected", name }));
  mount();
  assert.equal(elements.get("mobile-connection-name").textContent, name);
  assert.equal(elements.get("mobile-connection-status").textContent, "Подключено");
  assert.equal(elements.get("mobile-connection").dataset.state, "connected");
  assert.equal(elements.get("mobile-connection").title, `${name} · Подключено`);
  for (const [state, expected] of [["connecting", "Подключение…"], ["idle", "Не подключено"], ["disconnected", "Нет связи"], ["unexpected", "Нет связи"], ["unconfigured", "Выбрать компьютер"]]) {
    window.__tvDispatch(JSON.stringify({ type: "connection-state", state, name: "Xeon" }));
    assert.equal(elements.get("mobile-connection-status").textContent, expected);
  }
  assert.equal(elements.get("mobile-connection-name").textContent, "Подключиться к компьютеру");
});

test("connection button opens once, isolates the terminal and returns focus when closed", async () => {
  const { window, elements, document, handlers, mount } = load();
  const received = [];
  window.__tvSetBridge({ async invokeMethodAsync(method, json) { received.push(JSON.parse(json).type); } });
  mount();
  const button = elements.get("mobile-connection");
  button.focus(); button.click();
  assert.deepEqual(received, ["open-connection"]);
  window.__tvConnectionConfig(true);
  assert.equal(button.getAttribute("aria-expanded"), "true");
  assert.equal(elements.get("app").inert, true);
  assert.equal(document.activeElement.id, "connection-close");
  let prevented = false;
  for (const handler of handlers.get("keydown")) handler({ key: "Escape", preventDefault() { prevented = true; }, stopImmediatePropagation() {} });
  assert.equal(prevented, true);
  assert.equal(received.filter(type => type === "close-connection").length, 1);
  window.__tvConnectionConfig(false);
  assert.equal(elements.get("app").inert, false);
  assert.equal(button.getAttribute("aria-expanded"), "false");
  assert.equal(document.activeElement, button);
});
