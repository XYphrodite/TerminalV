function isActiveLimit(limit) {
  if (!limit?.resetsAt) return true;
  const date = new Date(limit.resetsAt);
  return Number.isNaN(date.getTime()) || date.getTime() > Date.now();
}

function formatReset(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const diff = date.getTime() - Date.now();
  if (diff <= 0) return "";
  const h = Math.floor(diff / 3600000);
  const m = Math.floor((diff % 3600000) / 60000);
  const label = h > 0 ? `${h}ч ${m}м` : `${m}м`;
  return `через ${label}`;
}

function percentClass(value) {
  if (value == null) return "";
  if (value >= 85) return "ai-limit danger";
  if (value >= 60) return "ai-limit warn";
  return "ai-limit";
}

function activeLimits(service) {
  if (!service?.authenticated || service.authOk === false || service.limitsStale) return [];
  return (service.limits ?? []).filter(isActiveLimit);
}

function serviceTone(service) {
  if (!service.installed) return "missing";
  if (service.authenticated && service.authOk === false) return "expired";
  if (!service.authenticated) return "unauth";
  const maxPct = Math.max(0, ...activeLimits(service).map(l => l.usedPercent ?? 0));
  if (maxPct >= 100) return "exhausted";
  if (maxPct >= 85) return "limited";
  if (maxPct >= 60 || service.limitsStale) return "warn";
  return "ok";
}

export const SERVICE_GROUPS = Object.freeze([
  Object.freeze({ id: "available", title: "Лимиты есть" }),
  Object.freeze({ id: "exhausted", title: "Лимитов нет" }),
  Object.freeze({ id: "stale", title: "Кэш устарел" }),
  Object.freeze({ id: "unauth", title: "Нет авторизации" }),
  Object.freeze({ id: "missing", title: "Не установлено" })
]);

export function servicePriority(service) {
  if (!service?.installed) return 6;
  if (!service.authenticated) return 5;
  if (service.authOk === false) return 4;
  if (service.limitsStale) return 3;
  const active = activeLimits(service);
  if (active.length === 0) return 1;
  const isExhausted = active.some(l => (l.usedPercent ?? 0) >= 100);
  return isExhausted ? 2 : 0;
}

export function serviceGroupId(service) {
  const priority = servicePriority(service);
  if (priority <= 1) return "available";
  if (priority === 2) return "exhausted";
  if (priority === 3) return "stale";
  if (priority <= 5) return "unauth";
  return "missing";
}

export function sortServices(services) {
  return (services ?? [])
    .map((service, index) => ({ service, index, priority: servicePriority(service) }))
    .sort((a, b) => a.priority - b.priority || a.index - b.index)
    .map(entry => entry.service);
}

export function groupServices(services) {
  const sorted = sortServices(services);
  const buckets = new Map(SERVICE_GROUPS.map(g => [g.id, []]));
  for (const service of sorted) {
    buckets.get(serviceGroupId(service))?.push(service);
  }
  return SERVICE_GROUPS
    .map(group => ({ id: group.id, title: group.title, services: buckets.get(group.id) ?? [] }))
    .filter(group => group.services.length > 0);
}

