// Mobile shim for window.chrome.webview <-> Blazor DotNet
(function () {
  const queue = [];
  const dispatchQueue = [];
  const listeners = [];
  window.__tvListeners = listeners;
  window.__tvDispatch = function (json) {
    if (listeners.length === 0) { dispatchQueue.push(json); return; }
    let msg;
    try { msg = JSON.parse(json); } catch { return; }
    const event = { data: msg };
    listeners.slice().forEach((cb) => { try { cb(event); } catch (e) { console.error(e); } });
  };
  function flushDispatchQueue() {
    while (dispatchQueue.length && listeners.length) {
      const json = dispatchQueue.shift();
      let msg;
      try { msg = JSON.parse(json); } catch { continue; }
      const event = { data: msg };
      listeners.slice().forEach((cb) => { try { cb(event); } catch (e) { console.error(e); } });
    }
  }

  function flushQueue() {
    if (!window.DotNet || !DotNet.invokeMethodAsync) return false;
    while (queue.length) {
      const json = queue.shift();
      DotNet.invokeMethodAsync("TerminalV.Mobile", "HandleMessage", json).catch(() => {});
    }
    return true;
  }
  // Periodically flush if DotNet becomes available late (Blazor startup race)
  setInterval(flushQueue, 300);

  function ensureChrome() {
    window.chrome = window.chrome || {};
    if (!window.chrome.webview) {
      window.chrome.webview = {
        postMessage: function (msg) {
          try {
            const json = typeof msg === "string" ? msg : JSON.stringify(msg);
            if (window.DotNet && DotNet.invokeMethodAsync) {
              if (queue.length) flushQueue();
              DotNet.invokeMethodAsync("TerminalV.Mobile", "HandleMessage", json).catch(() => {
                queue.push(json);
              });
            } else if (window.__mobileDotNetRef) {
              window.__mobileDotNetRef.invokeMethodAsync("HandleMessage", json).catch(() => {});
            } else {
              queue.push(json);
            }
          } catch (e) { console.error("[mobile-bridge] postMessage", e); }
        },
        addEventListener: function (type, cb) {
          if (type === "message" && typeof cb === "function") { listeners.push(cb); flushDispatchQueue(); }
        },
        removeEventListener: function (type, cb) {
          if (type === "message") {
            const idx = listeners.indexOf(cb);
            if (idx >= 0) listeners.splice(idx, 1);
          }
        }
      };
    }
  }

  ensureChrome();
  // Expose helper for C# to set DotNet ref if needed
  window.__mobileBridgeReady = true;
})();
