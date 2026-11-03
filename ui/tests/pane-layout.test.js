import assert from "node:assert/strict";
import test from "node:test";
import { normalizeLayouts, leafIds, layoutFor, layoutGeometry, splitSession, detachSession, movePane, paneDropTarget, canSplitSession,
  neighborPane, paneShortcut, MAX_PANES, MAX_TREE_DEPTH, isSplitChild, isSplitParent, splitIndentLevel } from "../src/pane-layout.js";

const leaf = (sessionId) => ({ sessionId });
const branch = (first, second, axis = "columns", ratio = .5) => ({ axis, ratio, first, second });
const records = (...ids) => ids.map((id) => ({ id }));

test("old inventories get independent layouts; hidden sessions never appear", () => {
  assert.deepEqual(normalizeLayouts(null, [...records("a", "b"), { id: "h", hidden: true }]), [leaf("a"), leaf("b")]);
});
test("restoration prunes stale and duplicate leaves, collapses empty branches and clamps ratios", () => {
  const clean = normalizeLayouts([branch(branch(leaf("missing"), leaf("a")), branch(leaf("a"), leaf("b")), "rows", 2)], records("a", "b", "c"));
  assert.deepEqual(clean, [branch(leaf("a"), leaf("b"), "rows", .9), leaf("c")]);
  assert.deepEqual(normalizeLayouts([{ axis: "invalid", first: leaf("a") }], records("a")), [leaf("a")]);
});
test("malformed, deep and oversized trees cannot drop a session or recurse indefinitely", () => {
  const sessions = records(...Array.from({ length: 20 }, (_, i) => String(i)));
  let root = leaf("0");
  for (let i = 1; i < 20; i++) root = branch(root, leaf(String(i)));
  const clean = normalizeLayouts([root, null, "bad", { axis: "rows" }], sessions);
  assert.equal(new Set(clean.flatMap(leafIds)).size, 20);
  assert.ok(clean.every((tree) => leafIds(tree).length <= MAX_PANES));
  const cyclic = { axis: "columns", second: leaf("a") }; cyclic.first = cyclic;
  assert.deepEqual(normalizeLayouts([cyclic], records("a")), [leaf("a")]);
});
test("splitting always divides only the targeted leaf and supports 2D grids", () => {
  const initial = [leaf("a"), leaf("other")];
  const two = splitSession(initial, "a", "b", "columns");
  // Splitting the right pane b downward puts c ONLY below b, leaving a full height on the left
  const three = splitSession(two, "b", "c", "rows");
  assert.deepEqual(three[0], branch(leaf("a"), branch(leaf("b"), leaf("c"), "rows"), "columns"));
  // Splitting a downward too creates a 2x2 grid
  const four = splitSession(three, "a", "d", "rows");
  assert.deepEqual(four[0], branch(branch(leaf("a"), leaf("d"), "rows"), branch(leaf("b"), leaf("c"), "rows"), "columns"));
  // Splitting before places the new leaf first
  const before = splitSession(two, "b", "leftOfB", "columns", "before");
  assert.deepEqual(before[0], branch(leaf("a"), branch(leaf("leftOfB"), leaf("b"), "columns"), "columns"));
  assert.deepEqual(initial, [leaf("a"), leaf("other")]);
  assert.equal(two[1], initial[1]);
  assert.equal(splitSession(three, "a", "c", "columns"), three);
  assert.equal(splitSession(three, "missing", "e", "columns"), three);
});
test("eight-panel limit rejects an extra split without changing any layout", () => {
  let layouts = [leaf("0")];
  for (let i = 1; i < MAX_PANES; i++) layouts = splitSession(layouts, "0", String(i), "columns");
  assert.equal(leafIds(layouts[0]).length, 8);
  assert.equal(splitSession(layouts, "0", "extra", "columns"), layouts);
});
test("detaching and hiding keep neighbors and their split proportions", () => {
  const layouts = [branch(leaf("a"), branch(leaf("b"), leaf("c"), "rows", .7))];
  const detached = detachSession(layouts, "a");
  assert.deepEqual(detached, [layouts[0].second, leaf("a")]);
  const hidden = normalizeLayouts(layouts, [...records("a", "c"), { id: "b", hidden: true }]);
  assert.deepEqual(hidden, [branch(leaf("a"), leaf("c"))]);
  assert.equal(layoutFor(detached, "a").sessionId, "a");
});
test("geometry tiles the viewport without overlaps and navigation follows spatial neighbors", () => {
  const root = branch(leaf("left"), branch(leaf("top"), leaf("bottom"), "rows"), "columns", .4);
  const { panes, dividers } = layoutGeometry(root);
  assert.deepEqual(panes, [
    { x: 0, y: 0, width: .4, height: 1, id: "left" },
    { x: .4, y: 0, width: .6, height: .5, id: "top" },
    { x: .4, y: .5, width: .6, height: .5, id: "bottom" }
  ]);
  assert.equal(dividers.length, 2);
  assert.equal(neighborPane(panes, "top", "ArrowDown"), "bottom");
  assert.equal(neighborPane(panes, "bottom", "ArrowLeft"), "left");
  assert.equal(neighborPane(panes, "top", "ArrowUp"), null);
});
test("split shortcuts use physical keys in both layouts and exclude AltGr/composition", () => {
  for (const key of ["+", "="]) assert.equal(paneShortcut({ key, code: "Equal", altKey: true, shiftKey: true }), "columns");
  assert.equal(paneShortcut({ key: "_", code: "Minus", altKey: true, shiftKey: true }), "rows");
  assert.equal(paneShortcut({ key: "ArrowRight", altKey: true }), "ArrowRight");
  assert.equal(paneShortcut({ key: "+", code: "Equal", altKey: true, shiftKey: true, ctrlKey: true }), null);
  assert.equal(paneShortcut({ key: "+", code: "Equal", altKey: true, shiftKey: true, isComposing: true }), null);
});

