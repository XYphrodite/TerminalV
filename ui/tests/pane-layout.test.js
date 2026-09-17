import assert from "node:assert/strict";
import test from "node:test";
import { normalizeLayouts, leafIds, layoutFor, layoutGeometry, splitSession, detachSession,
  neighborPane, paneShortcut, MAX_PANES, isSplitChild, isSplitParent } from "../src/pane-layout.js";

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
test("nested splitting creates only the new leaf and keeps other views intact", () => {
  const initial = [leaf("a"), leaf("other")];
  const two = splitSession(initial, "a", "b", "columns");
  const three = splitSession(two, "b", "c", "rows");
  assert.deepEqual(three[0], branch(leaf("a"), branch(leaf("b"), leaf("c"), "rows")));
  assert.deepEqual(initial, [leaf("a"), leaf("other")]);
  assert.equal(three[1], initial[1]);
  assert.equal(splitSession(three, "a", "c", "columns"), three);
  assert.equal(splitSession(three, "missing", "d", "columns"), three);
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

test("split child/parent are detectable for sidebar rendering", () => {
  const base = [leaf("a"), leaf("other")];
  const split = splitSession(base, "a", "b", "columns");
  assert.equal(isSplitChild(split, "b"), true);
  assert.equal(isSplitParent(split, "a"), true);
  assert.equal(isSplitChild(split, "a"), false);
  assert.equal(isSplitParent(split, "b"), false);
  assert.equal(isSplitChild(split, "other"), false);
  // nested split: b is parent of c
  const nested = splitSession(split, "b", "c", "rows");
  assert.equal(isSplitChild(nested, "c"), true);
  assert.equal(isSplitParent(nested, "b"), true);
  // a remains parent of b, not of c directly
  assert.equal(isSplitParent(nested, "a"), true);
  assert.equal(isSplitChild(nested, "a"), false);
  // detached child loses direct relation, but sibling collapses: a remains parent of c
  const detached = detachSession(nested, "b");
  assert.equal(isSplitChild(detached, "b"), false);
  assert.equal(isSplitParent(detached, "b"), false);
  assert.equal(isSplitChild(detached, "c"), true);
  assert.equal(isSplitParent(detached, "a"), true);
});
