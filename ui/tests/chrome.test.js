import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const cssPath = resolve(dirname(fileURLToPath(import.meta.url)), "../src/chrome.css");
const css = readFileSync(cssPath, "utf8");

test("session options button is always visible in expanded sidebar", () => {
  assert.match(css, /\.tab-close,\s*\.tab-options\s*\{[^}]*opacity:\s*1[^}]*\}/, "tab-options should have opacity:1");
  assert.match(css, /\.tab-close,\s*\.tab-options\s*\{[^}]*background:\s*var\(--surface\)[^}]*\}/, "tab-options should have surface background");
  assert.match(css, /\.tab-close,\s*\.tab-options\s*\{[^}]*border:\s*1px solid var\(--border-soft\)/, "tab-options should have border");
});

test("collapsed sidebar shows options on hover", () => {
  assert.match(css, /#app\.collapsed\s*\.tab:hover\s*\.tab-options/, "collapsed hover should show tab-options");
  assert.match(css, /#app\.collapsed\s*\.tab\.split-child/, "collapsed split-child should be handled");
});
