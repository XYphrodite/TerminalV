import assert from "node:assert/strict";
import test from "node:test";
import { claimLiveScreenInput } from "../src/terminal-viewport.js";

function fakeTerm({ y, base, mouse = "none", rows = 24 } = {}) {
  return {
    rows,
    modes: { mouseTrackingMode: mouse },
    buffer: { active: { viewportY: y, baseY: base } },
    scrolled: 0,
    scrollToBottom() {
      this.scrolled++;
      this.buffer.active.viewportY = this.buffer.active.baseY;
    }
  };
}

function fakeHost(tuiLock) {
  return { classList: { contains: (c) => tuiLock && c === "tui-lock" } };
}

test("plain shell history clicks and keys stay put", () => {
  const term = fakeTerm({ y: 10, base: 100, mouse: "none" });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), false);
  assert.equal(term.scrolled, 0);
});

test("mouse-tracked TUI input deep in history returns to the live screen once", () => {
  const term = fakeTerm({ y: 10, base: 100, mouse: "any", rows: 24 });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), true);
  assert.equal(term.scrolled, 1);
  assert.equal(term.buffer.active.viewportY, 100);
  // A second event on the live screen is delivered normally.
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), false);
});

test("tui-lock input deep in history also claims the live screen", () => {
  const term = fakeTerm({ y: 0, base: 80, mouse: "none", rows: 24 });
  assert.equal(claimLiveScreenInput(term, fakeHost(true)), true);
  assert.equal(term.scrolled, 1);
});

test("already at the live screen never swallows input", () => {
  const term = fakeTerm({ y: 100, base: 100, mouse: "vt200" });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), false);
  assert.equal(term.scrolled, 0);
});

test("x10 button tracking still claims the live screen when deep in history", () => {
  const term = fakeTerm({ y: 5, base: 50, mouse: "x10", rows: 24 });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), true);
  assert.equal(term.scrolled, 1);
});

// Regression: 0.7.39 ate every Approve press because a live TUI often sits 1–2
// rows above baseY. Near the bottom the event must go through untouched.
test("one or two rows above baseY still delivers Approve input", () => {
  for (const y of [98, 99]) {
    const term = fakeTerm({ y, base: 100, mouse: "any", rows: 24 });
    assert.equal(claimLiveScreenInput(term, fakeHost(false)), false, `y=${y}`);
    assert.equal(term.scrolled, 0);
  }
});

test("keyboard snap never swallows the key even deep in history", () => {
  const term = fakeTerm({ y: 5, base: 80, mouse: "any", rows: 24 });
  assert.equal(claimLiveScreenInput(term, fakeHost(false), { swallow: false }), false);
  assert.equal(term.scrolled, 1, "still returns to the live screen");
  assert.equal(term.buffer.active.viewportY, 80);
});
