import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { WebglAddon } from "@xterm/addon-webgl";
import { SerializeAddon } from "@xterm/addon-serialize";
import { SearchAddon } from "@xterm/addon-search";
import "@xterm/xterm/css/xterm.css";
import "./styles.css";
import "./chrome.css";
import { icon, mountIcons } from "./icons.js";
import { THEMES, getTheme } from "./themes.js";
import { getPlainSelection } from "./selection.js";
import { createPasteController } from "./paste-confirmation.js";
import { createCloseConfirmation } from "./close-confirmation.js";
import { createTerminalSearch, isSearchShortcut, SEARCH_HIGHLIGHT_LIMIT } from "./terminal-search.js";
import { createSessionOptions, sessionMetadata, sessionGroups, SESSION_COLORS, insertAfter } from "./session-management.js";
import { createNotifications, createNotificationOutput } from "./notifications.js";
import { createLaunchProfiles, PROFILE_SHELLS } from "./launch-profiles.js";
import { createLaunchMenu } from "./launch-menu.js";
import { normalizeLayouts, layoutFor, leafIds, splitSession, detachSession, layoutGeometry,
  neighborPane, paneShortcut, MAX_PANES, MIN_PANE_WIDTH, MIN_PANE_HEIGHT, isSplitChild, isSplitParent } from "./pane-layout.js";
import { createPaneView } from "./pane-view.js";
import { createShortcuts } from "./shortcuts.js";
import { WRITE_CHUNK, chunkText } from "./write-chunk.js";

mountIcons(document);

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
const searchBtn = document.getElementById("search-btn");
const settingsClose = document.getElementById("settings-close");
const collapseBtn = document.getElementById("collapse-btn");
const themeGrid = document.getElementById("theme-grid");
const fontFamilyEl = document.getElementById("font-family");
const fontSizeEl = document.getElementById("font-size");
const fontSizeValue = document.getElementById("font-size-value");
const zoomValue = document.getElementById("zoom-value");
const bgPick = document.getElementById("bg-pick");
const bgClear = document.getElementById("bg-clear");
const bgOpacityEl = document.getElementById("bg-opacity");
const bgOpacityValue = document.getElementById("bg-opacity-value");
const shortcuts = createShortcuts({ root: document.getElementById("shortcut-settings"), post });
const sessionFilter = document.getElementById("session-filter");
const hiddenSessionsBtn = document.getElementById("hidden-sessions");
const notificationStatus = document.getElementById("notification-status");
let showHiddenSessions = false;

const tabs = [];
let activeId = null;
let layouts = [];
let shellName = "PowerShell";
let buildNumber = 22621;
let nextIndex = 1;
let appVersion = "0.5.5";
let updateSupported = false;
let persistTimer = 0;
let fitTimer = 0;
let fitRaf = 0;
let ignoreFitUntil = 0;
let readyForPersist = false;
const clipboardWaiters = new Map();
let draggedTabId = null;

const settings = {
  themeId: "midnight",
  fontFamily: "Cascadia Code, Cascadia Mono, Consolas, Courier New, monospace",
  fontSize: 14,
  zoom: 0,
  sidebarCollapsed: false,
  backgroundPath: null,
  backgroundOpacity: 0.25
};

const pasteController = createPasteController({
  dialog: document.getElementById("paste-confirmation"),
  readClipboard,
  canPaste: (tab) => tabs.includes(tab) && tab.id === activeId && !tab.exited && !closeController.isOpen && !sessionOptions.isOpen && !launchProfiles.isOpen && !launchMenu.isOpen,
  restoreFocus: () => currentTab()?.term.focus()
});

const searchController = createTerminalSearch({
  panel: document.getElementById("terminal-search"),
  onVisibilityChange: (tab) => scheduleFit(tab, true)
});

const closeController = createCloseConfirmation({
  dialog: document.getElementById("close-confirmation"),
  canClose: (tab) => tabs.includes(tab),
  onConfirm: removeTab,
  // A clipboard request started just before closing must not open a second dialog.
  onShow: () => pasteController.cancel(currentTab()),
  restoreFocus: (previous) => {
    if (searchController.isOpen && previous?.isConnected && searchController.contains(previous)) {
      previous.focus({ preventScroll: true });
    } else {
      currentTab()?.term.focus();
    }
  }
});

const notifications = createNotifications({
  isKnown: (tab) => tabs.includes(tab),
  isActive: (tab) => tab.id === activeId && !tab.hidden,
  sound: (tab) => post({ type: "bell", id: tab.id }),
  changed: (tab) => {
    patchTabRow(tab);
    updateSessionSwitcher();
    notificationStatus.textContent = tab.attention
      ? `Сигнал от сессии «${(tab.customTitle || tab.title).slice(0, 160)}».` : "";
  }
});

const sessionOptions = createSessionOptions({
  dialog: document.getElementById("session-options"),
  getSessions: () => tabs,
  onSave: (tab, values, action) => {
    Object.assign(tab, values);
    if (action !== "hide") { renderTabs(); persistSessions(); }
  },
  onHide: hideTab,
  restoreFocus: () => {
    const tab = currentTab();
    if (tab) tab.term.focus();
    else (tabsEl.querySelector(".tab") || emptyNewBtn).focus();
  }
});

const launchProfiles = createLaunchProfiles({
  dialog: document.getElementById("launch-profiles"), post,
  onLaunch: (options) => newTab(options),
  onChanged: () => launchMenu.refreshProfiles(),
  restoreFocus: () => {
    const tab = currentTab();
    if (tab) tab.term.focus();
    else document.getElementById("launch-profiles-btn").focus();
  }
});

const launchMenu = createLaunchMenu({
  dialog: document.getElementById("launch-menu"), trigger: document.getElementById("launch-profiles-btn"), post,
  getProfiles: () => launchProfiles.profiles,
  onLaunch: options => newTab(options), onEdit: id => launchProfiles.open(id)
});

function isModalOpen() {
  return pasteController.isOpen || closeController.isOpen || sessionOptions.isOpen || launchProfiles.isOpen || launchMenu.isOpen;
}

const paneToolbar = document.getElementById("pane-toolbar");
const splitRightBtn = document.getElementById("split-right");
const splitDownBtn = document.getElementById("split-down");
const detachPaneBtn = document.getElementById("detach-pane");
const paneView = createPaneView({
  container: panesEl, getRoot: () => layoutFor(layouts, activeId), getTabs: () => tabs,
  canResize: () => !isModalOpen() && settingsEl.classList.contains("hidden"),
  onResize: () => { scheduleFit(); schedulePersist(); updatePaneToolbar(); }
});

function visibleTabs() {
  const ids = leafIds(layoutFor(layouts, activeId));
  return ids.map((id) => tabs.find((tab) => tab.id === id)).filter(Boolean);
}

