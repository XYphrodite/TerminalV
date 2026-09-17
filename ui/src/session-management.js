export const SESSION_COLORS = {
  blue: { name: "Синий", value: "#60a5fa" },
  green: { name: "Зелёный", value: "#4ade80" },
  amber: { name: "Жёлтый", value: "#fbbf24" },
  rose: { name: "Розовый", value: "#fb7185" },
  violet: { name: "Фиолетовый", value: "#c084fc" }
};

export function sessionMetadata(record) {
  return {
    group: typeof record.group === "string" ? record.group.trim().slice(0, 80) : "",
    color: Object.hasOwn(SESSION_COLORS, record.color) ? record.color : "",
    pinned: record.pinned === true,
    hidden: record.hidden === true,
    muted: record.muted === true
  };
}

export function insertAfter(tabs, newTab, parentId) {
  if (parentId) {
    const idx = tabs.findIndex((t) => t.id === parentId);
    if (idx !== -1) {
      tabs.splice(idx + 1, 0, newTab);
      return;
    }
  }
  tabs.push(newTab);
}

// Stable groups follow stored tab order; pinned tabs lead each group.
export function sessionGroups(sessions, { hidden = false, query = "" } = {}) {
  const needle = query.trim().toLocaleLowerCase();
  const groups = new Map();
  for (const tab of sessions) {
    if (Boolean(tab.hidden) !== hidden) continue;
    if (needle && !`${tab.customTitle || tab.title}\n${tab.group || ""}`.toLocaleLowerCase().includes(needle)) continue;
    const name = tab.group || "";
    if (!groups.has(name)) groups.set(name, []);
    groups.get(name).push(tab);
  }
  return [...groups].map(([name, tabs]) => ({
    name,
    tabs: [...tabs.filter((tab) => tab.pinned), ...tabs.filter((tab) => !tab.pinned)]
  }));
}

export function createSessionOptions({ dialog, getSessions, onSave, onHide, restoreFocus }) {
  const form = dialog.querySelector("form");
  const name = dialog.querySelector("[data-session-name]");
  const group = dialog.querySelector("[data-session-group]");
  const colors = dialog.querySelector("[data-session-color]");
  const pinned = dialog.querySelector("[data-session-pinned]");
  const muted = dialog.querySelector("[data-session-muted]");
  const hide = dialog.querySelector("[data-session-hide]");
  const suggestions = dialog.querySelector("datalist");
  let pending = null;

  for (const [id, color] of Object.entries(SESSION_COLORS)) {
    colors.add(new Option(color.name, id));
  }

  function finish(action) {
    const tab = pending;
    if (!tab) return;
    pending = null;
    if (dialog.open) dialog.close();
    if (getSessions().includes(tab) && action !== "cancel") {
      onSave(tab, sessionMetadata({ group: group.value, color: colors.value, pinned: pinned.checked,
        hidden: tab.hidden, muted: muted.checked }), action);
      if (action === "hide") onHide(tab);
    }
    restoreFocus();
  }

  form.addEventListener("submit", (event) => { event.preventDefault(); finish("save"); });
  hide.addEventListener("click", () => finish("hide"));
  dialog.querySelector("[data-session-cancel]").addEventListener("click", () => finish("cancel"));
  dialog.addEventListener("cancel", (event) => { event.preventDefault(); finish("cancel"); });
  dialog.addEventListener("close", () => { if (!dialog.open) finish("cancel"); });
  dialog.addEventListener("keydown", (event) => event.stopPropagation());

  return {
    get isOpen() { return dialog.open; },
    open(tab) {
      if (pending || !getSessions().includes(tab)) return;
      pending = tab;
      name.textContent = tab.customTitle || tab.title;
      group.value = tab.group || "";
      colors.value = tab.color || "";
      pinned.checked = Boolean(tab.pinned);
      muted.checked = Boolean(tab.muted);
      hide.hidden = Boolean(tab.hidden);
      hide.textContent = tab.exited ? "Сохранить и скрыть" : "Скрыть, оставив работать";
      suggestions.replaceChildren(...[...new Set(getSessions().map((item) => item.group).filter(Boolean))]
        .map((value) => new Option(value, value)));
      try {
        dialog.showModal();
        group.focus({ preventScroll: true });
      } catch (error) {
        finish("cancel");
        throw error;
      }
    },
    cancel(tab) { if (!tab || pending === tab) finish("cancel"); }
  };
}
