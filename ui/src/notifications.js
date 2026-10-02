export const GLOBAL_BELL_INTERVAL = 2000;
export const SESSION_BELL_INTERVAL = 5000;

export function createNotifications({ isKnown, isActive, sound, changed, now = () => performance.now() }) {
  const lastSound = new WeakMap();
  let lastGlobalSound = -Infinity;
  return {
    bell(tab) {
      if (!isKnown(tab) || tab.exited || isActive(tab)) return;
      if (!tab.attention) {
        tab.attention = true;
        changed(tab);
      }
      // Muting affects sound, not the visual signal, and consumes no cooldown.
      if (tab.muted) return;
      const time = now();
      if (time - lastGlobalSound < GLOBAL_BELL_INTERVAL ||
          time - (lastSound.get(tab) ?? -Infinity) < SESSION_BELL_INTERVAL) return;
      lastGlobalSound = time;
      lastSound.set(tab, time);
      sound(tab);
    },
    acknowledge(tab) {
      if (!tab?.attention) return;
      tab.attention = false;
      changed(tab);
    }
  };
}

// xterm parses writes asynchronously. A flag around term.write() itself would
// incorrectly notify for history or suppress subsequent live output. Empty
// writes provide ordered callbacks in xterm's existing queue, without timers
// or a second output buffer. No terminal bytes are added or removed.
// ESC[3J (ED3) erases scrollback. Inline TUIs such as Kimi use 2J 3J as a full
// frame redraw: xterm clamps ydisp to 0 and can leave isUserScrolling stuck, so
// the re-dumped frame is parked at the top instead of the live screen. The
// reader's history is gone either way; follow the live screen after the write.
const ED3_RE = /(?:\x1b\[|\x1b\[\?)[\d;]*3J/;

export function createNotificationOutput(term, onBell, onParsed = () => {}) {
  let replay = false;
  let disposed = false;
  let generation = 0;
  let pending = 0;
  let hasParsed = false;
  const barriers = new Set();
  const subscription = term.onBell(() => { if (!disposed && !replay) onBell(); });
  const followLiveScreen = () => {
    try { term.scrollToBottom(); } catch {}
  };
  return {
    get pending() { return !disposed && pending > 0; },
    get hasParsed() { return hasParsed; },
    write(data, historical = false) {
      if (disposed) return;
      const version = generation;
      const followsAfterClear = !historical && ED3_RE.test(data);
      pending++;
      term.write("", () => { replay = historical || version !== generation; });
      term.write(data, () => {
        pending--;
        if (!disposed && version === generation) {
          hasParsed = true;
          if (followsAfterClear) {
            followLiveScreen();
            // A viewport scrollbar refresh runs on the next frame and can flip
            // isUserScrolling again; pin the live screen once more after paint.
            try {
              requestAnimationFrame(() => {
                if (!disposed && version === generation) followLiveScreen();
              });
            } catch {}
          }
          onParsed();
        }
      });
    },
    whenParsed(callback) {
      if (disposed || !pending) { callback(); return; }
      const finish = () => { if (barriers.delete(finish)) callback(); };
      barriers.add(finish);
      term.write("", finish);
    },
    reset() {
      if (disposed) return;
      generation++;
      replay = true;
      pending++;
      term.write("", () => {
        pending--;
        if (!disposed) {
          term.reset();
          hasParsed = true;
          onParsed();
        }
      });
    },
    dispose() {
      disposed = true;
      subscription.dispose();
      for (const finish of barriers) finish();
    }
  };
}
