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
