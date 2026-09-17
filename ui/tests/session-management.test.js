import assert from "node:assert/strict";
import test from "node:test";
import { sessionMetadata, sessionGroups, insertAfter } from "../src/session-management.js";

test("old records default to visible and ungrouped; invalid colors cannot inject styles", () => {
  assert.deepEqual(sessionMetadata({}), { group: "", color: "", pinned: false, hidden: false, muted: false });
  assert.equal(sessionMetadata({ color: "__proto__", group: "  Проект  " }).color, "");
  assert.equal(sessionMetadata({ color: "blue", group: "  Проект  " }).group, "Проект");
  assert.equal(sessionMetadata({ group: "Я".repeat(100) }).group.length, 80);
});

test("groups and pins have stable order, without modifying the persisted array", () => {
  const records = [
    { id: "a", group: "Проект" }, { id: "b", group: "Логи" },
    { id: "c", group: "Проект", pinned: true }, { id: "d", hidden: true }
  ];
  assert.deepEqual(sessionGroups(records).map((group) => [group.name, group.tabs.map((tab) => tab.id)]),
    [["Проект", ["c", "a"]], ["Логи", ["b"]]]);
  assert.deepEqual(records.map((tab) => tab.id), ["a", "b", "c", "d"]);
  assert.deepEqual(sessionGroups(records, { hidden: true })[0].tabs.map((tab) => tab.id), ["d"]);
});

test("session search matches Cyrillic titles and groups literally, including hidden sessions", () => {
  const records = [
    { title: "Вывод", customTitle: "Сборка [dev]", group: "Проект" },
    { title: "Логи", hidden: true, group: "ТЕСТ" }
  ];
  assert.equal(sessionGroups(records, { query: "сБОРКА [" })[0].tabs[0], records[0]);
  assert.equal(sessionGroups(records, { query: "проект" })[0].tabs[0], records[0]);
  assert.equal(sessionGroups(records, { query: "Вывод" }).length, 0);
  assert.equal(sessionGroups(records, { hidden: true, query: "тест" })[0].tabs[0], records[1]);
});

test("split tab is inserted adjacent to parent, not appended last", () => {
  const tabs = [{ id: "a" }, { id: "b" }, { id: "c" }, { id: "d" }];
  insertAfter(tabs, { id: "x" }, "b");
  assert.deepEqual(tabs.map(t => t.id), ["a", "b", "x", "c", "d"]);
  // parent at end → still adjacent (becomes last, not second-last)
  const tabs2 = [{ id: "a" }, { id: "b" }];
  insertAfter(tabs2, { id: "y" }, "b");
  assert.deepEqual(tabs2.map(t => t.id), ["a", "b", "y"]);
  // parent in middle with different group — still adjacent in raw order
  const tabs3 = [{ id: "a", group: "G1" }, { id: "b", group: "G1" }, { id: "c", group: "G2" }];
  insertAfter(tabs3, { id: "x", group: "G1" }, "a");
  assert.deepEqual(tabs3.map(t => t.id), ["a", "x", "b", "c"]);
  assert.deepEqual(sessionGroups(tabs3).find(g => g.name === "G1").tabs.map(t => t.id), ["a", "x", "b"]);
  // missing parent → append
  const tabs4 = [{ id: "a" }, { id: "b" }];
  insertAfter(tabs4, { id: "z" }, "missing");
  assert.deepEqual(tabs4.map(t => t.id), ["a", "b", "z"]);
  // no parent → append (normal new tab)
  const tabs5 = [{ id: "a" }];
  insertAfter(tabs5, { id: "n" }, null);
  assert.deepEqual(tabs5.map(t => t.id), ["a", "n"]);
  insertAfter(tabs5, { id: "m" }, undefined);
  assert.deepEqual(tabs5.map(t => t.id), ["a", "n", "m"]);
});