function isPaneVisible(tab) {
  return Boolean(tab && !tab.hidden && leafIds(layoutFor(layouts, activeId)).includes(tab.id));
}

function canSplit(axis) {
  const tab = currentTab(), count = visibleTabs().length;
  if (!tab || !count || count >= MAX_PANES) return false;
  const box = tab.pane.getBoundingClientRect();
  return axis === "columns" ? box.width >= MIN_PANE_WIDTH * 2 + 6 : box.height >= MIN_PANE_HEIGHT * 2 + 6;
}

function updatePaneToolbar() {
  const count = visibleTabs().length;
  paneToolbar.hidden = !count;
  document.getElementById("pane-count").textContent = `Панели · ${count}`;
  updateWorkspaceContext();
  splitRightBtn.disabled = !canSplit("columns");
  splitDownBtn.disabled = !canSplit("rows");
  detachPaneBtn.disabled = count < 2;
  splitRightBtn.title = splitRightBtn.disabled ? `Недостаточно места или достигнут предел ${MAX_PANES} панелей` : "Разделить справа (Alt+Shift+=)";
  splitDownBtn.title = splitDownBtn.disabled ? `Недостаточно места или достигнут предел ${MAX_PANES} панелей` : "Разделить снизу (Alt+Shift+-)";
}

function updateWorkspaceContext() {
  const tab = currentTab();
  const title = document.getElementById("workspace-title");
  const path = document.getElementById("workspace-path");
  title.textContent = tab ? tab.customTitle || tab.title : "Терминал";
  title.title = title.textContent;
  path.textContent = tab?.cwd || (tab ? sessionMetaText(tab) : "");
  path.title = path.textContent;
  paneToolbar.classList.toggle("session-exited", Boolean(tab?.exited));
}

function renderPaneLayout() {
  paneView.render();
  for (const tab of tabs) {
    const visible = isPaneVisible(tab);
    tab.pane.classList.toggle("active", tab.id === activeId && visible);
    if (visible) ensureWebgl(tab); else dropWebgl(tab);
    patchTabRow(tab);
  }
  updatePaneToolbar();
  scheduleFit();
}

function splitPane(axis) {
  if (isModalOpen() || !settingsEl.classList.contains("hidden") || !canSplit(axis)) return;
  searchController.close({ focus: false });
  pasteController.cancel(currentTab());
  newTab({ splitFrom: activeId, splitAxis: axis });
}

splitRightBtn.addEventListener("click", () => splitPane("columns"));
splitDownBtn.addEventListener("click", () => splitPane("rows"));
detachPaneBtn.addEventListener("click", () => {
  if (isModalOpen() || visibleTabs().length < 2) return;
  layouts = detachSession(layouts, activeId);
  renderPaneLayout();
  persistSessions();
  currentTab()?.term.focus();
});
new ResizeObserver(() => { updatePaneToolbar(); scheduleFit(); }).observe(panesEl);

function openTabs() {
  return sessionGroups(tabs).flatMap((group) => group.tabs);
}

function showSessionOptions(tab) {
  if (isModalOpen() || !settingsEl.classList.contains("hidden")) return;
  pasteController.cancel(currentTab());
  searchController.close({ focus: false });
  sessionOptions.open(tab);
}

function revealTab(tab) {
  if (isModalOpen()) return;
  tab.hidden = false;
  layouts = normalizeLayouts(layouts, tabs);
  showHiddenSessions = false;
  sessionFilter.value = "";
  renderTabs();
  activate(tab.id);
  // A hidden terminal was not fitted to the window. Ask a live TUI to redraw
  // only once it is visible, including when the target size happens to match.
  window.setTimeout(() => { if (activeId === tab.id) pulseResize(tab); }, 100);
  persistSessions();
}

function hideTab(tab) {
  if (!tabs.includes(tab) || tab.hidden) return;
  const ordered = openTabs();
  const index = ordered.indexOf(tab);
  const neighbor = visibleTabs().find((item) => item !== tab);
  closeController.cancel(tab);
  pasteController.cancel(tab);
  tab.hidden = true;
  layouts = normalizeLayouts(layouts, tabs);
  tab.renaming = false;
  dropWebgl(tab);
  if (tab.id === activeId) {
    searchController.close({ focus: false });
    tab.pane.classList.remove("active");
    activeId = null;
    const next = neighbor || ordered[index + 1] || ordered[index - 1];
    if (next) activate(next.id);
  }
  if (!openTabs().length) showHiddenSessions = true;
  renderCwdNotice();
  renderTabs();
  renderPaneLayout();
  // Persist all sessions, including hidden ones. No kill, create or PTY input.
  persistSessions();
}

sessionFilter.addEventListener("input", renderTabs);
sessionFilter.addEventListener("keydown", (event) => {
  event.stopPropagation();
  if (event.isComposing) return;
  if (event.key === "Escape") {
    event.preventDefault();
    sessionFilter.value = "";
    renderTabs();
    currentTab()?.term.focus();
  } else if (event.key === "Enter") {
    event.preventDefault();
    const first = sessionGroups(tabs, { hidden: showHiddenSessions, query: sessionFilter.value })[0]?.tabs[0];
    if (first) revealTab(first);
  }
});
hiddenSessionsBtn.addEventListener("click", () => {
  if (isModalOpen()) return;
  showHiddenSessions = !showHiddenSessions;
  renderTabs();
});

function host() {
  return window.chrome?.webview ?? null;
}

function post(message) {
  host()?.postMessage(message);
}
function diag(area, msg, id = null) {
  try { post({ type: "diag", data: `${area}: ${msg}`, id }); } catch {}
  try { console.debug(`[diag] ${area}: ${msg}`); } catch {}
}

function openExternal(uri) {
  if (typeof uri !== "string") return;
  uri = uri.trim();
  if (!uri) return;
  try {
    const url = new URL(uri);
    if (url.protocol !== "http:" && url.protocol !== "https:") return;
    post({ type: "open-link", uri });
  } catch {
    // ignore invalid URLs
  }
}

