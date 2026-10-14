import assert from "node:assert/strict";
import test from "node:test";
import { downloadPercent, updateProgressView } from "../src/update-progress.js";

const texts = {
  download: "Скачивание обновления…",
  downloadPercent: "Скачивание обновления… {percent}%",
  verifying: "Проверка обновления…"
};

test("downloadPercent needs both a non-negative received and a positive total", () => {
  assert.equal(downloadPercent(5, 10), 50);
  assert.equal(downloadPercent(0, 10), 0);
  assert.equal(downloadPercent(20, 10), 100, "clamped to 100");
  assert.equal(downloadPercent(5, 0), null);
  assert.equal(downloadPercent(5, null), null);
  assert.equal(downloadPercent(-1, 10), null);
  assert.equal(downloadPercent(undefined, 10), null);
});

test("downloading without byte counts is indeterminate with the plain label", () => {
  const view = updateProgressView({ status: "downloading" }, texts);
  assert.equal(view.bar, "indeterminate");
  assert.equal(view.text, texts.download);
});

test("downloading with byte counts shows a percentage label and fill", () => {
  const view = updateProgressView({ status: "downloading", received: 30, total: 120 }, texts);
  assert.equal(view.bar, "percent");
  assert.equal(view.percent, 25);
  assert.equal(view.text, "Скачивание обновления… 25%");
});

test("download without a usable total falls back to indeterminate", () => {
  const view = updateProgressView({ status: "downloading", received: 30 }, texts);
  assert.equal(view.bar, "indeterminate");
});

test("verifying keeps the bar full and swaps the label", () => {
  const view = updateProgressView({ status: "verifying" }, texts);
  assert.equal(view.bar, "full");
  assert.equal(view.percent, 100);
  assert.equal(view.text, texts.verifying);
});

test("unrelated statuses are not progress views", () => {
  for (const status of ["available", "restarting", "installing", "current", "error", "unsupported"]) {
    assert.equal(updateProgressView({ status }, texts), null, status);
  }
  assert.equal(updateProgressView(null, texts), null);
  assert.equal(updateProgressView({}, texts), null);
});
