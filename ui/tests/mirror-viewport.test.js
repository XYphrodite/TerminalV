import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { MIRROR_SCROLL_GAIN, computeChainedScroll } from "../src/mirror-viewport.js";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

// Vertical pan runs in JS with touch-action: none so Android WebView never
// hijacks slow touches into broken native pan-y over a CSS-zoomed subtree.
// Horizontal pan is gone: a phone never shows a horizontal scrollbar — the
// grid is zoomed to fit width (and fit mode shrinks the PTY instead).
// Mouse-tracking TUIs (mimo) get wheel events instead of a dead panning host.
test("mirror vertical scroll uses xterm history, chains to host, and disables native pan-y", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overflow:\s*auto/, "grid can pan");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*none/,
    "touch-action: none keeps Android WebView from cancelling JS vertical scroll");
  assert.match(css, /\.terminal-host\.mirror-viewport\s+\.xterm-viewport\s*\{[^}]*touch-action:\s*none/,
    "zoomed xterm viewport also uses JS vertical scroll");
  assert.match(css, /\.terminal-host\.app-wheel\s*\{[^}]*touch-action:\s*none/,
    "mouse-tracking TUIs disable native pan so the gesture becomes wheel");

  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /computeChainedScroll\(gesture\.top,\s*hostMax,\s*gesture\.viewportTop,\s*vpMax,\s*historyDy/,
    "vertical pan chains host and xterm viewport scroll");
  assert.doesNotMatch(src, /scrollLeft\s*=/,
    "horizontal pan is removed — no sideways scroll on a phone");
  assert.match(src, /gesture\.axis = "y"/, "every gesture is vertical");
  assert.match(src, /gesture\.scrollRaf = requestAnimationFrame/,
    "pan commits are coalesced to one scroll write per frame");
  assert.match(src, /historyDy\s*=\s*dy\s*\*\s*MIRROR_SCROLL_GAIN/, "finger travel is amplified");
  assert.match(src, /function startFling\(/, "a flick coasts after the finger lifts");
  // Android WebView will not scroll a CSS-zoomed subtree from touch, so the
  // vertical history pan is always driven from JS with our own inertia.
  assert.match(src, /Vertical history\/host pan always runs in JS/,
    "native pan-y is not relied on for the zoomed grid");
  const threshold = src.indexOf("Math.max(Math.abs(dx), Math.abs(dy)) < 6");
  const nextReturn = src.indexOf("return;", threshold);
  assert.equal(src.slice(threshold, nextReturn).includes("preventDefault"), false, "a tap does not cancel the gesture");
});

test("computeChainedScroll chains smoothly between tall zoomed host and xterm history", () => {
  // Scrolling UP (delta < 0) from bottom: first reveals top of tall zoomed host, then enters history with zoom compensation.
  assert.deepEqual(
    computeChainedScroll(80, 80, 500, 500, -120, 0.5),
    { hostTop: 0, vpTop: 420 }
  );
  // Scrolling DOWN (delta > 0) from history: returns to live bottom of history first, then reveals bottom of tall host.
  assert.deepEqual(
    computeChainedScroll(0, 80, 420, 500, 120, 0.5),
    { hostTop: 80, vpTop: 500 }
  );
  // Pure history (no host overflow) compensates for 0.5x CSS zoom.
  assert.deepEqual(
    computeChainedScroll(0, 0, 500, 500, -100, 0.5),
    { hostTop: 0, vpTop: 300 }
  );
  // Pure host (no history) scrolls host directly without dividing by zoom.
  assert.deepEqual(
    computeChainedScroll(0, 150, 0, 0, 140, 0.5),
    { hostTop: 140, vpTop: 0 }
  );
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
