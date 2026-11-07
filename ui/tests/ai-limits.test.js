import assert from "node:assert/strict";
import test from "node:test";
import { sortServices, servicePriority } from "../../examples/extensions/terminalv.ai-limits/ui/main.js";

test("ai-limits places active subscriptions first ahead of expired, unauthenticated and uninstalled services", () => {
  const future = new Date(Date.now() + 3600_000).toISOString();
  const input = [
    { id: "claude", name: "Claude", installed: true, authenticated: true, authOk: false, limits: [] },
    { id: "codex", name: "Codex", installed: true, authenticated: true, authOk: false, limits: [] },
    { id: "kimi", name: "Kimi", installed: false, authenticated: false, authOk: false, limits: [] },
    { id: "grok", name: "Grok", installed: true, authenticated: true, authOk: true, limits: [{ id: "w", title: "7 дней", usedPercent: 41, resetsAt: future }] },
    { id: "mimo", name: "MiMo", installed: true, authenticated: false, authOk: false, limits: [] },
    { id: "muse", name: "Muse", installed: true, authenticated: true, authOk: true, limits: [] },
    { id: "agy", name: "agy", installed: true, authenticated: true, authOk: true, limits: [{ id: "5h", title: "5 часов", usedPercent: 18, resetsAt: future }] },
    { id: "stale", name: "Stale", installed: true, authenticated: true, authOk: true, limitsStale: true, limits: [] }
  ];

  const sorted = sortServices(input);
  assert.deepEqual(
    sorted.map(s => s.id),
    ["grok", "agy", "muse", "stale", "claude", "codex", "mimo", "kimi"]
  );
  assert.equal(servicePriority(input[3]), 0, "active subscription with live quota is top priority");
  assert.equal(servicePriority(input[5]), 1, "active auth without quota bars follows live quota subscriptions");
  assert.equal(servicePriority(input[7]), 2, "stale cache follows live active services");
  assert.equal(servicePriority(input[0]), 3, "expired auth follows active services");
  assert.equal(servicePriority(input[4]), 4, "unauthenticated follows expired");
  assert.equal(servicePriority(input[2]), 5, "not installed is last");
});
