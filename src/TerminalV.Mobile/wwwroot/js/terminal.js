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

    // Expose isolated API
    window.TerminalV = { create: createTerminal, write, resize, measure, destroy, terminals };

    // Auto-create default terminal on load for standalone testing (isolation: no backend required)
    document.addEventListener('DOMContentLoaded', () => {
        if (!document.getElementById('terminal-container').hasChildNodes()) {
            const term = createTerminal('default', 80, 24);
            if (term) {
                term.writeln('\x1b[1;34mTerminalV Mobile\x1b[0m - xterm.js ready (isolated)');
                term.writeln('Waiting for host session...');
            }
        }
    });

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
