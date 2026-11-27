import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { MOBILE_KEY_SEQUENCES, applyTerminalModifiers, createMobileKeys } from "../src/mobile-keys.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

function fakeButton(key) {
  const handlers = {};
  const classes = new Set();
  const attrs = {};
  return {
    dataset: { key },
    classes,
    attrs,
    classList: {
      toggle: (name, force) => (force === undefined ? !classes.has(name) : force) ? (classes.add(name), true) : (classes.delete(name), false),
      add: (name) => classes.add(name),
      remove: (name) => classes.delete(name),
      contains: (name) => classes.has(name),
    },
    setAttribute: (name, val) => { attrs[name] = String(val); },
    addEventListener: (type, fn) => { (handlers[type] ??= []).push(fn); },
    fire: (type, event = {}) => { for (const fn of handlers[type] ?? []) fn(event); },
  };
}

function fakeHandle(initialText = "Клавиши") {
  const classes = new Set();
  const span = { textContent: initialText };
  return {
    span,
    dataset: {},
    classes,
    classList: { toggle: (name, force) => (force === undefined ? !classes.has(name) : force) ? (classes.add(name), true) : (classes.delete(name), false), contains: (n) => classes.has(n) },
    querySelector: (sel) => sel === "span" ? span : null,
  };
}

function fakeRoot(keys) {
  const buttons = keys.map(fakeButton);
  const handlers = {};
  return {
    buttons,
    querySelectorAll: (selector) => {
      const m = /^\[data-key="([^"]+)"\]$/.exec(selector);
      return m ? buttons.filter((b) => b.dataset.key === m[1]) : buttons;
    },
    addEventListener: (type, fn) => { (handlers[type] ??= []).push(fn); },
    fire: (type, event = {}) => { for (const fn of handlers[type] ?? []) fn(event); },
  };
}

test("arrow keys send VT100 cursor sequences, Esc/Tab send their controls", () => {
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowUp, "\x1b[A");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowDown, "\x1b[B");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowRight, "\x1b[C");
  assert.equal(MOBILE_KEY_SEQUENCES.ArrowLeft, "\x1b[D");
  assert.equal(MOBILE_KEY_SEQUENCES.Escape, "\x1b");
  assert.equal(MOBILE_KEY_SEQUENCES.Tab, "\t");
  for (const d of "0123456789") assert.equal(MOBILE_KEY_SEQUENCES[d], d);
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

test("digit 7 sends once, not twice, on pointer tap", () => {
  const root = fakeRoot(["7"]);
  const sent = [];
  createMobileKeys({ root, send: (key) => sent.push(key) });
  root.buttons[0].fire("pointerdown", { pointerId: 1, clientX: 50, clientY: 20, preventDefault: () => {} });
  assert.deepEqual(sent, [], "must not fire on pointerdown before knowing if user is scrolling");
  root.buttons[0].fire("pointerup", { pointerId: 1, clientX: 52, clientY: 20 });
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, ["7"]);
});

test("scrolling or dragging across digits never presses any digit", () => {
  const root = fakeRoot(["3", "4", "8"]);
  const sent = [];
  createMobileKeys({ root, send: (key) => sent.push(key) });

  // Case 1: finger moves beyond tap slop before pointerup
  root.buttons[0].fire("pointerdown", { pointerId: 1, clientX: 100, clientY: 20, preventDefault: () => {} });
  root.buttons[0].fire("pointermove", { pointerId: 1, clientX: 120, clientY: 20 });
  root.buttons[0].fire("pointerup", { pointerId: 1, clientX: 120, clientY: 20 });
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, []);

  // Case 2: browser claims gesture for native horizontal scroll (pointercancel)
  root.buttons[1].fire("pointerdown", { pointerId: 2, clientX: 140, clientY: 20, preventDefault: () => {} });
  root.buttons[1].fire("pointercancel", { pointerId: 2 });
  root.buttons[1].fire("click", {});
  assert.deepEqual(sent, []);

  // Case 3: row scroll event fires while finger is down, then lifts on another key
  root.buttons[0].fire("pointerdown", { pointerId: 3, clientX: 100, clientY: 20, preventDefault: () => {} });
  root.fire("scroll");
  root.buttons[2].fire("pointerup", { pointerId: 3, clientX: 200, clientY: 20 });
  root.buttons[0].fire("pointerup", { pointerId: 3, clientX: 100, clientY: 20 });
  assert.deepEqual(sent, []);

  // Subsequent real tap on digit 8 works immediately
  root.buttons[2].fire("pointerdown", { pointerId: 4, clientX: 200, clientY: 20, preventDefault: () => {} });
  root.buttons[2].fire("pointerup", { pointerId: 4, clientX: 201, clientY: 21 });
  root.buttons[2].fire("click", {});
  assert.deepEqual(sent, ["8"]);
});

