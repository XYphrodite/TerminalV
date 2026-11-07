import assert from "node:assert/strict";
import test from "node:test";
import {
  SERVICE_GROUPS,
  activate,
  groupServices,
  serviceGroupId,
  servicePriority,
  sortServices
} from "../../examples/extensions/terminalv.ai-limits/ui/main.js";

test("ai-limits orders exhausted Grok above stale Kimi across the 5 canonical groups", () => {
  const future = new Date(Date.now() + 3600_000).toISOString();
  const input = [
    { id: "claude", name: "Claude", installed: true, authenticated: true, authOk: false, limits: [] },
    { id: "codex", name: "Codex", installed: true, authenticated: true, authOk: false, limits: [] },
    { id: "grok", name: "Grok", installed: true, authenticated: true, authOk: true, limits: [{ id: "w", title: "7 дней", usedPercent: 100, resetsAt: future }] },
    { id: "agy", name: "agy", installed: true, authenticated: true, authOk: true, limits: [{ id: "5h", title: "5 часов", usedPercent: 57, resetsAt: future }] },
    { id: "mimo", name: "MiMo", installed: true, authenticated: false, authOk: false, limits: [] },
    { id: "kimi", name: "Kimi", installed: true, authenticated: true, authOk: true, limitsStale: true, limits: [] },
    { id: "muse", name: "Muse", installed: true, authenticated: true, authOk: true, limits: [] },
    { id: "missing-cli", name: "Missing", installed: false, authenticated: false, authOk: false, limits: [] }
  ];

  const sorted = sortServices(input);
  assert.deepEqual(
    sorted.map(s => s.id),
    ["agy", "muse", "grok", "kimi", "claude", "codex", "mimo", "missing-cli"]
  );

  assert.equal(servicePriority(input[3]), 0, "usable quota (<100%) is priority 0");
  assert.equal(servicePriority(input[6]), 1, "active without quota bars is priority 1");
  assert.equal(servicePriority(input[2]), 2, "exhausted (100%) is priority 2 (above stale)");
  assert.equal(servicePriority(input[5]), 3, "stale cache is priority 3 (below exhausted)");
  assert.equal(servicePriority(input[0]), 4, "expired auth is priority 4");
  assert.equal(servicePriority(input[4]), 5, "unauthenticated is priority 5");
  assert.equal(servicePriority(input[7]), 6, "not installed is priority 6");

  assert.equal(serviceGroupId(input[3]), "available");
  assert.equal(serviceGroupId(input[6]), "available");
  assert.equal(serviceGroupId(input[2]), "exhausted");
  assert.equal(serviceGroupId(input[5]), "stale");
  assert.equal(serviceGroupId(input[0]), "unauth");
  assert.equal(serviceGroupId(input[4]), "unauth");
  assert.equal(serviceGroupId(input[7]), "missing");

  const groups = groupServices(input);
  assert.deepEqual(
    groups.map(g => ({ id: g.id, title: g.title, ids: g.services.map(s => s.id) })),
    [
      { id: "available", title: "Лимиты есть", ids: ["agy", "muse"] },
      { id: "exhausted", title: "Лимитов нет", ids: ["grok"] },
      { id: "stale", title: "Кэш устарел", ids: ["kimi"] },
      { id: "unauth", title: "Нет авторизации", ids: ["claude", "codex", "mimo"] },
      { id: "missing", title: "Не установлено", ids: ["missing-cli"] }
    ]
  );

  assert.deepEqual(
    SERVICE_GROUPS.map(g => g.title),
    ["Лимиты есть", "Лимитов нет", "Кэш устарел", "Нет авторизации", "Не установлено"]
  );
});

