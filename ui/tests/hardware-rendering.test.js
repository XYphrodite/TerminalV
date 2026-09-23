import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

const dir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const html = readFileSync(resolve(dir, "index.html"), "utf8");
const main = readFileSync(resolve(dir, "src/main.js"), "utf8");
const cs = readFileSync(resolve(dir, "../src/TerminalV/Data/AppSettings.cs"), "utf8");

test("settings dialog has hardware rendering switch", () => {
  assert.match(html, /<input[^>]*id="hardware-rendering"[^>]*type="checkbox"/, "checkbox must exist");
});

test("renderer honors the setting with fallback", () => {
  assert.match(main, /hardwareRendering:\s*true/, "default must be on");
  assert.match(main, /!settings\.hardwareRendering/, "ensureWebgl must be gated");
  assert.match(main, /dropWebgl\(tab\)/, "toggle off must dispose webgl");
  assert.match(main, /persistSettings\(\)/, "choice must persist");
});

test("host settings model carries the flag default-on", () => {
  assert.match(cs, /bool\s+HardwareRendering\s*\{[^}]*\}\s*=\s*true/, "C# default must be true");
});
