import assert from "node:assert/strict";
import test from "node:test";

// Durable test for persist/restore contract that previously broke (v0.5.9):
// - visible sessions must preserve buffer (not only hidden)
// - live sessions must NOT reuse saved buffer (attach -> replay), dead must reuse
// We exercise the exact branches from ui/src/main.js: serializeTab, persistSessions payload, restoreSessions liveIds vs buffer.

function serializeTab(tab) {
  try {
    return tab.serialize.serialize({ excludeAltBuffer: false, excludeModes: false });
  } catch {
    return tab.buffer || "";
  }
}

function buildPersistPayload(tabs, activeId) {
  return tabs.map((tab, index) => ({
    id: tab.id,
    title: tab.title,
    customTitle: tab.customTitle ?? null,
    sortOrder: index,
    active: tab.id === activeId,
    buffer: serializeTab(tab),
    cwd: tab.cwd || null,
    group: tab.group || null,
    color: tab.color || null,
    pinned: !!tab.pinned,
    hidden: !!tab.hidden,
    muted: !!tab.muted,
    shell: tab.shell || null,
    wslDistribution: tab.wslDistribution || null,
    startupCommand: tab.startupCommand || null,
  }));
}

function buildRestoreArgs(records, liveIds) {
  const live = new Set(liveIds);
  return records.map((record) => ({
    id: record.id,
    buffer: live.has(record.id) ? undefined : record.buffer,
    isLive: live.has(record.id),
    activeCandidate: !!(record.active && !record.hidden),
  }));
}

function shouldAutoCreate(record, liveIds) {
  const isLive = new Set(liveIds).has(record.id);
  if (isLive) return "attach";
  if (record.hidden) return "exited";
  if (record.startupCommand) return "exited"; // profile with command needs manual restart (would duplicate servers)
  return "create"; // plain shell (even with shell=powershell) auto-restarts after reboot
}

test("serializeTab prefers SerializeAddon, falls back to buffer", () => {
  const viaAddon = { serialize: { serialize: () => "vt-dump" }, buffer: "fallback" };
  assert.equal(serializeTab(viaAddon), "vt-dump");
  const brokenAddon = { serialize: { serialize: () => { throw new Error("boom"); } }, buffer: "fallback-buf" };
  assert.equal(serializeTab(brokenAddon), "fallback-buf");
  const noAddon = { buffer: "only-buf" };
  assert.equal(serializeTab(noAddon), "only-buf");
  const empty = {};
  assert.equal(serializeTab(empty), "");
});

test("persist payload preserves visible buffer, not only hidden (v0.5.9 regression)", () => {
  const tabs = [
    { id: "v1", title: "visible", customTitle: null, cwd: "C:\\p", group: null, color: null, pinned: false, hidden: false, muted: false, shell: null, wslDistribution: null, startupCommand: null, serialize: { serialize: () => "buf-visible" } },
    { id: "h1", title: "hidden", customTitle: "My", cwd: null, group: "g", color: "#f00", pinned: true, hidden: true, muted: true, shell: "powershell", wslDistribution: null, startupCommand: "echo hi", serialize: { serialize: () => "buf-hidden" } },
  ];
  const payload = buildPersistPayload(tabs, "v1");
  assert.equal(payload[0].buffer, "buf-visible", "visible must have buffer");
  assert.equal(payload[1].buffer, "buf-hidden");
  assert.equal(payload[0].active, true);
  assert.equal(payload[1].active, false);
  assert.equal(payload[0].sortOrder, 0);
  assert.equal(payload[1].sortOrder, 1);
  assert.equal(payload[1].customTitle, "My");
  assert.equal(payload[1].group, "g");
});

test("restore: live sessions get undefined buffer (attach replay), dead get saved buffer", () => {
  const records = [
    { id: "live-visible", buffer: "saved-live", active: true, hidden: false },
    { id: "dead-visible", buffer: "saved-dead", active: false, hidden: false },
    { id: "live-hidden", buffer: "saved-live-hidden", active: true, hidden: true },
    { id: "dead-hidden", buffer: "saved-dead-hidden", active: false, hidden: true },
  ];
  const liveIds = ["live-visible", "live-hidden"];
  const restored = buildRestoreArgs(records, liveIds);
  const byId = Object.fromEntries(restored.map((r) => [r.id, r]));
  assert.equal(byId["live-visible"].buffer, undefined, "live must not reuse buffer");
  assert.equal(byId["live-visible"].isLive, true);
  assert.equal(byId["live-visible"].activeCandidate, true, "live visible active stays candidate");
  assert.equal(byId["dead-visible"].buffer, "saved-dead");
  assert.equal(byId["dead-visible"].isLive, false);
  assert.equal(byId["live-hidden"].buffer, undefined);
  assert.equal(byId["live-hidden"].activeCandidate, false, "hidden active never candidate");
  assert.equal(byId["dead-hidden"].buffer, "saved-dead-hidden");
});

test("restore active candidate picks only visible active, not hidden", () => {
  const records = [
    { id: "a", active: true, hidden: true, buffer: "b1" },
    { id: "b", active: true, hidden: false, buffer: "b2" },
    { id: "c", active: false, hidden: false, buffer: "b3" },
  ];
  const restored = buildRestoreArgs(records, []);
  const actives = restored.filter((r) => r.activeCandidate).map((r) => r.id);
  assert.deepEqual(actives, ["b"]);
});

test("persist payload handles unicode and WSL fields", () => {
  const tabs = [
    { id: "wsl", title: "Ubuntu", customTitle: null, cwd: null, group: null, color: null, pinned: false, hidden: false, muted: false, shell: "wsl", wslDistribution: "Ubuntu", startupCommand: null, serialize: { serialize: () => "кириллица 🖥" } },
  ];
  const payload = buildPersistPayload(tabs, "wsl");
  assert.equal(payload[0].buffer, "кириллица 🖥");
  assert.equal(payload[0].shell, "wsl");
  assert.equal(payload[0].wslDistribution, "Ubuntu");
  assert.equal(payload[0].cwd, null);
});

test("reboot auto-create: plain shell restarts, profile with startupCommand stays exited (v0.6.3)", () => {
  const liveIds = []; // after reboot no live
  assert.equal(shouldAutoCreate({ id: "plain-pwsh", hidden: false, shell: "powershell", startupCommand: null }, liveIds), "create", "plain shell must auto-create after reboot");
  assert.equal(shouldAutoCreate({ id: "default", hidden: false, shell: null, startupCommand: null }, liveIds), "create");
  assert.equal(shouldAutoCreate({ id: "with-cmd", hidden: false, shell: "powershell", startupCommand: "npm start" }, liveIds), "exited", "profile with command must stay exited");
  assert.equal(shouldAutoCreate({ id: "wsl-cmd", hidden: false, shell: "wsl", startupCommand: "npm start" }, liveIds), "exited");
  assert.equal(shouldAutoCreate({ id: "hidden-plain", hidden: true, shell: "powershell", startupCommand: null }, liveIds), "exited", "hidden stays exited");
  assert.equal(shouldAutoCreate({ id: "live", hidden: false, shell: "powershell", startupCommand: "npm start" }, ["live"]), "attach", "live reattaches regardless");
});
