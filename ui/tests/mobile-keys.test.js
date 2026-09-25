import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { MOBILE_KEY_SEQUENCES, createMobileKeys } from "../src/mobile-keys.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

function fakeButton(key) {
  const handlers = {};
  return {
    dataset: { key },
    addEventListener: (type, fn) => { (handlers[type] ??= []).push(fn); },
    fire: (type, event = {}) => { for (const fn of handlers[type] ?? []) fn(event); },
  };
}

function fakeRoot(keys) {
  const buttons = keys.map(fakeButton);
  return { buttons, querySelectorAll: () => buttons };
}

test("arrow keys send VT100 cursor sequences, Esc/Tab send their controls", () => {
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowUp, "\x1b[A");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowDown, "\x1b[B");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowRight, "\x1b[C");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowLeft, "\x1b[D");
  assert.equal(MOBILE_KEY_SEQUENCES.Escape, "\x1b");
  assert.equal(MOBILE_KEY_SEQUENCES.Tab, "\t");
});

test("documented aliases map to same sequences", () => {
  assert.equal(MOBILE_KEY_SEQUENCES.Esc, "\x1b");
  assert.equal(MOBILE_KEY_SEQUENCES.Up, "\x1b[A");
  assert.equal(MOBILE_KEY_SEQUENCES.Down, "\x1b[B");
  assert.equal(MOBILE_KEY_SEQUENCES.Left, "\x1b[D");
  assert.equal(MOBILE_KEY_SEQUENCES.Right, "\x1b[C");
});

test("pointer tap sends once without focus change; keyboard click still works", () => {
  const root = fakeRoot(["ArrowUp"]);
  const sent = [];
  createMobileKeys({ root, send: (key) => sent.push(key) });
  let prevented = 0;
  root.buttons[0].fire("pointerdown", { preventDefault: () => { prevented++; } });
  root.buttons[0].fire("click", {});
  assert.equal(prevented, 1);
  assert.deepEqual(sent, ["ArrowUp"]);
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, ["ArrowUp", "ArrowUp"]);
});

test("unknown keys are ignored and missing root is a noop", () => {
  const root = fakeRoot(["Enter"]);
  const sent = [];
  const api = createMobileKeys({ root, send: (key) => sent.push(key) });
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, []);
  api.sendKey("ArrowUp");
  assert.deepEqual(sent, ["ArrowUp"]);
  api.sendKey("Enter");
  assert.deepEqual(sent, ["ArrowUp"]);
  assert.doesNotThrow(() => createMobileKeys({ root: null, send: () => {} }));
});

test("key bar markup exists in desktop and mobile shells", () => {
  for (const htmlPath of ["ui/index.html", "src/TerminalV.Mobile/wwwroot/index.html"]) {
    const html = readFileSync(r(htmlPath), "utf8");
    assert.match(html, /id="mobile-keys"/, `${htmlPath}: key bar`);
    assert.match(html, /data-mobile-only/, `${htmlPath}: mobile-only`);
    for (const key of ["Escape", "Tab", "ArrowLeft", "ArrowUp", "ArrowDown", "ArrowRight"]) {
      assert.match(html, new RegExp(`data-key="${key}"`), `${htmlPath}: ${key} button`);
    }
    assert.match(html, /id="font-size"[^>]*min="8"/, `${htmlPath}: slider allows 8`);
  }
});

test("main.js wires the key bar to the pty write channel", () => {
  const main = readFileSync(r("ui/src/main.js"), "utf8");
  assert.match(main, /createMobileKeys\(\{/, "binds key bar");
  assert.match(main, /post\(\{ type: "write", id: tab\.id, data \}\)/, "sends raw sequences to pty");
  assert.match(main, /letterSpacing: 0/, "no extra letter spacing");
  assert.match(main, /Roboto Mono.*Droid Sans Mono.*Noto Sans Mono/, "android monospace fallbacks");
});

test("key bar styling stays hidden on desktop and fits touch targets", () => {
  const css = readFileSync(r("ui/src/chrome.css"), "utf8");
  assert.match(css, /\.mobile-keys\[hidden\]\s*\{\s*display:\s*none/, "hidden wins over flex");
  assert.match(css, /\.mobile-keys button\s*\{[^}]*min-height:\s*44px/, "touch-sized keys");
});