function handleLink(event, uri) {
  try { event?.preventDefault?.(); } catch {}
  openExternal(uri);
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
  root.style.colorScheme = theme.kind;
  root.dataset.themeKind = theme.kind;
  appEl.classList.toggle("collapsed", settings.sidebarCollapsed);
  collapseBtn.setAttribute("aria-expanded", String(!settings.sidebarCollapsed));
  collapseBtn.title = settings.sidebarCollapsed
    ? "Показать сессии (Ctrl+B)"
    : "Свернуть список (Ctrl+B)";
  collapseBtn.setAttribute("aria-label", collapseBtn.title);
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

function isTui(tab) {
  return (
    tab.term.buffer.active.type === "alternate" ||
    Boolean(tab.term.element?.classList.contains("enable-mouse-events")) ||
    Boolean(tab.tuiHint)
  );
}

function pinViewport(tab) {
  tab.term.scrollToBottom();
  const viewport = tab.host.querySelector(".xterm-viewport");
  if (viewport && viewport.scrollTop) {
    viewport.scrollTop = 0;
  }
}

function syncScrollLock(tab) {
  const lock = isTui(tab);
  const wasLock = tab.host.classList.contains("tui-lock");
  tab.host.classList.toggle("tui-lock", lock);
  if (lock) {
    if (tab.term.options.scrollback !== 0) {
      tab.term.options.scrollback = 0;
    }
    pinViewport(tab);
    return;
  }
  if (wasLock) {
    tab.term.options.scrollback = 8000;
  }
}

function pulseResize(tab) {
  if (!isPaneVisible(tab) || tab.exited || !tabs.includes(tab)) return;
  const cols = tab.term.cols;
  const rows = tab.term.rows;
  if (cols < 10 || rows < 4) {
    return;
  }
  post({ type: "resize", id: tab.id, cols: cols - 1, rows });
  window.setTimeout(() => {
    if (tab.exited || !tabs.includes(tab)) return;
    // A split/drag may have changed the size while the redraw pulse was pending.
    post({ type: "resize", id: tab.id, cols: tab.term.cols, rows: tab.term.rows });
    tab.term.refresh(0, Math.max(0, tab.term.rows - 1));
  }, 40);
}

function applyFit(tab) {
  if (!isPaneVisible(tab)) {
    return false;
  }
  if (Date.now() < ignoreFitUntil) {
    return false;
  }

  const proposed = tab.fit.proposeDimensions();
  if (!proposed || proposed.cols < 2 || proposed.rows < 1) {
    return false;
  }
  if (proposed.cols === tab.term.cols && proposed.rows === tab.term.rows) {
    return false;
  }

  const t0 = performance.now();
  if (!tab.exited) post({ type: "resize", id: tab.id, cols: proposed.cols, rows: proposed.rows });
  tab.term.resize(proposed.cols, proposed.rows);
  tab.term.refresh(0, Math.max(0, tab.term.rows - 1));
  diag("fit", `applyFit id=${tab.id} ${proposed.cols}x${proposed.rows} ms=${(performance.now()-t0).toFixed(1)}`, tab.id);
  return true;
}

function cancelFit() {
  if (fitRaf) { try { cancelAnimationFrame(fitRaf); } catch {} fitRaf = 0; }
  window.clearTimeout(fitTimer);
  fitTimer = 0;
}
function scheduleFit(tab, immediate = false) {
  const fitVisible = () => {
    fitRaf = 0;
    fitTimer = 0;
    const t0 = performance.now();
    let changed = 0;
    for (const item of visibleTabs()) if (applyFit(item)) changed++;
    if (changed) diag("fit", `scheduleFit done changed=${changed} ms=${(performance.now()-t0).toFixed(1)}`, null);
  };
  if (immediate) {
    cancelFit();
    fitVisible();
    return;
  }
  // Window/pane resize should feel instant — coalesce via rAF (16ms), not 80ms debounce.
  // Fallback to 16ms timeout when rAF unavailable (Node tests).
  if (fitRaf || fitTimer) return;
  if (typeof window.requestAnimationFrame === "function") {
    fitRaf = window.requestAnimationFrame(fitVisible);
  } else {
    fitTimer = window.setTimeout(fitVisible, 16);
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
    if (isPaneVisible(tab)) {
      applyFit(tab);
    }
  }
  zoomValue.textContent = `${Math.round((size / settings.fontSize) * 100)}%`;
}

function persistSettings() {
  post({ type: "persist-settings", data: JSON.stringify(settings) });
}

const cwdNotice = document.getElementById("cwd-notice");
let legacyHostNotice = "";

function renderCwdNotice() {
  const text = currentTab()?.cwdNotice || legacyHostNotice;
  cwdNotice.hidden = !text;
  cwdNotice.querySelector("span").textContent = text;
}

cwdNotice.querySelector("button").addEventListener("click", () => {
  const tab = currentTab();
  if (tab?.cwdNotice) tab.cwdNotice = "";
  else legacyHostNotice = "";
  renderCwdNotice();
  if (!isModalOpen() && !searchController.isOpen) tab?.term.focus();
});

function serializeTab(tab) {
  try {
    return tab.serialize.serialize({
      excludeAltBuffer: false,
      excludeModes: false
    });
  } catch {
    return tab.buffer || "";
  }
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
    active: tab.id === activeId,
    buffer: serializeTab(tab),
    cwd: tab.cwd || null,
    group: tab.group || null,
    color: tab.color || null,
    pinned: tab.pinned,
    hidden: tab.hidden,
    muted: tab.muted,
    shell: tab.shell || null,
    wslDistribution: tab.wslDistribution || null,
    startupCommand: tab.startupCommand || null
  }));
  post({ type: "persist-sessions", sessions: payload, layouts });
}

function schedulePersist() {
  window.clearTimeout(persistTimer);
  persistTimer = window.setTimeout(persistSessions, 400);
}

window.terminalvFlush = () => {
  window.clearTimeout(persistTimer);
  readyForPersist = true;
  persistSessions();
  persistSettings();
};

function updateSessionSwitcher() {
  const target = tabs.filter((tab) => Boolean(tab.hidden) !== showHiddenSessions);
  const signals = target.filter((tab) => tab.attention).length;
  hiddenSessionsBtn.querySelector("[data-hidden-label]").textContent = `${showHiddenSessions ? "Открытые" : "Скрытые"} · ${target.length}${signals ? ` · ! ${signals}` : ""}`;
  document.getElementById("session-count").textContent = String(tabs.filter((tab) => !tab.hidden).length);
  updateWorkspaceContext();
  hiddenSessionsBtn.setAttribute("aria-pressed", String(showHiddenSessions));
  hiddenSessionsBtn.title = `${showHiddenSessions ? "Показать открытые сессии" : "Показать скрытые сессии"}${signals ? `. Сигналов: ${signals}` : ""}`;
  hiddenSessionsBtn.setAttribute("aria-label", hiddenSessionsBtn.title);
  hiddenSessionsBtn.classList.toggle("has-attention", signals > 0);
}

function sessionMetaText(tab) {
  return [tab.attention && "Сигнал", tab.muted && "Без звука", tab.pinned && "★",
    tab.exited ? "завершена" : tab.shell === "wsl" ? `${tab.wslDistribution} · WSL` : tab.shell && tab.shell !== "auto" ? PROFILE_SHELLS[tab.shell] || tab.shell : shellName]
    .filter(Boolean).join(" · ");
}

