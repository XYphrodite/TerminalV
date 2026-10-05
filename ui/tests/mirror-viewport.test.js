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
test("mirror vertical scroll uses xterm history and horizontal pans the grid", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overflow:\s*auto/, "grid can pan");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*pan-y/, "native vertical, JS horizontal");
  assert.match(css, /\.terminal-host\.mirror-viewport\s+\.xterm-viewport\s*\{[^}]*overscroll-behavior:\s*auto/,
    "exhausted history chains to the host");

  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /viewport\.scrollTop\s*=\s*gesture\.viewportTop\s*\+\s*historyDy/, "manual vertical scrolls xterm history");
  assert.match(src, /tab\.host\.scrollLeft\s*=\s*gesture\.left\s*\+\s*dx/, "horizontal pans the grid");
  assert.match(src, /historyDy\s*=\s*dy\s*\*\s*MIRROR_SCROLL_GAIN/, "manual finger travel is amplified");
  assert.match(src, /function fling\(/, "a flick coasts after the finger lifts on the manual path");
  assert.match(src, /gesture\.axis === "y" && !gesture\.manual/, "trusted vertical is left to the browser");
  assert.match(src, /manual:\s*!event\.isTrusted/, "synthetic touches use the manual path");
  const threshold = src.indexOf("Math.max(Math.abs(dx), Math.abs(dy)) < 6");
  const nextReturn = src.indexOf("return;", threshold);
  assert.equal(src.slice(threshold, nextReturn).includes("preventDefault"), false, "a tap does not cancel the gesture");
});

test("phone scroll gain is above 1:1 so history is reachable without huge swipes", () => {
  assert.equal(MIRROR_SCROLL_GAIN > 1.2, true, `gain ${MIRROR_SCROLL_GAIN} should exceed the old 1.2`);
  assert.equal(MIRROR_SCROLL_GAIN < 2, true, "gain stays usable for precise pans");
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