test("second init on same root does not double-wire (mobile double-terminal guard)", () => {
  const root = fakeRoot(["1", "2"]);
  // simulate dataset as real DOM element
  root.dataset = {};
  const sent = [];
  createMobileKeys({ root, send: (key) => sent.push(key) });
  // second wiring should be no-op due to dataset flag
  createMobileKeys({ root, send: (key) => sent.push(key) });
  root.buttons[0].fire("pointerdown", { preventDefault: () => {} });
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, ["1"]);
});

test("unknown keys are ignored and missing root is a noop", () => {
  const root = fakeRoot(["F13"]);
  const sent = [];
  const api = createMobileKeys({ root, send: (key) => sent.push(key) });
  root.buttons[0].fire("click", {});
  assert.deepEqual(sent, []);
  api.sendKey("ArrowUp");
  assert.deepEqual(sent, ["ArrowUp"]);
  api.sendKey("F13");
  assert.deepEqual(sent, ["ArrowUp"]);
  assert.doesNotThrow(() => createMobileKeys({ root: null, send: () => {} }));
});

test("key bar markup exists in desktop and mobile shells", () => {
  for (const htmlPath of ["ui/index.html", "src/TerminalV.Mobile/wwwroot/index.html"]) {
    const html = readFileSync(r(htmlPath), "utf8");
    assert.match(html, /id="mobile-keys"/, `${htmlPath}: key bar`);
    assert.match(html, /data-mobile-only/, `${htmlPath}: mobile-only`);
    for (const key of ["Escape", "Tab", "Ctrl", "Alt", "ArrowLeft", "ArrowUp", "ArrowDown", "ArrowRight"]) {
      assert.match(html, new RegExp(`data-key="${key}"`), `${htmlPath}: ${key} button`);
    }
    for (const d of "0123456789") {
      assert.match(html, new RegExp(`data-key="${d}"`), `${htmlPath}: digit ${d}`);
    }
    assert.match(html, /data-row="digits"/, `${htmlPath}: digits row`);
    assert.match(html, /data-row="nav"/, `${htmlPath}: nav row`);
    assert.match(html, /id="font-size"[^>]*min="8"/, `${htmlPath}: slider allows 8`);
    const handleBlock = html.slice(html.indexOf('handle.addEventListener("pointerdown"'));
    assert.match(handleBlock, /e\.preventDefault\(\)/, `${htmlPath}: handle keeps soft keyboard open`);
  }
});

