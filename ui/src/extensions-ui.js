import { createExtensionRuntime } from "./extensions.js";

export function createExtensionsUi({ post, document: doc = document }) {
  const settings = doc.getElementById("extension-settings");
  const list = doc.getElementById("extension-list");
  const status = doc.getElementById("extension-status");
  const sidebar = doc.getElementById("extension-sidebar");
  const panels = doc.getElementById("extension-settings-panels");
  const notices = doc.getElementById("extension-notifications");
  const surfaces = new Map();
  let initialized = false;

  const runtime = createExtensionRuntime({ post, createSurface, notify, changed: render,
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
    const label = element("h3", "extension-panel-title", title);
    const badge = element("span", "count-badge");
    badge.hidden = true;
    const body = element("div", "extension-panel-body");
    heading.append(label, badge);
    panel.append(heading, body);
    container.append(panel);
    container.hidden = false;
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
    const close = element("button", "ghost", "Закрыть");
    close.type = "button";
    const timer = setTimeout(() => item.remove(), 8000);
    close.addEventListener("click", () => { clearTimeout(timer); item.remove(); });
    item.append(close);
    notices.append(item);
    while (notices.children.length > 3) notices.firstElementChild.remove();
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
    if (!catalog.length) list.append(element("p", "shortcut-hint", "Расширения пока не установлены."));
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
        ? "Изменение применится после перезапуска TerminalV."
        : item.active ? "Включено" : item.enabled ? "Не удалось запустить" : "Отключено"));
      if (item.error || item.uiError) row.append(element("p", "extension-error", item.error || item.uiError));
      const commands = element("div", "extension-commands");
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

  doc.getElementById("extensions-open-folder")?.addEventListener("click", () => void run(() => runtime.openFolder()));
  return {
    receive: message => runtime.receive(message),
    initialize({ extensionsSupported, extensionsError, mobile }) {
      if (settings) settings.hidden = mobile === true || (!extensionsSupported && !extensionsError);
      if (extensionsError && status) status.textContent = extensionsError;
      if (!extensionsSupported || mobile === true || initialized) return;
      initialized = true;
      void run(() => runtime.refresh());
    },
    dispose: () => runtime.dispose()
  };
}