export async function activate(api) {
  const doc = globalThis.document;
  const panel = api.ui.createSidebarPanel({ id: "ai-limits", title: "AI Limits" });
  const root = doc.createElement("div");
  root.className = "ai-limits-root";
  panel.element.append(root);

  const style = doc.createElement("style");
  style.textContent = `
    .ai-limits-root { display: grid; gap: 10px; font-size: 12px; }
    .ai-limits-toolbar { display: flex; gap: 6px; align-items: center; }
    .ai-limits-toolbar button {
      cursor: pointer; border: 1px solid var(--sidebar-edge); border-radius: 6px;
      background: transparent; color: inherit; padding: 4px 8px; font: inherit;
    }
    .ai-limits-list { display: grid; gap: 10px; }
    .ai-group { display: grid; gap: 6px; }
    .ai-group.collapsed { gap: 0; }
    .ai-group-toggle {
      display: flex; align-items: center; gap: 6px; width: 100%;
      padding: 3px 4px; margin: 0; border: 0; border-radius: 6px;
      background: transparent; color: color-mix(in srgb, var(--text) 78%, transparent);
      font: inherit; font-size: 11px; font-weight: 600;
      text-align: left; cursor: pointer;
    }
    .ai-group-toggle:hover {
      background: color-mix(in srgb, var(--text) 6%, transparent);
      color: var(--text);
    }
    .ai-group-chevron {
      width: 0; height: 0;
      border-left: 4px solid currentColor;
      border-top: 3.5px solid transparent;
      border-bottom: 3.5px solid transparent;
      opacity: 0.75; transform: rotate(90deg);
      transition: transform 0.12s ease; flex-shrink: 0;
    }
    .ai-group.collapsed .ai-group-chevron { transform: rotate(0deg); }
    .ai-group-title { flex: 1; min-width: 0; }
    .ai-group-count {
      padding: 0 6px; border: 1px solid var(--sidebar-edge); border-radius: 999px;
      font-size: 10.5px; line-height: 1.5; font-weight: 500; opacity: 0.75;
      font-variant-numeric: tabular-nums; flex-shrink: 0;
    }
    .ai-group-body { display: grid; gap: 8px; }
    .ai-group.collapsed .ai-group-body { display: none; }
    .ai-card {
      border: 1px solid var(--sidebar-edge); border-radius: 8px; padding: 8px;
      display: grid; gap: 6px;
    }
    .ai-card-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
    .ai-card-title { display: flex; align-items: center; gap: 6px; font-weight: 600; }
    .ai-dot {
      width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0;
      box-sizing: border-box; background: var(--text); opacity: 0.35;
    }
    .ai-dot.ok { background: #3ecf8e; opacity: 1; }
    .ai-dot.warn { background: #e2b93b; opacity: 1; }
    .ai-dot.limited { background: #e26d3b; opacity: 1; }
    .ai-dot.exhausted { background: var(--danger, #e25555); opacity: 1; }
    .ai-dot.expired { background: transparent; border: 1.75px solid #e2b93b; opacity: 1; }
    .ai-dot.unauth { background: transparent; border: 1.75px solid var(--muted, #8b98a8); opacity: 0.9; }
    .ai-dot.missing { background: transparent; border: 1.5px dashed var(--muted, #8b98a8); opacity: 0.4; }
    .ai-meta { opacity: 0.75; overflow-wrap: anywhere; }
    .ai-meta.ai-stale { color: #e2b93b; opacity: 1; }
    .ai-limit-row { display: grid; gap: 2px; }
    .ai-limit-label { display: flex; justify-content: space-between; gap: 8px; opacity: 0.85; }
    .ai-bar {
      height: 5px; border-radius: 999px; background: color-mix(in srgb, var(--text) 12%, transparent);
      overflow: hidden;
    }
    .ai-bar > i { display: block; height: 100%; background: var(--accent, #6aa9ff); border-radius: inherit; }
    .ai-limit.warn .ai-bar > i { background: #e2b93b; }
    .ai-limit.danger .ai-bar > i { background: var(--danger, #e25555); }
  `;
  root.append(style);

  const toolbar = doc.createElement("div");
  toolbar.className = "ai-limits-toolbar";
  const refresh = doc.createElement("button");
  refresh.type = "button";
  refresh.textContent = "Обновить";
  const stamp = doc.createElement("span");
  stamp.className = "ai-meta";
  toolbar.append(refresh, stamp);

  const list = doc.createElement("div");
  list.className = "ai-limits-list";
  root.append(toolbar, list);

  const collapsedGroups = new Set();
  for (const group of SERVICE_GROUPS) {
    try {
      if (globalThis.localStorage?.getItem(`tv-ai-limits-group:${group.id}`) === "1") {
        collapsedGroups.add(group.id);
      }
    } catch { /* storage may be unavailable */ }
  }

  function setGroupCollapsed(groupId, collapsed) {
    if (collapsed) collapsedGroups.add(groupId);
    else collapsedGroups.delete(groupId);
    try {
      globalThis.localStorage?.setItem(`tv-ai-limits-group:${groupId}`, collapsed ? "1" : "0");
    } catch { /* ignore */ }
  }

  function renderGroup(group) {
    const section = doc.createElement("section");
    const isCollapsed = collapsedGroups.has(group.id);
    section.className = isCollapsed ? "ai-group collapsed" : "ai-group";
    section.dataset.group = group.id;

    const toggle = doc.createElement("button");
    toggle.type = "button";
    toggle.className = "ai-group-toggle";
    toggle.setAttribute("aria-expanded", String(!isCollapsed));

    const chevron = doc.createElement("span");
    chevron.className = "ai-group-chevron";
    chevron.setAttribute("aria-hidden", "true");

    const title = doc.createElement("span");
    title.className = "ai-group-title";
    title.textContent = group.title;

    const count = doc.createElement("span");
    count.className = "ai-group-count";
    count.textContent = String(group.services.length);

    toggle.append(chevron, title, count);
    toggle.addEventListener("click", () => {
      const next = !section.classList.contains("collapsed");
      section.classList.toggle("collapsed", next);
      toggle.setAttribute("aria-expanded", String(!next));
      setGroupCollapsed(group.id, next);
    });

    const body = doc.createElement("div");
    body.className = "ai-group-body";
    body.append(...group.services.map(renderCard));

    section.append(toggle, body);
    return section;
  }

  function renderState(state) {
    const services = sortServices(state?.services ?? []);
    const groups = groupServices(services);
    stamp.textContent = state?.checkedAt
      ? `Проверено: ${new Date(state.checkedAt).toLocaleTimeString()}`
      : "";
    list.replaceChildren(...groups.map(renderGroup));
    const available = services.filter(s => serviceGroupId(s) === "available").length;
    panel.setBadge(String(available));
  }

  function renderCard(service) {
    const card = doc.createElement("section");
    card.className = "ai-card";

    const head = doc.createElement("div");
    head.className = "ai-card-head";
    const title = doc.createElement("div");
    title.className = "ai-card-title";
    const dot = doc.createElement("span");
    dot.className = `ai-dot ${serviceTone(service)}`;
    title.append(dot, doc.createTextNode(service.name));
    head.append(title);

    const meta = doc.createElement("div");
    meta.className = "ai-meta";
    const parts = [];
    if (!service.installed) parts.push("не установлен");
    else if (service.authenticated && service.authOk === false) parts.push("авторизация истекла");
    else if (!service.authenticated) parts.push("нет авторизации");
    else if (service.account) parts.push(service.account);
    if (service.detail) parts.push(service.detail);
    meta.textContent = parts.join(" · ");
    if (service.authOk === false || service.limitsStale) meta.classList.add("ai-stale");

    card.append(head, meta);

    for (const limit of activeLimits(service)) {
      const row = doc.createElement("div");
      row.className = percentClass(limit.usedPercent);
      const label = doc.createElement("div");
      label.className = "ai-limit-label";
      const left = doc.createElement("span");
      left.textContent = limit.title;
      const right = doc.createElement("span");
      const pct = limit.usedPercent != null ? `${Math.round(limit.usedPercent)}%` : "";
      const reset = formatReset(limit.resetsAt);
      right.textContent = [pct, reset].filter(Boolean).join(" · ");
      label.append(left, right);

      const bar = doc.createElement("div");
      bar.className = "ai-bar";
      const fill = doc.createElement("i");
      fill.style.width = `${Math.max(0, Math.min(100, limit.usedPercent ?? 0))}%`;
      bar.append(fill);
      row.append(label, bar);
      card.append(row);
    }
    return card;
  }

  async function load() {
    try {
      renderState(await api.host.invoke("getState"));
    } catch (error) {
      if (!api.signal.aborted) api.ui.showNotification(error.message);
    }
  }

  refresh.addEventListener("click", () => {
    refresh.disabled = true;
    api.host.invoke("refresh")
      .then(renderState)
      .catch(error => { if (!api.signal.aborted) api.ui.showNotification(error.message); })
      .finally(() => { refresh.disabled = false; });
  }, { signal: api.signal });

  api.host.on("changed", renderState);
  api.commands.register("refresh", "Обновить лимиты AI", () => api.host.invoke("refresh").then(renderState));
  await load();
}
