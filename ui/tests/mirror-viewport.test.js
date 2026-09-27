import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

// Before the mirror, a vertical drag scrolled xterm's viewport through the
// whole scrollback. Panning the host only reaches the extra desktop rows.
test("mirror vertical scroll uses xterm history and horizontal pans the grid", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overflow:\s*auto/, "grid can pan");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*none/, "host does not eat the gesture");

  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  assert.match(src, /viewport\.scrollTop\s*=\s*gesture\.viewportTop\s*\+\s*dy/, "vertical scrolls xterm history");
  assert.match(src, /tab\.host\.scrollLeft\s*=\s*gesture\.left\s*\+\s*dx/, "horizontal pans the grid");
  const threshold = src.indexOf("Math.max(Math.abs(dx), Math.abs(dy)) < 6");
  const nextReturn = src.indexOf("return;", threshold);
  assert.equal(src.slice(threshold, nextReturn).includes("preventDefault"), false, "a tap does not cancel the gesture");
});
