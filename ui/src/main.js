import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { WebglAddon } from "@xterm/addon-webgl";
import "@xterm/xterm/css/xterm.css";
import "./styles.css";
import { THEMES, getTheme } from "./themes.js";

const tabsEl = document.getElementById("tabs");
const panesEl = document.getElementById("panes");
const emptyEl = document.getElementById("empty");
const newTabBtn = document.getElementById("new-tab");
const emptyNewBtn = document.getElementById("empty-new");
const updateBar = document.getElementById("update-bar");
const updateText = document.getElementById("update-text");
const updateApply = document.getElementById("update-apply");
const versionBtn = document.getElementById("app-version");
const appEl = document.getElementById("app");
const settingsEl = document.getElementById("settings");
const settingsBtn = document.getElementById("settings-btn");
const settingsClose = document.getElementById("settings-close");
const collapseBtn = document.getElementById("collapse-btn");
const expandBtn = document.getElementById("expand-btn");
const themeGrid = document.getElementById("theme-grid");
const fontFamilyEl = document.getElementById("font-family");
const fontSizeEl = document.getElementById("font-size");
const fontSizeValue = document.getElementById("font-size-value");
const zoomValue = document.getElementById("zoom-value");
const bgPick = document.getElementById("bg-pick");
const bgClear = document.getElementById("bg-clear");
const bgOpacityEl = document.getElementById("bg-opacity");
const bgOpacityValue = document.getElementById("bg-opacity-value");

const tabs = [];
let activeId = null;
let shellName = "PowerShell";
let buildNumber = 22621;
let nextIndex = 1;
let appVersion = "0.3.0";
let updateSupported = false;
let persistTimer = 0;
let readyForPersist = false;
const clipboardWaiters = new Map();

const settings = {
  themeId: "midnight",
  fontFamily: "Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace",
  fontSize: 14,
  zoom: 0,
  sidebarCollapsed: false,
  backgroundPath: null,
  backgroundOpacity: 0.25
};

function host() {
  return window.chrome?.webview ?? null;
}

function post(message) {
  host()?.postMessage(message);
}

function uuid() {
  return crypto.randomUUID();
}

function currentTab() {
  return tabs.find((tab) => tab.id === activeId) ?? null;
}

function effectiveFontSize() {
  return Math.min(36, Math.max(8, settings.fontSize + settings.zoom));
}

function applyChrome() {
  const theme = getTheme(settings.themeId);
  const root = document.documentElement;
  const chrome = theme.chrome;
  root.style.setProperty("--bg", chrome.bg);
  root.style.setProperty("--sidebar", chrome.sidebar);
  root.style.setProperty("--sidebar-edge", chrome.sidebarEdge);
  root.style.setProperty("--tab-hover", chrome.tabHover);
  root.style.setProperty("--tab-active", chrome.tabActive);
  root.style.setProperty("--accent", chrome.accent);
  root.style.setProperty("--accent-dim", chrome.accentDim);
  root.style.setProperty("--text", chrome.text);
  root.style.setProperty("--muted", chrome.muted);
  root.style.setProperty("--danger", chrome.danger);
  root.style.setProperty("--ink", chrome.ink);
  root.style.setProperty("--scrollbar", chrome.scrollbar);
  root.style.setProperty("--scrollbar-hover", chrome.scrollbarHover);
  root.style.setProperty("--overlay", chrome.overlay);
  root.style.setProperty("--font-mono", settings.fontFamily);
  appEl.classList.toggle("collapsed", settings.sidebarCollapsed);
  document.body.style.background = chrome.bg;
}

function termTheme() {
  const theme = { ...getTheme(settings.themeId).term };
  if (settings.backgroundPath) {
    theme.background = "#00000000";
  }
  return theme;
}

function applyBackground(pane) {
  let bg = pane.querySelector(".pane-bg");
  if (!bg) {
    bg = document.createElement("div");
    bg.className = "pane-bg";
    pane.prepend(bg);
  }
  if (settings.backgroundPath) {
    bg.style.backgroundImage = `url("${settings.backgroundPath}")`;
    bg.style.opacity = String(settings.backgroundOpacity);
    bg.style.display = "block";
  } else {
    bg.style.backgroundImage = "none";
    bg.style.display = "none";
  }
}

