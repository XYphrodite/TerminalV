import assert from "node:assert/strict";
import test from "node:test";
import { updateScrollToBottom } from "../src/scroll-to-bottom.js";

function fakeTerm({ y, base, rows = 24, type = "normal" } = {}) {
  return {
    rows,
    buffer: { active: { viewportY: y, baseY: base, type } }
  };
}

function fakeHost(tuiLock) {
  return { classList: { contains: (c) => tuiLock && c === "tui-lock" } };
}

function fakeTab(options) {
  const button = { hidden: true };
  return { term: fakeTerm(options), host: fakeHost(options.tuiLock), scrollToBottomBtn: button, button };
}

test("deep history in the normal buffer shows the button", () => {
  const tab = fakeTab({ y: 10, base: 100 });
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, false);
});

test("live bottom hides the button", () => {
  const tab = fakeTab({ y: 100, base: 100 });
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, true);
});

test("one or two rows above baseY is still the bottom, not reading history", () => {
  for (const y of [98, 99]) {
    const tab = fakeTab({ y, base: 100 });
    updateScrollToBottom(tab);
    assert.equal(tab.button.hidden, true, `y=${y}`);
  }
});

test("tui-lock hides the button even deep in history", () => {
  const tab = fakeTab({ y: 10, base: 100, tuiLock: true });
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, true);
});

test("alternate buffer never shows the button", () => {
  const tab = fakeTab({ y: 0, base: 0, type: "alternate" });
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, true);
});

test("leaving tui-lock at the previous history position shows the button again", () => {
  const tab = fakeTab({ y: 10, base: 100 });
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, false);
  tab.host = fakeHost(true);
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, true);
  tab.host = fakeHost(false);
  updateScrollToBottom(tab);
  assert.equal(tab.button.hidden, false);
});

test("tab without an attached button is a safe no-op", () => {
  const tab = { term: fakeTerm({ y: 10, base: 100 }), host: fakeHost(false) };
  updateScrollToBottom(tab);
});
