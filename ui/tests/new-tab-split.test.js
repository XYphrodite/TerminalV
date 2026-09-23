import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const dir = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(resolve(dir, "../index.html"), "utf8");
const mainJs = readFileSync(resolve(dir, "../src/main.js"), "utf8");
const launchMenuJs = readFileSync(resolve(dir, "../src/launch-menu.js"), "utf8");
const chromeCss = readFileSync(resolve(dir, "../src/chrome.css"), "utf8");
const stylesCss = readFileSync(resolve(dir, "../src/styles.css"), "utf8");

test("new tab is a split button with profile combo", () => {
  assert.match(html, /id="new-tab-split"/, "split container must exist");
  assert.match(html, /id="new-tab"/, "primary button must exist");
  assert.match(html, /id="new-tab-arrow"/, "arrow button must exist");
  assert.match(html, /aria-haspopup="dialog"/, "arrow must be dialog trigger");
  assert.match(html, /aria-controls="launch-menu"/, "arrow must control launch menu");
  // verify arrow is inside split container and after new-tab
  const splitIndex = html.indexOf('id="new-tab-split"');
  const tabIndex = html.indexOf('id="new-tab"');
  const arrowIndex = html.indexOf('id="new-tab-arrow"');
  assert.ok(splitIndex !== -1 && tabIndex > splitIndex && arrowIndex > tabIndex, "arrow must be inside split after new-tab");
});

test("split button has correct styling", () => {
  assert.match(chromeCss, /\.new-tab-split\s*\{[^}]*display:\s*flex/, "split container must be flex");
  assert.match(chromeCss, /\.new-tab-split\s+#new-tab\s*\{[^}]*flex:\s*1/, "primary button must flex");
  assert.match(chromeCss, /\.new-tab-arrow\s*\{[^}]*background:\s*var\(--accent\)/, "arrow must use accent background");
  assert.match(chromeCss, /#app\.collapsed\s+#new-tab-arrow\s*\{[^}]*display:\s*none/, "arrow must hide when collapsed");
});

test("main button creates default tab, arrow opens profile menu", () => {
  assert.match(mainJs, /newTabBtn\.addEventListener\("click",\s*\(\) => newTab\(\)\)/, "new-tab click must create default tab");
  assert.match(mainJs, /newTabArrowBtn\.addEventListener\("click"/, "arrow must have click handler");
  assert.match(mainJs, /launchMenu\.open\(newTabArrowBtn\)/, "arrow must open launch menu with anchor");
});

test("launch menu supports alternate trigger", () => {
  assert.match(launchMenuJs, /let activeTrigger/, "must track active trigger");
  assert.match(launchMenuJs, /open\(anchor/, "open must accept anchor param");
  assert.match(launchMenuJs, /activeTrigger.*getBoundingClientRect/, "position must use active trigger");
});