function applyToTerminals() {
  const theme = termTheme();
  const size = effectiveFontSize();
  for (const tab of tabs) {
    tab.term.options.theme = theme;
    tab.term.options.fontFamily = settings.fontFamily;
    tab.term.options.fontSize = size;
    tab.term.options.allowTransparency = Boolean(settings.backgroundPath);
    applyBackground(tab.pane);
    if (tab.id === activeId) {
      tab.fit.fit();
      post({ type: "resize", id: tab.id, cols: tab.term.cols, rows: tab.term.rows });
    }
  }
  zoomValue.textContent = `${Math.round((size / settings.fontSize) * 100)}%`;
}

function persistSettings() {
  post({ type: "persist-settings", data: JSON.stringify(settings) });
}

function persistSessions() {
  if (!readyForPersist) {
    return;
  }

  const payload = tabs.map((tab, index) => ({
    id: tab.id,
    title: tab.title,
    customTitle: tab.customTitle ?? null,
    sortOrder: index,
    active: tab.id === activeId
  }));
  post({ type: "persist-sessions", sessions: payload });
}

function schedulePersist() {
  window.clearTimeout(persistTimer);
  persistTimer = window.setTimeout(persistSessions, 80);
}

window.terminalvFlush = () => {
  window.clearTimeout(persistTimer);
  readyForPersist = true;
  persistSessions();
  persistSettings();
};

function renderTabs() {
  tabsEl.replaceChildren();
  for (const tab of tabs) {
    const row = document.createElement("div");
    row.className = `tab${tab.id === activeId ? " active" : ""}${tab.unread ? " unread" : ""}`;
    row.dataset.id = tab.id;
    row.setAttribute("role", "tab");
    row.setAttribute("aria-selected", String(tab.id === activeId));
    row.title = tab.customTitle || tab.title;

    const accent = document.createElement("span");
    accent.className = "tab-accent";

    const body = document.createElement("div");
    body.className = "tab-body";

    if (tab.renaming) {
      const input = document.createElement("input");
      input.className = "tab-rename";
      input.value = tab.customTitle || tab.title;
      input.addEventListener("click", (event) => event.stopPropagation());
      input.addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
          finishRename(tab, input.value);
        } else if (event.key === "Escape") {
          tab.renaming = false;
          renderTabs();
        }
      });
      input.addEventListener("blur", () => finishRename(tab, input.value));
      body.append(input);
      queueMicrotask(() => {
        input.focus();
        input.select();
      });
    } else {
      const title = document.createElement("div");
      title.className = "tab-title";
      title.textContent = tab.customTitle || tab.title;
      body.append(title);
    }

    const meta = document.createElement("div");
    meta.className = "tab-meta";
    meta.textContent = tab.exited ? "завершена" : shellName;
    body.append(meta);

    const close = document.createElement("button");
    close.className = "tab-close";
    close.type = "button";
    close.title = "Закрыть";
    close.textContent = "×";
    close.addEventListener("click", (event) => {
      event.stopPropagation();
      closeTab(tab.id);
    });

    row.append(accent, body, close);
    row.addEventListener("click", () => activate(tab.id));
    row.addEventListener("dblclick", (event) => {
      event.preventDefault();
      tab.renaming = true;
      renderTabs();
    });
    row.addEventListener("auxclick", (event) => {
      if (event.button === 1) {
        event.preventDefault();
        closeTab(tab.id);
      }
    });
    tabsEl.append(row);
  }

  emptyEl.classList.toggle("hidden", tabs.length > 0);
}

function finishRename(tab, value) {
  const next = value.trim();
  tab.customTitle = next || undefined;
  tab.renaming = false;
  renderTabs();
  schedulePersist();
}

