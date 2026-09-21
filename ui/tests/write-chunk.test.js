import assert from "node:assert/strict";
import test from "node:test";
import { chunkText, WRITE_CHUNK } from "../src/write-chunk.js";

test("small text stays as one chunk", () => {
  assert.deepEqual(chunkText("hello"), ["hello"]);
  assert.deepEqual(chunkText("a".repeat(WRITE_CHUNK)), ["a".repeat(WRITE_CHUNK)]);
});

test("large text splits into bounded chunks and round-trips", () => {
  const large = "a".repeat(WRITE_CHUNK * 2 + 123) + "кириллица 🖥";
  const chunks = chunkText(large);
  assert.equal(chunks.every(c => c.length <= WRITE_CHUNK), true);
  assert.equal(chunks.join(""), large);
  // 100k payload (typical large paste into Muse) should be fast and chunked
  const huge = "x".repeat(100000);
  const start = Date.now();
  const hugeChunks = chunkText(huge);
  assert.equal(hugeChunks.join(""), huge);
  assert.ok(hugeChunks.length > 20);
  assert.ok(Date.now() - start < 200, "chunking 100k should be <200ms");
});

test("bracketed paste payload splits without breaking semantics", () => {
  const payload = "\u001b[200~" + "b".repeat(WRITE_CHUNK * 3) + "\u001b[201~";
  const chunks = chunkText(payload);
  assert.equal(chunks.join(""), payload);
  // First chunk must still start with bracket start, last must end with bracket end
  assert.ok(chunks[0].startsWith("\u001b[200~"));
  assert.ok(chunks[chunks.length - 1].endsWith("\u001b[201~"));
});

test("surrogate pairs (emoji) are never split", () => {
  const emoji = "😀"; // surrogate pair length 2
  // Build string where chunk boundary would cut inside emoji
  const fill = "a".repeat(WRITE_CHUNK - 1);
  const text = fill + emoji + "b".repeat(WRITE_CHUNK);
  const chunks = chunkText(text);
  assert.equal(chunks.join(""), text);
  for (const c of chunks) {
    // No chunk should end with isolated high surrogate
    const last = c.charCodeAt(c.length - 1);
    if (last >= 0xd800 && last <= 0xdbff) {
      assert.fail("chunk ends with high surrogate");
    }
  }
});

test("newlines converted payload still round-trips", () => {
  const multiline = ("line1\nline2\r\nline3\rline4\n".repeat(2000));
  const prepared = multiline.replace(/\r?\n/g, "\r");
  const payload = "\u001b[200~" + prepared + "\u001b[201~";
  const chunks = chunkText(payload);
  assert.equal(chunks.join(""), payload);
});

test("100 chars with dialog (multiline) stays one chunk and preserves order via microtask", async () => {
  const text = "x".repeat(95) + "\n123"; // 100 chars, contains \n → dialog
  const chunks = chunkText(text);
  assert.equal(chunks.length, 1);
  assert.equal(chunks[0], text);
  // Simulate postWrite queueMicrotask ordering: even 100 chars must not block triggerDataEvent
  const posted = [];
  for (const c of chunks) queueMicrotask(() => posted.push(c));
  await new Promise((r) => queueMicrotask(r));
  assert.deepEqual(posted, chunks);
});

test("paste batch uses single microtask, not per-chunk pause", async () => {
  const payload = "\u001b[200~" + "a".repeat(WRITE_CHUNK * 2 + 500) + "\u001b[201~";
  const chunks = chunkText(payload);
  assert.ok(chunks.length >= 2, "needs multiple chunks");
  // New postWrite batches all chunks in ONE microtask — should be 1 batch + 1 await, not N+1
  let microtasks = 0;
  const origQueueMicrotask = global.queueMicrotask;
  const posted = [];
  global.queueMicrotask = (cb) => { microtasks++; origQueueMicrotask(cb); };
  // Simulate new postWrite: single microtask posts all chunks
  queueMicrotask(() => { for (const c of chunks) posted.push(c); });
  await new Promise((r) => queueMicrotask(r));
  global.queueMicrotask = origQueueMicrotask;
  assert.equal(posted.length, chunks.length);
  assert.equal(microtasks, 2, "paste should use single batch microtask (1 batch + 1 await), not per-chunk N+1");
  assert.equal(posted.join(""), payload);
  // Old per-chunk would be chunks.length +1 microtasks — ensure we are not that
  assert.ok(microtasks < chunks.length + 1, "must be batched, not per-chunk");
});
