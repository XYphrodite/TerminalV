export const MAX_PANES = 8;
export const MAX_TREE_DEPTH = 16;
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

export const layoutFor = (layouts, id) => Array.isArray(layouts) ? layouts.find((root) => leafIds(root).includes(id)) : undefined;

function leafDepth(node, id, depth = 0) {
  if (!node || typeof node !== "object") return -1;
  if (node.sessionId) return node.sessionId === id ? depth : -1;
  const left = leafDepth(node.first, id, depth + 1);
  return left >= 0 ? left : leafDepth(node.second, id, depth + 1);
}

function maxTreeDepth(node) {
  if (!node || typeof node !== "object" || node.sessionId) return 0;
  return 1 + Math.max(maxTreeDepth(node.first), maxTreeDepth(node.second));
}

export function canSplitSession(layouts, id) {
  const root = layoutFor(layouts, id);
  if (!root || leafIds(root).length >= MAX_PANES) return false;
  const depth = leafDepth(root, id);
  return depth >= 0 && depth < MAX_TREE_DEPTH;
}

export function splitSession(layouts, id, newId, axis, placement = "after") {
  const root = layoutFor(layouts, id);
  if (!root || !["columns", "rows"].includes(axis) || !["before", "after"].includes(placement)) return layouts;
  if (leafIds(root).length >= MAX_PANES || layoutFor(layouts, newId)) return layouts;
  function split(node, depth = 0) {
    if (node.sessionId === id) {
      if (depth >= MAX_TREE_DEPTH) return node;
      const added = { sessionId: newId };
      return placement === "before"
        ? { axis, ratio: .5, first: added, second: node }
        : { axis, ratio: .5, first: node, second: added };
    }
    if (node.sessionId) return node;
    const first = split(node.first, depth + 1);
    const second = split(node.second, depth + 1);
    return first === node.first && second === node.second ? node : { ...node, first, second };
  }
  const next = split(root);
  return next === root ? layouts : layouts.map((item) => item === root ? next : item);
}

function removeLeaf(node, id) {
  if (node.sessionId) return node.sessionId === id ? null : node;
  const first = removeLeaf(node.first, id), second = removeLeaf(node.second, id);
  return first && second ? { ...node, first, second } : first || second;
}

const PANE_ZONES = {
  left: { axis: "columns", placement: "before", root: false },
  right: { axis: "columns", placement: "after", root: false },
  top: { axis: "rows", placement: "before", root: false },
  bottom: { axis: "rows", placement: "after", root: false },
  "root-left": { axis: "columns", placement: "before", root: true },
  "root-right": { axis: "columns", placement: "after", root: true },
  "root-top": { axis: "rows", placement: "before", root: true },
  "root-bottom": { axis: "rows", placement: "after", root: true }
};

export function movePane(layouts, sourceId, targetId, zone) {
  if (!Array.isArray(layouts) || !sourceId || !targetId) return layouts;
  const sourceRoot = layoutFor(layouts, sourceId), targetRoot = layoutFor(layouts, targetId);
  if (!sourceRoot || !targetRoot) return layouts;

  if (zone === "center") {
    if (sourceId === targetId) return layouts;
    const swap = (node) => {
      if (node.sessionId === sourceId) return { sessionId: targetId };
      if (node.sessionId === targetId) return { sessionId: sourceId };
      return node.sessionId ? node : { ...node, first: swap(node.first), second: swap(node.second) };
    };
    return layouts.map(swap);
  }

  const spec = PANE_ZONES[zone];
  if (!spec) return layouts;
  const anchorId = spec.root
    ? (targetId !== sourceId ? targetId : leafIds(targetRoot).find((id) => id !== sourceId))
    : (targetId !== sourceId ? targetId : null);
  if (!anchorId) return layouts;
  if (sourceRoot !== targetRoot && leafIds(targetRoot).length >= MAX_PANES) return layouts;

  const withoutSource = layouts.map((item) => removeLeaf(item, sourceId)).filter(Boolean);
  let result;
  if (spec.root) {
    const anchorRoot = layoutFor(withoutSource, anchorId);
    if (!anchorRoot || maxTreeDepth(anchorRoot) >= MAX_TREE_DEPTH) return layouts;
    const added = { sessionId: sourceId };
    const wrapped = spec.placement === "before"
      ? { axis: spec.axis, ratio: .5, first: added, second: anchorRoot }
      : { axis: spec.axis, ratio: .5, first: anchorRoot, second: added };
    result = withoutSource.map((item) => item === anchorRoot ? wrapped : item);
  } else {
    result = splitSession(withoutSource, anchorId, sourceId, spec.axis, spec.placement);
  }
  if (result === withoutSource || !layoutFor(result, sourceId) || !layoutFor(result, anchorId)) return layouts;
  return result;
}

export function detachSession(layouts, id) {
  return [...layouts.map((item) => removeLeaf(item, id)).filter(Boolean), { sessionId: id }];
}