function activate(id) {
  const tab = tabs.find((item) => item.id === id);
  if (!tab) {
    return;
  }

  activeId = id;
  tab.unread = false;
  for (const item of tabs) {
    item.pane.classList.toggle("active", item.id === id);
  }
  renderTabs();
  tab.term.focus();
  requestAnimationFrame(() => {
    tab.fit.fit();
    post({ type: "resize", id: tab.id, cols: tab.term.cols, rows: tab.term.rows });
  });
  schedulePersist();
}

function closeTab(id) {
  const index = tabs.findIndex((tab) => tab.id === id);
  if (index < 0) {
    return;
  }

  const [tab] = tabs.splice(index, 1);
  post({ type: "kill", id });
  tab.term.dispose();
  tab.pane.remove();

  if (activeId === id) {
    const next = tabs[index] || tabs[index - 1] || null;
    activeId = next?.id ?? null;
    if (next) {
      activate(next.id);
      return;
    }
  }

  renderTabs();
  schedulePersist();
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    post({ type: "clipboard-write", data: text });
  }
}

function readClipboard() {
  return navigator.clipboard.readText().catch(
    () =>
      new Promise((resolve) => {
        const requestId = uuid();
        clipboardWaiters.set(requestId, resolve);
        post({ type: "clipboard-read", requestId });
        setTimeout(() => {
          if (clipboardWaiters.has(requestId)) {
            clipboardWaiters.delete(requestId);
            resolve("");
          }
        }, 1000);
      })
  );
}

function isZoomEvent(event) {
  if (!(event.ctrlKey || event.metaKey) || event.altKey) {
    return false;
  }
  return (
    event.key === "+" ||
    event.key === "=" ||
    event.key === "-" ||
    event.key === "_" ||
    event.key === "0" ||
    event.code === "NumpadAdd" ||
    event.code === "NumpadSubtract" ||
    event.code === "Numpad0"
  );
}

function applyZoomDelta(delta) {
  if (delta === 0) {
    settings.zoom = 0;
  } else {
    settings.zoom = Math.min(16, Math.max(-8, settings.zoom + delta));
  }
  applyToTerminals();
  persistSettings();
}

function attachCopyPaste(tab) {
  tab.term.attachCustomKeyEventHandler((event) => {
    if (event.type !== "keydown") {
      return true;
    }

    if (isZoomEvent(event)) {
      return false;
    }

    const key = event.key.toLowerCase();
    if ((event.ctrlKey || event.metaKey) && !event.altKey) {
      if (key === "c" && tab.term.hasSelection()) {
        copyText(tab.term.getSelection());
        return false;
      }
      if (key === "v" && !event.shiftKey) {
        readClipboard().then((text) => {
          if (text) {
            post({ type: "write", id: tab.id, data: text });
          }
        });
        return false;
      }
      if (event.shiftKey && key === "c") {
        copyText(tab.term.getSelection());
        return false;
      }
      if (event.shiftKey && key === "v") {
        readClipboard().then((text) => {
          if (text) {
            post({ type: "write", id: tab.id, data: text });
          }
        });
        return false;
      }
    }
    return true;
  });

  tab.host.addEventListener("contextmenu", (event) => {
    event.preventDefault();
    if (tab.term.hasSelection()) {
      copyText(tab.term.getSelection());
      tab.term.clearSelection();
      return;
    }
    readClipboard().then((text) => {
      if (text) {
        post({ type: "write", id: tab.id, data: text });
      }
    });
  });
}

