import assert from "node:assert/strict";
import test from "node:test";
import { wantsAppWheel } from "../src/tui-scroll.js";

test("no lock means normal terminal scrolling", () => {
  assert.equal(wantsAppWheel({ tuiLock: false, mouseMode: false, zoomModifier: false }), false);
  assert.equal(wantsAppWheel({ tuiLock: false, mouseMode: true, zoomModifier: false }), false);
});

test("keyboard-driven TUI keeps swallowing wheel", () => {
  assert.equal(wantsAppWheel({ tuiLock: true, mouseMode: false, zoomModifier: false }), false);
});

test("mouse-interactive TUI (own scrollbar) receives wheel", () => {
  assert.equal(wantsAppWheel({ tuiLock: true, mouseMode: true, zoomModifier: false }), true);
});

test("zoom gestures never go to the app", () => {
  assert.equal(wantsAppWheel({ tuiLock: true, mouseMode: true, zoomModifier: true }), false);
  assert.equal(wantsAppWheel({ tuiLock: true, mouseMode: false, zoomModifier: true }), false);
});
