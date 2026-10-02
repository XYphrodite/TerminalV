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
// "Approve" onto whatever cell occupies that row of the live screen. For apps
// that own the screen (mouse tracking or tui-lock), the first input while the
// reader is in history returns to the live screen and swallows that event so a
// stale frame cannot activate the wrong control.
export function claimLiveScreenInput(term, host) {
  try {
    const buffer = term.buffer.active;
    const appOwnsInput = term.modes.mouseTrackingMode !== "none" ||
      Boolean(host?.classList?.contains("tui-lock"));
    if (!appOwnsInput || buffer.viewportY >= buffer.baseY) return false;
    term.scrollToBottom();
    syncTerminalViewport(term);
    return true;
  } catch {
    return false;
  }
}