test("ai-limits renders collapsible groups and preserves collapsed state across updates", async () => {
  function makeNode(tag) {
    const classes = new Set();
    const attrs = new Map();
    const listeners = new Map();
    const node = {
      tagName: String(tag).toUpperCase(),
      textContent: "",
      disabled: false,
      type: "",
      dataset: {},
      style: {},
      children: [],
      get className() { return [...classes].join(" "); },
      set className(val) {
        classes.clear();
        for (const c of String(val ?? "").split(/\s+/).filter(Boolean)) classes.add(c);
      },
      classList: {
        add: (...items) => items.forEach(c => classes.add(c)),
        remove: (...items) => items.forEach(c => classes.delete(c)),
        contains: c => classes.has(c),
        toggle(c, force) {
          const next = force === undefined ? !classes.has(c) : Boolean(force);
          if (next) classes.add(c); else classes.delete(c);
          return next;
        }
      },
      setAttribute: (k, v) => attrs.set(k, String(v)),
      getAttribute: k => attrs.get(k) ?? null,
      addEventListener: (ev, fn) => {
        const list = listeners.get(ev) ?? [];
        list.push(fn);
        listeners.set(ev, list);
      },
      click: () => (listeners.get("click") ?? []).forEach(fn => fn()),
      append: (...items) => { node.children.push(...items); },
      replaceChildren: (...items) => { node.children = [...items]; }
    };
    return node;
  }

  const storage = new Map();
  const prevDoc = globalThis.document;
  const prevStorage = globalThis.localStorage;
  globalThis.document = {
    createElement: tag => makeNode(tag),
    createTextNode: text => ({ nodeType: 3, textContent: String(text) })
  };
  globalThis.localStorage = {
    getItem: k => storage.get(k) ?? null,
    setItem: (k, v) => storage.set(k, String(v))
  };

  try {
    const future = new Date(Date.now() + 3600_000).toISOString();
    const state = {
      checkedAt: new Date().toISOString(),
      services: [
        { id: "grok", name: "Grok", installed: true, authenticated: true, authOk: true, limits: [{ id: "w", title: "7 дней", usedPercent: 100, resetsAt: future }] },
        { id: "kimi", name: "Kimi", installed: true, authenticated: true, authOk: true, limitsStale: true, limits: [] },
        { id: "agy", name: "agy", installed: true, authenticated: true, authOk: true, limits: [{ id: "5h", title: "5 часов", usedPercent: 20, resetsAt: future }] },
        { id: "claude", name: "Claude", installed: true, authenticated: true, authOk: false, limits: [] }
      ]
    };

    const panelRoot = makeNode("div");
    let changedHandler = null;
    const api = {
      signal: new AbortController().signal,
      ui: {
        createSidebarPanel: () => ({ element: panelRoot, setBadge() {} }),
        showNotification() {}
      },
      host: {
        invoke: async () => state,
        on: (ev, fn) => { if (ev === "changed") changedHandler = fn; }
      },
      commands: { register() {} }
    };

    await activate(api);
    const root = panelRoot.children[0];
    const list = root.children[2];
    assert.equal(list.children.length, 4, "empty groups are omitted");
    assert.deepEqual(
      list.children.map(section => section.dataset.group),
      ["available", "exhausted", "stale", "unauth"]
    );

    const unauthSection = list.children[3];
    const unauthToggle = unauthSection.children[0];
    assert.equal(unauthSection.classList.contains("collapsed"), false);
    assert.equal(unauthToggle.getAttribute("aria-expanded"), "true");

    unauthToggle.click();
    assert.equal(unauthSection.classList.contains("collapsed"), true);
    assert.equal(unauthToggle.getAttribute("aria-expanded"), "false");
    assert.equal(storage.get("tv-ai-limits-group:unauth"), "1");

    // Trigger host state refresh: collapsed state must remain collapsed
    changedHandler(state);
    const refreshedUnauth = list.children[3];
    assert.equal(refreshedUnauth.classList.contains("collapsed"), true);
    assert.equal(refreshedUnauth.children[0].getAttribute("aria-expanded"), "false");
  } finally {
    globalThis.document = prevDoc;
    globalThis.localStorage = prevStorage;
  }
});
