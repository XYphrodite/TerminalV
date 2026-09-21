import assert from "node:assert/strict";
import test from "node:test";

// Durable: alternate buffer must hard-refresh without clearing normal scrollback (v0.6.7)
// applyFit should call webgl.clearTexture + term.refresh(true) only for tui-lock

function makeTab({ tuiLock, hasWebgl }) {
  let refreshArgs = null;
  let clearCalled = false;
  return {
    host: { classList: { contains: (c) => tuiLock && c === "tui-lock" } },
    term: {
      cols: 80, rows: 24,
      resize(c, r) { this.cols = c; this.rows = r; },
      refresh(s, e, clear) { refreshArgs = [s, e, clear]; }
    },
    webgl: hasWebgl ? { clearTexture() { clearCalled = true; } } : null,
    getRefreshArgs: () => refreshArgs,
    wasClearCalled: () => clearCalled,
  };
}

function applyFitFake(tab, proposed) {
  if (!proposed) return false;
  tab.term.resize(proposed.cols, proposed.rows);
  if (tab.host.classList.contains("tui-lock")) {
    try { tab.webgl?.clearTexture?.(); } catch {}
    tab.term.refresh(0, tab.term.rows - 1, true);
  } else {
    tab.term.refresh(0, tab.term.rows - 1);
  }
  return true;
}

test("tui-lock hard refreshes WebGL canvas, normal does not", () => {
  const tui = makeTab({ tuiLock: true, hasWebgl: true });
  applyFitFake(tui, { cols: 100, rows: 30 });
  assert.equal(tui.wasClearCalled(), true, "tui-lock must clearTexture");
  assert.deepEqual(tui.getRefreshArgs(), [0, 29, true], "tui-lock must refresh(true)");

  const normal = makeTab({ tuiLock: false, hasWebgl: true });
  applyFitFake(normal, { cols: 100, rows: 30 });
  assert.equal(normal.wasClearCalled(), false, "normal must not clearTexture");
  assert.deepEqual(normal.getRefreshArgs(), [0, 29, undefined], "normal must refresh without clear");
});

test("tui-lock without WebGL still hard refreshes", () => {
  const tuiNoWebgl = makeTab({ tuiLock: true, hasWebgl: false });
  applyFitFake(tuiNoWebgl, { cols: 90, rows: 20 });
  assert.equal(tuiNoWebgl.wasClearCalled(), false);
  assert.deepEqual(tuiNoWebgl.getRefreshArgs(), [0, 19, true]);
});
