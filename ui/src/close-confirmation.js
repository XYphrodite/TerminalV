export function createCloseConfirmation({ dialog, canClose, onConfirm, onShow, restoreFocus }) {
  const name = dialog.querySelector("[data-close-name]");
  const confirm = dialog.querySelector("[data-close-confirm]");
  const cancel = dialog.querySelector("[data-close-cancel]");
  let pending = null;

  function finish(approved) {
    const operation = pending;
    if (!operation) return;
    // Clear first: double clicks and queued close events must not close another tab.
    pending = null;
    if (dialog.open) dialog.close();
    name.textContent = "";
    try {
      if (approved && canClose(operation.tab)) onConfirm(operation.tab);
    } finally {
      restoreFocus(operation.focusTarget);
    }
  }

  confirm.addEventListener("click", () => {
    if (dialog.open) finish(true);
  });
  cancel.addEventListener("click", () => finish(false));
  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    finish(false);
  });
  dialog.addEventListener("close", () => {
    if (pending && !dialog.open) finish(false);
  });
  dialog.addEventListener("keydown", (event) => event.stopPropagation());

  return {
    get isOpen() { return dialog.open; },
    request(tab) {
      if (pending || !canClose(tab)) return;
      pending = { tab, focusTarget: document.activeElement };
      if (tab.exited) {
        finish(true);
        return;
      }
      // Titles can come from terminal output; never interpret them as markup.
      name.textContent = tab.customTitle || tab.title || "Сессия";
      try {
        onShow();
        dialog.showModal();
        cancel.focus({ preventScroll: true });
      } catch (error) {
        finish(false);
        throw error;
      }
    },
    cancel(tab) {
      if (pending && (!tab || pending.tab === tab)) finish(false);
    }
  };
}
