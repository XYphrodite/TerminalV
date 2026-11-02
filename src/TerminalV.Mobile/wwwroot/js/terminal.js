// TerminalV.Mobile - xterm.js isolation bridge
// Works without Blazor; Blazor can call window.terminalInterop later.
(function () {
    const terminals = new Map();

    function createTerminal(id, cols, rows, containerId) {
        const container = document.getElementById(containerId || 'terminal-container');
        if (!container || typeof Terminal === 'undefined') return null;
        if (terminals.has(id)) destroy(id);

        const term = new Terminal({
            cols: cols || 80,
            rows: rows || 24,
            cursorBlink: true,
            fontFamily: 'Cascadia Code, Cascadia Mono, Roboto Mono, Droid Sans Mono, Noto Sans Mono, Consolas, Courier New, monospace',
            fontSize: 14,
            letterSpacing: 0,
            allowProposedApi: true,
            theme: { background: '#0B0D10', foreground: '#E8EEF5', cursor: '#E8EEF5' },
            allowTransparency: false
        });

        let fitAddon = null;
        if (typeof FitAddon !== 'undefined') {
            fitAddon = new FitAddon.FitAddon();
            term.loadAddon(fitAddon);
        }
        if (typeof WebLinksAddon !== 'undefined') {
            term.loadAddon(new WebLinksAddon.WebLinksAddon());
        }

        term.open(container);
        if (fitAddon) fitAddon.fit();

        // Forward user input to host via postMessage-like channel if available
        term.onData(data => {
            // Blazor WebView message channel - isolated from main process
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: 'write', id, data }));
            } else if (window.Blazor) {
                // Blazor interop fallback
                DotNet.invokeMethodAsync('TerminalV.Mobile', 'OnTerminalData', id, data);
            }
        });

        terminals.set(id, { term, fitAddon });
        return term;
    }

    function write(id, data) {
        const entry = terminals.get(id);
        if (entry) entry.term.write(data);
    }

    function resize(id, cols, rows) {
        const entry = terminals.get(id);
        if (entry) entry.term.resize(cols, rows);
        if (entry && entry.fitAddon) entry.fitAddon.fit();
    }

    function measure(id) {
        const entry = terminals.get(id);
        if (!entry) return null;
        if (entry.fitAddon) {
            try {
                const dims = entry.fitAddon.proposeDimensions();
                if (dims && dims.cols > 0 && dims.rows > 0) return { cols: dims.cols, rows: dims.rows };
            } catch { /* fall through */ }
        }
        return { cols: entry.term.cols, rows: entry.term.rows };
    }

    function destroy(id) {
        const entry = terminals.get(id);
        if (!entry) return;
        terminals.delete(id);
        try { entry.term.dispose(); } catch { /* already gone */ }
    }

    // Mobile keys — same sequences as ui/src/mobile-keys.js, ported for MAUI
    const MOBILE_KEY_SEQUENCES = {
        Escape: "\x1b", Esc: "\x1b", Tab: "\t", Enter: "\r", "Alt+Enter": "\x1b\r", AltEnter: "\x1b\r",
        ArrowLeft: "\x1b[D", ArrowUp: "\x1b[A", ArrowDown: "\x1b[B", ArrowRight: "\x1b[C",
        Left: "\x1b[D", Up: "\x1b[A", Down: "\x1b[B", Right: "\x1b[C",
        Home: "\x1b[H", End: "\x1b[F", PageUp: "\x1b[5~", PgUp: "\x1b[5~", PageDown: "\x1b[6~", PgDn: "\x1b[6~",
        Delete: "\x1b[3~", Del: "\x1b[3~", Insert: "\x1b[2~", Ins: "\x1b[2~",
        F1: "\x1bOP", F2: "\x1bOQ", F3: "\x1bOR", F4: "\x1bOS", F5: "\x1b[15~", F6: "\x1b[17~", F7: "\x1b[18~", F8: "\x1b[19~", F9: "\x1b[20~", F10: "\x1b[21~", F11: "\x1b[23~", F12: "\x1b[24~",
        "Ctrl+ArrowLeft": "\x1b[1;5D", "Ctrl+ArrowRight": "\x1b[1;5C", "Ctrl+ArrowUp": "\x1b[1;5A", "Ctrl+ArrowDown": "\x1b[1;5B",
        "Ctrl+Left": "\x1b[1;5D", "Ctrl+Right": "\x1b[1;5C", "Ctrl+Up": "\x1b[1;5A", "Ctrl+Down": "\x1b[1;5B",
        "0": "0", "1": "1", "2": "2", "3": "3", "4": "4", "5": "5", "6": "6", "7": "7", "8": "8", "9": "9"
    };

    function getActiveTerminalId() {
        const active = document.querySelector('.pane.active');
        if (active && active.dataset.id) return active.dataset.id;
        const first = terminals.keys().next();
        if (!first.done) return first.value;
        // fallback for isolated default terminal
        return 'default';
    }

    function sendMobileKey(key) {
        const data = MOBILE_KEY_SEQUENCES[key];
        if (!data) return;
        const id = getActiveTerminalId();
        // Prefer the same host channel as terminal input (Blazor WebView / __terminalvHost)
        const host = window.__terminalvHost || (window.chrome && window.chrome.webview ? { postMessage: (m) => window.chrome.webview.postMessage(typeof m === 'string' ? m : JSON.stringify(m)) } : null);
        if (host && typeof host.postMessage === 'function') {
            try { host.postMessage({ type: 'write', id, data }); return; } catch {}
        }
        // Fallback: write directly into isolated terminal if present
        const entry = terminals.get(id);
        if (entry && entry.term) {
            // For local demo terminals, echo via input channel
            if (window.chrome && window.chrome.webview) {
                try { window.chrome.webview.postMessage(JSON.stringify({ type: 'write', id, data })); return; } catch {}
            }
        }
    }

    function initMobileKeys() {
        const root = document.getElementById('mobile-keys');
        if (!root) return;
        if (root.dataset?.mobileKeysInitialized) return;
        if (root.dataset) root.dataset.mobileKeysInitialized = "true";
        // MAUI is always "mobile": unhide data-mobile-only when no desktop bundle has done so yet
        if (root.hidden && root.hasAttribute('data-mobile-only')) {
            // Defer to let desktop bundle claim ownership first; fallback unhides after 300ms
            setTimeout(() => {
                const r = document.getElementById('mobile-keys');
                if (r && r.hidden) r.hidden = false;
            }, 300);
        }
        const TAP_SLOP_PX = 8;
        let scrolledDuringGesture = false;
        root.addEventListener('scroll', () => { scrolledDuringGesture = true; }, true);
        for (const btn of root.querySelectorAll('[data-key]')) {
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
            }
            function samePointer(e) {
                return activePointer !== null &&
                    (e.pointerId === undefined || activePointer.id === undefined || e.pointerId === activePointer.id);
            }
            function exceededSlop(e) {
                if (!activePointer) return false;
                const x = e.clientX ?? activePointer.x;
                const y = e.clientY ?? activePointer.y;
                return Math.hypot(x - activePointer.x, y - activePointer.y) > TAP_SLOP_PX;
            }
            btn.addEventListener('pointerdown', (e) => {
                if (e.button !== undefined && e.button !== 0) return;
                e.preventDefault();
                scrolledDuringGesture = false;
                resetSuppressClick();
                activePointer = { id: e.pointerId, x: e.clientX ?? 0, y: e.clientY ?? 0, moved: false };
            });
            btn.addEventListener('pointermove', (e) => {
                if (!samePointer(e)) return;
                if (scrolledDuringGesture || exceededSlop(e)) activePointer.moved = true;
            });
            btn.addEventListener('pointercancel', (e) => {
                if (!samePointer(e)) return;
                activePointer = null;
                armSuppressClick();
            });
            btn.addEventListener('pointerup', (e) => {
                if (!samePointer(e)) return;
                const moved = activePointer.moved || scrolledDuringGesture || exceededSlop(e);
                activePointer = null;
                armSuppressClick();
                if (!moved) sendMobileKey(btn.dataset.key);
            });
            btn.addEventListener('click', () => {
                if (suppressClick) { resetSuppressClick(); return; }
                if (activePointer) {
                    const moved = activePointer.moved || scrolledDuringGesture;
                    activePointer = null;
                    if (!moved) sendMobileKey(btn.dataset.key);
                    return;
                }
                sendMobileKey(btn.dataset.key);
            });
        }
    }

    // Expose for testing / parity with ui/src/mobile-keys.js
    window.MOBILE_KEY_SEQUENCES = MOBILE_KEY_SEQUENCES;

    // Expose isolated API
    window.TerminalV = { create: createTerminal, write, resize, measure, destroy, terminals, sendMobileKey, MOBILE_KEY_SEQUENCES };

    // Auto-create default terminal on load for standalone testing (isolation: no backend required)
    document.addEventListener('DOMContentLoaded', () => {
        initMobileKeys();
        if (!document.getElementById('terminal-container')) {
            // Blazor hybrid has no terminal-container; keys/slide already initialized
            return;
        }
        if (!document.getElementById('terminal-container').hasChildNodes()) {
            const term = createTerminal('default', 80, 24);
            if (term) {
                term.writeln('\x1b[1;34mTerminalV Mobile\x1b[0m - xterm.js ready (isolated)');
                term.writeln('Waiting for host session...');
            }
        }
    });
    // Slide panel fallback: if mobile-bridge.js did not define swipe/toggle, provide minimal toggle
    if (typeof window.__terminalvToggleSidebar !== 'function') {
        window.__terminalvToggleSidebar = function () {
            const app = document.getElementById('app');
            if (!app) return;
            const next = !app.classList.contains('collapsed');
            app.classList.toggle('collapsed', next);
            if (typeof window.__tvSidebarState === 'function') window.__tvSidebarState(next);
            else if (window.__terminalvSetSidebarCollapsed) window.__terminalvSetSidebarCollapsed(next);
        };
    }

    // Also init if script loads after DOMContentLoaded
    if (document.readyState !== 'loading') initMobileKeys();

    // Handle window resize with isolation (debounced).
    // After refit, report the new grid to .NET so the remote pty follows.
    let resizeTimer = 0;
    window.addEventListener('resize', () => {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => {
            terminals.forEach((e, id) => {
                if (e.fitAddon) e.fitAddon.fit();
                if (window.Blazor && window.__tvResizeNotify !== false) {
                    try {
                        DotNet.invokeMethodAsync('TerminalV.Mobile', 'OnTerminalResizeStatic', id, e.term.cols, e.term.rows);
                    } catch { /* Blazor not ready */ }
                }
            });
        }, 500);
    });
})();
