import assert from "node:assert/strict";
import test from "node:test";
import { sessionMetadata, sessionGroups } from "../src/session-management.js";

test("old records default to visible and ungrouped; invalid colors cannot inject styles", () => {
  assert.deepEqual(sessionMetadata({}), { group: "", color: "", pinned: false, hidden: false });
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
