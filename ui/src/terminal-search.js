export const SEARCH_HIGHLIGHT_LIMIT = 1000;

export function isSearchShortcut(event) {
  return Boolean((event.ctrlKey || event.metaKey) && event.shiftKey && !event.altKey &&
    (event.code === "KeyF" || event.key?.toLowerCase() === "f"));
}

export function createTerminalSearch({ panel, onVisibilityChange }) {
  const input = panel.querySelector("[data-search-input]");
  const count = panel.querySelector("[data-search-count]");
  const matchCase = panel.querySelector("[data-search-case]");
  const previous = panel.querySelector("[data-search-previous]");
  const next = panel.querySelector("[data-search-next]");
  const closeButton = panel.querySelector("[data-search-close]");
  let session = null;
  let timer = 0;
  let caseSensitive = false;
  let lastResult = { resultIndex: -1, resultCount: 0 };

  function showResults(found) {
    const { resultIndex, resultCount } = lastResult;
    const limited = resultCount >= SEARCH_HIGHLIGHT_LIMIT;
    count.textContent = !input.value ? "" : !found ? "Нет совпадений" :
      resultCount === 0 ? "Совпадение" :
      `${resultIndex < 0 ? "—" : resultIndex + 1} / ${limited ? "≥" : ""}${resultCount}`;
    count.title = limited ? `Подсвечены первые ${SEARCH_HIGHLIGHT_LIMIT} совпадений; переход доступен по всем.` : "";
    panel.classList.toggle("no-results", Boolean(input.value) && !found);
    previous.disabled = next.disabled = !found;
  }

  function options() {
    const style = getComputedStyle(panel);
    const accent = style.getPropertyValue("--accent").trim() || "#3d9eff";
    return {
      caseSensitive,
      regex: false,
      decorations: {
        // Let xterm render match backgrounds without replacing ANSI text colors.
        matchBackground: style.getPropertyValue("--tab-active").trim() || "#1c2736",
        matchBorder: style.getPropertyValue("--accent-dim").trim() || accent,
        activeMatchBorder: accent,
        matchOverviewRuler: accent,
        activeMatchColorOverviewRuler: accent
      }
    };
  }

  function find(direction = 1, reset = false) {
    window.clearTimeout(timer);
    if (!session) return;
    const { tab } = session;
    if (reset) {
      // Invalidate the addon's cached search after an option or buffer change.
      tab.search.findNext("");
      tab.term.clearSelection();
    }
    const found = direction < 0
      ? tab.search.findPrevious(input.value, options())
      : tab.search.findNext(input.value, options());
    showResults(found);
  }

  function close({ focus = true } = {}) {
    window.clearTimeout(timer);
    if (!session) return;
    const closing = session;
    session = null;
    closing.results.dispose();
    closing.buffer.dispose();
    // An empty search also stops the addon from re-highlighting on new output.
    closing.tab.search.findNext("");
    closing.tab.term.clearSelection();
    panel.hidden = true;
    onVisibilityChange(closing.tab);
    if (focus) closing.tab.term.focus();
  }

  function open(tab) {
    if (!tab) return;
    if (session?.tab !== tab) {
      close({ focus: false });
      const opening = { tab };
      session = opening;
      opening.results = tab.search.onDidChangeResults((result) => {
        if (session !== opening) return;
        lastResult = result;
        showResults(tab.term.hasSelection());
      });
      opening.buffer = tab.term.buffer.onBufferChange(() => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => find(1, true), 0);
      });
      panel.hidden = false;
      onVisibilityChange(tab);
      find(1, true);
    }
    input.focus({ preventScroll: true });
    input.select();
  }

  input.addEventListener("input", () => {
    window.clearTimeout(timer);
    if (!session) return;
    count.textContent = input.value ? "Поиск…" : "";
    previous.disabled = next.disabled = true;
    timer = window.setTimeout(() => find(1, true), 100);
  });
  matchCase.addEventListener("click", () => {
    caseSensitive = !caseSensitive;
    matchCase.setAttribute("aria-pressed", String(caseSensitive));
    find(1, true);
  });
  previous.addEventListener("click", () => find(-1));
  next.addEventListener("click", () => find(1));
  closeButton.addEventListener("click", () => close());

  return {
    open,
    close,
    get isOpen() { return session !== null; },
    contains(target) { return panel.contains(target); },
    handleKeyDown(event) {
      if (!session) return false;
      const inside = panel.contains(event.target);
      if (event.isComposing || event.keyCode === 229) {
        if (inside) event.stopPropagation();
        return inside;
      }
      if (event.key === "Escape") {
        event.preventDefault();
        event.stopPropagation();
        close();
        return true;
      }
      if (!inside) return false;
      // Let text editing and Tab work normally, but isolate them from app/PTY shortcuts.
      event.stopPropagation();
      if (event.key === "Enter" && event.target === input) {
        event.preventDefault();
        find(event.shiftKey ? -1 : 1);
      }
      return true;
    }
  };
}
