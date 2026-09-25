// Mobile shim for window.chrome.webview <-> Blazor DotNet
(function () {
  const queue = [];
  const dispatchQueue = [];
  const listeners = [];
  let connection = { state: "unconfigured", name: "" };
  let configOpen = false;
  let previousFocus = null;
  window.__tvListeners = listeners;
  window.__tvDispatch = function (json) {
    let msg;
    try { msg = JSON.parse(json); } catch { return; }
    // Connection state belongs to the mobile shell, including updates before DOM ready.
    if (msg?.type === "connection-state") { connection = msg; renderConnection(); return; }
    if (listeners.length === 0) { dispatchQueue.push(json); return; }
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

  let dotNetRef = null;
  let sending = false;
  async function flushQueue() {
    if (!dotNetRef || sending) return;
    sending = true;
    try {
      while (queue.length && dotNetRef) {
        const json = queue.shift();
        try { await dotNetRef.invokeMethodAsync("HandleMessage", json); }
        catch (error) { console.error("[mobile-bridge] HandleMessage", error); }
      }
    } finally { sending = false; }
  }

  // Keep the native chrome.webview transport intact: Blazor itself uses it on Windows.
  window.__terminalvHost = {
    postMessage(msg) {
      const json = typeof msg === "string" ? msg : JSON.stringify(msg);
      let type;
      try { type = typeof msg === "string" ? JSON.parse(msg)?.type : msg?.type; } catch {}
      // UI controls must stay responsive while an SSH write waits for a connection.
      if (dotNetRef && (type === "open-connection" || type === "close-connection")) {
        void dotNetRef.invokeMethodAsync("HandleMessage", json)
          .catch(error => console.error("[mobile-bridge] connection dialog", error));
        return;
      }
      queue.push(json);
      void flushQueue();
    },
    addEventListener(type, cb) {
      if (type === "message" && typeof cb === "function") {
        listeners.push(cb);
        flushDispatchQueue();
      }
    },
    removeEventListener(type, cb) {
      const index = listeners.indexOf(cb);
      if (type === "message" && index >= 0) listeners.splice(index, 1);
    }
  };
  window.__tvSetBridge = function (reference) {
    dotNetRef = reference;
    void flushQueue();
  };

  function renderConnection() {
    const button = document.getElementById('mobile-connection');
    if (!button) return;
    const labels = { unconfigured: 'Выбрать компьютер', idle: 'Не подключено',
      connecting: 'Подключение…', connected: 'Подключено', disconnected: 'Нет связи' };
    const state = Object.hasOwn(labels, connection.state) ? connection.state : 'disconnected';
    const name = state === 'unconfigured' ? 'Подключиться к компьютеру'
      : (typeof connection.name === 'string' && connection.name.trim() ? connection.name.trim() : 'Компьютер');
    button.dataset.state = state;
    document.getElementById('mobile-connection-name').textContent = name;
    document.getElementById('mobile-connection-status').textContent = labels[state];
    button.title = `${name} · ${labels[state]}`;
    button.setAttribute('aria-label', `${name}, ${labels[state]}. Открыть подключение`);
  }

  window.__tvConnectionConfig = function (show) {
    const wasOpen = configOpen;
    configOpen = !!show;
    const app = document.getElementById('app');
    const handle = document.getElementById('sidebar-swipe-handle');
    const button = document.getElementById('mobile-connection');
    if (app) app.inert = configOpen;
    if (handle) handle.inert = configOpen;
    button?.setAttribute('aria-expanded', String(configOpen));
    if (configOpen && !wasOpen) {
      previousFocus = document.activeElement;
      previousFocus?.blur();
      updateConnectionViewport();
      document.getElementById('connection-close')?.focus({ preventScroll: true });
      window.__terminalvHost.postMessage({ type: 'connection-check' });
    } else if (!configOpen && wasOpen) {
      const visible = element => element?.isConnected && element.getClientRects().length > 0 && !element.disabled;
      const target = visible(previousFocus) ? previousFocus :
        (visible(button) ? button : document.getElementById('mobile-sessions-open'));
      target?.focus({ preventScroll: true });
      previousFocus = null;
    }
  };

  function updateConnectionViewport() {
    const dialog = document.getElementById('mobile-config-host');
    const viewport = window.visualViewport;
    if (!configOpen || !dialog || !viewport) return;
    dialog.style.setProperty('--connection-viewport-height', `${viewport.height}px`);
    dialog.style.setProperty('--connection-viewport-top', `${viewport.offsetTop}px`);
  }
  window.visualViewport?.addEventListener('resize', updateConnectionViewport);
  window.visualViewport?.addEventListener('scroll', updateConnectionViewport);

  function bindConnection() {
    renderConnection();
    document.getElementById('mobile-connection')?.addEventListener('click', () => {
      window.__terminalvHost.postMessage({ type: 'open-connection' });
    });
    document.getElementById('mobile-sessions-open')?.addEventListener('click', () => {
      window.__terminalvToggleSidebar();
      document.getElementById('collapse-btn')?.focus({ preventScroll: true });
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', bindConnection, { once: true });
  else bindConnection();

  function refreshCatalog() {
    if (document.hidden || !dotNetRef || !['connected', 'disconnected'].includes(connection.state)) return;
    window.__terminalvHost.postMessage({ type: 'connection-check' });
  }
  // Keep the desktop tab catalog current while the phone is in use. Suspended
  // WebViews do no polling, and foregrounding refreshes immediately.
  window.setInterval(refreshCatalog, 15000);
  document.addEventListener('visibilitychange', refreshCatalog);

  // This classic script loads before the deferred desktop module, whose shortcuts
  // also capture on window. Register at the same level first so none can escape.
  window.addEventListener('keydown', event => {
    if (!configOpen) return;
    // Keep desktop document shortcuts out of the connection form. Text input's
    // default behavior is preserved; only Tab/Escape need their own handling.
    event.stopImmediatePropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      window.__terminalvHost.postMessage({ type: 'close-connection' });
    } else if (event.key === 'Tab') {
      const dialog = document.getElementById('mobile-config-host');
      const controls = Array.from(dialog?.querySelectorAll('button, input, select, textarea, summary, a[href], [tabindex="0"]') || [])
        .filter(element => !element.disabled && element.getClientRects().length > 0);
      const first = controls[0], last = controls.at(-1);
      if (first && (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
        event.preventDefault(); (event.shiftKey ? last : first).focus();
      }
    }
  }, true);

  // Export/import wiring for settings backup (JSON, Share)
  (function setupExportImport() {
    function post(type) {
      const wv = window.__terminalvHost;
      if (wv && wv.postMessage) wv.postMessage({ type, requestId: Math.random().toString(36).slice(2) });
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
      const wv = window.__terminalvHost;
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


  // Mobile collapsed sidebar: completely hidden + swipe from left edge
  (function setupSwipe() {
    const EDGE = 24;
    let startX = 0, startY = 0, tracking = false;
    const app = () => document.getElementById('app');
    const handle = () => document.getElementById('sidebar-swipe-handle');
    function isMobile() { return window.matchMedia('(max-width: 700px)').matches; }
    let previousCollapsed;
    window.__tvSidebarState = function (collapsed) {
      document.getElementById('mobile-sessions-open')?.setAttribute('aria-expanded', String(!collapsed));
      document.getElementById('collapse-btn')?.setAttribute('aria-expanded', String(!collapsed));
      if (previousCollapsed === true && !collapsed) window.__terminalvHost.postMessage({ type: 'connection-check' });
      previousCollapsed = !!collapsed;
    };
    function setCollapsed(v) {
      if (configOpen) return;
      if (window.__terminalvSetSidebarCollapsed) { window.__terminalvSetSidebarCollapsed(!!v); return; }
      const a = app(); if (!a) return;
      a.classList.toggle('collapsed', !!v);
      window.__tvSidebarState(!!v);
    }
    function onOverlayClick(e) {
      const a = app(); if (!a || a.classList.contains('collapsed')) return;
      // Click on dimmed overlay (pseudo ::after) is not directly targetable, so detect click on #main when sidebar open
      const sidebar = document.getElementById('sidebar');
      if (sidebar && !sidebar.contains(e.target) && !e.target.closest('#sidebar')) setCollapsed(true);
    }
    // Capture the drawer action before the shared desktop toggle to avoid toggling twice.
    document.addEventListener('click', (e) => {
      if (!isMobile() || configOpen) return;
      if (e.target.closest('#collapse-btn')) {
        e.preventDefault(); e.stopImmediatePropagation();
        const a = app(); if (a) setCollapsed(!a.classList.contains('collapsed'));
      } else onOverlayClick(e);
    }, true);
    window.addEventListener('touchstart', (e) => {
      if (!isMobile() || configOpen) return;
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

})();
