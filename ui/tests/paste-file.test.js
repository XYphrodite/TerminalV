import assert from "node:assert/strict";
import test from "node:test";
import { PASTE_FILE_THRESHOLD, PASTE_FILE_STORE_TIMEOUT_MS, shouldStoreAsFile } from "../src/paste-file.js";

test("threshold is a sane positive value with a bounded store timeout", () => {
  assert.ok(PASTE_FILE_THRESHOLD > 0);
  assert.ok(PASTE_FILE_STORE_TIMEOUT_MS >= 1000);
});

test("small and borderline pastes stay inline", () => {
  assert.equal(shouldStoreAsFile(""), false);
  assert.equal(shouldStoreAsFile("a".repeat(100)), false);
  assert.equal(shouldStoreAsFile("a".repeat(PASTE_FILE_THRESHOLD)), false);
});

test("large pastes go to file", () => {
  assert.equal(shouldStoreAsFile("a".repeat(PASTE_FILE_THRESHOLD + 1)), true);
  // Typical slow case: tens of KB into a slice-digesting TUI
  assert.equal(shouldStoreAsFile("x".repeat(26886)), true);
  assert.equal(shouldStoreAsFile("x".repeat(157767)), true);
});

test("non-string input never triggers file store", () => {
  assert.equal(shouldStoreAsFile(null), false);
  assert.equal(shouldStoreAsFile(undefined), false);
  assert.equal(shouldStoreAsFile(12345), false);
});
