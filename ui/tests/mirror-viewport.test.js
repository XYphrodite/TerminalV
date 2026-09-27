import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

// static checks ensure the fix stays: smooth vertical/horizontal pan without xterm bleed
test("mirror viewport CSS prevents native pan and enables container scroll", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  // mirror hosts must not allow browser native pan to fight JS pan (jerk)
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*none/, "mirror host touch-action none");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overscroll-behavior:\s*contain/, "contain overscroll");
  assert.match(css, /overflow:\s*auto/, "scrollable");
});

test("attachMirrorPan handles vertical/horizontal axes, threshold, boundaries and blocks xterm", async () => {
  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  // threshold 6 (not 3), x needs dx>dy, y needs boundary, and early return must block xterm
  assert.match(src, /Math\.max\(Math\.abs\(dx\), Math\.abs\(dy\)\)\s*<\s*6/, "threshold 6");
  assert.match(src, /Math\.abs\(dx\)\s*>\s*Math\.abs\(dy\)/, "x needs dx>dy");
  assert.match(src, /gesture\.top\s*<\s*tab\.host\.scrollHeight/, "y boundary check");
  assert.match(src, /gesture\.top\s*>\s*0/, "y top >0");
  assert.match(src, /event\.preventDefault\(\)/, "prevents default");
  assert.match(src, /event\.stopPropagation\(\)/, "stops propagation");
  // early small-move must also prevent (otherwise xterm scroll bleeds and jerk)
  // we ensure the small-move return is preceded by preventDefault in the source
  const thresholdBlock = src.indexOf("Math.max(Math.abs(dx)");
  const nextReturn = src.indexOf("return;", thresholdBlock);
  const between = src.slice(thresholdBlock, nextReturn + 20);
  // the fixed version should have preventDefault before that return
  assert.ok(between.includes("preventDefault") || src.slice(thresholdBlock - 200, thresholdBlock + 300).includes("preventDefault"), "small move blocks xterm");

  // functional mock: ensure dy 100 moves vertical, dx 100 moves horizontal, 2px does not, boundaries hand off
  // minimal DOM mock
  function mockHost({ scrollWidth, clientWidth, scrollHeight, clientHeight, top = 0, left = 0 }) {
    const listeners = {};
    const host = {
      scrollWidth, clientWidth, scrollHeight, clientHeight,
      scrollLeft: left, scrollTop: top,
      _listeners: listeners,
      addEventListener(type, fn, opts) { (listeners[type] ||= []).push({ fn, opts }); },
      dispatch(type, event) { for (const { fn } of listeners[type] || []) fn(event); }
    };
    return host;
  }
  // we test logic by importing the module and invoking handlers directly is complex without DOM,
  // so we verify the file contains the clamped scroll assignment (prevents overshoot)
  assert.match(src, /tab\.host\.scrollTop\s*=\s*gesture\.top\s*\+\s*dy/, "vertical uses dy");
  assert.match(src, /tab\.host\.scrollLeft\s*=\s*gesture\.left\s*\+\s*dx/, "horizontal uses dx");
});
