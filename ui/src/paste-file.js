// Large pastes are stored to a temp file by the host; the terminal inserts
// the file path instead of the raw text. Some TUI inputs digest big pastes
// slowly (fixed-size slices with per-slice cost), a path pastes instantly.
// The same shape serves image-only clipboards: the host stores a temp PNG and
// the path is pasted — hermes --tui and other TUI chats attach the image when
// a path starts the input.
export const PASTE_FILE_THRESHOLD = 4000;
export const PASTE_FILE_STORE_TIMEOUT_MS = 5000;

export function shouldStoreAsFile(text) {
  return typeof text === "string" && text.length > PASTE_FILE_THRESHOLD;
}

// A pasted path must stay one token for path detectors (quotes, not escapes),
// and a quoted path is also a valid argument in PowerShell and bash.
export function formatPastedPath(path) {
  return typeof path === "string" && /\s/.test(path) ? `"${path}"` : path;
}

// Clipboard text always wins; a stored file path is the fallback for empty
// text (image-only clipboard). No text and no image pastes nothing.
export function pasteTextForClipboard(text, storedPath) {
  if (typeof text === "string" && text.length > 0) {
    return text;
  }
  return storedPath ? formatPastedPath(storedPath) : "";
}
