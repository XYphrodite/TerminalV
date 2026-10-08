import { icon } from "./icons.js";
import { t } from "./i18n.js";
import { syncTerminalViewport } from "./terminal-viewport.js";

// Floating "scroll to bottom" button, one per pane (Telegram-style). It lives
// in .pane rather than .terminal-host because the mirror viewport turns the
// host into a scrolling container — a child of the host would pan away.
// Visibility: only when the normal buffer is scrolled away from the live
// bottom; never while a TUI owns scrolling (tui-lock set by syncScrollLock).
export function attachScrollToBottom(tab) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "scroll-to-bottom";
  button.hidden = true;
  button.title = t("ScrollToBottom");
  button.setAttribute("aria-label", t("ScrollToBottom"));
  button.append(icon("toBottom"));
  button.addEventListener("click", () => {
    tab.term.scrollToBottom();
    syncTerminalViewport(tab.term);
  });
  tab.pane.append(button);
  tab.scrollToBottomBtn = button;
  tab.term.onScroll(() => updateScrollToBottom(tab));
  // xterm suppresses its public onScroll for viewport-originated wheel scrolls
  // (BufferService.scrollLines with suppressScrollEvent), so listen to the DOM
  // scroll event as well; it fires after xterm's own listener updated ydisp.
  tab.host.querySelector(".xterm-viewport")?.addEventListener("scroll", () => updateScrollToBottom(tab));
  updateScrollToBottom(tab);
}

export function updateScrollToBottom(tab) {
  try {
    const button = tab.scrollToBottomBtn;
    if (!button) return;
    const hidden = tab.host.classList.contains("tui-lock") || !scrolledUp(tab);
    if (button.hidden !== hidden) button.hidden = hidden;
  } catch {}
}

function scrolledUp(tab) {
  const buffer = tab.term.buffer.active;
  if (buffer.type !== "normal") return false;
  const rows = tab.term.rows || 24;
  // Same "real history offset" threshold as claimLiveScreenInput: a 1–2 row
  // lag from trailing blank lines is still the bottom, not reading history.
  return buffer.baseY - buffer.viewportY > Math.max(2, Math.floor(rows * 0.2));
}
