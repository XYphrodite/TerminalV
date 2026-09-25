import assert from "node:assert/strict";
import test from "node:test";
import { createExtensionRuntime } from "../src/extensions.js";

function setup(modules = {}, options = {}) {
  const messages = [], surfaces = [], errors = [], notices = [];
  const runtime = createExtensionRuntime({ post: message => messages.push(message),
    loadModule: async url => {
      if (!modules[url]) throw new Error("Module missing");
      return modules[url];
    },
    createSurface: (id, location, args) => {
      const surface = { id, location, args, disposed: false, dispose() { this.disposed = true; } };
      surfaces.push(surface);
      return surface;
    },
    notify: (...args) => notices.push(args), log: (...args) => errors.push(args), ...options
  });
  const reply = (message, result, error) => runtime.receive({ type: "extensions:result",
    requestId: message.requestId, extensionId: message.extensionId, result, error });
  const catalog = (id, fields = {}) => ({ id, name: id, enabled: true, active: true, valid: true,
    uiUrl: id, ...fields });
  const start = async items => {
    const task = runtime.refresh();
    reply(messages.at(-1), items);
    await task;
  };
  return { runtime, messages, surfaces, errors, notices, reply, catalog, start };
}

test("only active modules load, once across repeated init/catalog updates", async () => {
  let calls = 0;
  const s = setup({ a: { activate() { calls++; } } });
  const items = [s.catalog("a"), s.catalog("disabled", { active: false, enabled: false }),
    s.catalog("backend", { uiUrl: null })];
  await s.start(items);
  await s.start(items);
  assert.equal(calls, 1);
  assert.equal(s.errors.length, 0);
  await s.runtime.dispose();
});

test("concurrent host calls correlate responses and reject wrong extension ids", async () => {
  let api;
  const s = setup({ a: { activate(value) { api = value; } } });
  await s.start([s.catalog("a")]);
  const one = api.host.invoke("one", { x: 1 }), two = api.host.invoke("two");
  const [m1, m2] = s.messages.slice(-2);
  s.reply({ ...m1, extensionId: "b" }, "wrong");
  s.reply(m2, 2);
  s.reply(m1, 1);
  assert.deepEqual(await Promise.all([one, two]), [1, 2]);
  assert.deepEqual(m1.args, { x: 1 });
  const rejected = api.host.invoke("fail");
  s.reply(s.messages.at(-1), null, "Host error");
  await assert.rejects(rejected, /Host error/);
  await s.runtime.dispose();
});

test("events route by extension, survive a failing listener, and subscriptions dispose", async () => {
  const calls = [];
  let off;
  const s = setup({
    a: { activate(api) {
      api.host.on("changed", () => { throw new Error("listener failed"); });
      off = api.host.on("changed", data => calls.push(data));
    } },
    b: { activate(api) { api.host.on("changed", () => calls.push("wrong")); } }
  });
  await s.start([s.catalog("a"), s.catalog("b")]);
  s.runtime.receive({ type: "extensions:event", extensionId: "a", event: "changed", data: 7 });
  assert.deepEqual(calls, [7]);
  assert.match(s.runtime.catalog[0].uiError, /listener failed/);
  off.dispose();
  s.runtime.receive({ type: "extensions:event", extensionId: "a", event: "changed", data: 8 });
  assert.deepEqual(calls, [7]);
  await s.runtime.dispose();
});

test("activation failures clean partial UI and commands while other extensions run", async () => {
  let cleaned = 0;
  const s = setup({
    bad: { activate(api) {
      api.ui.createSidebarPanel({ id: "test", title: "Test" });
      api.commands.register("bad", "Bad", () => {});
      api.subscriptions.push(() => cleaned++);
      throw new Error("broken activation");
    } },
    good: { activate(api) { api.commands.register("hello", "Hello", () => 42); } }
  });
  await s.start([s.catalog("bad"), s.catalog("good")]);
  assert.equal(cleaned, 1);
  assert.equal(s.surfaces[0].disposed, true);
  assert.deepEqual(s.runtime.commands("bad"), []);
  assert.equal(await s.runtime.execute("good", "hello"), 42);
  assert.match(s.runtime.catalog[0].uiError, /broken activation/);
  await s.runtime.dispose();
});

