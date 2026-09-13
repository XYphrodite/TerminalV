export const MAX_PANES = 8;
export const MIN_PANE_WIDTH = 160;
export const MIN_PANE_HEIGHT = 100;
export const clampRatio = (value) => Number.isFinite(value) ? Math.max(.1, Math.min(.9, value)) : .5;

export function leafIds(node) {
  if (!node) return [];
  return node.sessionId ? [node.sessionId] : [...leafIds(node.first), ...leafIds(node.second)];
}

// Unknown, hidden and duplicated sessions are pruned. Every remaining session
// gets a home, including records saved by a version without split support.
export function normalizeLayouts(layouts, sessions) {
  const ids = sessions.filter((s) => !s.hidden).map((s) => s.id);
  const allowed = new Set(ids), used = new Set();
  function clean(node, state, depth = 0) {
    if (!node || typeof node !== "object" || depth > 16 || state.count >= MAX_PANES) return null;
    if (typeof node.sessionId === "string") {
      if (!allowed.has(node.sessionId) || used.has(node.sessionId)) return null;
      used.add(node.sessionId);
      state.count++;
      return { sessionId: node.sessionId };
    }
    if (node.axis !== "columns" && node.axis !== "rows") return null;
    const first = clean(node.first, state, depth + 1), second = clean(node.second, state, depth + 1);
    return first && second ? { axis: node.axis, ratio: clampRatio(node.ratio), first, second } : first || second;
  }
  const result = (Array.isArray(layouts) ? layouts : []).map((root) => clean(root, { count: 0 })).filter(Boolean);
  for (const id of ids) if (!used.has(id)) { result.push({ sessionId: id }); used.add(id); }
  return result;
}

export const layoutFor = (layouts, id) => layouts.find((root) => leafIds(root).includes(id));

export function splitSession(layouts, id, newId, axis) {
  const root = layoutFor(layouts, id);
  if (!root || !["columns", "rows"].includes(axis) || leafIds(root).length >= MAX_PANES || layoutFor(layouts, newId)) return layouts;
  function split(node) {
    if (node.sessionId === id) return { axis, ratio: .5, first: node, second: { sessionId: newId } };
    return node.sessionId ? node : { ...node, first: split(node.first), second: split(node.second) };
  }
  return layouts.map((item) => item === root ? split(item) : item);
}

export function detachSession(layouts, id) {
  function remove(node) {
    if (node.sessionId) return node.sessionId === id ? null : node;
    const first = remove(node.first), second = remove(node.second);
    return first && second ? { ...node, first, second } : first || second;
  }
  return [...layouts.map(remove).filter(Boolean), { sessionId: id }];
}

export function layoutGeometry(root) {
  const panes = [], dividers = [];
  function visit(node, rect, path = "root") {
    if (!node) return;
    if (node.sessionId) { panes.push({ ...rect, id: node.sessionId }); return; }
    const ratio = clampRatio(node.ratio), columns = node.axis === "columns";
    const split = columns ? rect.x + rect.width * ratio : rect.y + rect.height * ratio;
    dividers.push({ ...rect, path, node, split });
    visit(node.first, columns ? { ...rect, width: rect.width * ratio } : { ...rect, height: rect.height * ratio }, path + ".first");
    visit(node.second, columns ? { ...rect, x: split, width: rect.width * (1 - ratio) }
      : { ...rect, y: split, height: rect.height * (1 - ratio) }, path + ".second");
  }
  visit(root, { x: 0, y: 0, width: 1, height: 1 });
  return { panes, dividers };
}

export function neighborPane(panes, id, direction) {
  const current = panes.find((p) => p.id === id);
  if (!current) return null;
  const horizontal = ["ArrowLeft", "ArrowRight"].includes(direction);
  const positive = ["ArrowRight", "ArrowDown"].includes(direction);
  const center = (p) => horizontal ? p.x + p.width / 2 : p.y + p.height / 2;
  const cross = (p) => horizontal ? p.y + p.height / 2 : p.x + p.width / 2;
  return panes.filter((p) => p !== current && (positive ? center(p) > center(current) : center(p) < center(current)))
    .sort((a, b) => (Math.abs(center(a) - center(current)) + 2 * Math.abs(cross(a) - cross(current))) -
      (Math.abs(center(b) - center(current)) + 2 * Math.abs(cross(b) - cross(current))))[0]?.id || null;
}

export function paneShortcut(event) {
  if (!event.altKey || event.ctrlKey || event.metaKey || event.isComposing) return null;
  if (!event.shiftKey && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return event.key;
  if (event.shiftKey && ["Equal", "NumpadAdd"].includes(event.code)) return "columns";
  if (event.shiftKey && ["Minus", "NumpadSubtract"].includes(event.code)) return "rows";
  return null;
}
