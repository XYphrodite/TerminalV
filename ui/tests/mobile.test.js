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
  // parity: Home now hosts desktop UI via bridge, primary button text changed but connection fields persist
  assert.match(home, /Сохранить и показать терминал|Подключиться/, "Save/Connect button");
  assert.match(home, /SshService|MobileBridge/, "uses SshService or MobileBridge");
  assert.match(home, /SshConnectionOptions|MobileDataStore/, "uses connection/storage");
});

test("Mobile parity: wwwroot hosts desktop UI and bridge shim", () => {
  const html = readFileSync(r("src/TerminalV.Mobile/wwwroot/index.html"), "utf8");
  assert.match(html, /id="app"/, "desktop #app");
  assert.match(html, /id="sidebar"/, "sidebar");
  assert.match(html, /id="tabs"/, "tabs");
  assert.match(html, /id="panes"/, "panes");
  assert.match(html, /id="new-tab-split"/, "new-tab split");
  assert.match(html, /js\/mobile-bridge\.js/, "mobile bridge shim");
  assert.match(html, /assets\/index-.*\.js/, "desktop bundle");
  assert.match(html, /assets\/index-.*\.css/, "desktop styles");
  assert.match(html, /blazor-root/, "blazor root isolated");
  assert.match(html, /blazor\.webview\.js/, "blazor bootstrap");
});

test("MobileBridge handles desktop protocol and persists via Preferences", () => {
  const bridge = readFileSync(r("src/TerminalV.Mobile/Host/MobileBridge.cs"), "utf8");
  assert.match(bridge, /class MobileBridge/, "MobileBridge exists");
  assert.match(bridge, /SendInit/, "SendInit");
  assert.match(bridge, /Handle\(string json\)/, "Handle json");
  assert.match(bridge, /case "create"/, "handles create");
  assert.match(bridge, /case "write"/, "handles write");
  assert.match(bridge, /case "resize"/, "handles resize");
  assert.match(bridge, /case "persist-settings"/, "persists settings");
  assert.match(bridge, /MobileDataStore/, "uses MobileDataStore");
  assert.match(bridge, /__tvDispatch/, "dispatches to JS");
  const shim = readFileSync(r("src/TerminalV.Mobile/wwwroot/js/mobile-bridge.js"), "utf8");
  assert.match(shim, /chrome\.webview/, "shims chrome.webview");
  assert.match(shim, /__tvDispatch/, "dispatch helper");
  assert.match(shim, /DotNet\.invokeMethodAsync/, "calls DotNet");
  const store = readFileSync(r("src/TerminalV.Mobile/Host/MobileDataStore.cs"), "utf8");
  assert.match(store, /Preferences\.Default/, "Preferences storage");
  assert.match(store, /LoadSettings|SaveSettings/, "settings");
  assert.match(store, /LoadSessions|SaveSessions/, "sessions");
});

test("Mobile csproj copies desktop UI assets and links Data", () => {
  const csproj = readFileSync(r("src/TerminalV.Mobile/TerminalV.Mobile.csproj"), "utf8");
  assert.match(csproj, /CopyDesktopUi/, "CopyDesktopUi target");
  assert.match(csproj, /TerminalV\\wwwroot\\assets/, "copies assets");
  assert.match(csproj, /AppSettings\.cs/, "links AppSettings");
  assert.match(csproj, /SessionRecord\.cs/, "links SessionRecord");
  assert.match(csproj, /PaneLayout\.cs/, "links PaneLayout");
});

