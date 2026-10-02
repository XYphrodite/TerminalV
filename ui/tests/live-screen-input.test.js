import assert from "node:assert/strict";
import test from "node:test";
import { claimLiveScreenInput } from "../src/terminal-viewport.js";

function fakeTerm({ y, base, mouse = "none" } = {}) {
  return {
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

test("mouse-tracked TUI input while scrolled back returns to the live screen", () => {
  const term = fakeTerm({ y: 10, base: 100, mouse: "any" });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), true);
  assert.equal(term.scrolled, 1);
  assert.equal(term.buffer.active.viewportY, 100);
  // A second event on the live screen is delivered normally.
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), false);
});

test("tui-lock input while scrolled back also claims the live screen", () => {
  const term = fakeTerm({ y: 0, base: 80, mouse: "none" });
  assert.equal(claimLiveScreenInput(term, fakeHost(true)), true);
  assert.equal(term.scrolled, 1);
});

test("already at the live screen never swallows input", () => {
  const term = fakeTerm({ y: 100, base: 100, mouse: "vt200" });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), false);
  assert.equal(term.scrolled, 0);
});

test("x10 button tracking still claims the live screen", () => {
  const term = fakeTerm({ y: 5, base: 50, mouse: "x10" });
  assert.equal(claimLiveScreenInput(term, fakeHost(false)), true);
  assert.equal(term.scrolled, 1);
});