test("dispose cancels pending requests and releases signals, surfaces and returned cleanup", async () => {
  let api, cleaned = 0;
  const s = setup({ a: { activate(value) {
    api = value;
    api.ui.createSidebarPanel({ id: "a", title: "A" });
    api.ui.createSettingsPanel({ id: "b", title: "B" });
    return () => cleaned++;
  } } });
  await s.start([s.catalog("a")]);
  const pending = api.host.invoke("waiting");
  const rejected = assert.rejects(pending, /closed/);
  await s.runtime.dispose();
  await rejected;
  await s.runtime.dispose();
  assert.equal(cleaned, 1);
  assert.equal(api.signal.aborted, true);
  assert.ok(s.surfaces.every(item => item.disposed));
  assert.throws(() => api.ui.createSettingsPanel({ id: "late", title: "Late" }), /disposed/);
});

test("request timeout removes the waiter and ignores late answers", async () => {
  let api;
  const s = setup({ a: { activate(value) { api = value; } } }, { timeoutMs: 15 });
  await s.start([s.catalog("a")]);
  const call = api.host.invoke("slow");
  const message = s.messages.at(-1);
  await assert.rejects(call, /timed out/);
  assert.equal(s.reply(message, "late"), true);
  await s.runtime.dispose();
});

test("timed out activation aborts its scope and disposes a late returned cleanup", async () => {
  let finish, api, cleaned = 0;
  const s = setup({ a: { async activate(value) {
    api = value;
    await new Promise(resolve => { finish = resolve; });
    return () => cleaned++;
  } } }, { activationTimeoutMs: 15 });
  await s.start([s.catalog("a")]);
  assert.equal(api.signal.aborted, true);
  assert.match(s.runtime.catalog[0].uiError, /timed out/);
  finish();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(cleaned, 1);
  await s.runtime.dispose();
});

test("enablement persists for restart without activating or tearing down current modules", async () => {
  let activated = 0;
  const s = setup({ a: { activate() { activated++; } } });
  await s.start([s.catalog("a", { enabled: false, active: false, uiUrl: null })]);
  const enable = s.runtime.setEnabled("a", true);
  assert.equal(s.messages.at(-1).type, "extensions:set-enabled");
  s.reply(s.messages.at(-1), [s.catalog("a", { active: false, uiUrl: null, restartRequired: true })]);
  await enable;
  assert.equal(activated, 0);
  assert.equal(s.runtime.catalog[0].restartRequired, true);
  await s.runtime.dispose();
});

test("storage calls are scoped and expose no secret operation", async () => {
  let api;
  const s = setup({ a: { activate(value) { api = value; } } });
  await s.start([s.catalog("a")]);
  const write = api.storage.set("config", '{"x":1}');
  assert.equal(s.messages.at(-1).extensionId, "a");
  assert.equal(s.messages.at(-1).type, "extensions:storage");
  assert.deepEqual(s.messages.at(-1).args, { key: "config", value: '{"x":1}' });
  s.reply(s.messages.at(-1), null);
  await write;
  const read = api.storage.get("config");
  s.reply(s.messages.at(-1), '{"x":1}');
  assert.equal(await read, '{"x":1}');
  const remove = api.storage.delete("config");
  assert.deepEqual(s.messages.at(-1).args, { key: "config", value: null });
  s.reply(s.messages.at(-1), null);
  await remove;
  assert.equal(api.secrets, undefined);
  await s.runtime.dispose();
});

test("cleanup failures do not prevent remaining panels and listeners from disposing", async () => {
  const s = setup({ a: { activate(api) {
    api.ui.createSidebarPanel({ id: "a", title: "A" });
    api.subscriptions.push(() => { throw new Error("cleanup failed"); });
    return () => { throw new Error("returned cleanup failed"); };
  } } });
  await s.start([s.catalog("a")]);
  await s.runtime.dispose();
  assert.equal(s.surfaces[0].disposed, true);
  assert.equal(s.errors.length, 2);
});