export function paneDropTarget(panes, box, clientX, clientY, { sourceId = null, allowSplit = true } = {}) {
  if (!Array.isArray(panes) || !panes.length || !box || box.width <= 0 || box.height <= 0) return null;
  if (clientX < box.left || clientX > box.left + box.width || clientY < box.top || clientY > box.top + box.height) return null;

  if (panes.length >= 2 && allowSplit) {
    const rimX = Math.min(18, box.width * .06), rimY = Math.min(18, box.height * .06);
    const dl = clientX - box.left, dr = box.left + box.width - clientX;
    const dt = clientY - box.top, db = box.top + box.height - clientY;
    const sourcePane = sourceId ? panes.find((p) => p.id === sourceId) : null;
    const anchor = panes.find((p) => p.id !== sourceId)?.id;
    if (anchor) {
      const candidates = [];
      if (dl <= rimX) candidates.push({ dist: dl, zone: "root-left", preview: { x: 0, y: 0, width: .5, height: 1 },
        noop: sourcePane && sourcePane.x === 0 && sourcePane.y === 0 && sourcePane.height === 1 });
      if (dr <= rimX) candidates.push({ dist: dr, zone: "root-right", preview: { x: .5, y: 0, width: .5, height: 1 },
        noop: sourcePane && Math.abs(sourcePane.x + sourcePane.width - 1) < 1e-6 && sourcePane.y === 0 && sourcePane.height === 1 });
      if (dt <= rimY) candidates.push({ dist: dt, zone: "root-top", preview: { x: 0, y: 0, width: 1, height: .5 },
        noop: sourcePane && sourcePane.x === 0 && sourcePane.y === 0 && sourcePane.width === 1 });
      if (db <= rimY) candidates.push({ dist: db, zone: "root-bottom", preview: { x: 0, y: .5, width: 1, height: .5 },
        noop: sourcePane && sourcePane.x === 0 && Math.abs(sourcePane.y + sourcePane.height - 1) < 1e-6 && sourcePane.width === 1 });
      candidates.sort((a, b) => a.dist - b.dist);
      if (candidates.length) {
        return candidates[0].noop ? null : { targetId: anchor, zone: candidates[0].zone, preview: candidates[0].preview };
      }
    }
  }

  const gx = (clientX - box.left) / box.width, gy = (clientY - box.top) / box.height;
  const pane = panes.find((p) => gx >= p.x && gx <= p.x + p.width && gy >= p.y && gy <= p.y + p.height)
    || panes.reduce((best, p) => {
      const dx = Math.max(p.x - gx, 0, gx - (p.x + p.width));
      const dy = Math.max(p.y - gy, 0, gy - (p.y + p.height));
      const dist = dx * dx + dy * dy;
      return !best || dist < best.dist ? { p, dist } : best;
    }, null)?.p;
  if (!pane || pane.id === sourceId) return null;

  const rx = Math.max(0, Math.min(1, (gx - pane.x) / pane.width));
  const ry = Math.max(0, Math.min(1, (gy - pane.y) / pane.height));
  if (!allowSplit || (rx > .25 && rx < .75 && ry > .25 && ry < .75)) {
    return { targetId: pane.id, zone: "center", preview: { x: pane.x, y: pane.y, width: pane.width, height: pane.height } };
  }

  const edges = [
    { dist: rx, zone: "left", preview: { x: pane.x, y: pane.y, width: pane.width / 2, height: pane.height } },
    { dist: 1 - rx, zone: "right", preview: { x: pane.x + pane.width / 2, y: pane.y, width: pane.width / 2, height: pane.height } },
    { dist: ry, zone: "top", preview: { x: pane.x, y: pane.y, width: pane.width, height: pane.height / 2 } },
    { dist: 1 - ry, zone: "bottom", preview: { x: pane.x, y: pane.y + pane.height / 2, width: pane.width, height: pane.height / 2 } }
  ].sort((a, b) => a.dist - b.dist);
  return { targetId: pane.id, zone: edges[0].zone, preview: edges[0].preview };
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

export function isSplitChild(layouts, id) {
  if (!Array.isArray(layouts) || !id) return false;
  const root = layoutFor(layouts, id);
  if (!root || root.sessionId) return false;
  const leaves = leafIds(root);
  return leaves.length > 1 && leaves[0] !== id;
}

// Sidebar keeps one flat indent level under the workspace lead pane regardless
// of how nested the 2D spatial split tree is on screen.
export function splitIndentLevel(layouts, id) {
  return isSplitChild(layouts, id) ? 1 : 0;
}

export function isSplitParent(layouts, id) {
  if (!Array.isArray(layouts) || !id) return false;
  const root = layoutFor(layouts, id);
  if (!root || root.sessionId) return false;
  const leaves = leafIds(root);
  return leaves.length > 1 && leaves[0] === id;
}

export function paneShortcut(event) {
  if (!event.altKey || event.ctrlKey || event.metaKey || event.isComposing) return null;
  if (!event.shiftKey && ["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown"].includes(event.key)) return event.key;
  if (event.shiftKey && ["Equal", "NumpadAdd"].includes(event.code)) return "columns";
  if (event.shiftKey && ["Minus", "NumpadSubtract"].includes(event.code)) return "rows";
  return null;
}
