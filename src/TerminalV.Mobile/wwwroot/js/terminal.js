// TerminalV.Mobile - xterm.js isolation bridge
// Works without Blazor; Blazor can call window.terminalInterop later.
(function () {
    const terminals = new Map();
    let stickyCtrl = false;
    let stickyAlt = false;

    const CYRILLIC_TO_LATIN = {
        "й": "q", "ц": "w", "у": "e", "к": "r", "е": "t", "н": "y", "г": "u", "ш": "i", "щ": "o", "з": "p", "х": "[", "ъ": "]",
        "ф": "a", "ы": "s", "в": "d", "а": "f", "п": "g", "р": "h", "о": "j", "л": "k", "д": "l", "ж": ";", "э": "'",
        "я": "z", "ч": "x", "с": "c", "м": "v", "и": "b", "т": "n", "ь": "m", "б": ",", "ю": ".", "ё": "`",
        "Й": "Q", "Ц": "W", "У": "E", "К": "R", "Е": "T", "Н": "Y", "Г": "U", "Ш": "I", "Щ": "O", "З": "P", "Х": "{", "Ъ": "}",
        "Ф": "A", "Ы": "S", "В": "D", "А": "F", "П": "G", "Р": "H", "О": "J", "Л": "K", "Д": "L", "Ж": ":", "Э": '"',
        "Я": "Z", "Ч": "X", "С": "C", "М": "V", "И": "B", "Т": "N", "Ь": "M", "Б": "<", "Ю": ">", "Ё": "~"
    };
    const CTRL_SYMBOL_MAP = {
        " ": "\x00", "@": "\x00", "2": "\x00", "[": "\x1b", "3": "\x1b", "\\": "\x1c", "4": "\x1c",
        "]": "\x1d", "5": "\x1d", "^": "\x1e", "6": "\x1e", "~": "\x1e", "_": "\x1f", "7": "\x1f",
        "-": "\x1f", "/": "\x1f", "?": "\x7f", "8": "\x7f"
    };

    function isTerminalProtocolReport(data) {
        if (data === "\x1b[I" || data === "\x1b[O") return true;
        if (data.startsWith("\x1b[200~") || data.startsWith("\x1b[201~")) return true;
        if (data.startsWith("\x1b]") || data.startsWith("\x1b[M")) return true;
        return /^\x1b\[<\d+;\d+;\d+[Mm]$/.test(data)
            || /^\x1b\[\??[\d;]*R$/.test(data)
            || /^\x1b\[[>?][\d;]*c$/.test(data);
    }

    function encodeModifiedChar(ch, opts) {
        const mapped = CYRILLIC_TO_LATIN[ch] || ch;
        let out = mapped;
        if (opts.ctrl) {
            const code = mapped.charCodeAt(0);
            if ((code >= 65 && code <= 90) || (code >= 97 && code <= 122)) {
                out = String.fromCharCode(code & 0x1f);
            } else if (Object.hasOwn(CTRL_SYMBOL_MAP, mapped)) {
                out = CTRL_SYMBOL_MAP[mapped];
            }
        }
        return opts.alt ? `\x1b${out}` : out;
    }

    function applyTerminalModifiers(data, opts) {
        const ctrl = Boolean(opts && opts.ctrl);
        const alt = Boolean(opts && opts.alt);
        if ((!ctrl && !alt) || typeof data !== "string" || data.length === 0) return { data, consumed: false };
        if (isTerminalProtocolReport(data)) return { data, consumed: false };
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
        if (ss3Match) return { data: `\x1b[1;${modBits + 1}${ss3Match[1]}`, consumed: true };
        if (data === "\x1b[Z") return { data: alt ? "\x1b\x1b[Z" : data, consumed: true };
        if (data === "\x1b") return { data, consumed: true };
        if (data === "\t" || data === "\r" || data === "\n") return { data: alt ? `\x1b${data}` : data, consumed: true };
        if (data === "\x7f" || data === "\x08") {
            const key = ctrl ? "\x08" : data;
            return { data: alt ? `\x1b${key}` : key, consumed: true };
        }
        if (data.startsWith("\x1b")) {
            return { data: alt && !data.startsWith("\x1b\x1b") ? `\x1b${data}` : data, consumed: true };
        }
        const chars = Array.from(data);
        return { data: encodeModifiedChar(chars[0], { ctrl, alt }) + chars.slice(1).join(""), consumed: true };
    }

    function updateFallbackModifierUI() {
        const root = document.getElementById('mobile-keys');
        if (root) {
            for (const [k, active] of [['Ctrl', stickyCtrl], ['Alt', stickyAlt]]) {
                for (const btn of root.querySelectorAll(`[data-key="${k}"]`)) {
                    btn.classList.toggle('active', active);
                    btn.setAttribute('aria-pressed', active ? 'true' : 'false');
                }
            }
        }
        const handle = document.getElementById('mobile-keys-handle');
        if (handle) {
            const mods = [];
            if (stickyCtrl) mods.push('Ctrl');
            if (stickyAlt) mods.push('Alt');
            const label = handle.querySelector('span') || handle;
            if (!handle.dataset.baseLabel) handle.dataset.baseLabel = label.textContent.trim() || 'Клавиши';
            handle.classList.toggle('has-modifiers', mods.length > 0);
            label.textContent = mods.length > 0 ? `${handle.dataset.baseLabel} · ${mods.join('+')}` : handle.dataset.baseLabel;
        }
    }

    function applyFallbackInputModifiers(data) {
        if (!stickyCtrl && !stickyAlt) return data;
        const result = applyTerminalModifiers(data, { ctrl: stickyCtrl, alt: stickyAlt });
        if (result.consumed) {
            stickyCtrl = false;
            stickyAlt = false;
            updateFallbackModifierUI();
        }
        return result.data;
    }

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
        term.onData(raw => {
            const data = applyFallbackInputModifiers(raw);
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
    const BASE_KEY_SEQUENCES = {
        Escape: "\x1b", Esc: "\x1b", Tab: "\t", Enter: "\r",
        ArrowLeft: "\x1b[D", ArrowUp: "\x1b[A", ArrowDown: "\x1b[B", ArrowRight: "\x1b[C",
        Left: "\x1b[D", Up: "\x1b[A", Down: "\x1b[B", Right: "\x1b[C",
        Home: "\x1b[H", End: "\x1b[F", PageUp: "\x1b[5~", PgUp: "\x1b[5~", PageDown: "\x1b[6~", PgDn: "\x1b[6~",
        Delete: "\x1b[3~", Del: "\x1b[3~", Insert: "\x1b[2~", Ins: "\x1b[2~",
        F1: "\x1bOP", F2: "\x1bOQ", F3: "\x1bOR", F4: "\x1bOS", F5: "\x1b[15~", F6: "\x1b[17~", F7: "\x1b[18~", F8: "\x1b[19~", F9: "\x1b[20~", F10: "\x1b[21~", F11: "\x1b[23~", F12: "\x1b[24~",
        "0": "0", "1": "1", "2": "2", "3": "3", "4": "4", "5": "5", "6": "6", "7": "7", "8": "8", "9": "9"
    };
    const MOBILE_KEY_SEQUENCES = Object.assign({}, BASE_KEY_SEQUENCES, { "Alt+Enter": "\x1b\r", AltEnter: "\x1b\r" });
    for (const [key, baseSeq] of Object.entries(BASE_KEY_SEQUENCES)) {
        for (const [prefix, mods] of [["Ctrl+", { ctrl: true }], ["Alt+", { alt: true }], ["Ctrl+Alt+", { ctrl: true, alt: true }]]) {
            const name = `${prefix}${key}`;
            if (!Object.hasOwn(MOBILE_KEY_SEQUENCES, name)) {
                MOBILE_KEY_SEQUENCES[name] = applyTerminalModifiers(baseSeq, mods).data;
            }
        }
    }

    function getActiveTerminalId() {
        const active = document.querySelector('.pane.active');
        if (active && active.dataset.id) return active.dataset.id;
        const first = terminals.keys().next();
        if (!first.done) return first.value;
        // fallback for isolated default terminal
        return 'default';
    }

    function sendMobileKey(key) {
        if (key === 'Ctrl') { stickyCtrl = !stickyCtrl; updateFallbackModifierUI(); return; }
        if (key === 'Alt') { stickyAlt = !stickyAlt; updateFallbackModifierUI(); return; }
        let resolvedKey = key;
        if (stickyCtrl || stickyAlt) {
            const prefix = `${stickyCtrl ? 'Ctrl+' : ''}${stickyAlt ? 'Alt+' : ''}`;
            const combo = `${prefix}${key}`;
            if (!key.startsWith('Ctrl+') && !key.startsWith('Alt+') && Object.hasOwn(MOBILE_KEY_SEQUENCES, combo)) {
                resolvedKey = combo;
            }
            stickyCtrl = false; stickyAlt = false;
            updateFallbackModifierUI();
        }
        const data = MOBILE_KEY_SEQUENCES[resolvedKey];
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
    window.TerminalV = { create: createTerminal, write, resize, measure, destroy, terminals, sendMobileKey, MOBILE_KEY_SEQUENCES, applyTerminalModifiers };

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
