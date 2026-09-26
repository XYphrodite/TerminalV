import { t } from "./i18n.js";

// An explicit one-shot action, not a persisted setting or terminal command.
export function createShortcuts({ root, post }) {
  const startMenu = root.querySelector("#shortcut-start-menu");
  const desktop = root.querySelector("#shortcut-desktop");
  const button = root.querySelector("#create-shortcuts");
  const status = root.querySelector("#shortcuts-status");
  let supported = false;
  let pending = null;

  function render() {
    startMenu.disabled = desktop.disabled = !supported || Boolean(pending);
    button.disabled = !supported || Boolean(pending) || (!startMenu.checked && !desktop.checked);
    button.textContent = pending ? t("Shortcuts_Creating") : t("Shortcuts_Create");
    root.setAttribute("aria-busy", String(Boolean(pending)));
  }
  startMenu.addEventListener("change", render);
  desktop.addEventListener("change", render);
  button.addEventListener("click", () => {
    if (button.disabled) return;
    pending = { requestId: crypto.randomUUID(), startMenu: startMenu.checked, desktop: desktop.checked };
    status.textContent = t("Shortcuts_CreatingSelected");
    status.classList.remove("has-error");
    render();
    try { post({ type: "create-shortcuts", ...pending }); }
    catch { receive({ requestId: pending.requestId, error: t("Shortcuts_SendFailed") }); }
  });

  function receive(message) {
    if (!pending || message.requestId !== pending.requestId) return;
    const selected = pending;
    pending = null;
    let hasError = Boolean(message.error);
    const lines = [];
    if (message.error) lines.push(t("Shortcuts_FailedPrefix", { error: message.error }));
    else {
      for (const [destination, label] of [["startMenu", t("Shortcuts_StartMenu")], ["desktop", t("Shortcuts_Desktop")]]) {
        if (!selected[destination]) continue;
        const result = message.results?.find((item) => item.destination === destination);
        const error = result?.error || (!result?.path ? t("Shortcuts_NotConfirmed") : null);
        hasError ||= Boolean(error);
        lines.push(error ? `${label}: ${error}` : `${label}: ${t("Shortcuts_Done")}\n${result.path}`);
      }
    }
    status.textContent = lines.join("\n");
    status.classList.toggle("has-error", hasError);
    render();
  }

  render();
  return {
    receive,
    setSupported(value) {
      supported = value === true;
      if (!pending) status.textContent = supported ? "" : t("Shortcuts_Unavailable");
      render();
    }
  };
}