test("Android immersive hides status and navigation bars", () => {
  const main = readFileSync(r("src/TerminalV.Mobile/Platforms/Android/MainActivity.cs"), "utf8");
  assert.match(main, /HideSystemBars/, "HideSystemBars");
  assert.match(main, /SetDecorFitsSystemWindows\(false\)/, "edge-to-edge");
  assert.match(main, /Hide\(.*StatusBars\(\)/, "hides status");
  assert.match(main, /NavigationBars\(\)/, "hides navigation");
  assert.match(main, /ShowTransientBarsBySwipe/, "transient swipe");
  assert.match(main, /OnWindowFocusChanged/, "re-hide on focus");
});

test("Connection overlay scrolls and FAB respects safe-area", () => {
  const home = readFileSync(r("src/TerminalV.Mobile/Components/Pages/Home.razor"), "utf8");
  assert.match(home, /overflow-y:auto/, "overlay scroll");
  assert.match(home, /-webkit-overflow-scrolling:touch/, "touch scroll");
  assert.match(home, /env\(safe-area-inset-/, "safe-area");
  assert.match(home, /font-size:16px/, "16px prevents iOS zoom");
  assert.match(home, /mobile-config-fab/, "FAB");
  assert.match(home, /bottom:calc\(.*safe-area-inset-bottom/, "FAB bottom safe-area");
  assert.match(home, /right:calc\(.*safe-area-inset-right/, "FAB right safe-area");
});

test("Mobile fixes: scale default, black bar, collapsed swipe, sync empty, write error", () => {
  const store = readFileSync(r("src/TerminalV.Mobile/Host/MobileDataStore.cs"), "utf8");
  assert.match(store, /FontSize = 12/, "mobile default FontSize 12");
  const main = readFileSync(r("src/TerminalV.Mobile/Platforms/Android/MainActivity.cs"), "utf8");
  assert.match(main, /SetStatusBarColor.*Transparent/, "status bar transparent");
  assert.match(main, /SetNavigationBarColor.*Transparent/, "nav bar transparent");
  assert.match(main, /LayoutInDisplayCutoutMode\.ShortEdges/, "cutout ShortEdges");
  const html = readFileSync(r("src/TerminalV.Mobile/wwwroot/index.html"), "utf8");
  assert.match(html, /sidebar-swipe-handle/, "swipe handle");
  assert.match(html, /@media.*max-width: 700px/, "collapsed drawer CSS");
  assert.match(html, /translateX\(-100%\)/, "hidden when collapsed");
  const bridge = readFileSync(r("src/TerminalV.Mobile/Host/MobileBridge.cs"), "utf8");
  assert.match(bridge, /Where\(s => liveSet\.Contains/, "filters dead empty sessions");
  assert.match(bridge, /PaneLayout\.Normalize/, "normalizes layouts");
  assert.match(bridge, /Ошибка подключения/, "posts data on connect error");
  assert.match(bridge, /WriteAsync/, "WriteAsync");
  assert.match(bridge, /ResizeAsync/, "ResizeAsync");
});

test("Export/import JSON replaces all via Share", () => {
  const html2 = readFileSync(r("src/TerminalV.Mobile/wwwroot/index.html"), "utf8");
  assert.match(html2, /id="export-json"/, "export button");
  assert.match(html2, /id="import-json"/, "import button");
  assert.match(html2, /id="export-status"/, "export status");
  const bridge2 = readFileSync(r("src/TerminalV.Mobile/Host/MobileBridge.cs"), "utf8");
  assert.match(bridge2, /HandleExportAsync/, "export handler");
  assert.match(bridge2, /HandleImportAsync/, "import handler");
  assert.match(bridge2, /ExportDto/, "ExportDto");
  assert.match(bridge2, /ExportConnection/, "ExportConnection");
  assert.match(bridge2, /Version = 1/, "version 1");
  assert.match(bridge2, /Share\.Default\.RequestAsync/, "Share sheet");
  assert.match(bridge2, /FilePicker\.Default\.PickAsync/, "FilePicker import");
  assert.match(bridge2, /case "export"/, "export case");
  assert.match(bridge2, /case "import"/, "import case");
  const shim = readFileSync(r("src/TerminalV.Mobile/wwwroot/js/mobile-bridge.js"), "utf8");
  assert.match(shim, /export-json/, "shim export");
  assert.match(shim, /import-json/, "shim import");
});