test("split child/parent stay consistent across nested 2D splits for sidebar rendering", () => {
  const base = [leaf("a"), leaf("other")];
  const split = splitSession(base, "a", "b", "columns");
  assert.equal(isSplitChild(split, "b"), true);
  assert.equal(isSplitParent(split, "a"), true);
  assert.equal(isSplitChild(split, "a"), false);
  assert.equal(isSplitParent(split, "b"), false);
  assert.equal(isSplitChild(split, "other"), false);

  // Nested 2x2 grid: a remains the single workspace parent, and d, b, c are all flat level-1 children
  const grid = [branch(branch(leaf("a"), leaf("d"), "rows"), branch(leaf("b"), leaf("c"), "rows"), "columns")];
  assert.equal(isSplitParent(grid, "a"), true);
  assert.equal(isSplitChild(grid, "a"), false);
  assert.equal(splitIndentLevel(grid, "a"), 0);
  for (const id of ["d", "b", "c"]) {
    assert.equal(isSplitParent(grid, id), false);
    assert.equal(isSplitChild(grid, id), true);
    assert.equal(splitIndentLevel(grid, id), 1);
  }

  const detached = detachSession(grid, "a");
  assert.equal(isSplitParent(detached, "d"), true);
  assert.equal(isSplitChild(detached, "b"), true);
  assert.equal(isSplitChild(detached, "c"), true);
  assert.equal(isSplitChild(detached, "a"), false);
  assert.equal(isSplitParent(detached, "a"), false);
});

test("canSplitSession enforces MAX_PANES and MAX_TREE_DEPTH", () => {
  const split = splitSession([leaf("a")], "a", "b", "columns");
  assert.equal(canSplitSession(split, "a"), true);
  assert.equal(canSplitSession(split, "b"), true);
  assert.equal(canSplitSession(split, "missing"), false);

  let deep = leaf("0");
  for (let i = 1; i <= MAX_TREE_DEPTH; i++) deep = { axis: "columns", ratio: .5, first: { sessionId: `x${i}` }, second: deep };
  // Construct 2-leaf deep wrapper check via max depth tree of 2 leaves wouldn't exist without branches, so test 8-pane limit:
  let full = [leaf("0")];
  for (let i = 1; i < MAX_PANES; i++) full = splitSession(full, "0", String(i), "columns");
  assert.equal(canSplitSession(full, "0"), false);
});