function applyNotificationRow(row, tab) {
  row.classList.toggle("has-attention", Boolean(tab.attention));
  row.classList.toggle("exited", Boolean(tab.exited));
  tab.pane.classList.toggle("exited", Boolean(tab.exited));
  row.title = `${tab.customTitle || tab.title}${tab.attention ? " — получен сигнал" : ""}${tab.muted ? " — без звука" : ""}`;
  row.setAttribute("aria-label", row.title);
}

function renderTabs() {
  tabsEl.replaceChildren();
  const hiddenCount = tabs.filter((tab) => tab.hidden).length;
  updateSessionSwitcher();
  tabsEl.setAttribute("aria-label", showHiddenSessions ? "Скрытые сессии" : "Открытые сессии");
  tabsEl.setAttribute("role", showHiddenSessions ? "group" : "tablist");
  const groups = sessionGroups(tabs, { hidden: showHiddenSessions, query: sessionFilter.value });
  const hasGroups = groups.some((group) => group.name);
  for (const group of groups) {
    if (hasGroups) {
      const heading = document.createElement("div");
      heading.className = "session-group";
      heading.textContent = `${group.name || "Без группы"} · ${group.tabs.length}`;
      heading.title = group.name || "Без группы";
      tabsEl.append(heading);
    }
    for (const tab of group.tabs) {
      const row = document.createElement("div");
      row.className = `tab${tab.id === activeId ? " active" : ""}${tab.unread ? " unread" : ""}`;
      row.draggable = !tab.renaming && !tab.hidden;
      row.tabIndex = 0;
      row.dataset.id = tab.id;
      row.setAttribute("role", tab.hidden ? "button" : "tab");
      if (!tab.hidden) row.setAttribute("aria-selected", String(tab.id === activeId));
      applyNotificationRow(row, tab);
      row.classList.toggle("pinned", tab.pinned);
      row.classList.toggle("in-view", isPaneVisible(tab));
      row.classList.toggle("split-child", isSplitChild(layouts, tab.id));
      row.classList.toggle("split-parent", isSplitParent(layouts, tab.id));
      if (tab.color) row.style.setProperty("--session-color", SESSION_COLORS[tab.color].value);

      const accent = document.createElement("span");
      accent.className = "tab-accent";
      accent.setAttribute("aria-hidden", "true");
      const symbol = document.createElement("span");
      symbol.className = "tab-symbol";
      symbol.append(icon("terminal"));

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
      meta.textContent = sessionMetaText(tab);
      body.append(meta);

      const options = document.createElement("button");
      options.className = "tab-options";
      options.type = "button";
      options.append(icon("more"));
      options.title = "Группа, цвет, закрепление, звук и скрытие";
      options.setAttribute("aria-label", "Управление сессией");
      options.addEventListener("click", (event) => { event.stopPropagation(); showSessionOptions(tab); });
      options.addEventListener("dblclick", (event) => event.stopPropagation());

      const close = document.createElement("button");
      close.className = "tab-close";
      close.type = "button";
      close.title = tab.hidden ? "Вернуть сессию" : "Закрыть";
      close.setAttribute("aria-label", close.title);
      close.append(icon(tab.hidden ? "restore" : "close"));
      close.addEventListener("click", (event) => {
        event.stopPropagation();
        if (tab.hidden) revealTab(tab);
        else closeTab(tab.id);
      });

      close.addEventListener("dblclick", (event) => event.stopPropagation());
      row.append(accent, symbol, body, options, close);
      row.addEventListener("click", () => { if (tab.hidden) revealTab(tab); else activate(tab.id); });
      row.addEventListener("contextmenu", (event) => { event.preventDefault(); showSessionOptions(tab); });
      row.addEventListener("keydown", (event) => {
        if (event.target !== row || event.isComposing) return;
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          if (tab.hidden) revealTab(tab); else activate(tab.id);
        } else if (event.key === "F10" && event.shiftKey) {
          event.preventDefault();
          showSessionOptions(tab);
        }
      });
      row.addEventListener("dblclick", (event) => {
        event.preventDefault();
        if (tab.hidden || isModalOpen()) return;
        tab.renaming = true;
        renderTabs();
      });
      row.addEventListener("auxclick", (event) => {
        if (event.button === 1 && !tab.hidden) {
          event.preventDefault();
          closeTab(tab.id);
        }
      });
      row.addEventListener("dragstart", (event) => {
        draggedTabId = tab.id;
        row.classList.add("dragging");
        event.dataTransfer.effectAllowed = "move";
        event.dataTransfer.setData("text/plain", tab.id);
      });
      row.addEventListener("dragover", (event) => {
        if (tab.hidden || !draggedTabId || draggedTabId === tab.id) {
          return;
        }
        event.preventDefault();
        event.dataTransfer.dropEffect = "move";
        clearTabDropIndicators();
        row.classList.add(event.clientY < row.getBoundingClientRect().top + row.offsetHeight / 2
          ? "drag-over-before"
          : "drag-over-after");
      });
      row.addEventListener("dragleave", () => {
        row.classList.remove("drag-over-before", "drag-over-after");
      });
      row.addEventListener("drop", (event) => {
        event.preventDefault();
        if (!draggedTabId || draggedTabId === tab.id) {
          return;
        }
        const before = event.clientY < row.getBoundingClientRect().top + row.offsetHeight / 2;
        moveTab(draggedTabId, tab.id, before);
      });
      row.addEventListener("dragend", () => {
        draggedTabId = null;
        clearTabDropIndicators();
        row.classList.remove("dragging");
      });
      tabsEl.append(row);
    }
  }

  if (!groups.length) {
    const empty = document.createElement("div");
    empty.className = "session-list-empty";
    empty.textContent = sessionFilter.value.trim() ? "Сессии не найдены" : showHiddenSessions ? "Нет скрытых сессий" : "Нет открытых сессий";
    tabsEl.append(empty);
  }
  emptyEl.classList.toggle("hidden", openTabs().length > 0);
  emptyEl.querySelector(".empty-sub").textContent = hiddenCount
    ? "Скрытые сессии доступны в списке слева. Верните нужную или создайте новую."
    : "Команды, проекты и инструменты — каждый в своей сессии.";
}

function clearTabDropIndicators() {
  tabsEl.querySelectorAll(".drag-over-before, .drag-over-after").forEach((row) => {
    row.classList.remove("drag-over-before", "drag-over-after");
  });
}

