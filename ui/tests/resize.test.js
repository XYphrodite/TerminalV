import assert from "node:assert/strict";
import test from "node:test";

// Reproduces: window resize should update terminal content without 80ms lag.
// Old scheduleFit used 80ms debounce; new uses rAF/16ms. This test ensures the
// coalesced fit happens within 30ms, not 80ms+.

function makeScheduler({ useRaf }) {
  let fitTimer = 0;
  let fitRaf = 0;
  let calls = 0;
  const fitVisible = () => { fitRaf = 0; fitTimer = 0; calls++; };
  function cancelFit() {
    if (fitRaf) { fitRaf = 0; }
    clearTimeout(fitTimer);
    fitTimer = 0;
  }
  function scheduleFitOld() {
    clearTimeout(fitTimer);
    fitTimer = setTimeout(fitVisible, 80);
  }
  function scheduleFitNew() {
    if (fitRaf || fitTimer) return;
    if (useRaf && typeof global.requestAnimationFrame === "function") {
      fitRaf = global.requestAnimationFrame(fitVisible);
    } else {
      fitTimer = setTimeout(fitVisible, 16);
    }
  }
  return { scheduleOld: scheduleFitOld, scheduleNew: scheduleFitNew, getCalls: () => calls, cancelFit };
}

test("window resize fits within one frame, not 80ms", async () => {
  // New scheduler should fire within 30ms
  const s = makeScheduler({ useRaf: false });
  const t0 = Date.now();
  s.scheduleNew();
  // rapid second call should be coalesced
  s.scheduleNew();
  s.scheduleNew();
  await new Promise((r) => setTimeout(r, 35));
  assert.equal(s.getCalls(), 1, "new scheduler must have fired within 35ms (rAF/16ms)");
  assert.ok(Date.now() - t0 < 60, "too slow");
  s.cancelFit();
  // Old scheduler would NOT have fired within 35ms (needs 80ms)
  const old = makeScheduler({ useRaf: false });
  old.scheduleOld();
  await new Promise((r) => setTimeout(r, 35));
  assert.equal(old.getCalls(), 0, "old 80ms debounce must not fire within 35ms");
  await new Promise((r) => setTimeout(r, 60));
  assert.equal(old.getCalls(), 1, "old must fire after 80ms");
  old.cancelFit();
});

test("rAF coalesces multiple resize events into one frame", async () => {
  // Simulate browser rAF with 16ms timeout
  const origRaf = global.requestAnimationFrame;
  const origCancel = global.cancelAnimationFrame;
  global.requestAnimationFrame = (cb) => setTimeout(cb, 16);
  global.cancelAnimationFrame = (id) => clearTimeout(id);
  const s = makeScheduler({ useRaf: true });
  s.scheduleNew();
  s.scheduleNew();
  s.scheduleNew();
  await new Promise((r) => setTimeout(r, 30));
  assert.equal(s.getCalls(), 1, "rAF must coalesce 3 schedules into 1 frame");
  s.cancelFit();
  global.requestAnimationFrame = origRaf;
  global.cancelAnimationFrame = origCancel;
});
