// Large pastes are stored to a temp file by the host; the terminal inserts
// the file path instead of the raw text. Some TUI inputs digest big pastes
// slowly (fixed-size slices with per-slice cost), a path pastes instantly.
export const PASTE_FILE_THRESHOLD = 4000;
export const PASTE_FILE_STORE_TIMEOUT_MS = 5000;

export function shouldStoreAsFile(text) {
  return typeof text === "string" && text.length > PASTE_FILE_THRESHOLD;
}
