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

  // Export/import wiring for settings backup (JSON, Share)
  (function setupExportImport() {
    function post(type) {
      const wv = window.chrome && window.chrome.webview;
      if (wv && wv.postMessage) wv.postMessage({ type, requestId: Math.random().toString(36).slice(2) });
      else if (window.DotNet) window.DotNet.invokeMethodAsync("TerminalV.Mobile", "HandleMessage", JSON.stringify({ type, requestId: Math.random().toString(36).slice(2) }));
    }
    function bind() {
      const exp = document.getElementById('export-json');
      const imp = document.getElementById('import-json');
      const status = document.getElementById('export-status');
      if (exp && !exp.dataset.bound) {
        exp.dataset.bound = "1";
        exp.addEventListener('click', () => {
          if (status) status.textContent = "Экспорт…";
          post("export");
        });
      }
      if (imp && !imp.dataset.bound) {
        imp.dataset.bound = "1";
        imp.addEventListener('click', () => {
          if (status) status.textContent = "Выбери файл…";
          post("import");
        });
      }
      // Listen for host replies to update status
      const wv = window.chrome && window.chrome.webview;
      if (wv && !wv._exportImportHooked) {
        wv._exportImportHooked = true;
        wv.addEventListener("message", (e) => {
          const m = e.data;
          if (!m || (m.type !== "exported" && m.type !== "imported")) return;
          if (status) status.textContent = m.error ? ("Ошибка: " + m.error) : (m.message || "Готово");
        });
      }
    }
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", bind);
    else bind();
    // Re-bind when settings dialog opens
    new MutationObserver(bind).observe(document.documentElement, { childList: true, subtree: true });
  })();

  ensureChrome();

  // Mobile collapsed sidebar: completely hidden + swipe from left edge
  (function setupSwipe() {
    const EDGE = 24;
    let startX = 0, startY = 0, tracking = false;
    const app = () => document.getElementById('app');
    const handle = () => document.getElementById('sidebar-swipe-handle');
    function isMobile() { return window.matchMedia('(max-width: 700px)').matches; }
    function setCollapsed(v) {
      const a = app(); if (!a) return;
      a.classList.toggle('collapsed', !!v);
      try { localStorage.setItem('terminalv.sidebarCollapsed', v ? '1' : '0'); } catch {}
      // Close overlay when opening sidebar
      if (!v) { a.addEventListener('click', onOverlayClick, { once: true }); }
    }
    function onOverlayClick(e) {
      const a = app(); if (!a || a.classList.contains('collapsed')) return;
      // Click on dimmed overlay (pseudo ::after) is not directly targetable, so detect click on #main when sidebar open
      const sidebar = document.getElementById('sidebar');
      if (sidebar && !sidebar.contains(e.target) && !e.target.closest('#sidebar')) setCollapsed(true);
    }
    document.addEventListener('click', (e) => {
      if (!isMobile()) return;
      const a = app(); if (!a || a.classList.contains('collapsed')) return;
      if (e.target.closest('#collapse-btn')) { setCollapsed(true); }
    });
    // Also hook collapse button to use our drawer logic on mobile
    document.addEventListener('DOMContentLoaded', () => {
      const btn = document.getElementById('collapse-btn');
      if (btn) btn.addEventListener('click', (e) => {
        if (!isMobile()) return;
        e.preventDefault(); e.stopPropagation();
        const a = app(); setCollapsed(!a.classList.contains('collapsed'));
      });
    });
    window.addEventListener('touchstart', (e) => {
      if (!isMobile()) return;
      const t = e.touches[0]; if (!t) return;
      const a = app(); const h = handle();
      const collapsed = a && a.classList.contains('collapsed');
      // Start tracking only from left edge when collapsed, or anywhere on sidebar/main when open
      if (collapsed) {
        if (t.clientX > EDGE) return;
      } else {
        // When open, allow swipe to close from anywhere on sidebar
        const sb = document.getElementById('sidebar');
        if (!sb || (!sb.contains(e.target) && t.clientX > sb.getBoundingClientRect().right + 20)) return;
      }
      startX = t.clientX; startY = t.clientY; tracking = true;
    }, { passive: true });
    window.addEventListener('touchmove', (e) => {
      if (!tracking || !isMobile()) return;
      const t = e.touches[0]; if (!t) return;
      const dx = t.clientX - startX; const dy = Math.abs(t.clientY - startY);
      if (dy > 40 && Math.abs(dx) < 20) { tracking = false; return; }
    }, { passive: true });
    window.addEventListener('touchend', (e) => {
      if (!tracking || !isMobile()) { tracking = false; return; }
      const t = e.changedTouches[0]; if (!t) { tracking = false; return; }
      const dx = t.clientX - startX;
      const a = app();
      if (a && a.classList.contains('collapsed') && dx > 60) setCollapsed(false);
      else if (a && !a.classList.contains('collapsed') && dx < -60) setCollapsed(true);
      tracking = false;
    }, { passive: true });
    // Expose for manual toggle
    window.__terminalvToggleSidebar = () => { const a = app(); if (a) setCollapsed(!a.classList.contains('collapsed')); };
  })();

  ensureChrome();
  // Expose helper for C# to set DotNet ref if needed
  window.__mobileBridgeReady = true;
})();