test("sticky Alt and Ctrl apply to Gboard/onData input, Cyrillic keys, panel keys, and ignore mouse/focus reports", () => {
  // Pure modifier encoding checks
  assert.deepEqual(applyTerminalModifiers("m", { alt: true }), { data: "\x1bm", consumed: true });
  assert.deepEqual(applyTerminalModifiers("ь", { alt: true }), { data: "\x1bm", consumed: true });
  assert.deepEqual(applyTerminalModifiers("M", { alt: true }), { data: "\x1bM", consumed: true });
  assert.deepEqual(applyTerminalModifiers("c", { ctrl: true }), { data: "\x03", consumed: true });
  assert.deepEqual(applyTerminalModifiers("с", { ctrl: true }), { data: "\x03", consumed: true });
  assert.deepEqual(applyTerminalModifiers("m", { ctrl: true, alt: true }), { data: "\x1b\r", consumed: true });
  assert.deepEqual(applyTerminalModifiers("\x7f", { alt: true }), { data: "\x1b\x7f", consumed: true });
  assert.deepEqual(applyTerminalModifiers("\x7f", { ctrl: true }), { data: "\x08", consumed: true });
  assert.deepEqual(applyTerminalModifiers("\x1b[A", { alt: true }), { data: "\x1b[1;3A", consumed: true });
  assert.deepEqual(applyTerminalModifiers("\x1b[D", { ctrl: true, alt: true }), { data: "\x1b[1;7D", consumed: true });
  assert.equal(MOBILE_KEY_SEQUENCES["Alt+ArrowUp"], "\x1b[1;3A");
  assert.equal(MOBILE_KEY_SEQUENCES["Ctrl+Alt+ArrowLeft"], "\x1b[1;7D");
  assert.equal(MOBILE_KEY_SEQUENCES["Alt+0"], "\x1b0");

  // Terminal protocol reports must not consume modifiers
  for (const report of ["\x1b[I", "\x1b[O", "\x1b[<0;12;5M", "\x1b[<35;12;5m", "\x1b[12;34R", "\x1b[200~pasted"]) {
    assert.deepEqual(applyTerminalModifiers(report, { alt: true }), { data: report, consumed: false });
  }

  // Stateful sticky modifier lifecycle + handle badge
  const root = fakeRoot(["Ctrl", "Alt", "ArrowUp", "0"]);
  const handle = fakeHandle();
  const sent = [];
  const api = createMobileKeys({ root, handle, send: (k) => sent.push(k) });

  // Tap Alt -> handle & Alt button show active state
  root.buttons[1].fire("click", {});
  assert.equal(api.altActive, true);
  assert.equal(api.ctrlActive, false);
  assert.equal(root.buttons[1].classList.contains("active"), true);
  assert.equal(root.buttons[1].attrs["aria-pressed"], "true");
  assert.equal(handle.span.textContent, "Клавиши · Alt");
  assert.equal(handle.classList.contains("has-modifiers"), true);

  // Focusing terminal emits \x1b[I and clicking emits mouse report -> Alt stays armed
  assert.equal(api.applyInput("\x1b[I"), "\x1b[I");
  assert.equal(api.applyInput("\x1b[<0;10;20M"), "\x1b[<0;10;20M");
  assert.equal(api.altActive, true);

  // Typing "m" on soft keyboard produces \x1bm and disarms Alt
  assert.equal(api.applyInput("m"), "\x1bm");
  assert.equal(api.altActive, false);
  assert.equal(root.buttons[1].classList.contains("active"), false);
  assert.equal(handle.span.textContent, "Клавиши");
  assert.equal(handle.classList.contains("has-modifiers"), false);

  // Arm both Ctrl and Alt, then tap ArrowUp on key bar
  root.buttons[0].fire("click", {});
  root.buttons[1].fire("click", {});
  assert.equal(handle.span.textContent, "Клавиши · Ctrl+Alt");
  root.buttons[2].fire("click", {});
  assert.deepEqual(sent, ["Ctrl+Alt+ArrowUp"]);
  assert.equal(api.ctrlActive, false);
  assert.equal(api.altActive, false);

  // Toggle off Alt without firing anything
  root.buttons[1].fire("click", {});
  assert.equal(api.altActive, true);
  root.buttons[1].fire("click", {});
  assert.equal(api.altActive, false);
  assert.equal(handle.span.textContent, "Клавиши");
});