function moveTab(sourceId, targetId, before) {
  const sourceIndex = tabs.findIndex((tab) => tab.id === sourceId);
  const targetIndex = tabs.findIndex((tab) => tab.id === targetId);
  if (sourceIndex < 0 || targetIndex < 0 || sourceIndex === targetIndex) {
    return;
  }

  const [source] = tabs.splice(sourceIndex, 1);
  const target = tabs.find((tab) => tab.id === targetId);
  // Dropping into another group also adopts its pinned lane, so visual and
  // stored order agree. Metadata can always be changed in the session dialog.
  source.group = target.group;
  source.pinned = target.pinned;
  let insertIndex = tabs.findIndex((tab) => tab.id === targetId);
  if (!before) {
    insertIndex += 1;
  }
  tabs.splice(insertIndex, 0, source);
  draggedTabId = null;
  renderTabs();
  schedulePersist();
}

function finishRename(tab, value) {
  const next = value.trim();
  tab.customTitle = next || undefined;
  tab.renaming = false;
  renderTabs();
  schedulePersist();
}

function dropWebgl(tab) {
  if (!tab.webgl) {
    return;
  }

  try {
    tab.webgl.dispose();
  } catch {
    // already gone
  }
  tab.webgl = null;
}

function ensureWebgl(tab) {
  if (tab.webgl) {
    return;
  }

  try {
    tab.webgl = new WebglAddon();
    tab.webgl.onContextLoss?.(() => dropWebgl(tab));
    tab.term.loadAddon(tab.webgl);
  } catch {
    tab.webgl = null;
  }
}

function patchTabRow(tab) {
  tab.pane.classList.toggle("exited", Boolean(tab.exited));
  if (tab.id === activeId) updateWorkspaceContext();
  if (tab.paneTitle) {
    tab.paneTitle.textContent = tab.customTitle || tab.title;
    tab.paneTitle.title = tab.customTitle || tab.title;
    tab.pane.setAttribute("aria-label", tab.customTitle || tab.title);
  }
  const row = tabsEl.querySelector(`[data-id="${CSS.escape(tab.id)}"]`);
  if (!row) {
    return;
  }

  row.classList.toggle("active", tab.id === activeId);
  row.classList.toggle("in-view", isPaneVisible(tab));
  if (!tab.hidden) row.setAttribute("aria-selected", String(tab.id === activeId));
  applyNotificationRow(row, tab);
  row.classList.toggle("unread", Boolean(tab.unread));
  row.classList.toggle("split-child", isSplitChild(layouts, tab.id));
  row.classList.toggle("split-parent", isSplitParent(layouts, tab.id));
  const title = row.querySelector(".tab-title");
  if (title && !tab.renaming) {
    title.textContent = tab.customTitle || tab.title;
  }
  const meta = row.querySelector(".tab-meta");
  if (meta) {
    meta.textContent = sessionMetaText(tab);
  }
}

function activate(id, { focus = true } = {}) {
  const tab = tabs.find((item) => item.id === id);
  if (!tab || tab.hidden) {
    return;
  }

  if (activeId !== id) {
    sessionOptions.cancel();
    closeController.cancel();
    searchController.close({ focus: false });
    pasteController.cancel(currentTab());
  }
  activeId = id;
  notifications.acknowledge(tab);
  renderCwdNotice();
  tab.unread = false;
  renderPaneLayout();
  // Change the input target now: a keystroke before the next paint must not
  // reach the previously focused panel. Deferred focus could also steal a click.
  if (focus && !searchController.isOpen && !isModalOpen() && settingsEl.classList.contains("hidden")) tab.term.focus();
  requestAnimationFrame(() => {
    if (!tabs.includes(tab) || tab.id !== activeId) return;
    tab.term.refresh(0, Math.max(0, tab.term.rows - 1));
    for (const item of visibleTabs()) applyFit(item);
  });
  schedulePersist();
}

function closeTab(id) {
  if (isModalOpen()) return;
  const tab = tabs.find((item) => item.id === id);
  if (tab) closeController.request(tab);
}

function removeTab(tab) {
  // Use object identity so an old confirmation cannot target a replacement session.
  const index = tabs.indexOf(tab);
  if (index < 0) {
    return;
  }

  const closingActive = activeId === tab.id;
  const ordered = openTabs();
  const visibleIndex = ordered.indexOf(tab);
  const neighbor = visibleTabs().find((item) => item !== tab);
  sessionOptions.cancel(tab);
  if (closingActive) searchController.close({ focus: false });
  tabs.splice(index, 1);
  layouts = normalizeLayouts(layouts, tabs);
  notifications.acknowledge(tab);
  pasteController.cancel(tab);
  if (!tab.exited) post({ type: "kill", id: tab.id });
  dropWebgl(tab);
  tab.output.dispose();
  tab.resizeObserver?.disconnect();
  tab.mouseWatch?.disconnect();
  try {
    tab.term.dispose();
  } catch {
    // already gone
  }
  tab.pane.remove();

  if (closingActive) {
    const next = neighbor || ordered[visibleIndex + 1] || ordered[visibleIndex - 1] || null;
    activeId = next?.id ?? null;
    renderCwdNotice();
    renderTabs();
    if (next) {
      activate(next.id);
      return;
    }
    schedulePersist();
    renderPaneLayout();
    return;
  }

  renderTabs();
  renderPaneLayout();
  schedulePersist();
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    post({ type: "clipboard-write", data: text });
  }
}

