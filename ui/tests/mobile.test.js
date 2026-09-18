import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, existsSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "../..");
const r = (p) => resolve(repoRoot, p);

test("MAUI project exists with correct targets", () => {
  const csproj = readFileSync(r("src/TerminalV.Mobile/TerminalV.Mobile.csproj"), "utf8");
  assert.match(csproj, /net10\.0-android/, "android target");
  assert.match(csproj, /net10\.0-ios/, "ios target");
  assert.match(csproj, /net10\.0-windows/, "windows target");
  assert.match(csproj, /SSH\.NET/, "SSH.NET reference");
  assert.match(csproj, /UseMaui.*true/, "UseMaui");
});

test("Mobile terminal.js handles xterm write/onData and WebLinks", () => {
  const js = readFileSync(r("src/TerminalV.Mobile/wwwroot/js/terminal.js"), "utf8");
  assert.match(js, /new Terminal/, "creates Terminal");
  assert.match(js, /WebLinksAddon/, "WebLinks");
  assert.match(js, /term\.onData/, "onData");
  assert.match(js, /function write/, "write function");
  assert.match(js, /window\.TerminalV/, "exposes TerminalV");
});

test("SSH service covers direct and gateway with shell", () => {
  const base = readFileSync(r("src/TerminalV/Ssh/SshSessionBase.cs"), "utf8");
  const opts = readFileSync(r("src/TerminalV/Ssh/SshConnectionOptions.cs"), "utf8");
  const direct = readFileSync(r("src/TerminalV/Ssh/SshNetSession.cs"), "utf8");
  const gateway = readFileSync(r("src/TerminalV/Ssh/GatewaySshSession.cs"), "utf8");
  assert.match(opts, /Host/, "Host");
  assert.match(opts, /Port/, "Port");
  assert.match(opts, /Username/, "Username");
  assert.match(direct, /SshClient/, "SshClient");
  assert.match(direct, /ShellStream/, "ShellStream");
  assert.match(direct, /xterm-256color/, "term type");
  assert.match(gateway, /ClientWebSocket/, "ClientWebSocket");
  assert.match(gateway, /xterm-256color/, "term type gateway");
  assert.match(base, /WriteAsync/, "WriteAsync");
  assert.match(base, /Resize/, "Resize");
});

test("Mobile Slnx includes TerminalV.Mobile", () => {
  const slnx = readFileSync(r("TerminalV.slnx"), "utf8");
  assert.match(slnx, /TerminalV\.Mobile/, "slnx includes Mobile");
});

test("WPF Ssh folder exists for shared gateway", () => {
  assert.ok(existsSync(r("src/TerminalV/Ssh/GatewaySshSession.cs")), "GatewaySshSession exists");
  assert.ok(existsSync(r("src/TerminalV/Ssh/SshNetSession.cs")), "SshNetSession exists");
});

test("Mobile Home has connection form with Host/Port/User/Password/Gateway and terminal", () => {
  const home = readFileSync(r("src/TerminalV.Mobile/Components/Pages/Home.razor"), "utf8");
  assert.match(home, /Хост/, "Host field");
  assert.match(home, /Порт/, "Port field");
  assert.match(home, /Пользователь/, "User field");
  assert.match(home, /Пароль/, "Password field");
  assert.match(home, /Gateway/, "Gateway toggle");
  assert.match(home, /Подключиться/, "Connect button");
  assert.match(home, /SshService/, "uses SshService");
  assert.match(home, /SshConnectionOptions/, "uses SshConnectionOptions");
  assert.match(home, /terminal-container/, "terminal pane");
  assert.match(home, /terminalInterop\.create/, "creates terminal");
  assert.match(home, /DataReceived/, "handles DataReceived");
});
