// Mouse tracking in TUIs (hermes --tui, mimo, codex) breaks text copying:
//
// 1. xterm disables its selection service while the app tracks the mouse, so a
//    plain drag never selects anything (only Shift+drag, xterm's force-select).
// 2. xterm clears the selection on `coreService.onUserInput`, and mouse reports
//    count as user input. Any-motion tracking (1003h) reports on every hover
//    move and wheel notch, so even a Shift+drag selection dies before Ctrl+C —
//    and the copy shortcut then falls through to the app as ^C.
//
// This module restores "drag selects, click clicks" on top of xterm:
//  - mouse reports are pointer noise, not typing, so they keep the selection;
//  - a plain button-0 gesture is taken away from xterm before it sees it: a tap
//    is replayed to xterm so the app gets exactly the press/release reports it
//    got before (Approve buttons keep working), and a drag becomes an xterm
//    force-selection that the app never sees.
// Modified gestures (Shift/Ctrl/Alt/Meta) and non-mouse pointers fall through
// to xterm untouched.

import { claimLiveScreenInput } from "./terminal-viewport.js";

const DRAG_THRESHOLD_PX = 5;
const MULTICLICK_MS = 400;
const MULTICLICK_PX = 4;
const SGR_MOUSE_REPORT = /^\x1b\[<\d+;\d+;\d+[Mm]$/;

function isMouseReport(data) {
  return typeof data === "string" && data.length > 0 &&
    (data.startsWith("\x1b[M") || SGR_MOUSE_REPORT.test(data));
}

// Kept isolated with the other xterm private-API workaround (terminal-viewport.js)
// until xterm distinguishes mouse reports from keyboard input itself.
export function preserveSelectionOnMouseReports(term) {
  try {
    const core = term._core?.coreService;
    if (!core || core.triggerDataEvent.__selectionGuard) return;
    const triggerDataEvent = core.triggerDataEvent;
    const guarded = function (data, wasUserInput = false) {
      return triggerDataEvent.call(this, data, wasUserInput && !isMouseReport(data));
    };
    guarded.__selectionGuard = true;
    core.triggerDataEvent = guarded;
  } catch {
    // Private API drift: without the guard xterm behaves as before.
  }
}

export function attachTuiMouse(tab) {
  const { term, host } = tab;
  let gesture = null;
  let press = null;

  function synthetic(type, x, y, init = {}) {
    const event = new MouseEvent(type, {
      bubbles: true,
      cancelable: true,
      view: window,
      button: 0,
      clientX: x,
      clientY: y,
      detail: init.detail ?? 1,
      buttons: init.buttons ?? (type === "mouseup" ? 0 : 1),
      shiftKey: Boolean(init.shiftKey)
    });
    event.__tuiSynthetic = true;
    return event;
  }

  // Next click count at (x, y): 2 and 3 give xterm's word/line selection.
  function nextDetail(x, y) {
    const now = performance.now();
    const detail = press && now - press.time < MULTICLICK_MS &&
      Math.abs(x - press.x) <= MULTICLICK_PX && Math.abs(y - press.y) <= MULTICLICK_PX
      ? (press.detail % 3) + 1
      : 1;
    press = { time: now, x, y, detail };
    return detail;
  }

  function onPointerMove(event) {
    if (event.__tuiSynthetic || !gesture) return;
    const { x0, y0 } = gesture;
    if (!gesture.dragging &&
      Math.max(Math.abs(event.clientX - x0), Math.abs(event.clientY - y0)) > DRAG_THRESHOLD_PX) {
      gesture.dragging = true;
      // A forced (Shift) press starts xterm's own selection machinery: word and
      // line modes on multi-click, drag-scroll past the viewport edge.
      term.element.dispatchEvent(synthetic("mousedown", x0, y0, { shiftKey: true, detail: gesture.detail }));
    }
    if (gesture.dragging) {
      term.element.dispatchEvent(synthetic("mousemove", event.clientX, event.clientY, { shiftKey: true, buttons: 1 }));
    }
  }

  // Compatibility mousemove/mouseup still fire around a canceled pointerdown
  // and would leak hover/drag reports to the app mid-selection.
  function onCompatMove(event) {
    if (event.__tuiSynthetic || !gesture) return;
    event.stopPropagation();
  }

  function onCompatUp(event) {
    if (event.__tuiSynthetic) return;
    event.stopPropagation();
    event.preventDefault();
  }

  function onUp(event) {
    if (event.__tuiSynthetic || !gesture) return;
    const current = gesture;
    gesture = null;
    if (current.dragging) {
      term.element.dispatchEvent(synthetic("mouseup", event.clientX, event.clientY, { shiftKey: true }));
    } else {
      // A tap is a click: the app must see exactly the press/release pair it saw
      // before this interception existed.
      if (term.hasSelection()) term.clearSelection();
      term.element.dispatchEvent(synthetic("mousedown", current.x0, current.y0, { detail: current.detail }));
      term.element.dispatchEvent(synthetic("mouseup", event.clientX, event.clientY, { detail: current.detail, buttons: 0 }));
    }
    // Detach on the next task: the browser may still dispatch the compatibility
    // mouseup/click of this gesture in the same turn, and the replayed press has
    // armed xterm's own mouseup listener.
    setTimeout(disarm, 0);
  }

  function onCancel(event) {
    if (event.__tuiSynthetic || !gesture) return;
    const current = gesture;
    gesture = null;
    if (current.dragging) {
      term.element.dispatchEvent(synthetic("mouseup", current.x0, current.y0, { shiftKey: true }));
    }
    setTimeout(disarm, 0);
  }

  function disarm() {
    window.removeEventListener("pointermove", onPointerMove, true);
    window.removeEventListener("mousemove", onCompatMove, true);
    window.removeEventListener("pointerup", onUp, true);
    window.removeEventListener("mouseup", onCompatUp, true);
    window.removeEventListener("pointercancel", onCancel, true);
    window.removeEventListener("click", onCompatUp, true);
  }

  function arm() {
    window.addEventListener("pointermove", onPointerMove, true);
    window.addEventListener("mousemove", onCompatMove, true);
    window.addEventListener("pointerup", onUp, true);
    window.addEventListener("mouseup", onCompatUp, true);
    window.addEventListener("pointercancel", onCancel, true);
    window.addEventListener("click", onCompatUp, true);
  }

  host.addEventListener("pointerdown", (event) => {
    if (event.button !== 0 && event.button !== 1) return;
    // Mouse reports are screen-relative: a press on scrolled-back history must
    // snap to the live screen first (and that press is dropped).
    if (claimLiveScreenInput(term, host)) {
      event.preventDefault();
      event.stopPropagation();
      return;
    }
    if (event.button !== 0 || event.__tuiSynthetic) return;
    if (event.pointerType && event.pointerType !== "mouse") return;
    if (event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return;
    // Without tracking xterm selects natively; nothing to intercept.
    if (term.modes.mouseTrackingMode === "none") return;
    // Cancelling pointerdown suppresses the compatibility mousedown, so xterm
    // and the app see nothing of this gesture until it is replayed on tap.
    event.preventDefault();
    term.focus();
    gesture = { x0: event.clientX, y0: event.clientY, detail: nextDetail(event.clientX, event.clientY), dragging: false };
    arm();
  }, true);
}
