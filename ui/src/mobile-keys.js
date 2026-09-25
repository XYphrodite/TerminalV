// Mobile-only key bar: on-screen arrows/Esc/Tab for soft keyboards without cursor keys.
// Sends raw escape sequences straight to the pty through the same "write"
// channel as typed input, bypassing window keydown shortcuts (so Alt+Arrow
// pane navigation can never trigger from these buttons).
export const MOBILE_KEY_SEQUENCES = {
  Escape: "\x1b",
  Esc: "\x1b",
  Tab: "\t",
  ArrowLeft: "\x1b[D",
  ArrowUp: "\x1b[A",
  ArrowDown: "\x1b[B",
  ArrowRight: "\x1b[C",
  Left: "\x1b[D",
  Up: "\x1b[A",
  Down: "\x1b[B",
  Right: "\x1b[C",
  "0": "0",
  "1": "1",
  "2": "2",
  "3": "3",
  "4": "4",
  "5": "5",
  "6": "6",
  "7": "7",
  "8": "8",
  "9": "9"
};

export function createMobileKeys({ root, send }) {
  if (!root || typeof send !== "function") return { sendKey() {} };
  function sendKey(key) {
    if (!Object.hasOwn(MOBILE_KEY_SEQUENCES, key)) return;
    send(key);
  }
  for (const button of root.querySelectorAll("[data-key]")) {
    let pointerHandled = false;
    // pointerdown keeps the soft keyboard open: no focus change, no click delay.
    button.addEventListener("pointerdown", (event) => {
      event.preventDefault();
      pointerHandled = true;
      sendKey(button.dataset.key);
    });
    // Keyboard users (Tab + Enter) only produce click; the flag drops the
    // duplicate when pointerdown already sent the key.
    button.addEventListener("click", () => {
      if (pointerHandled) { pointerHandled = false; return; }
      sendKey(button.dataset.key);
    });
  }
  return { sendKey };
}
