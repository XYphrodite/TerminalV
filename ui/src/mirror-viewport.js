// A mirrored PTY has one character grid. Viewport changes on the phone may
// change the visible portion, but must not reflow cursor-addressed desktop data.
export function layoutMirrorViewport(tab) {
  if (!tab.mirrorSize || !tab.term.element) return;
  const dimensions = tab.term._core?._renderService?.dimensions?.css;
  const cell = dimensions?.cell;
  if (!(cell?.width > 0 && cell?.height > 0)) return;
  const style = getComputedStyle(tab.term.element);
  const padding = side => Number.parseFloat(style[side]) || 0;
  const scrollbar = tab.term._core?.viewport?.scrollBarWidth || 0;
  const width = Math.ceil(cell.width * tab.mirrorSize.cols + scrollbar
    + padding("paddingLeft") + padding("paddingRight"));
  const height = Math.ceil(cell.height * tab.mirrorSize.rows
    + padding("paddingTop") + padding("paddingBottom"));
  const element = tab.term.element;
  // A desktop grid shorter than the phone leaves a blank band under the last
  // row. Zoom fills that height; a taller grid stays 1:1 and scrolls.
  const avail = tab.host.clientHeight;
  const zoom = height > 0 && avail > height ? Math.round((avail / height) * 1000) / 1000 : 1;
  if (element.style.width !== `${width}px`) element.style.width = `${width}px`;
  if (element.style.height !== `${height}px`) element.style.height = `${height}px`;
  const zoomText = zoom === 1 ? "" : String(zoom);
  if (element.style.zoom !== zoomText) element.style.zoom = zoomText;
}

export function setMirrorSize(tab, cols, rows) {
  if (!Number.isInteger(cols) || !Number.isInteger(rows) || cols < 1 || cols > 1000 || rows < 1 || rows > 1000)
    return false;
  // Disable local fitting immediately, including while earlier writes are
  // parsing. Put the actual resize between old-grid and new-grid output.
  tab.remoteGeometry = true;
  tab.output.whenParsed(() => {
    tab.mirrorSize = { cols, rows };
    tab.ptySize = { cols, rows };
    tab.host.classList.add("mirror-viewport");
    tab.term.resize(cols, rows);
    layoutMirrorViewport(tab);
  });
  return true;
}

export function attachMirrorPan(tab) {
  let gesture = null;
  tab.host.addEventListener("touchstart", event => {
    if (!tab.remoteGeometry || event.touches.length !== 1) { gesture = null; return; }
    const touch = event.touches[0];
    const viewport = tab.host.querySelector(".xterm-viewport");
    gesture = {
      x: touch.clientX, y: touch.clientY,
      left: tab.host.scrollLeft, top: tab.host.scrollTop,
      viewportTop: viewport?.scrollTop || 0, axis: null
    };
  }, { capture: true, passive: true });
  tab.host.addEventListener("touchmove", event => {
    if (!gesture || event.touches.length !== 1) return;
    const touch = event.touches[0];
    const dx = gesture.x - touch.clientX, dy = gesture.y - touch.clientY;
    if (!gesture.axis) {
      if (Math.max(Math.abs(dx), Math.abs(dy)) < 6) return;
      // Wide grid pans sideways. Vertical is the pre-mirror xterm viewport:
      // the whole scrollback, not the few extra rows of the desktop screen.
      gesture.axis = Math.abs(dx) > Math.abs(dy) && tab.host.scrollWidth > tab.host.clientWidth ? "x" : "y";
    }
    event.preventDefault();
    event.stopPropagation();
    if (gesture.axis === "x") {
      tab.host.scrollLeft = gesture.left + dx;
      return;
    }
    const viewport = tab.host.querySelector(".xterm-viewport");
    if (viewport && viewport.scrollHeight > viewport.clientHeight + 1)
      viewport.scrollTop = gesture.viewportTop + dy;
    else tab.host.scrollTop = gesture.top + dy;
  }, { capture: true, passive: false });
  const finish = () => { gesture = null; };
  tab.host.addEventListener("touchend", finish, { passive: true });
  tab.host.addEventListener("touchcancel", finish, { passive: true });
  tab.term.onRender(() => layoutMirrorViewport(tab));
}