test("main.js wires the key bar to the pty write channel", () => {
  const main = readFileSync(r("ui/src/main.js"), "utf8");
  assert.match(main, /createMobileKeys\(\{/, "binds key bar");
  assert.match(main, /mobileKeys\.applyInput\(data\)/, "applies sticky modifiers to typed input");
  assert.match(main, /post\(\{ type: "write", id: tab\.id, data \}\)/, "sends raw sequences to pty");
  assert.match(main, /letterSpacing: 0/, "no extra letter spacing");
  assert.match(main, /Roboto Mono.*Droid Sans Mono.*Noto Sans Mono/, "android monospace fallbacks");
  const mobileKeysBlock = main.slice(main.indexOf("createMobileKeys({"), main.indexOf("createMobileKeys({") + 400);
  assert.doesNotMatch(mobileKeysBlock, /tab\.term\.focus\(\)/, "key bar does not force keyboard");
  assert.match(main, /window\.dispatchEvent\(new Event\("resize"\)\)/, "resizes terminal when key bar toggles");
});

test("key bar styling stays hidden on desktop and fits touch targets", () => {
  const css = readFileSync(r("ui/src/chrome.css"), "utf8");
  assert.match(css, /\.mobile-keys\[hidden\]\s*\{\s*display:\s*none/, "hidden wins over flex");
  assert.match(css, /\.mobile-keys button\s*\{[^}]*min-height:\s*40px/, "touch-sized keys");
  assert.match(css, /\.mobile-keys button\s*\{[^}]*touch-action:\s*pan-x/, "buttons allow horizontal pan");
  assert.match(css, /\.mobile-keys-row\s*\{[^}]*flex-wrap:\s*nowrap/, "rows do not wrap");
  assert.match(css, /\.mobile-keys-row\s*\{[^}]*overflow-x:\s*auto/, "key rows scrollable by default");
  assert.match(css, /\.mobile-keys-row\s*\{[^}]*touch-action:\s*pan-x/, "row allows horizontal pan");
  assert.match(css, /\.mobile-keys-row\[data-row="digits"\]\s*\{[^}]*overflow-x:\s*hidden/, "digits row fits screen without scroll");
  assert.match(css, /\.mobile-keys-row\[data-row="digits"\]\s*button\s*\{[^}]*flex:\s*1\s+1\s+0/, "all 10 digits share width evenly");
  assert.match(css, /\.mobile-keys-row\[data-row="digits"\]\s*button\s*\{[^}]*min-width:\s*0/, "digits shrink to fit narrow phones");
  assert.match(css, /\.mobile-keys\s*\{[^}]*flex-direction:\s*column/, "two rows without top gap");
  assert.match(css, /button\[data-key="Alt"\]\.active/, "Alt sticky active style");
  assert.match(css, /\.mobile-keys-handle\.has-modifiers/, "handle modifier indicator style");
  const appCss = readFileSync(r("src/TerminalV.Mobile/wwwroot/css/app.css"), "utf8");
  assert.match(appCss, /\.mobile-keys-row\[data-row="digits"\]\s*button\s*\{[^}]*flex:\s*1\s+1\s+0/, "mobile app.css also fits all 10 digits");
  assert.match(appCss, /\.mobile-keys-row\[data-row="digits"\]\s*button\s*\{[^}]*min-width:\s*0/, "mobile app.css does not force min-width overflow");
  assert.match(appCss, /button\[data-key="Alt"\]\.active/, "mobile app.css Alt sticky active style");
  assert.match(appCss, /\.mobile-keys-handle\.has-modifiers/, "mobile app.css handle modifier indicator style");
  const mobileJs = readFileSync(r("src/TerminalV.Mobile/wwwroot/js/terminal.js"), "utf8");
  assert.match(mobileJs, /pointercancel/, "fallback mobile terminal.js also cancels on scroll");
  assert.match(mobileJs, /applyTerminalModifiers/, "fallback mobile terminal.js supports sticky Alt/Ctrl");
});

test("mobile fit mode toggles desktop PTY sizing", () => {
  const html = readFileSync(r("ui/index.html"), "utf8");
  assert.match(html, /id="mobile-fit-mode"/, "fit toggle exists");
  assert.match(html, /data-mobile-only/, "fit toggle mobile-only");
  const main = readFileSync(r("ui/src/main.js"), "utf8");
  assert.match(main, /mobileFitModeEl/, "JS wires fit toggle");
  assert.match(main, /MobileFitMode/, "persists as MobileFitMode for C#");
  assert.match(main, /pty-geometry/, "desktop syncs shared PTY geometry to prevent ghost columns");
  assert.match(main, /reclaimDesktopFit/, "desktop reclaims full window width on user interaction");
  assert.match(main, /release-geometry/, "disabling fit mode releases phone geometry ownership");
  const cs = readFileSync(r("src/TerminalV/Data/AppSettings.cs"), "utf8");
  assert.match(cs, /MobileFitMode/, "AppSettings has MobileFitMode");
  const bridge = readFileSync(r("src/TerminalV.Mobile/Host/MobileBridge.cs"), "utf8");
  assert.match(bridge, /MobileFitMode/, "bridge respects fit mode");
  assert.match(bridge, /ReleaseMirroredGeometry/, "mobile bridge releases geometry when fit mode turns off");
});
