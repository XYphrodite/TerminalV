const PREVIEW_LIMIT = 12000;

export function createPasteController({ dialog, readClipboard, canPaste, restoreFocus }) {
  const preview = dialog.querySelector("[data-paste-preview]");
  const truncated = dialog.querySelector("[data-paste-truncated]");
  const confirm = dialog.querySelector("[data-paste-confirm]");
  const cancel = dialog.querySelector("[data-paste-cancel]");
  let pending = null;

  function finish(approved) {
    const operation = pending;
    if (!operation) {
      return;
    }
    pending = null;
    if (dialog.open) {
      dialog.close();
    }
    preview.textContent = "";
    truncated.hidden = true;
    try {
      if (approved && operation.text && canPaste(operation.tab)) {
        // Keep xterm's newline normalization and bracketed paste support.
        operation.tab.term.paste(operation.text);
      }
    } finally {
      restoreFocus();
    }
  }

  confirm.addEventListener("click", () => finish(true));
  cancel.addEventListener("click", () => finish(false));
  dialog.addEventListener("cancel", (event) => {
    event.preventDefault();
    finish(false);
  });
  dialog.addEventListener("close", () => {
    // Ignore a queued close event from a previous dialog/operation.
    if (pending?.showing && !dialog.open) {
      finish(false);
    }
  });
  dialog.addEventListener("keydown", (event) => event.stopPropagation());

  return {
    get isOpen() {
      return dialog.open;
    },
    async request(tab) {
      if (pending || !canPaste(tab)) {
        return;
      }
      const operation = { tab, text: "", showing: false };
      pending = operation;
      try {
        const text = await readClipboard();
        // A closed or switched tab must not receive a late clipboard reply.
        if (pending !== operation) {
          return;
        }
        if (!text || !canPaste(tab)) {
          finish(false);
          return;
        }
        operation.text = text;
        if (!/[\r\n]/.test(text)) {
          finish(true);
          return;
        }
        // Never interpret clipboard text as HTML. Only the preview is capped;
        // confirmation sends the exact snapshot that was read for this dialog.
        preview.textContent = text.slice(0, PREVIEW_LIMIT);
        truncated.hidden = text.length <= PREVIEW_LIMIT;
        operation.showing = true;
        dialog.showModal();
        cancel.focus({ preventScroll: true });
      } catch {
        if (pending === operation) {
          finish(false);
        }
      }
    },
    cancel(tab) {
      if (pending?.tab === tab) {
        finish(false);
      }
    }
  };
}
