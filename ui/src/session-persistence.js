// A saved screen or host replay reaches xterm asynchronously. Never replace the
// last snapshot with the empty terminal that exists before its first parse.
export function serializeSessionBuffer(tab) {
  if (!tab.output.hasParsed && tab.buffer) return tab.buffer;
  try {
    tab.buffer = tab.serialize.serialize({ excludeAltBuffer: false, excludeModes: false });
  } catch {
    // Keep the most recent successful snapshot if serialization fails.
  }
  return tab.buffer || "";
}

export function createSessionPersistence({ isReady, getTabs, save, saveSettings, skipped,
  delay = 400, maxDelay = 2000,
  setTimer = (callback, ms) => setTimeout(callback, ms),
  clearTimer = (timer) => clearTimeout(timer) }) {
  let timer = null;
  let deadline = null;

  function cancel() {
    clearTimer(timer);
    clearTimer(deadline);
    timer = deadline = null;
  }

  function persist(requestId, settings = false) {
    cancel();
    if (!isReady()) {
      if (requestId) skipped(requestId);
      return;
    }
    const commit = () => {
      if (settings) saveSettings(requestId);
      save(requestId);
    };
    const pending = getTabs().filter(tab => tab.output.pending);
    if (!pending.length) {
      commit();
      return;
    }
    // Queue a barrier behind the writes already received, rather than waiting
    // for a quiet terminal (a running log stream might never become quiet).
    let remaining = pending.length;
    for (const tab of pending) {
      tab.output.whenParsed(() => { if (--remaining === 0) commit(); });
    }
  }

  return {
    persist,
    schedule() {
      if (!isReady()) return;
      clearTimer(timer);
      timer = setTimer(() => persist(), delay);
      if (deadline === null) deadline = setTimer(() => persist(), maxDelay);
    },
    flush(requestId) { persist(requestId, true); }
  };
}
