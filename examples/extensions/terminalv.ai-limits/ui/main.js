function formatReset(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const diff = date.getTime() - Date.now();
  const abs = Math.abs(diff);
  const h = Math.floor(abs / 3600000);
  const m = Math.floor((abs % 3600000) / 60000);
  const label = h > 0 ? `${h}ч ${m}м` : `${m}м`;
  return diff >= 0 ? `через ${label}` : `просрочено ${label}`;
}

function percentClass(value) {
  if (value == null) return "";
  if (value >= 85) return "ai-limit danger";
  if (value >= 60) return "ai-limit warn";
  return "ai-limit";
}

function serviceTone(service) {
  if (!service.installed) return "missing";
  if (service.authenticated && service.authOk === false) return "warn";
  if (service.limits?.some(l => (l.usedPercent ?? 0) >= 85)) return "limited";
  if (service.limitsStale) return "warn";
  if (service.authenticated) return "ok";
  return "warn";
}

export async function activate(api) {
  const panel = api.ui.createSidebarPanel({ id: "ai-limits", title: "AI Limits" });
  const root = document.createElement("div");
  root.className = "ai-limits-root";
  panel.element.append(root);

  const style = document.createElement("style");
  style.textContent = `
    .ai-limits-root { display: grid; gap: 10px; font-size: 12px; }
    .ai-limits-toolbar { display: flex; gap: 6px; align-items: center; }
    .ai-limits-toolbar button {
      cursor: pointer; border: 1px solid var(--sidebar-edge); border-radius: 6px;
      background: transparent; color: inherit; padding: 4px 8px; font: inherit;
    }
    .ai-limits-list { display: grid; gap: 8px; }
    .ai-card {
      border: 1px solid var(--sidebar-edge); border-radius: 8px; padding: 8px;
      display: grid; gap: 6px;
    }
    .ai-card-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
    .ai-card-title { display: flex; align-items: center; gap: 6px; font-weight: 600; }
    .ai-dot { width: 8px; height: 8px; border-radius: 50%; background: var(--text); opacity: 0.35; }
    .ai-dot.ok { background: #3ecf8e; opacity: 1; }
    .ai-dot.warn { background: #e2b93b; opacity: 1; }
    .ai-dot.limited { background: #e26d3b; opacity: 1; }
    .ai-dot.missing { background: var(--danger, #e25555); opacity: 1; }
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

  const toolbar = document.createElement("div");
  toolbar.className = "ai-limits-toolbar";
  const refresh = document.createElement("button");
  refresh.type = "button";
  refresh.textContent = "Обновить";
  const stamp = document.createElement("span");
  stamp.className = "ai-meta";
  toolbar.append(refresh, stamp);

  const list = document.createElement("div");
  list.className = "ai-limits-list";
  root.append(toolbar, list);

  function renderState(state) {
    const services = state?.services ?? [];
    stamp.textContent = state?.checkedAt
      ? `Проверено: ${new Date(state.checkedAt).toLocaleTimeString()}`
      : "";
    list.replaceChildren(...services.map(renderCard));
    const limited = services.filter(s => serviceTone(s) === "limited" || serviceTone(s) === "missing").length;
    panel.setBadge(limited > 0 ? String(limited) : "");
  }

  function renderCard(service) {
    const card = document.createElement("section");
    card.className = "ai-card";

    const head = document.createElement("div");
    head.className = "ai-card-head";
    const title = document.createElement("div");
    title.className = "ai-card-title";
    const dot = document.createElement("span");
    dot.className = `ai-dot ${serviceTone(service)}`;
    title.append(dot, document.createTextNode(service.name));
    head.append(title);

    const meta = document.createElement("div");
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

    for (const limit of service.limits ?? []) {
      const row = document.createElement("div");
      row.className = percentClass(limit.usedPercent);
      const label = document.createElement("div");
      label.className = "ai-limit-label";
      const left = document.createElement("span");
      left.textContent = limit.title;
      const right = document.createElement("span");
      const pct = limit.usedPercent != null ? `${Math.round(limit.usedPercent)}%` : "";
      const reset = formatReset(limit.resetsAt);
      right.textContent = [pct, reset].filter(Boolean).join(" · ");
      label.append(left, right);

      const bar = document.createElement("div");
      bar.className = "ai-bar";
      const fill = document.createElement("i");
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