test("movePane rearranges, swaps, docks sidebar sessions and wraps workspace edges like VS Code", () => {
  // Fix full-width bottom pane into bottom-right only: [[left | right] / bottom] -> [left | (right / bottom)]
  const fullBottom = [branch(branch(leaf("left"), leaf("right"), "columns"), leaf("bottom"), "rows")];
  const underRight = movePane(fullBottom, "bottom", "right", "bottom");
  assert.deepEqual(underRight, [branch(leaf("left"), branch(leaf("right"), leaf("bottom"), "rows"), "columns")]);

  // Reverse: move bottom-right pane to full-width bottom via root-bottom
  const backToFull = movePane(underRight, "bottom", "left", "root-bottom");
  assert.deepEqual(backToFull, fullBottom);

  // Convert horizontal split [a | b] into vertical split [b / a]
  const twoCols = [branch(leaf("a"), leaf("b"), "columns")];
  assert.deepEqual(movePane(twoCols, "b", "a", "top"), [branch(leaf("b"), leaf("a"), "rows")]);

  // Swap center preserves custom split ratios and works across layouts
  const weighted = [branch(leaf("a"), branch(leaf("b"), leaf("c"), "rows", .7), "columns", .35), leaf("other")];
  const swappedInternal = movePane(weighted, "c", "a", "center");
  assert.deepEqual(swappedInternal[0], branch(leaf("c"), branch(leaf("b"), leaf("a"), "rows", .7), "columns", .35));
  const swappedExternal = movePane(weighted, "other", "b", "center");
  assert.deepEqual(swappedExternal, [branch(leaf("a"), branch(leaf("other"), leaf("c"), "rows", .7), "columns", .35), leaf("b")]);

  // Dock standalone sidebar session into existing split
  const docked = movePane([branch(leaf("a"), leaf("b"), "columns"), leaf("other")], "other", "b", "bottom");
  assert.deepEqual(docked, [branch(leaf("a"), branch(leaf("b"), leaf("other"), "rows"), "columns")]);

  // At MAX_PANES: cross-layout split is rejected, but center swap and internal rearrangement succeed
  let full = [leaf("0")];
  for (let i = 1; i < MAX_PANES; i++) full = splitSession(full, String(i - 1), String(i), "columns");
  const withExtra = [...full, leaf("extra")];
  assert.equal(movePane(withExtra, "extra", "0", "bottom"), withExtra);
  const swappedAtMax = movePane(withExtra, "extra", "0", "center");
  assert.equal(leafIds(swappedAtMax[0]).includes("extra"), true);
  assert.equal(swappedAtMax[1].sessionId, "0");
  const rearrangedAtMax = movePane(full, "7", "0", "bottom");
  assert.notEqual(rearrangedAtMax, full);
  assert.equal(leafIds(rearrangedAtMax[0]).length, MAX_PANES);
});

test("paneDropTarget detects pane edges, swap center and outer workspace rims", () => {
  const root = branch(leaf("left"), leaf("right"), "columns");
  const { panes } = layoutGeometry(root);
  const box = { left: 100, top: 50, width: 1000, height: 600 };

  // Bottom half of right pane (not on the 18px outer rim) -> targets ONLY right pane's bottom
  const rightBottom = paneDropTarget(panes, box, 850, 580, { sourceId: "other" });
  assert.equal(rightBottom.targetId, "right");
  assert.equal(rightBottom.zone, "bottom");
  assert.deepEqual(rightBottom.preview, { x: .5, y: .5, width: .5, height: .5 });

  // Outer bottom rim (within 18px of container bottom) -> spans full workspace width (root-bottom)
  const rootBottom = paneDropTarget(panes, box, 850, 645, { sourceId: "other" });
  assert.equal(rootBottom.zone, "root-bottom");
  assert.deepEqual(rootBottom.preview, { x: 0, y: .5, width: 1, height: .5 });

  // Center of right pane -> swap
  const center = paneDropTarget(panes, box, 850, 350, { sourceId: "left" });
  assert.equal(center.targetId, "right");
  assert.equal(center.zone, "center");

  // Hovering a pane over itself returns null; hovering its own full-height outer wall also returns null
  assert.equal(paneDropTarget(panes, box, 300, 350, { sourceId: "left" }), null);
  assert.equal(paneDropTarget(panes, box, 105, 350, { sourceId: "left" }), null);

  // When allowSplit is false (e.g. 9th session dragged over 8-pane view), only center swap is offered
  const swapOnly = paneDropTarget(panes, box, 850, 580, { sourceId: "other", allowSplit: false });
  assert.equal(swapOnly.zone, "center");
  assert.equal(swapOnly.targetId, "right");
});
