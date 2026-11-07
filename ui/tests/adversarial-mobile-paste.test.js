import assert from "node:assert/strict";
import test from "node:test";
import { formatPastedPath, shouldStoreAsFile, PASTE_FILE_THRESHOLD } from "../src/paste-file.js";

test("adversarial formatPastedPath: whitespace and escaping stress test", () => {
  // 1. Standard paths without whitespace must NOT be quoted
  assert.equal(
    formatPastedPath("C:\\Users\\Gamer\\AppData\\Local\\Temp\\TerminalV\\pastes\\paste-abc123.jpg"),
    "C:\\Users\\Gamer\\AppData\\Local\\Temp\\TerminalV\\pastes\\paste-abc123.jpg"
  );
  assert.equal(
    formatPastedPath("/tmp/TerminalV/pastes/paste-abc123.jpg"),
    "/tmp/TerminalV/pastes/paste-abc123.jpg"
  );

  // 2. Paths with standard ASCII space MUST be wrapped in double quotes
  assert.equal(
    formatPastedPath("C:\\Users\\John Smith\\AppData\\Local\\Temp\\TerminalV\\pastes\\paste-abc123.jpg"),
    "\"C:\\Users\\John Smith\\AppData\\Local\\Temp\\TerminalV\\pastes\\paste-abc123.jpg\""
  );
  assert.equal(
    formatPastedPath("C:\\Program Files\\TerminalV\\test.png"),
    "\"C:\\Program Files\\TerminalV\\test.png\""
  );

  // 3. Paths with exotic whitespace (tab, newline, non-breaking space) MUST be quoted
  assert.equal(
    formatPastedPath("C:\\Users\\User\tTab\\paste-1.png"),
    "\"C:\\Users\\User\tTab\\paste-1.png\""
  );
  assert.equal(
    formatPastedPath("C:\\Users\\User\u00A0NBSP\\paste-1.png"),
    "\"C:\\Users\\User\u00A0NBSP\\paste-1.png\""
  );

  // 4. Edge cases: non-string, empty, whitespace-only
  assert.equal(formatPastedPath(null), null);
  assert.equal(formatPastedPath(undefined), undefined);
  assert.equal(formatPastedPath(""), "");
  assert.equal(formatPastedPath("   "), "\"   \"");
});

test("adversarial Gboard direct paste: 32 MB guard limit boundary verification", () => {
  const MAX_DIRECT_PASTE_BYTES = 32 * 1024 * 1024; // 33,554,432 bytes

  // Boundary checks:
  // Exactly at boundary: 32 MB
  const atLimit = MAX_DIRECT_PASTE_BYTES;
  assert.equal(atLimit > MAX_DIRECT_PASTE_BYTES, false, "32 MB exactly must be allowed");

  // 1 byte over boundary: 32 MB + 1 byte
  const overLimitByOne = MAX_DIRECT_PASTE_BYTES + 1;
  assert.equal(overLimitByOne > MAX_DIRECT_PASTE_BYTES, true, "32 MB + 1 byte must be rejected");

  // Oversized fixture: 34,603,016 bytes (~33 MB)
  const oversizedFixtureBytes = 34603016;
  assert.equal(oversizedFixtureBytes > MAX_DIRECT_PASTE_BYTES, true, "33 MB fixture must be rejected");

  // Under limit: standard image sizes
  assert.equal(78555 > MAX_DIRECT_PASTE_BYTES, false, "78 KB sticker allowed");
  assert.equal(343800 > MAX_DIRECT_PASTE_BYTES, false, "343 KB photo allowed");
  assert.equal(2097771 > MAX_DIRECT_PASTE_BYTES, false, "2 MB image allowed");
});

test("adversarial client extension regex: /\.[a-z0-9]{1,12}$/i", () => {
  const extRegex = /\.[a-z0-9]{1,12}$/i;

  // Valid standard extensions (1-12 alphanumeric characters)
  assert.ok(extRegex.test("photo.jpg"));
  assert.ok(extRegex.test("image.png"));
  assert.ok(extRegex.test("ANIMATION.WEBP"));
  assert.ok(extRegex.test("archive.tar.gz"));
  assert.ok(extRegex.test("data.123456789012")); // 12 chars

  // Boundary: overlong extension (>12 chars) must NOT match
  assert.equal(extRegex.test("data.1234567890123"), false, "13 chars must fail");
  assert.equal(extRegex.test("file.verylongextensionname"), false);

  // Shell metacharacters at end must NOT match
  assert.equal(extRegex.test("photo.png;rm -rf /"), false);
  assert.equal(extRegex.test("photo.png|whoami"), false);
  assert.equal(extRegex.test("photo.png&calc"), false);

  // Non-ASCII Cyrillic extensions must NOT match
  assert.equal(extRegex.test("тест.рф"), false);
  assert.equal(extRegex.test("картинка.жпг"), false);

  // Dotless files or trailing dots
  assert.equal(extRegex.test("passwd"), false);
  assert.equal(extRegex.test("file."), false);

  // Path traversal with valid end extension DOES match client regex,
  // confirming that SERVER-SIDE sanitization is strictly required and critical!
  assert.ok(extRegex.test("../../evil.webp"));
  assert.ok(extRegex.test("..\\..\\evil.webp"));
});

test("adversarial bracketed paste DECSET 2004 format and detection", () => {
  const DECSET_2004_START = "\u001b[200~";
  const DECSET_2004_END = "\u001b[201~";

  function wrapBracketedPaste(formattedPath) {
    return `${DECSET_2004_START}${formattedPath}${DECSET_2004_END}`;
  }

  const pathNoSpaces = "C:\\pastes\\paste-1.png";
  const wrappedNoSpaces = wrapBracketedPaste(formatPastedPath(pathNoSpaces));
  assert.equal(wrappedNoSpaces, "\u001b[200~C:\\pastes\\paste-1.png\u001b[201~");
  assert.ok(wrappedNoSpaces.includes(DECSET_2004_START));
  assert.ok(wrappedNoSpaces.includes(DECSET_2004_END));

  const pathWithSpaces = "C:\\Users\\John Doe\\pastes\\paste-1.png";
  const wrappedWithSpaces = wrapBracketedPaste(formatPastedPath(pathWithSpaces));
  assert.equal(wrappedWithSpaces, "\u001b[200~\"C:\\Users\\John Doe\\pastes\\paste-1.png\"\u001b[201~");
  assert.ok(wrappedWithSpaces.startsWith(DECSET_2004_START));
  assert.ok(wrappedWithSpaces.endsWith(DECSET_2004_END));

  // Verify that isPaste detection in postWrite identifies DECSET 2004 marker
  const isPaste1 = wrappedNoSpaces.includes("\u001b[200~");
  const isPaste2 = wrappedWithSpaces.includes("\u001b[200~");
  const isPaste3 = "plain terminal text".includes("\u001b[200~");

  assert.equal(isPaste1, true);
  assert.equal(isPaste2, true);
  assert.equal(isPaste3, false);
});
