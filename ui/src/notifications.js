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
export function createNotificationOutput(term, onBell, onParsed = () => {}) {
  let replay = false;
  let disposed = false;
  let generation = 0;
  let pending = 0;
  let hasParsed = false;
  const barriers = new Set();
  const subscription = term.onBell(() => { if (!disposed && !replay) onBell(); });
  return {
    get pending() { return !disposed && pending > 0; },
    get hasParsed() { return hasParsed; },
    write(data, historical = false) {
      if (disposed) return;
      const version = generation;
      pending++;
      term.write("", () => { replay = historical || version !== generation; });
      term.write(data, () => {
        pending--;
        if (!disposed && version === generation) {
          hasParsed = true;
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
