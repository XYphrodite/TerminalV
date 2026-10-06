import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { MIRROR_SCROLL_GAIN } from "../src/mirror-viewport.js";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

// Vertical uses native scrolling on real touches (touch-action: pan-y + inertia).
// Synthetic events (tests) fall back to the manual xterm-history / host path.
// Horizontal always pans the wide grid via JS.
// Mouse-tracking TUIs (mimo) get wheel events instead of a dead panning host.
test("mirror vertical scroll uses xterm history and horizontal pans the grid", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overflow:\s*auto/, "grid can pan");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*pan-y/, "native vertical, JS horizontal");
  assert.match(css, /\.terminal-host\.mirror-viewport\s+\.xterm-viewport\s*\{[^}]*overscroll-behavior:\s*auto/,
    "exhausted history chains to the host");
  assert.match(css, /\.terminal-host\.app-wheel\s*\{[^}]*touch-action:\s*none/,
    "mouse-tracking TUIs disable native pan so the gesture becomes wheel");

  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /viewport\.scrollTop\s*=\s*gesture\.viewportTop\s*\+\s*historyDy/, "manual vertical scrolls xterm history");
  assert.match(src, /tab\.host\.scrollLeft\s*=\s*gesture\.left\s*\+\s*dx/, "horizontal pans the grid");
  assert.match(src, /historyDy\s*=\s*dy\s*\*\s*MIRROR_SCROLL_GAIN/, "finger travel is amplified");
  assert.match(src, /function fling\(/, "a flick coasts after the finger lifts");
  // Android WebView will not scroll a CSS-zoomed subtree from touch, so the
  // vertical history pan is always driven from JS with our own inertia.
  assert.match(src, /Vertical history\/host pan always runs in JS/,
    "native pan-y is not relied on for the zoomed grid");
  assert.match(src, /manual:\s*!event\.isTrusted/, "synthetic touches use the manual path");
  // A slightly diagonal swipe on a wide grid must not steal the vertical pan.
  assert.match(src, /Math\.abs\(dx\) > Math\.abs\(dy\) \* 1\.35/,
    "horizontal lock requires a clear sideways intent");
  const threshold = src.indexOf("Math.max(Math.abs(dx), Math.abs(dy)) < 6");
  const nextReturn = src.indexOf("return;", threshold);
  assert.equal(src.slice(threshold, nextReturn).includes("preventDefault"), false, "a tap does not cancel the gesture");
});

test("phone scroll gain is above 1:1 so history is reachable without huge swipes", () => {
  assert.equal(MIRROR_SCROLL_GAIN > 1.2, true, `gain ${MIRROR_SCROLL_GAIN} should exceed the old 1.2`);
  assert.equal(MIRROR_SCROLL_GAIN < 2, true, "gain stays usable for precise pans");
});

test("mouse-tracking TUIs (mimo) receive wheel events from vertical touch", () => {
  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /export function appWantsWheel\(tab\)/, "mouse tracking mode is detected");
  assert.match(src, /mouse === "vt200" \|\| mouse === "drag" \|\| mouse === "any"/,
    "all wheel-capable tracking modes count");
  assert.match(src, /function emitWheel\(/, "wheel notches are synthesized");
  assert.match(src, /deltaMode: 1/, "wheel deltas are line-based, matching xterm row reports");
  assert.match(src, /function wheelFling\(/, "a flick keeps scrolling the TUI after the finger lifts");
  assert.match(src, /gesture\.axis === "y" && gesture\.wheel/, "vertical touch in a TUI becomes wheel, not host pan");
  // xterm touch handlers would preventDefault and cancel the native pan.
  assert.match(src, /event\.stopPropagation\(\)/, "xterm's own touch handlers are isolated");
});

test("mirror layout measures CSS once per cell size, not every paint", () => {
  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /const measureCache = new WeakMap\(\)/, "measurement is cached");
  assert.match(src, /function measureMirror\(tab\)/, "getComputedStyle lives in measureMirror");
  const layout = src.slice(src.indexOf("export function layoutMirrorViewport"));
  const body = layout.slice(0, layout.indexOf("export function setMirrorSize"));
  assert.equal(body.includes("getComputedStyle"), false, "layout hot path does not call getComputedStyle");
  assert.match(src, /requestAnimationFrame\(\(\) => \{\s*layoutRaf = 0;\s*layoutMirrorViewport\(tab\);/,
    "onRender coalesces to one layout per frame");
});