async function readClipboard() {
  try {
    return await navigator.clipboard.readText();
  } catch {
    return new Promise((resolve) => {
      const requestId = uuid();
      clipboardWaiters.set(requestId, resolve);
      post({ type: "clipboard-read", requestId });
      setTimeout(() => {
        if (clipboardWaiters.has(requestId)) {
          clipboardWaiters.delete(requestId);
          resolve("");
        }
      }, 1000);
    });
  }
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
  // Shortcuts and right-click use pasteController, including confirmation.
  // Stop xterm's native paste event so the text is not sent a second time.
  tab.host.addEventListener("paste", (event) => {
    event.preventDefault();
    event.stopPropagation();
  }, true);
  tab.host.addEventListener("copy", (event) => {
    if (!tab.term.hasSelection()) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (event.clipboardData) {
      event.clipboardData.setData("text/plain", tab.term.getSelection());
    } else {
      copyText(tab.term.getSelection());
    }
  }, true);

  tab.term.attachCustomKeyEventHandler((event) => {
    if (event.type !== "keydown") {
      return true;
    }

    if (isZoomEvent(event)) {
      return false;
    }

    const key = event.key.toLowerCase();
    const isCopyKey = key === "c" || event.code === "KeyC";
    const isPasteKey = key === "v" || event.code === "KeyV";
    if ((event.ctrlKey || event.metaKey) && event.altKey && isCopyKey) {
      event.preventDefault();
      event.stopPropagation();
      if (tab.term.hasSelection()) {
        copyText(getPlainSelection(tab.term));
      }
      return false;
    }
    if ((event.ctrlKey || event.metaKey) && !event.altKey) {
      if (isCopyKey && !event.shiftKey) {
        event.preventDefault();
        event.stopPropagation();
        if (tab.term.hasSelection()) {
          copyText(tab.term.getSelection());
        } else {
          post({ type: "write", id: tab.id, data: "\u0003" });
        }
        return false;
      }
      if (isPasteKey) {
        event.preventDefault();
        event.stopPropagation();
        if (event.repeat) {
          return false;
        }
        void pasteController.request(tab);
        return false;
      }
      if (event.shiftKey && isCopyKey) {
        event.preventDefault();
        event.stopPropagation();
        if (tab.term.hasSelection()) {
          copyText(tab.term.getSelection());
        }
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
    void pasteController.request(tab);
  });
}

function newTab(options = {}) {
  // Restored tabs explicitly carry their own cwd (possibly null). Only a fresh
  // tab inherits the active session, never a title or another restored tab.
  const cwd = options.shell === "wsl" ? null : Object.hasOwn(options, "cwd") ? options.cwd : currentTab()?.shell === "wsl" ? null : currentTab()?.cwd;
  const metadata = sessionMetadata({ ...options, group: Object.hasOwn(options, "group") ? options.group : currentTab()?.group });
  const id = options.id || uuid();
  const index = options.title ? nextIndex : nextIndex++;
  const pane = document.createElement("div");
  pane.className = "pane";
  pane.dataset.id = id;
  pane.setAttribute("role", "region");
  const paneHead = document.createElement("div");
  paneHead.className = "pane-head";
  const paneTitle = document.createElement("span");
  paneTitle.className = "pane-title";
  const paneClose = document.createElement("button");
  paneClose.type = "button";
  paneClose.className = "pane-close";
  paneClose.append(icon("close"));
  paneClose.title = "Закрыть эту панель";
  paneClose.setAttribute("aria-label", "Закрыть эту панель");
  paneClose.addEventListener("click", (event) => { event.stopPropagation(); closeTab(id); });
  const paneState = document.createElement("span");
  paneState.className = "pane-state";
  paneState.setAttribute("aria-hidden", "true");
  paneHead.append(paneState, paneTitle, paneClose);
  pane.append(paneHead);

  const hostEl = document.createElement("div");
  hostEl.className = "terminal-host";

  const overlay = document.createElement("div");
  overlay.className = "overlay";
  const overlayText = document.createElement("span");
  const overlayBtn = document.createElement("button");
  overlayBtn.type = "button";
  overlayBtn.textContent = "Перезапустить";
  if (options.startupCommand) {
    overlayBtn.textContent = "Запустить со стартовой командой";
    overlayBtn.title = options.startupCommand;
  }
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
    windowsPty: { backend: "conpty", buildNumber },
    linkHandler: {
      activate: (event, uri) => handleLink(event, uri)
    }
  });
  const fit = new FitAddon();
  term.loadAddon(fit);
  term.loadAddon(new WebLinksAddon((event, uri) => handleLink(event, uri)));
  const serialize = new SerializeAddon();
  term.loadAddon(serialize);
  const search = new SearchAddon({ highlightLimit: SEARCH_HIGHLIGHT_LIMIT });
  term.loadAddon(search);
  term.open(hostEl);

  const tab = {
    ...metadata,
    id,
    title: options.title || `Сессия ${index}`,
    customTitle: options.customTitle || undefined,
    cwd: cwd || undefined,
    shell: options.shell || undefined,
    wslDistribution: options.wslDistribution || undefined,
    startupCommand: options.startupCommand || undefined,
    buffer: options.buffer || "",
    renaming: false,
    unread: false,
    exited: false,
    pane,
    paneTitle,
    host: hostEl,
    overlay,
    overlayText,
    overlayBtn,
    term,
    fit,
    serialize,
    search,
    webgl: null,
    tuiHint: false
  };

  overlayBtn.addEventListener("click", () => restart(tab));
  pane.addEventListener("pointerdown", (event) => {
    if (!isPaneVisible(tab) || isModalOpen() || event.target.closest("button")) return;
    if (activeId !== id) activate(id, { focus: false });
  }, true);
  paneHead.addEventListener("click", (event) => { if (!event.target.closest("button") && !isModalOpen()) activate(id); });
  hostEl.addEventListener("focusin", () => {
    if (activeId !== id && isPaneVisible(tab) && !isModalOpen()) activate(id, { focus: false });
  });
  function postWrite(targetId, data) {
    const chunks = chunkText(data, WRITE_CHUNK);
    const isPaste = data.includes("\u001b[200~");
    const t0 = performance.now();
    if (isPaste || chunks.length > 1) {
      diag("ui", `postWrite async id=${targetId} len=${data.length} chunks=${chunks.length} isPaste=${isPaste}`, targetId);
      for (let i = 0; i < chunks.length; i++) {
        const c = chunks[i];
        queueMicrotask(() => {
          const t1 = performance.now();
          post({ type: "write", id: targetId, data: c });
          const dt = performance.now() - t1;
          if (dt > 10) diag("ui", `write post slow chunk ${i}/${chunks.length} ms=${dt.toFixed(1)}`, targetId);
        });
      }
      // watchdog: if still not writable after 2s, report
      setTimeout(() => {
        const dt = performance.now() - t0;
        diag("ui", `postWrite watchdog id=${targetId} after ${dt.toFixed(0)}ms`, targetId);
      }, 2000);
    } else {
      if (data.length > 100) diag("ui", `postWrite sync len=${data.length}`, targetId);
      for (const chunk of chunks) post({ type: "write", id: targetId, data: chunk });
    }
  }
  term.onData((data) => postWrite(id, data));
  tab.output = createNotificationOutput(term, () => notifications.bell(tab), () => syncScrollLock(tab));
  if (options.buffer) tab.output.write(options.buffer, true);
  term.onTitleChange((title) => {
    const cleaned = title?.trim();
    if (!cleaned || tab.customTitle) {
      return;
    }
    tab.title = cleaned;
    if (sessionFilter.value) renderTabs();
    else patchTabRow(tab);
    schedulePersist();
  });
  attachCopyPaste(tab);
  tab.term.buffer.onBufferChange(() => syncScrollLock(tab));
  const xtermEl = tab.term.element;
  if (xtermEl) {
    const mouseWatch = new MutationObserver(() => syncScrollLock(tab));
    tab.mouseWatch = mouseWatch;
    mouseWatch.observe(xtermEl, { attributes: true, attributeFilter: ["class"] });
  }
  tab.host.addEventListener(
    "wheel",
    (event) => {
      if (!tab.host.classList.contains("tui-lock")) {
        return;
      }
      if (event.ctrlKey || event.metaKey) {
        return;
      }
      event.preventDefault();
      pinViewport(tab);
    },
    { passive: false, capture: true }
  );
  tab.host.querySelector(".xterm-viewport")?.addEventListener(
    "scroll",
    () => {
      if (tab.host.classList.contains("tui-lock")) {
        pinViewport(tab);
      }
    },
    { passive: true }
  );
  tab.term.attachCustomWheelEventHandler((event) => {
    if (!tab.host.classList.contains("tui-lock")) {
      return true;
    }
    if (!(event.ctrlKey || event.metaKey)) {
      event.preventDefault();
    }
    return true;
  });
  syncScrollLock(tab);

  const observer = new ResizeObserver(() => {
    if (!isPaneVisible(tab)) {
      return;
    }
    scheduleFit(tab);
  });
  observer.observe(hostEl);
  tab.resizeObserver = observer;

  insertAfter(tabs, tab, options.splitFrom);
  if (!options.skipActivate) {
    layouts = options.splitFrom ? splitSession(layouts, options.splitFrom, id, options.splitAxis)
      : normalizeLayouts(layouts, tabs);
  }
  if (!options.skipActivate && !tab.hidden) {
    ensureWebgl(tab);
  }
  const cols = term.cols || 80;
  const rows = term.rows || 24;
  diag("restore", `newTab id=${id} live=${!!options.live} restored=${!!options.restored} hidden=${!!tab.hidden} shell=${tab.shell||""} startup=${(tab.startupCommand||"").slice(0,40)}`, id);
  if (options.live) {
    post({ type: "attach", id });
    window.setTimeout(() => pulseResize(tab), 300);
  } else if (tab.hidden || (options.restored && tab.startupCommand)) {
    // The shell is gone (e.g. Windows restarted). Keep the saved screen, but
    // never silently start a new process for a hidden session or a profile with a startup command (would duplicate servers/tests).
    // Plain shell sessions (no startupCommand) auto-restart — they are just a shell.
    tab.exited = true;
    tab.overlayText.textContent = "Сессия не запущена. Сохранённый вывод доступен; запуск — кнопкой ниже.";
    tab.overlay.classList.add("visible");
    diag("restore", `exited hidden/startup id=${id}`, id);
  } else {
    const createMsg = { type: "create", id, cols, rows, cwd: cwd || undefined,
      shell: tab.shell, startupCommand: tab.startupCommand, wslDistribution: tab.wslDistribution };
    if (!options.skipActivate) {
      fit.fit();
      createMsg.cols = term.cols;
      createMsg.rows = term.rows;
    }
    post(createMsg);
  }
  if (!options.skipActivate && !tab.hidden) {
    showHiddenSessions = false;
    sessionFilter.value = "";
    renderTabs();
    activate(id);
  }
}

