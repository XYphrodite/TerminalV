// xterm 5.5 measures a display:none viewport as zero pixels high while parsing
// background output. refresh() only redraws cells, and resize() with unchanged
// dimensions does nothing, leaving the scrollbar one screen too short.
// Keep this private-API workaround isolated until xterm exposes viewport sync.
export function syncTerminalViewport(term) {
  if (!term.element?.offsetParent) return;
  // Reconcile DOM geometry with the current buffer position. Do not scroll to
  // bottom: a user reading history must remain at the same line.
  term._core?.viewport?.syncScrollArea(true);
}

// xterm mouse reports are viewport-relative (visible rows 0..rows-1), not
// scrollback positions. A TUI hit-test therefore maps a click on a scrolled-back
// "Approve" onto whatever cell occupies that row of the live screen.
//
// 0.7.39 swallowed every key and click whenever viewportY < baseY. Live TUIs
// often sit one row above baseY (trailing blank, redraw), so Approve never
// arrived. Only a real history offset claims the live screen, and keyboard is
// never dropped — it is delivered after the snap so Enter/Space still approve.
export function claimLiveScreenInput(term, host, { swallow = true } = {}) {
  try {
    const buffer = term.buffer.active;
    const appOwnsInput = term.modes.mouseTrackingMode !== "none" ||
      Boolean(host?.classList?.contains("tui-lock"));
    if (!appOwnsInput) return false;
    const rows = term.rows || 24;
    // Trailing blank rows and TUI redraws leave a 1–2 row lag at the "bottom".
    const lag = buffer.baseY - buffer.viewportY;
    if (lag <= Math.max(2, Math.floor(rows * 0.2))) return false;
    term.scrollToBottom();
    syncTerminalViewport(term);
    return swallow;
  } catch {
    return false;
  }
}
