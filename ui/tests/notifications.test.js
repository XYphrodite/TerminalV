import assert from "node:assert/strict";
import test from "node:test";
import { createNotifications } from "../src/notifications.js";

function setup() {
  const a = {}, b = {};
  const known = new Set([a, b]);
  const sounds = [], changes = [];
  let active = null, clock = 0;
  const controller = createNotifications({
    isKnown: (tab) => known.has(tab), isActive: (tab) => tab === active,
    sound: (tab) => sounds.push(tab), changed: (tab) => changes.push(tab), now: () => clock
  });
  return { a, b, known, sounds, changes, controller,
    active: (tab) => { active = tab; }, time: (ms) => { clock = ms; } };
}

test("active, exited and removed sessions cannot notify", () => {
  const s = setup();
  s.active(s.a);
  s.controller.bell(s.a);
  s.b.exited = true;
  s.controller.bell(s.b);
  s.controller.bell({});
  assert.equal(s.sounds.length, 0);
  assert.equal(s.changes.length, 0);
});

test("muted sessions still show attention, without consuming another session's cooldown", () => {
  const s = setup();
  s.a.muted = true;
  s.controller.bell(s.a);
  s.controller.bell(s.b);
  assert.equal(s.a.attention, true);
  assert.deepEqual(s.sounds, [s.b]);
  s.controller.acknowledge(s.a);
  assert.equal(s.a.attention, false);
});

test("a burst produces one visual update and one sound, with global and per-session limits", () => {
  const s = setup();
  for (let i = 0; i < 1000; i++) s.controller.bell(s.a);
  assert.equal(s.changes.length, 1);
  assert.deepEqual(s.sounds, [s.a]);
  s.time(1999); s.controller.bell(s.b);
  assert.equal(s.b.attention, true);
  assert.equal(s.sounds.length, 1);
  s.time(2000); s.controller.bell(s.b);
  s.time(4000); s.controller.bell(s.a); // still within its 5-second interval
  assert.deepEqual(s.sounds, [s.a, s.b]);
  s.time(5000); s.controller.bell(s.a);
  assert.deepEqual(s.sounds, [s.a, s.b, s.a]);
});

test("opening a tab clears only its attention and does not reset sound throttling", () => {
  const s = setup();
  s.controller.bell(s.a);
  s.controller.bell(s.b);
  s.controller.acknowledge(s.a);
  s.controller.acknowledge(s.a);
  assert.equal(s.a.attention, false);
  assert.equal(s.b.attention, true);
  s.time(2500); s.controller.bell(s.a);
  assert.equal(s.a.attention, true);
  assert.equal(s.sounds.length, 1);
  s.known.delete(s.b);
  s.time(8000); s.controller.bell(s.b);
  assert.equal(s.sounds.length, 1);
});