function restart(tab) {
  if (!tab.exited || isModalOpen()) return;
  sessionOptions.cancel(tab);
  closeController.cancel(tab);
  if (tab.id === activeId) searchController.close({ focus: false });
  pasteController.cancel(tab);
  tab.exited = false;
  tab.cwdNotice = "";
  renderCwdNotice();
  tab.overlay.classList.remove("visible");
  notifications.acknowledge(tab);
  tab.output.reset();
  tab.fit.fit();
  post({ type: "create", id: tab.id, cols: tab.term.cols, rows: tab.term.rows, cwd: tab.cwd,
    shell: tab.shell, startupCommand: tab.startupCommand, wslDistribution: tab.wslDistribution });
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
    button.setAttribute("aria-pressed", String(theme.id === settings.themeId));
    button.setAttribute("aria-label", theme.name);
    const swatch = document.createElement("span");
    swatch.className = "theme-swatch";
    swatch.setAttribute("aria-hidden", "true");
    swatch.style.setProperty("--preview-bg", theme.term.background);
    swatch.style.setProperty("--preview-side", theme.chrome.sidebar);
    swatch.style.setProperty("--preview-text", theme.term.foreground);
    swatch.style.setProperty("--preview-accent", theme.chrome.accent);
    const preview = document.createElement("span");
    preview.className = "theme-code";
    preview.textContent = ">_";
    swatch.append(preview);
    const label = document.createElement("span");
    label.className = "theme-name";
    label.textContent = theme.name;
    label.append(icon("check"));
    button.append(swatch, label);
    button.addEventListener("click", () => {
      settings.themeId = theme.id;
      applyChrome();
      applyToTerminals();
      persistSettings();
      renderThemeGrid();
      themeGrid.querySelector(".theme-card.active")?.focus({ preventScroll: true });
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
  if (isModalOpen()) return;
  pasteController.cancel(currentTab());
  searchController.close({ focus: false });
  settingsEl.classList.remove("hidden");
  appEl.inert = true;
  settingsClose.focus({ preventScroll: true });
}

function closeSettings() {
  settingsEl.classList.add("hidden");
  appEl.inert = false;
  if (currentTab()) currentTab().term.focus(); else settingsBtn.focus({ preventScroll: true });
}

function toggleSidebar() {
  settings.sidebarCollapsed = !settings.sidebarCollapsed;
  applyChrome();
  persistSettings();
  ignoreFitUntil = Date.now() + 200;
  cancelFit();
  scheduleFit();
}

function restoreSessions(records, savedLayouts) {
  if (!records?.length) {
    readyForPersist = true;
    newTab();
    persistSessions();
    return;
  }

  const live = new Set(window.__liveIds || []);
  let active = null;
  for (const record of records) {
    const isLive = live.has(record.id);
    newTab({
      id: record.id,
      title: record.title,
      customTitle: record.customTitle,
      ...sessionMetadata(record),
      buffer: isLive ? undefined : record.buffer,
      cwd: record.cwd,
      shell: record.shell,
      wslDistribution: record.wslDistribution,
      startupCommand: record.startupCommand,
      restored: true,
      live: isLive,
      skipActivate: true
    });
    if (record.active && !record.hidden) {
      active = record.id;
    }
  }
  nextIndex = tabs.length + 1;
  layouts = normalizeLayouts(savedLayouts, tabs);
  readyForPersist = true;
  const firstOpen = openTabs()[0];
  showHiddenSessions = !firstOpen;
  renderTabs();
  if (firstOpen) activate(active || firstOpen.id);
  else renderPaneLayout();
  persistSessions();
}

function handleHost(message) {
  if (!message || typeof message !== "object") {
    return;
  }

  if (message.type === "init") {
    shortcuts.setSupported(message.shortcutsSupported);
    launchProfiles.setProfiles(message.profiles);
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
    window.__liveIds = message.liveIds || [];
    legacyHostNotice = message.cwdTrackingSupported === false
      ? "Фоновый процесс TerminalV старой версии: текущая папка пока не отслеживается. После завершения нужных задач перезагрузите Windows. Работающие сессии не прерываются."
      : message.environmentRefreshSupported === false
        ? "Фоновый процесс TerminalV старой версии: PATH для новых вкладок пока не обновляется. Сохраните работу и перезагрузите Windows, чтобы включить исправление. Работающие сессии не прерываются."
        : "";
    syncSettingsForm();
    if (tabs.length === 0) {
      restoreSessions(message.sessions, message.layouts);
    } else {
      renderTabs();
    }
    renderCwdNotice();
    return;
  }

  if (message.type === "shortcuts-created") {
    shortcuts.receive(message);
    return;
  }

  if (message.type === "profiles-saved") {
    launchProfiles.receive(message);
    return;
  }

  if (message.type === "launch-targets") {
    launchMenu.receive(message);
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

  if (message.type === "cwd") {
    if (tab.shell === "wsl") return; // Windows cwd metadata is not a Linux working directory.
    if (typeof message.cwd === "string" && message.cwd.length > 0) tab.cwd = message.cwd;
    if (message.notice) tab.cwdNotice = message.notice;
    renderCwdNotice();
    if (tab.id === activeId) updateWorkspaceContext();
    schedulePersist();
    return;
  }

  if (message.type === "data") {
    const chunk = message.data ?? "";
    if (/\x1b\[\?(?:1049|47|1047)l/.test(chunk)) {
      tab.tuiHint = false;
    } else if (/\x1b\[\?(?:1049|47|1047|1000|1002|1003)h/.test(chunk)) {
      tab.tuiHint = true;
    }
    tab.output.write(chunk, message.replay === true);
    schedulePersist();
    if (tab.id !== activeId && !tab.unread) {
      tab.unread = true;
      patchTabRow(tab);
    }
    return;
  }

  if (message.type === "exit") {
    sessionOptions.cancel(tab);
    tab.tuiHint = false;
    tab.exited = true;
    closeController.cancel(tab);
    pasteController.cancel(tab);
    syncScrollLock(tab);
    tab.overlayText.textContent = `Процесс завершился с кодом ${message.code ?? 0}`;
    tab.overlay.classList.add("visible");
    renderTabs();
    return;
  }

  if (message.type === "error") {
    sessionOptions.cancel(tab);
    tab.exited = true;
    closeController.cancel(tab);
    pasteController.cancel(tab);
    tab.overlayText.textContent = message.message || "Не удалось запустить сессию";
    tab.overlay.classList.add("visible");
    renderTabs();
  }
}

newTabBtn.addEventListener("click", () => newTab());
document.getElementById("launch-profiles-btn").addEventListener("click", () => {
  if (isModalOpen() || !settingsEl.classList.contains("hidden")) return;
  pasteController.cancel(currentTab());
  searchController.close({ focus: false });
  launchMenu.open();
});
emptyNewBtn.addEventListener("click", () => newTab());
updateApply.addEventListener("click", () => post({ type: "update-apply" }));
versionBtn.addEventListener("click", () => post({ type: "update-check" }));
const authorLink = document.getElementById("author-link");
const openAuthor = (event) => {
  event.preventDefault(); // Never navigate the terminal WebView or open an embedded popup.
  if (!isModalOpen() && settingsEl.classList.contains("hidden")) post({ type: "open-author" });
};
authorLink.addEventListener("click", openAuthor);
authorLink.addEventListener("auxclick", (event) => { if (event.button === 1) openAuthor(event); });
settingsBtn.addEventListener("click", openSettings);
searchBtn.addEventListener("click", () => {
  if (!isModalOpen() && settingsEl.classList.contains("hidden")) {
    searchController.open(currentTab());
  }
});
settingsClose.addEventListener("click", closeSettings);
settingsEl.addEventListener("click", (event) => {
  if (event.target === settingsEl) {
    closeSettings();
  }
});
collapseBtn.addEventListener("click", toggleSidebar);
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
    if (!settingsEl.classList.contains("hidden")) {
      if (event.isComposing) return;
      if (event.key === "Escape" || ((event.ctrlKey || event.metaKey) && event.key === ",")) {
        event.preventDefault();
        closeSettings();
      } else if (event.key === "Tab") {
        const controls = [...settingsEl.querySelectorAll("button:not(:disabled), input:not(:disabled), select:not(:disabled)")]
          .filter((control) => control.getClientRects().length);
        const first = controls[0], last = controls.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
      }
      return;
    }
    if (isModalOpen() || event.target === sessionFilter) {
      return;
    }
    if (event.target.closest?.(".pane-divider")) return;
    const paneAction = paneShortcut(event);
    const editing = event.target.matches?.("input, textarea, select, [contenteditable=true]") &&
      !event.target.classList.contains("xterm-helper-textarea");
    if (paneAction && !editing && settingsEl.classList.contains("hidden")) {
      event.preventDefault();
      event.stopPropagation();
      if (paneAction === "columns" || paneAction === "rows") {
        if (!event.repeat) splitPane(paneAction);
      } else {
        const next = neighborPane(layoutGeometry(layoutFor(layouts, activeId)).panes, activeId, paneAction);
        if (next) activate(next);
      }
      return;
    }
    if (settingsEl.classList.contains("hidden")) {
      if (isSearchShortcut(event)) {
        event.preventDefault();
        event.stopPropagation();
        if (!event.repeat) searchController.open(currentTab());
        return;
      }
      if (searchController.handleKeyDown(event)) return;
    }
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
      event.stopPropagation();
      if (!event.repeat && activeId) {
        closeTab(activeId);
      }
      return;
    }
    if (event.ctrlKey && event.key === "Tab") {
      event.preventDefault();
      const visible = openTabs();
      if (visible.length < 2) {
        return;
      }
      const index = visible.findIndex((tab) => tab.id === activeId);
      const next = event.shiftKey
        ? visible[(index - 1 + visible.length) % visible.length]
        : visible[(index + 1) % visible.length];
      showHiddenSessions = false;
      sessionFilter.value = "";
      renderTabs();
      activate(next.id);
      return;
    }
    if (event.altKey && event.key >= "1" && event.key <= "9") {
      event.preventDefault();
      const tab = openTabs()[Number(event.key) - 1];
      if (tab) {
        showHiddenSessions = false;
        sessionFilter.value = "";
        renderTabs();
        activate(tab.id);
      }
    }
  },
  true
);

window.addEventListener(
  "wheel",
  (event) => {
    if (isModalOpen() || searchController.contains(event.target) || event.target === sessionFilter) {
      return;
    }
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
