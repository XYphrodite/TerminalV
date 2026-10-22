import { t } from "./i18n.js";
import { createExtensionRuntime } from "./extensions.js";

export function createExtensionsUi({ post, document: doc = document }) {
  const settings = doc.getElementById("extension-settings");
  const list = doc.getElementById("extension-list");
  const status = doc.getElementById("extension-status");
  const sidebar = doc.getElementById("extension-sidebar");
  const panels = doc.getElementById("extension-settings-panels");
  const notices = doc.getElementById("extension-notifications");
  const checkButton = doc.getElementById("extensions-check-updates");
  const updateAllButton = doc.getElementById("extensions-update-all");
  const updateStatus = doc.getElementById("extension-update-status");
  const surfaces = new Map();
  const announced = new Set();
  let initialized = false;
  let busy = false;

  const runtime = createExtensionRuntime({ post, createSurface, notify, changed: render, updates: announce,
    log: (message, error) => {
      console.error(message, error);
      post({ type: "diag", data: `${message} ${error?.message || error}` });
    }
  });

  function element(tag, className, text) {
    const node = doc.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function createSurface(extensionId, location, { id, title } = {}) {
    if (typeof id !== "string" || !/^[a-z0-9-]+$/.test(id) || typeof title !== "string" || !title)
      throw new Error("Panels need an id (lowercase letters, digits, hyphens) and title.");
    const key = `${extensionId}:${location}:${id}`;
    if (surfaces.has(key)) throw new Error("Panel id is already registered.");
    const container = location === "sidebar" ? sidebar : panels;
    if (!container) throw new Error("Extension panels are unavailable.");
    const panel = element("section", "extension-panel");
    panel.dataset.extensionId = extensionId;
    const heading = element("div", "extension-panel-heading");
    const toggle = element("button", "extension-panel-toggle");
    toggle.type = "button";
    toggle.setAttribute("aria-expanded", "true");
    const chevron = element("span", "extension-panel-chevron");
    chevron.setAttribute("aria-hidden", "true");
    const label = element("h3", "extension-panel-title", title);
    const badge = element("span", "count-badge");
    badge.hidden = true;
    toggle.append(chevron, label);
    heading.append(toggle, badge);
    const body = element("div", "extension-panel-body");
    panel.append(heading, body);
    container.append(panel);
    container.hidden = false;
    const storageKey = `tv-ext-panel:${key}`;
    try {
      if (globalThis.localStorage?.getItem(storageKey) === "1") {
        panel.classList.add("collapsed");
        toggle.setAttribute("aria-expanded", "false");
      }
    } catch { /* storage may be unavailable */ }
    toggle.addEventListener("click", () => {
      const collapsed = panel.classList.toggle("collapsed");
      toggle.setAttribute("aria-expanded", String(!collapsed));
      try { globalThis.localStorage?.setItem(storageKey, collapsed ? "1" : "0"); }
      catch { /* ignore */ }
    });
    surfaces.set(key, panel);
    return {
      element: body,
      setBadge(value) { badge.textContent = String(value ?? ""); badge.hidden = value == null || value === ""; },
      dispose() {
        panel.remove();
        surfaces.delete(key);
        container.hidden = container.children.length === 0;
      }
    };
  }

  function notify(id, text) {
    if (!notices) return;
    const item = element("div", "extension-notification");
    item.append(element("strong", "", runtime.catalog.find(item => item.id === id)?.name ?? id),
      element("div", "", text));
    const close = element("button", "ghost", t("Extensions_Close"));
    close.type = "button";
    const timer = setTimeout(() => item.remove(), 8000);
    close.addEventListener("click", () => { clearTimeout(timer); item.remove(); });
    item.append(close);
    notices.append(item);
    while (notices.children.length > 3) notices.firstElementChild.remove();
  }

  // An update is available when the host knows a newer version than the one already staged.
  const updatable = item => Boolean(item.updateVersion) && item.updateVersion !== item.stagedVersion;

  function setUpdateStatus(text) {
    if (updateStatus) updateStatus.textContent = text;
  }

  // Pushed by the host after its background check; each version is announced once.
  function announce(catalog) {
    for (const item of catalog) {
      const key = `${item.id}@${item.updateVersion}`;
      if (!updatable(item) || announced.has(key)) continue;
      announced.add(key);
      notifyUpdate(item);
    }
  }

  function notifyUpdate(item) {
    if (!notices) return;
    const box = element("div", "extension-notification");
    box.append(element("strong", "", item.name || item.id),
      element("div", "", t("Extensions_UpdateAvailable", { version: item.updateVersion })));
    const update = element("button", "ghost", t("Extensions_Update"));
    update.type = "button";
    const close = element("button", "ghost", t("Extensions_Close"));
    close.type = "button";
    const timer = setTimeout(() => box.remove(), 15000);
    close.addEventListener("click", () => { clearTimeout(timer); box.remove(); });
    update.addEventListener("click", () => {
      clearTimeout(timer);
      box.remove();
      void applyUpdates(item.id);
    });
    box.append(update, close);
    notices.append(box);
    while (notices.children.length > 3) notices.firstElementChild.remove();
  }

  async function checkUpdates() {
    busy = true;
    render();
    setUpdateStatus(t("Extensions_UpdateChecking"));
    try {
      await runtime.checkUpdates();
      const count = runtime.catalog.filter(updatable).length;
      setUpdateStatus(count ? t("Extensions_UpdatesFound", { count }) : t("Extensions_UpToDate"));
    } catch (error) {
      setUpdateStatus("");
      throw error;
    } finally {
      busy = false;
      render();
    }
  }

  // id == null stages every available update.
  async function applyUpdates(id = null) {
    busy = true;
    render();
    setUpdateStatus(t("Extensions_UpdateDownloading"));
    await run(async () => {
      try {
        await runtime.update(id);
        setUpdateStatus(t("Extensions_UpdateStaged"));
      } catch (error) {
        setUpdateStatus("");
        // Some packages may have been staged before the failure.
        await runtime.refresh().catch(() => {});
        throw error;
      }
    });
    busy = false;
    render();
  }

  async function run(action) {
    if (status) status.textContent = "";
    try { await action(); }
    catch (error) { if (status) status.textContent = error.message; }
  }

  function render() {
    if (!list) return;
    const focus = doc.activeElement?.dataset.extensionToggle;
    list.replaceChildren();
    const catalog = runtime.catalog;
    if (checkButton) checkButton.disabled = busy;
    if (updateAllButton) {
      updateAllButton.hidden = !catalog.some(updatable);
      updateAllButton.disabled = busy;
    }
    if (!catalog.length) list.append(element("p", "shortcut-hint", t("Extensions_NoneInstalled")));
    for (const item of catalog) {
      const row = element("section", "extension-item");
      const label = element("label", "shortcut-option");
      const toggle = element("input");
      toggle.type = "checkbox";
      toggle.checked = item.enabled;
      toggle.disabled = !item.valid;
      toggle.dataset.extensionToggle = item.id;
      toggle.addEventListener("change", () => {
        toggle.disabled = true;
        void run(async () => {
          try { await runtime.setEnabled(item.id, toggle.checked); }
          finally { render(); }
        });
      });
      label.append(toggle, doc.createTextNode(`${item.name}${item.version ? ` · ${item.version}` : ""}`));
      row.append(label);
      if (item.description) row.append(element("p", "shortcut-hint", item.description));
      row.append(element("p", "shortcut-hint", item.restartRequired
        ? t("Extensions_RestartRequired")
        : item.active ? t("Extensions_Enabled") : item.enabled ? t("Extensions_Failed") : t("Extensions_Disabled")));
      if (item.error || item.uiError) row.append(element("p", "extension-error", item.error || item.uiError));
      if (item.stagedVersion)
        row.append(element("p", "shortcut-hint extension-update", t("Extensions_UpdateStagedVersion", { version: item.stagedVersion })));
      const commands = element("div", "extension-commands");
      if (updatable(item)) {
        row.append(element("p", "shortcut-hint extension-update", t("Extensions_UpdateAvailable", { version: item.updateVersion })));
        const update = element("button", "ghost", t("Extensions_Update"));
        update.type = "button";
        update.disabled = busy;
        update.addEventListener("click", () => void applyUpdates(item.id));
        commands.append(update);
      }
      for (const command of runtime.commands(item.id)) {
        const button = element("button", "ghost", command.title);
        button.type = "button";
        button.addEventListener("click", () => void run(() => runtime.execute(item.id, command.id)));
        commands.append(button);
      }
      row.append(commands);
      list.append(row);
      if (focus === item.id) toggle.focus({ preventScroll: true });
    }
  }

  checkButton?.addEventListener("click", () => void run(checkUpdates));
  updateAllButton?.addEventListener("click", () => void applyUpdates(null));
  doc.getElementById("extensions-open-folder")?.addEventListener("click", () => void run(() => runtime.openFolder()));
  return {
    receive: message => runtime.receive(message),
    initialize({ extensionsSupported, extensionsError, mobile }) {
      if (checkButton) checkButton.textContent = t("Extensions_CheckUpdates");
      if (updateAllButton) updateAllButton.textContent = t("Extensions_UpdateAll");
      if (settings) settings.hidden = mobile === true || (!extensionsSupported && !extensionsError);
      if (extensionsError && status) status.textContent = extensionsError;
      if (!extensionsSupported || mobile === true || initialized) return;
      initialized = true;
      void run(() => runtime.refresh());
    },
    dispose: () => runtime.dispose()
  };
}
