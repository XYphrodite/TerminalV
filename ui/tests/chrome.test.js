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

test("collapsed sidebar never shows session options", () => {
  assert.match(css, /#app\.collapsed\s*\.tab\s*\.tab-options\s*\{[^}]*display:\s*none/, "collapsed should hide tab-options");
  assert.doesNotMatch(css, /#app\.collapsed[^{]*\.tab-options\s*\{[^}]*display:\s*grid/, "collapsed must not re-show tab-options");
  assert.match(css, /#app\.collapsed\s*\.tab\.split-child/, "collapsed split-child should be handled");
});
