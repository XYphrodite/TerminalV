import assert from "node:assert/strict";
import test from "node:test";
import xterm from "@xterm/xterm";
import serialization from "@xterm/addon-serialize";
import { createNotificationOutput } from "../src/notifications.js";
import { createSessionPersistence, serializeSessionBuffer } from "../src/session-persistence.js";

function terminal(t, buffer = "") {
  const term = new xterm.Terminal({ allowProposedApi: true });
  const serialize = new serialization.SerializeAddon();
  term.loadAddon(serialize);
  const bells = [];
  const output = createNotificationOutput(term, () => bells.push(true));
  t.after(() => { output.dispose(); term.dispose(); });
  return { term, serialize, output, buffer, bells };
}

function setup(t, tabs, options = {}) {
  const messages = [];
  let onSave;
  const saved = new Promise(resolve => { onSave = resolve; });
  // Manual timers keep these tests independent of rendering or wall-clock timing.
  let clock = 0;
  let nextTimer = 0;
  const timers = new Map();
  const controller = createSessionPersistence({
    isReady: () => true,
    getTabs: () => tabs,
    save(requestId) {
      const message = { type: "sessions", requestId, buffers: tabs.map(serializeSessionBuffer) };
      messages.push(message);
      onSave(message);
    },
    saveSettings: requestId => messages.push({ type: "settings", requestId }),
    skipped: requestId => messages.push({ type: "skipped", requestId }),
    setTimer(callback, ms) {
      const id = ++nextTimer;
      timers.set(id, { callback, at: clock + ms });
      return id;
    },
    clearTimer: id => timers.delete(id),
    ...options
  });
  return { controller, messages, saved, timers,
    advance(ms) {
      const end = clock + ms;
      while (true) {
        const next = [...timers.entries()].sort((a, b) => a[1].at - b[1].at)[0];
        if (!next || next[1].at > end) break;
        clock = next[1].at;
        timers.delete(next[0]);
        next[1].callback();
      }
      clock = end;
    }
  };
}

test("closing before init does not overwrite saved sessions or settings", t => {
  const s = setup(t, [], { isReady: () => false });
  s.controller.schedule();
  s.controller.persist();
  s.controller.flush("close-before-init");
  s.advance(3000);
  assert.deepEqual(s.messages, [{ type: "skipped", requestId: "close-before-init" }]);
  assert.equal(s.timers.size, 0);
});

test("restored output is saved only after xterm has parsed all tabs, including hidden ones", async t => {
  const visible = terminal(t, "VISIBLE HISTORY");
  const hidden = terminal(t, "HIDDEN HISTORY");
  hidden.hidden = true;
  const s = setup(t, [visible, hidden]);
  visible.output.write(visible.buffer, true);
  hidden.output.write(hidden.buffer, true);
  // This is the startup race: the serialize addon still sees empty terminals.
  assert.equal(visible.serialize.serialize(), "");
  s.controller.persist();
  assert.deepEqual(s.messages, []);
  assert.deepEqual((await s.saved).buffers, ["VISIBLE HISTORY", "HIDDEN HISTORY"]);
});

test("closing immediately after output waits for the last bytes and carries one request ID", async t => {
  const tab = terminal(t);
  const s = setup(t, [tab]);
  tab.output.write("last command\r\nlast result");
  s.controller.schedule();
  s.controller.flush("close-42");
  assert.equal(s.timers.size, 0);
  assert.deepEqual(s.messages, []);
  const saved = await s.saved;
  assert.match(saved.buffers[0], /last command\r\nlast result/);
  assert.deepEqual(s.messages.map(m => [m.type, m.requestId]),
    [["settings", "close-42"], ["sessions", "close-42"]]);
});

test("overlapping saves use current inventory and a close waits for queued alternate-screen output", async t => {
  const tab = terminal(t);
  const tabs = [tab];
  const s = setup(t, tabs);
  tab.output.write("normal history");
  s.controller.persist();
  tab.output.write("\x1b[?1049hTUI SCREEN");
  s.controller.flush("close-tui");
  await new Promise(resolve => tab.output.whenParsed(resolve));
  const close = s.messages.find(m => m.type === "sessions" && m.requestId === "close-tui");
  assert.ok(close, "close snapshot follows its queued terminal writes");
  assert.match(close.buffers[0], /normal history/);
  assert.match(close.buffers[0], /TUI SCREEN/);
  assert.ok(close.buffers[0].includes("\x1b[?1049h"));
});

test("waiting for host replay preserves the disk snapshot, then saves fresh output", async t => {
  const tab = terminal(t, "SAVED BEFORE REOPEN");
  const s = setup(t, [tab]);
  s.controller.persist();
  assert.deepEqual(s.messages[0].buffers, ["SAVED BEFORE REOPEN"]);
  tab.output.write("CURRENT HOST SCREEN", true);
  await new Promise(resolve => tab.output.whenParsed(resolve));
  s.controller.persist();
  assert.deepEqual(s.messages[1].buffers, ["CURRENT HOST SCREEN"]);
});

test("continuous output cannot postpone autosave indefinitely", t => {
  const s = setup(t, []);
  for (let i = 0; i < 20; i++) {
    s.controller.schedule();
    s.advance(100);
  }
  assert.equal(s.messages.length, 1, "save at the two-second deadline despite no quiet period");
  s.controller.schedule();
  s.advance(400);
  assert.equal(s.messages.length, 2, "ordinary debounce resumes after deadline save");
  assert.equal(s.timers.size, 0);
});

test("a quiet burst is coalesced and an explicit save cancels scheduled duplicates", t => {
  const s = setup(t, []);
  s.controller.schedule();
  s.advance(300);
  s.controller.schedule();
  s.advance(399);
  assert.equal(s.messages.length, 0);
  s.advance(1);
  assert.equal(s.messages.length, 1);
  s.controller.schedule();
  s.controller.persist();
  s.advance(3000);
  assert.equal(s.messages.length, 2);
});

test("removing a tab while a save waits for parsing cannot stall or resurrect it", async t => {
  const tab = terminal(t);
  const tabs = [tab];
  const s = setup(t, tabs);
  tab.output.write("pending history");
  s.controller.flush("close-after-remove");
  tabs.splice(0, 1);
  tab.output.dispose();
  assert.deepEqual((await s.saved).buffers, []);
});

test("serialization failures keep the latest successfully saved screen", async t => {
  const tab = terminal(t, "OLD SCREEN");
  tab.output.write("NEW SCREEN");
  await new Promise(resolve => tab.output.whenParsed(resolve));
  assert.equal(serializeSessionBuffer(tab), "NEW SCREEN");
  tab.serialize = { serialize() { throw new Error("unavailable"); } };
  assert.equal(serializeSessionBuffer(tab), "NEW SCREEN");
});

test("parse barriers preserve replay bell suppression and wait for reset", async t => {
  const tab = terminal(t);
  tab.output.write("HISTORY\x07", true);
  tab.output.write("LIVE\x07");
  await new Promise(resolve => tab.output.whenParsed(resolve));
  assert.equal(tab.bells.length, 1);
  const s = setup(t, [tab]);
  tab.output.reset();
  s.controller.flush("close-after-reset");
  assert.deepEqual((await s.saved).buffers, [""]);
});
