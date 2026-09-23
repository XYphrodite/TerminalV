// Wheel routing for TUI tabs (tui-lock).
// A fullscreen TUI owns the viewport: wheel must not scroll terminal
// scrollback. But a mouse-interactive TUI (own scrollbar, e.g. grok)
// needs the wheel itself — xterm forwards it as mouse reports, so the
// terminal must not swallow it.
export function wantsAppWheel({ tuiLock, mouseMode, zoomModifier }) {
  return Boolean(tuiLock && mouseMode && !zoomModifier);
}
