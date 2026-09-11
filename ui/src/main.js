import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { WebglAddon } from "@xterm/addon-webgl";
import "@xterm/xterm/css/xterm.css";
import "./styles.css";

const tabsEl = document.getElementById("tabs");
const panesEl = document.getElementById("panes");
const emptyEl = document.getElementById("empty");
const newTabBtn = document.getElementById("new-tab");
const emptyNewBtn = document.getElementById("empty-new");
const updateBar = document.getElementById("update-bar");
const updateText = document.getElementById("update-text");
const updateApply = document.getElementById("update-apply");
const versionBtn = document.getElementById("app-version");

const tabs = [];
let activeId = null;
let shellName = "PowerShell";
let buildNumber = 22621;
let nextIndex = 1;
let appVersion = "0.2.0";
let updateSupported = false;
const clipboardWaiters = new Map();

const theme = {
  background: "#0b0d10",
  foreground: "#d6deeb",
  cursor: "#3d9eff",
  cursorAccent: "#0b0d10",
  selectionBackground: "#264f78",
  black: "#1e1e1e",
  red: "#f44747",
  green: "#6a9955",
  yellow: "#dcdcaa",
  blue: "#569cd6",
  magenta: "#c586c0",
  cyan: "#4ec9b0",
  white: "#d4d4d4",
  brightBlack: "#808080",
  brightRed: "#f14c4c",
  brightGreen: "#b5cea8",
  brightYellow: "#dcdcaa",
  brightBlue: "#9cdcfe",
  brightMagenta: "#c586c0",
  brightCyan: "#4ec9b0",
  brightWhite: "#ffffff"
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

function renderTabs() {
  tabsEl.replaceChildren();
  for (const tab of tabs) {
    const row = document.createElement("div");
    row.className = `tab${tab.id === activeId ? " active" : ""}${tab.unread ? " unread" : ""}`;
    row.dataset.id = tab.id;
    row.setAttribute("role", "tab");
    row.setAttribute("aria-selected", String(tab.id === activeId));

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

function attachCopyPaste(tab) {
  tab.term.attachCustomKeyEventHandler((event) => {
    if (event.type !== "keydown") {
      return true;
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

function newTab() {
  const id = uuid();
  const index = nextIndex++;
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

  const term = new Terminal({
    fontFamily: '"Cascadia Code", "Cascadia Mono", Consolas, "Courier New", monospace',
    fontSize: 14,
    lineHeight: 1.2,
    cursorBlink: true,
    cursorStyle: "bar",
    cursorWidth: 2,
    scrollback: 8000,
    allowProposedApi: true,
    theme,
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
    title: `Сессия ${index}`,
    customTitle: undefined,
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
  fit.fit();
  post({ type: "create", id, cols: term.cols, rows: term.rows });
  activate(id);
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
    if (tabs.length === 0) {
      newTab();
    } else {
      renderTabs();
    }
    return;
  }

  if (message.type === "update") {
    handleUpdate(message);
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

newTabBtn.addEventListener("click", newTab);
emptyNewBtn.addEventListener("click", newTab);
updateApply.addEventListener("click", () => post({ type: "update-apply" }));
versionBtn.addEventListener("click", () => post({ type: "update-check" }));

window.addEventListener(
  "keydown",
  (event) => {
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

const webview = host();
if (webview) {
  webview.addEventListener("message", (event) => handleHost(event.data));
} else {
  emptyEl.querySelector(".empty-sub").textContent =
    "Откройте приложение TerminalV, а не этот файл в браузере.";
}
