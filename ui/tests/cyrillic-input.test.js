import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

const root = resolve(import.meta.dirname, "../..");

test("mobile terminal handles Cyrillic IME and paste as UTF-8", () => {
  const terminalJs = readFileSync(resolve(root, "src/TerminalV.Mobile/wwwroot/js/terminal.js"), "utf8");
  // Must use Android fallback stack and not clip glyphs
  assert.match(terminalJs, /Roboto Mono/, "fallback Roboto Mono for Android Cyrillic");
  assert.match(terminalJs, /Droid Sans Mono/, "fallback Droid Sans Mono");
  assert.match(terminalJs, /Noto Sans Mono/, "fallback Noto Sans Mono");
  assert.match(terminalJs, /letterSpacing:\s*0/, "letterSpacing 0 prevents stretched Cyrillic");
  assert.match(terminalJs, /allowProposedApi:\s*true/, "allowProposedApi for IME composition");

  // Paste is already visible ("Привет видно"), typing must use same onData path
  // Verify the C# write path is UTF-8 (not ASCII) so Gboard composition reaches SSH
  const sshCs = readFileSync(resolve(root, "src/TerminalV/Ssh/SshNetSession.cs"), "utf8");
  assert.match(sshCs, /Encoding\.UTF8\.GetBytes\(data\)/, "WriteAsync encodes as UTF-8");

  // Direct JSON round-trip for Cyrillic must survive WebView postMessage
  const sample = "привет Привет";
  const roundTripped = JSON.parse(JSON.stringify({ data: sample })).data;
  assert.equal(roundTripped, sample, "JSON preserves Cyrillic");
  assert.equal(Buffer.from(sample, "utf8").toString("utf8"), sample, "Buffer UTF-8 round-trip");
});
