import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

// v0.7.16 let the browser pan the mirror. v0.7.25 set touch-action: none and
// preventDefault on the first pixels, which left only a short vertical pan.
test("mirror viewport keeps native vertical and horizontal pan", () => {
  const css = readFileSync(r("ui/src/styles.css"), "utf8");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*overflow:\s*auto/, "scrollable");
  assert.match(css, /\.terminal-host\.mirror-viewport\s*\{[^}]*touch-action:\s*pan-x\s+pan-y/, "browser pans both axes");
  const block = css.match(/\.terminal-host\.mirror-viewport\s*\{[^}]*\}/)[0];
  assert.equal(block.includes("touch-action: none"), false, "native pan stays enabled");

  const src = readFileSync(r("ui/src/mirror-viewport.js"), "utf8");
  const threshold = src.indexOf("Math.max(Math.abs(dx), Math.abs(dy)) < 6");
  const nextReturn = src.indexOf("return;", threshold);
  assert.equal(src.slice(threshold, nextReturn).includes("preventDefault"), false, "early move does not cancel the browser pan");
});
