// Mobile-only key bar: on-screen keys for soft keyboards without cursor/function keys.
// Sends raw escape sequences straight to the pty through the same "write"
// channel as typed input, bypassing window keydown shortcuts.
export const MOBILE_KEY_SEQUENCES = {
  Escape: "\x1b",
  Esc: "\x1b",
  Tab: "\t",
  Enter: "\r",
  "Alt+Enter": "\x1b\r",
  AltEnter: "\x1b\r",
  ArrowLeft: "\x1b[D",
  ArrowUp: "\x1b[A",
  ArrowDown: "\x1b[B",
  ArrowRight: "\x1b[C",
  Left: "\x1b[D",
  Up: "\x1b[A",
  Down: "\x1b[B",
  Right: "\x1b[C",
  Home: "\x1b[H",
  End: "\x1b[F",
  PageUp: "\x1b[5~",
  PgUp: "\x1b[5~",
  PageDown: "\x1b[6~",
  PgDn: "\x1b[6~",
  Delete: "\x1b[3~",
  Del: "\x1b[3~",
  Insert: "\x1b[2~",
  Ins: "\x1b[2~",
  F1: "\x1bOP",
  F2: "\x1bOQ",
  F3: "\x1bOR",
  F4: "\x1bOS",
  F5: "\x1b[15~",
  F6: "\x1b[17~",
  F7: "\x1b[18~",
  F8: "\x1b[19~",
  F9: "\x1b[20~",
  F10: "\x1b[21~",
  F11: "\x1b[23~",
  F12: "\x1b[24~",
  "Ctrl+ArrowLeft": "\x1b[1;5D",
  "Ctrl+ArrowRight": "\x1b[1;5C",
  "Ctrl+ArrowUp": "\x1b[1;5A",
  "Ctrl+ArrowDown": "\x1b[1;5B",
  "Ctrl+Left": "\x1b[1;5D",
  "Ctrl+Right": "\x1b[1;5C",
  "Ctrl+Up": "\x1b[1;5A",
  "Ctrl+Down": "\x1b[1;5B",
  "0": "0",
  "1": "1",
  "2": "2",
  "3": "3",
  "4": "4",
  "5": "5",
  "6": "6",
  "7": "7",
  "8": "8",
  "9": "9",
};

export function createMobileKeys({ root, send }) {
  if (!root || typeof send !== "function") return { sendKey() {} };
  if (root.dataset?.mobileKeysInitialized) return { sendKey() {}, get ctrlActive() { return false; } };
  if (root.dataset) root.dataset.mobileKeysInitialized = "true";
  let ctrlActive = false;

  function updateCtrlUI() {
    for (const btn of root.querySelectorAll('[data-key="Ctrl"]')) {
      if (ctrlActive) {
        btn.classList.add("active");
        btn.setAttribute("aria-pressed", "true");
      } else {
        btn.classList.remove("active");
        btn.setAttribute("aria-pressed", "false");
      }
    }
  }

  function sendKey(key) {
    if (key === "Ctrl") {
      ctrlActive = !ctrlActive;
      updateCtrlUI();
      return;
    }
    let resolvedKey = key;
    if (ctrlActive) {
      const combo = `Ctrl+${key}`;
      if (Object.hasOwn(MOBILE_KEY_SEQUENCES, combo)) {
        resolvedKey = combo;
      }
      // consume sticky Ctrl after one non-Ctrl key (one-shot)
      ctrlActive = false;
      updateCtrlUI();
    }
    if (!Object.hasOwn(MOBILE_KEY_SEQUENCES, resolvedKey)) return;
    send(resolvedKey);
  }

  for (const button of root.querySelectorAll("[data-key]")) {
    let pointerHandled = false;
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      pointerHandled = true;
      sendKey(button.dataset.key);
    });
    button.addEventListener("click", () => {
      if (pointerHandled) { pointerHandled = false; return; }
      sendKey(button.dataset.key);
    });
  }
  return { sendKey, get ctrlActive() { return ctrlActive; } };
}