function newTab(options = {}) {
  const id = options.id || uuid();
  const index = options.title ? nextIndex : nextIndex++;
  const pane = document.createElement("div");
  pane.className = "pane";
  pane.dataset.id = id;

  const hostEl = document.createElement("div");
  hostEl.className = "terminal-host";

  const overlay = document.createElement("div");
  overlay.className = "overlay";
  const overlayText = document.createElement("span");
  const overlayBtn = document.createElement("button");
  overlayBtn.type = "button";
  overlayBtn.textContent = "Перезапустить";
  overlay.append(overlayText, overlayBtn);
  pane.append(hostEl, overlay);
  panesEl.append(pane);
  applyBackground(pane);

  const term = new Terminal({
    fontFamily: settings.fontFamily,
    fontSize: effectiveFontSize(),
    lineHeight: 1.2,
    cursorBlink: true,
    cursorStyle: "bar",
    cursorWidth: 2,
    scrollback: 8000,
    allowProposedApi: true,
    allowTransparency: Boolean(settings.backgroundPath),
    theme: termTheme(),
    windowsPty: { backend: "conpty", buildNumber }
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon());
  term.open(hostEl);
  try {
    term.loadAddon(new WebglAddon());
  } catch {
    // canvas renderer is fine
  }

  const tab = {
    id,
    title: options.title || `Сессия ${index}`,
    customTitle: options.customTitle || undefined,
    renaming: false,
    unread: false,
    exited: false,
    pane,
    host: hostEl,
    overlay,
    overlayText,
    overlayBtn,
    term,
    fit
  };

  overlayBtn.addEventListener("click", () => restart(tab));
  term.onData((data) => post({ type: "write", id, data }));
  term.onTitleChange((title) => {
    const cleaned = title?.trim();
    if (!cleaned || tab.customTitle) {
      return;
    }
    tab.title = cleaned;
    renderTabs();
    schedulePersist();
  });
  attachCopyPaste(tab);

  const observer = new ResizeObserver(() => {
    if (activeId !== id) {
      return;
    }
    fit.fit();
    post({ type: "resize", id, cols: term.cols, rows: term.rows });
  });
  observer.observe(hostEl);

  tabs.push(tab);
  if (!options.skipActivate) {
    fit.fit();
    post({ type: "create", id, cols: term.cols, rows: term.rows });
    activate(id);
  } else {
    post({ type: "create", id, cols: 80, rows: 24 });
  }
}

function restart(tab) {
  tab.exited = false;
  tab.overlay.classList.remove("visible");
  tab.term.reset();
  tab.fit.fit();
  post({ type: "create", id: tab.id, cols: tab.term.cols, rows: tab.term.rows });
  tab.term.focus();
  renderTabs();
}

function setUpdateBar(visible, text, options = {}) {
  updateBar.classList.toggle("hidden", !visible);
  updateBar.classList.toggle("error", Boolean(options.error));
  updateText.textContent = text || "";
  updateApply.classList.toggle("hidden", Boolean(options.hideButton));
  updateApply.disabled = Boolean(options.disabled);
  if (options.buttonLabel) {
    updateApply.textContent = options.buttonLabel;
  }
}

function handleUpdate(message) {
  if (message.status === "available") {
    setUpdateBar(true, `Доступно ${message.latest}`, { buttonLabel: "Обновить" });
    return;
  }
  if (message.status === "downloading") {
    setUpdateBar(true, "Скачивание обновления…", { hideButton: true });
    return;
  }
  if (message.status === "restarting") {
    setUpdateBar(true, `Установлено ${message.latest}. Перезапуск…`, { hideButton: true });
    return;
  }
  if (message.status === "current") {
    setUpdateBar(true, `Установлена актуальная версия ${message.current}`, {
      hideButton: true
    });
    setTimeout(() => updateBar.classList.add("hidden"), 2500);
    return;
  }
  if (message.status === "unsupported" || message.status === "error") {
    setUpdateBar(true, message.message || "Не удалось проверить обновление", {
      error: true,
      hideButton: true
    });
  }
}

function fillFonts(list) {
  const preferred = [
    "Cascadia Code",
    "Cascadia Mono",
    "Consolas",
    "Courier New",
    "JetBrains Mono",
    "Fira Code",
    "Source Code Pro",
    "Segoe UI"
  ];
  const names = [...new Set([...preferred, ...(list || [])])];
  fontFamilyEl.replaceChildren();
  for (const name of names) {
    const option = document.createElement("option");
    option.value = name;
    option.textContent = name;
    fontFamilyEl.append(option);
  }
  const current = settings.fontFamily.split(",")[0].trim().replaceAll('"', "");
  fontFamilyEl.value = names.includes(current) ? current : names[0];
}

function renderThemeGrid() {
  themeGrid.replaceChildren();
  for (const theme of THEMES) {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `theme-card${theme.id === settings.themeId ? " active" : ""}`;
    const swatch = document.createElement("span");
    swatch.className = "theme-swatch";
    swatch.style.background = theme.swatch;
    const label = document.createElement("span");
    label.textContent = theme.name;
    button.append(swatch, label);
    button.addEventListener("click", () => {
      settings.themeId = theme.id;
      applyChrome();
      applyToTerminals();
      persistSettings();
      renderThemeGrid();
    });
    themeGrid.append(button);
  }
}

function syncSettingsForm() {
  fontSizeEl.value = String(settings.fontSize);
  fontSizeValue.textContent = String(settings.fontSize);
  bgOpacityEl.value = String(Math.round(settings.backgroundOpacity * 100));
  bgOpacityValue.textContent = `${bgOpacityEl.value}%`;
  const current = settings.fontFamily.split(",")[0].trim().replaceAll('"', "");
  if ([...fontFamilyEl.options].some((option) => option.value === current)) {
    fontFamilyEl.value = current;
  }
  renderThemeGrid();
  applyChrome();
  applyToTerminals();
}

function openSettings() {
  settingsEl.classList.remove("hidden");
}

function closeSettings() {
  settingsEl.classList.add("hidden");
}

function toggleSidebar() {
  settings.sidebarCollapsed = !settings.sidebarCollapsed;
  applyChrome();
  persistSettings();
  const tab = currentTab();
  if (tab) {
    requestAnimationFrame(() => {
      tab.fit.fit();
      post({ type: "resize", id: tab.id, cols: tab.term.cols, rows: tab.term.rows });
    });
  }
}

function restoreSessions(records) {
  if (!records?.length) {
    readyForPersist = true;
    newTab();
    persistSessions();
    return;
  }

  let active = null;
  for (const record of records) {
    newTab({
      id: record.id,
      title: record.title,
      customTitle: record.customTitle,
      skipActivate: true
    });
    if (record.active) {
      active = record.id;
    }
  }
  nextIndex = tabs.length + 1;
  readyForPersist = true;
  activate(active || tabs[0].id);
  persistSessions();
}

function handleHost(message) {
  if (!message || typeof message !== "object") {
    return;
  }

  if (message.type === "init") {
    if (message.shellName) {
      shellName = message.shellName;
    }
    if (message.buildNumber) {
      buildNumber = message.buildNumber;
    }
    if (message.version) {
      appVersion = message.version;
      versionBtn.textContent = `v${appVersion}`;
    }
    updateSupported = Boolean(message.updateSupported);
    versionBtn.title = updateSupported
      ? "Проверить обновления"
      : "Самообновление работает в установленной копии";
    if (message.settings) {
      Object.assign(settings, message.settings);
    }
    fillFonts(message.fonts);
    syncSettingsForm();
    if (tabs.length === 0) {
      restoreSessions(message.sessions);
    } else {
      renderTabs();
    }
    return;
  }

  if (message.type === "update") {
    handleUpdate(message);
    return;
  }

  if (message.type === "background-picked") {
    settings.backgroundPath = message.path;
    applyToTerminals();
    persistSettings();
    return;
  }

  if (message.type === "clipboard-data") {
    const waiter = clipboardWaiters.get(message.requestId);
    if (waiter) {
      clipboardWaiters.delete(message.requestId);
      waiter(message.data ?? "");
    }
    return;
  }

  const tab = tabs.find((item) => item.id === message.id);
  if (!tab) {
    return;
  }

  if (message.type === "data") {
    tab.term.write(message.data ?? "");
    if (tab.id !== activeId) {
      tab.unread = true;
      renderTabs();
    }
    return;
  }

  if (message.type === "exit") {
    tab.exited = true;
    tab.overlayText.textContent = `Процесс завершился с кодом ${message.code ?? 0}`;
    tab.overlay.classList.add("visible");
    renderTabs();
    return;
  }

  if (message.type === "error") {
    tab.exited = true;
    tab.overlayText.textContent = message.message || "Не удалось запустить сессию";
    tab.overlay.classList.add("visible");
    renderTabs();
  }
}

newTabBtn.addEventListener("click", () => newTab());
emptyNewBtn.addEventListener("click", () => newTab());
updateApply.addEventListener("click", () => post({ type: "update-apply" }));
versionBtn.addEventListener("click", () => post({ type: "update-check" }));
settingsBtn.addEventListener("click", openSettings);
settingsClose.addEventListener("click", closeSettings);
settingsEl.addEventListener("click", (event) => {
  if (event.target === settingsEl) {
    closeSettings();
  }
});
collapseBtn.addEventListener("click", toggleSidebar);
expandBtn.addEventListener("click", toggleSidebar);
bgPick.addEventListener("click", () => post({ type: "pick-background" }));
bgClear.addEventListener("click", () => {
  settings.backgroundPath = null;
  applyToTerminals();
  persistSettings();
});
fontFamilyEl.addEventListener("change", () => {
  settings.fontFamily = `${fontFamilyEl.value}, Consolas, monospace`;
  applyToTerminals();
  persistSettings();
});
fontSizeEl.addEventListener("input", () => {
  settings.fontSize = Number(fontSizeEl.value);
  fontSizeValue.textContent = String(settings.fontSize);
  applyToTerminals();
});
fontSizeEl.addEventListener("change", persistSettings);
bgOpacityEl.addEventListener("input", () => {
  settings.backgroundOpacity = Number(bgOpacityEl.value) / 100;
  bgOpacityValue.textContent = `${bgOpacityEl.value}%`;
  applyToTerminals();
});
bgOpacityEl.addEventListener("change", persistSettings);

window.addEventListener(
  "keydown",
  (event) => {
    if (event.key === "Escape" && !settingsEl.classList.contains("hidden")) {
      event.preventDefault();
      closeSettings();
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key === ",") {
      event.preventDefault();
      if (settingsEl.classList.contains("hidden")) {
        openSettings();
      } else {
        closeSettings();
      }
      return;
    }
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "b" && !event.shiftKey) {
      event.preventDefault();
      toggleSidebar();
      return;
    }
    if (isZoomEvent(event)) {
      event.preventDefault();
      if (event.key === "0" || event.code === "Numpad0") {
        applyZoomDelta(0);
      } else if (event.key === "-" || event.key === "_" || event.code === "NumpadSubtract") {
        applyZoomDelta(-1);
      } else {
        applyZoomDelta(1);
      }
      return;
    }
    if (event.ctrlKey && event.shiftKey && event.code === "KeyT") {
      event.preventDefault();
      newTab();
      return;
    }
    if (event.ctrlKey && event.shiftKey && event.code === "KeyW") {
      event.preventDefault();
      if (activeId) {
        closeTab(activeId);
      }
      return;
    }
    if (event.ctrlKey && event.key === "Tab") {
      event.preventDefault();
      if (tabs.length < 2) {
        return;
      }
      const index = tabs.findIndex((tab) => tab.id === activeId);
      const next = event.shiftKey
        ? tabs[(index - 1 + tabs.length) % tabs.length]
        : tabs[(index + 1) % tabs.length];
      activate(next.id);
      return;
    }
    if (event.altKey && event.key >= "1" && event.key <= "9") {
      event.preventDefault();
      const tab = tabs[Number(event.key) - 1];
      if (tab) {
        activate(tab.id);
      }
    }
  },
  true
);

window.addEventListener(
  "wheel",
  (event) => {
    if (!(event.ctrlKey || event.metaKey)) {
      return;
    }
    event.preventDefault();
    applyZoomDelta(event.deltaY < 0 ? 1 : -1);
  },
  { passive: false }
);

const webview = host();
if (webview) {
  webview.addEventListener("message", (event) => handleHost(event.data));
  post({ type: "ready" });
} else {
  emptyEl.querySelector(".empty-sub").textContent =
    "Откройте приложение TerminalV, а не этот файл в браузере.";
}
