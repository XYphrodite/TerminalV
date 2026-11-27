// Mobile-only key bar: on-screen keys and sticky Ctrl/Alt modifiers for soft keyboards.
// Sticky modifiers apply both to taps on the key bar and to regular keyboard input
// coming through xterm's onData (e.g. tapping Alt then typing "m" on Gboard -> Alt+M).
const BASE_KEY_SEQUENCES = {
  Escape: "\x1b",
  Esc: "\x1b",
  Tab: "\t",
  Enter: "\r",
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

const CYRILLIC_TO_LATIN = {
  "й": "q", "ц": "w", "у": "e", "к": "r", "е": "t", "н": "y", "г": "u", "ш": "i", "щ": "o", "з": "p", "х": "[", "ъ": "]",
  "ф": "a", "ы": "s", "в": "d", "а": "f", "п": "g", "р": "h", "о": "j", "л": "k", "д": "l", "ж": ";", "э": "'",
  "я": "z", "ч": "x", "с": "c", "м": "v", "и": "b", "т": "n", "ь": "m", "б": ",", "ю": ".", "ё": "`",
  "Й": "Q", "Ц": "W", "У": "E", "К": "R", "Е": "T", "Н": "Y", "Г": "U", "Ш": "I", "Щ": "O", "З": "P", "Х": "{", "Ъ": "}",
  "Ф": "A", "Ы": "S", "В": "D", "А": "F", "П": "G", "Р": "H", "О": "J", "Л": "K", "Д": "L", "Ж": ":", "Э": '"',
  "Я": "Z", "Ч": "X", "С": "C", "М": "V", "И": "B", "Т": "N", "Ь": "M", "Б": "<", "Ю": ">", "Ё": "~",
};

const CTRL_SYMBOL_MAP = {
  " ": "\x00",
  "@": "\x00",
  "2": "\x00",
  "[": "\x1b",
  "3": "\x1b",
  "\\": "\x1c",
  "4": "\x1c",
  "]": "\x1d",
  "5": "\x1d",
  "^": "\x1e",
  "6": "\x1e",
  "~": "\x1e",
  "_": "\x1f",
  "7": "\x1f",
  "-": "\x1f",
  "/": "\x1f",
  "?": "\x7f",
  "8": "\x7f",
};

// Terminal protocol replies and mouse tracking emitted via xterm onData must
// never accidentally consume a user's sticky Ctrl/Alt before the actual key.
function isTerminalProtocolReport(data) {
  if (data === "\x1b[I" || data === "\x1b[O") return true;
  if (data.startsWith("\x1b[200~") || data.startsWith("\x1b[201~")) return true;
  if (data.startsWith("\x1b]")) return true;
  if (data.startsWith("\x1b[M")) return true;
  return /^\x1b\[<\d+;\d+;\d+[Mm]$/.test(data)
    || /^\x1b\[\??[\d;]*R$/.test(data)
    || /^\x1b\[[>?][\d;]*c$/.test(data);
}

function encodeModifiedChar(ch, { ctrl = false, alt = false }) {
  const mapped = CYRILLIC_TO_LATIN[ch] ?? ch;
  let out = mapped;
  if (ctrl) {
    const code = mapped.charCodeAt(0);
    if ((code >= 65 && code <= 90) || (code >= 97 && code <= 122)) {
      out = String.fromCharCode(code & 0x1f);
    } else if (Object.hasOwn(CTRL_SYMBOL_MAP, mapped)) {
      out = CTRL_SYMBOL_MAP[mapped];
    }
  }
  return alt ? `\x1b${out}` : out;
}

export function applyTerminalModifiers(data, { ctrl = false, alt = false } = {}) {
  if ((!ctrl && !alt) || typeof data !== "string" || data.length === 0) {
    return { data, consumed: false };
  }
  if (isTerminalProtocolReport(data)) {
    return { data, consumed: false };
  }

  const modBits = (alt ? 2 : 0) + (ctrl ? 4 : 0);

  const cursorMatch = /^\x1b\[(?:1;(\d+))?([A-DFH])$/.exec(data);
  if (cursorMatch) {
    const baseBits = cursorMatch[1] ? Math.max(0, Number(cursorMatch[1]) - 1) : 0;
    return { data: `\x1b[1;${(baseBits | modBits) + 1}${cursorMatch[2]}`, consumed: true };
  }

  const tildeMatch = /^\x1b\[(\d+)(?:;(\d+))?~$/.exec(data);
  if (tildeMatch) {
    const baseBits = tildeMatch[2] ? Math.max(0, Number(tildeMatch[2]) - 1) : 0;
    return { data: `\x1b[${tildeMatch[1]};${(baseBits | modBits) + 1}~`, consumed: true };
  }

  const ss3Match = /^\x1bO([PQRS])$/.exec(data);
  if (ss3Match) {
    return { data: `\x1b[1;${modBits + 1}${ss3Match[1]}`, consumed: true };
  }

  if (data === "\x1b[Z") {
    return { data: alt ? "\x1b\x1b[Z" : data, consumed: true };
  }
  if (data === "\x1b") {
    return { data, consumed: true };
  }
  if (data === "\t" || data === "\r" || data === "\n") {
    return { data: alt ? `\x1b${data}` : data, consumed: true };
  }
  if (data === "\x7f" || data === "\x08") {
    const key = ctrl ? "\x08" : data;
    return { data: alt ? `\x1b${key}` : key, consumed: true };
  }
  if (data.startsWith("\x1b")) {
    return { data: alt && !data.startsWith("\x1b\x1b") ? `\x1b${data}` : data, consumed: true };
  }

  const chars = Array.from(data);
  const first = encodeModifiedChar(chars[0], { ctrl, alt });
  return { data: first + chars.slice(1).join(""), consumed: true };
}

export const MOBILE_KEY_SEQUENCES = { ...BASE_KEY_SEQUENCES, "Alt+Enter": "\x1b\r", AltEnter: "\x1b\r" };
for (const [key, baseSeq] of Object.entries(BASE_KEY_SEQUENCES)) {
  for (const [prefix, mods] of [
    ["Ctrl+", { ctrl: true }],
    ["Alt+", { alt: true }],
    ["Ctrl+Alt+", { ctrl: true, alt: true }],
  ]) {
    const name = `${prefix}${key}`;
    if (!Object.hasOwn(MOBILE_KEY_SEQUENCES, name)) {
      MOBILE_KEY_SEQUENCES[name] = applyTerminalModifiers(baseSeq, mods).data;
    }
  }
}

export function createMobileKeys({ root, handle, send } = {}) {
  if (!root || typeof send !== "function") {
    return {
      sendKey() {},
      applyInput: (data) => data,
      get ctrlActive() { return false; },
      get altActive() { return false; },
      get hasModifiers() { return false; },
    };
  }
  if (root.dataset?.mobileKeysInitialized) {
    return {
      sendKey() {},
      applyInput: (data) => data,
      get ctrlActive() { return false; },
      get altActive() { return false; },
      get hasModifiers() { return false; },
    };
  }
  if (root.dataset) root.dataset.mobileKeysInitialized = "true";

  let ctrlActive = false;
  let altActive = false;
  const resolvedHandle = handle ?? root.ownerDocument?.getElementById?.("mobile-keys-handle") ?? null;
  const handleLabelEl = resolvedHandle?.querySelector?.("span") ?? resolvedHandle;
  const baseHandleLabel = handleLabelEl?.textContent?.trim() || "Клавиши";

  function syncModifierButtons(keyName, active) {
    for (const btn of root.querySelectorAll(`[data-key="${keyName}"]`)) {
      btn.classList?.toggle("active", active);
      btn.setAttribute?.("aria-pressed", active ? "true" : "false");
    }
  }

  function updateModifiersUI() {
    syncModifierButtons("Ctrl", ctrlActive);
    syncModifierButtons("Alt", altActive);

    if (!resolvedHandle) return;
    const activeMods = [];
    if (ctrlActive) activeMods.push("Ctrl");
    if (altActive) activeMods.push("Alt");
    const hasMods = activeMods.length > 0;
    resolvedHandle.classList?.toggle("has-modifiers", hasMods);
    if (resolvedHandle.dataset) {
      resolvedHandle.dataset.modifiers = activeMods.join("+");
    }
    if (handleLabelEl) {
      handleLabelEl.textContent = hasMods ? `${baseHandleLabel} · ${activeMods.join("+")}` : baseHandleLabel;
    }
  }

  function clearModifiers() {
    if (!ctrlActive && !altActive) return;
    ctrlActive = false;
    altActive = false;
    updateModifiersUI();
  }

  function sendKey(key) {
    if (key === "Ctrl") {
      ctrlActive = !ctrlActive;
      updateModifiersUI();
      return;
    }
    if (key === "Alt") {
      altActive = !altActive;
      updateModifiersUI();
      return;
    }
    let resolvedKey = key;
    if (ctrlActive || altActive) {
      const prefix = `${ctrlActive ? "Ctrl+" : ""}${altActive ? "Alt+" : ""}`;
      const combo = `${prefix}${key}`;
      if (!key.startsWith("Ctrl+") && !key.startsWith("Alt+") && Object.hasOwn(MOBILE_KEY_SEQUENCES, combo)) {
        resolvedKey = combo;
      }
      clearModifiers();
    }
    if (!Object.hasOwn(MOBILE_KEY_SEQUENCES, resolvedKey)) return;
    send(resolvedKey);
  }

  function applyInput(data) {
    if (!ctrlActive && !altActive) return data;
    const result = applyTerminalModifiers(data, { ctrl: ctrlActive, alt: altActive });
    if (result.consumed) clearModifiers();
    return result.data;
  }

  const TAP_SLOP_PX = 8;
  let scrolledDuringGesture = false;
  if (typeof root.addEventListener === "function") {
    root.addEventListener("scroll", () => {
      scrolledDuringGesture = true;
    }, true);
  }

  for (const button of root.querySelectorAll("[data-key]")) {
    let activePointer = null;
    let suppressClick = false;
    let clearSuppressTimer = null;

    function resetSuppressClick() {
      suppressClick = false;
      if (clearSuppressTimer !== null) {
        clearTimeout(clearSuppressTimer);
        clearSuppressTimer = null;
      }
    }

    function armSuppressClick() {
      resetSuppressClick();
      suppressClick = true;
      clearSuppressTimer = setTimeout(() => {
        suppressClick = false;
        clearSuppressTimer = null;
      }, 400);
      clearSuppressTimer?.unref?.();
    }

    function samePointer(event) {
      return activePointer !== null &&
        (event.pointerId === undefined || activePointer.id === undefined || event.pointerId === activePointer.id);
    }

    function exceededSlop(event) {
      if (!activePointer) return false;
      const x = event.clientX ?? activePointer.x;
      const y = event.clientY ?? activePointer.y;
      return Math.hypot(x - activePointer.x, y - activePointer.y) > TAP_SLOP_PX;
    }

    button.addEventListener("pointerdown", (event) => {
      if (event.button !== undefined && event.button !== 0) return;
      // Prevent focus transfer so the mobile soft keyboard stays open,
      // but wait for pointerup to distinguish a tap from row scrolling.
      event.preventDefault?.();
      scrolledDuringGesture = false;
      resetSuppressClick();
      activePointer = {
        id: event.pointerId,
        x: event.clientX ?? 0,
        y: event.clientY ?? 0,
        moved: false
      };
    });

    button.addEventListener("pointermove", (event) => {
      if (!samePointer(event)) return;
      if (scrolledDuringGesture || exceededSlop(event)) {
        activePointer.moved = true;
      }
    });

    button.addEventListener("pointercancel", (event) => {
      if (!samePointer(event)) return;
      activePointer = null;
      armSuppressClick();
    });

    button.addEventListener("pointerup", (event) => {
      if (!samePointer(event)) return;
      const moved = activePointer.moved || scrolledDuringGesture || exceededSlop(event);
      activePointer = null;
      armSuppressClick();
      if (!moved) {
        sendKey(button.dataset.key);
      }
    });

    button.addEventListener("click", () => {
      if (suppressClick) {
        resetSuppressClick();
        return;
      }
      if (activePointer) {
        const moved = activePointer.moved || scrolledDuringGesture;
        activePointer = null;
        if (!moved) sendKey(button.dataset.key);
        return;
      }
      sendKey(button.dataset.key);
    });
  }

  return {
    sendKey,
    applyInput,
    get ctrlActive() { return ctrlActive; },
    get altActive() { return altActive; },
    get hasModifiers() { return ctrlActive || altActive; },
  };
}
