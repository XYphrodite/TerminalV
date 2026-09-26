import { t } from "./i18n.js";
import { layoutGeometry, clampRatio, MIN_PANE_WIDTH, MIN_PANE_HEIGHT } from "./pane-layout.js";
import { syncTerminalViewport } from "./terminal-viewport.js";

// Panes stay mounted in their original DOM nodes: rearranging never recreates
// xterm, loses its selection, or reattaches/restarts a PTY.
export function createPaneView({ container, getRoot, getTabs, canResize, onResize }) {
  const dividers = new Map();
  let drag = null;

  function minimumExtent(node, axis) {
    if (node.sessionId) return axis === "columns" ? MIN_PANE_WIDTH : MIN_PANE_HEIGHT;
    const first = minimumExtent(node.first, axis), second = minimumExtent(node.second, axis);
    return node.axis === axis ? first + second + 6 : Math.max(first, second);
  }

  function limits(divider) {
    const size = container.getBoundingClientRect();
    const extent = divider.node.axis === "columns" ? size.width * divider.width : size.height * divider.height;
    const first = minimumExtent(divider.node.first, divider.node.axis) + 3;
    const second = minimumExtent(divider.node.second, divider.node.axis) + 3;
    if (extent < first + second) {
      const balanced = clampRatio(first / (first + second));
      return [balanced, balanced];
    }
    return [Math.max(.1, first / extent), Math.min(.9, 1 - second / extent)];
  }
  function change(divider, value) {
    const [min, max] = limits(divider);
    divider.node.ratio = Math.max(min, Math.min(max, clampRatio(value)));
    render();
    onResize();
  }
  function finish() {
    if (!drag) return;
    const { element, pointerId } = drag;
    drag = null;
    if (element.hasPointerCapture?.(pointerId)) element.releasePointerCapture(pointerId);
    container.classList.remove("resizing");
    onResize();
  }
  function makeDivider(path) {
    const element = document.createElement("div");
    element.className = "pane-divider";
    element.tabIndex = 0;
    element.setAttribute("role", "separator");
    element.dataset.path = path;
    element.addEventListener("pointerdown", (event) => {
      if (event.button !== 0 || !canResize()) return;
      event.preventDefault();
      const divider = element.divider;
      drag = { element, pointerId: event.pointerId, divider, root: getRoot() };
      element.setPointerCapture(event.pointerId);
      element.focus({ preventScroll: true });
      container.classList.add("resizing");
    });
    element.addEventListener("pointermove", (event) => {
      if (!drag || drag.element !== element || drag.pointerId !== event.pointerId) return;
      if (!canResize() || drag.root !== getRoot()) { finish(); return; }
      const box = container.getBoundingClientRect(), d = drag.divider;
      const ratio = d.node.axis === "columns" ? ((event.clientX - box.left) / box.width - d.x) / d.width
        : ((event.clientY - box.top) / box.height - d.y) / d.height;
      change(d, ratio);
    });
    for (const name of ["pointerup", "pointercancel", "lostpointercapture"]) element.addEventListener(name, finish);
    element.addEventListener("keydown", (event) => {
      event.stopPropagation();
      if (!canResize() || event.isComposing) return;
      const d = element.divider, columns = d.node.axis === "columns";
      let value = d.node.ratio;
      if (event.key === "Home") value = 0;
      else if (event.key === "End") value = 1;
      else if (event.key === (columns ? "ArrowLeft" : "ArrowUp")) value -= .05;
      else if (event.key === (columns ? "ArrowRight" : "ArrowDown")) value += .05;
      else return;
      event.preventDefault();
      change(d, value);
    });
    container.append(element);
    return element;
  }
  function render() {
    const root = getRoot();
    if (drag && drag.root !== root) finish();
    const geometry = layoutGeometry(root), panes = new Map(geometry.panes.map((p) => [p.id, p]));
    container.classList.toggle("split-view", panes.size > 1);
    for (const tab of getTabs()) {
      const rect = panes.get(tab.id);
      const wasVisible = tab.pane.classList.contains("visible");
      tab.pane.classList.toggle("visible", Boolean(rect));
      tab.pane.inert = !rect;
      if (!rect) continue;
      const right = rect.x + rect.width < .999999 ? 3 : 0, bottom = rect.y + rect.height < .999999 ? 3 : 0;
      const left = rect.x > 0 ? 3 : 0, top = rect.y > 0 ? 3 : 0;
      Object.assign(tab.pane.style, {
        left: `calc(${rect.x * 100}% + ${left}px)`, top: `calc(${rect.y * 100}% + ${top}px)`,
        width: `calc(${rect.width * 100}% - ${left + right}px)`, height: `calc(${rect.height * 100}% - ${top + bottom}px)`
      });
      // Sync before delayed DOM scroll events can overwrite xterm's position.
      if (!wasVisible) syncTerminalViewport(tab.term);
    }
    const wanted = new Set(geometry.dividers.map((d) => d.path));
    for (const [path, element] of dividers) if (!wanted.has(path)) { element.remove(); dividers.delete(path); }
    for (const d of geometry.dividers) {
      const element = dividers.get(d.path) || makeDivider(d.path);
      dividers.set(d.path, element);
      element.divider = d;
      const columns = d.node.axis === "columns";
      element.classList.toggle("columns", columns);
      element.setAttribute("aria-orientation", columns ? "vertical" : "horizontal");
      element.setAttribute("aria-label", t("Pane_ResizeAria"));
      element.title = t("Pane_ResizeTitle");
      const [min, max] = limits(d);
      element.setAttribute("aria-valuemin", String(Math.round(min * 100)));
      element.setAttribute("aria-valuemax", String(Math.round(max * 100)));
      element.setAttribute("aria-valuenow", String(Math.round(d.node.ratio * 100)));
      Object.assign(element.style, columns
        ? { left: `calc(${d.split * 100}% - 3px)`, top: `${d.y * 100}%`, width: "6px", height: `${d.height * 100}%` }
        : { left: `${d.x * 100}%`, top: `calc(${d.split * 100}% - 3px)`, width: `${d.width * 100}%`, height: "6px" });
    }
    return geometry;
  }
  window.addEventListener("blur", finish);
  return { render, finish };
}
