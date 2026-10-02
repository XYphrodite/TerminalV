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

// Finger pixels → content pixels. 1:1 felt sluggish on a phone (a full row is
// ~20 CSS px and scrollback is thousands of lines); v0.7.22 used 1.2.
export const MIRROR_SCROLL_GAIN = 1.35;

function scrollerFor(tab, hint) {
  const viewport = hint && hint.isConnected ? hint : tab.host.querySelector(".xterm-viewport");
  if (viewport && viewport.scrollHeight > viewport.clientHeight + 1) return { el: viewport, history: true };
  return { el: tab.host, history: false };
}

function fling(tab, samples, gain) {
  if (samples.length < 2) return;
  const first = samples[0], last = samples[samples.length - 1];
  const dt = last.t - first.t;
  if (dt < 16) return;
  // Finger velocity in px/ms, already in scroll direction.
  let velocity = ((first.p - last.p) / dt) * gain;
  if (Math.abs(velocity) < 0.08) return;
  let lastT = performance.now();
  const step = () => {
    const now = performance.now();
    const frame = Math.min(now - lastT, 32);
    lastT = now;
    // ~half-life 90ms: a flick coasts a few screens, a careful drag stops soon.
    velocity *= Math.pow(0.5, frame / 90);
    const { el } = scrollerFor(tab, null);
    el.scrollTop += velocity * frame;
    if (Math.abs(velocity) > 0.03) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
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
      viewportTop: viewport?.scrollTop || 0, axis: null,
      viewport,
      samples: [{ t: performance.now(), p: touch.clientY }]
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
    gesture.samples.push({ t: performance.now(), p: touch.clientY });
    while (gesture.samples.length > 6) gesture.samples.shift();
    const historyDy = dy * MIRROR_SCROLL_GAIN;
    const viewport = gesture.viewport && gesture.viewport.isConnected
      ? gesture.viewport : tab.host.querySelector(".xterm-viewport");
    if (viewport && viewport.scrollHeight > viewport.clientHeight + 1)
      viewport.scrollTop = gesture.viewportTop + historyDy;
    else tab.host.scrollTop = gesture.top + historyDy;
  }, { capture: true, passive: false });
  const finish = () => {
    if (!gesture) return;
    const samples = gesture.samples, gain = MIRROR_SCROLL_GAIN;
    gesture = null;
    fling(tab, samples, gain);
  };
  tab.host.addEventListener("touchend", finish, { passive: true });
  tab.host.addEventListener("touchcancel", finish, { passive: true });
  tab.term.onRender(() => layoutMirrorViewport(tab));
}
