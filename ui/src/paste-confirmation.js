const PREVIEW_LIMIT = 12000;

export function createPasteController({ dialog, readClipboard, canPaste, restoreFocus }) {
  const preview = dialog.querySelector("[data-paste-preview]");
  const truncated = dialog.querySelector("[data-paste-truncated]");
  const confirm = dialog.querySelector("[data-paste-confirm]");
  const cancel = dialog.querySelector("[data-paste-cancel]");
  let pending = null;

  function diag(msg) { try { window.chrome?.webview?.postMessage({ type: "diag", data: msg }); } catch {} }
  function finish(approved) {
    const operation = pending;
    if (!operation) {
      return;
    }
    const start = performance.now();
    const tLen = operation.text?.length ?? 0;
    diag(`paste finish approved=${approved} len=${tLen} ms=${(performance.now()-start).toFixed(1)}`);
    pending = null;
    if (dialog.open) {
      dialog.close();
    }
    preview.textContent = "";
    truncated.hidden = true;
    try {
      if (approved && operation.text && canPaste(operation.tab)) {
        const t0 = performance.now();
        diag(`paste term.paste start len=${operation.text.length}`);
        // Keep xterm's newline normalization and bracketed paste support.
        operation.tab.term.paste(operation.text);
        diag(`paste term.paste done ms=${(performance.now()-t0).toFixed(1)}`);
      } else if (approved) {
        diag(`paste approved but canPaste=false`);
      }
    } catch (e) { diag(`paste error ${e}`); }
    finally {
      restoreFocus();
      diag(`paste finish restoreFocus ms=${(performance.now()-start).toFixed(1)}`);
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
      const t0 = performance.now();
      if (pending || !canPaste(tab)) {
        diag(`paste request skip pending=${!!pending} canPaste=${canPaste(tab)}`);
        return;
      }
      diag(`paste request start tab=${tab.id}`);
      const operation = { tab, text: "", showing: false };
      pending = operation;
      try {
        const text = await readClipboard();
        diag(`paste clipboard done len=${text?.length ?? 0} ms=${(performance.now()-t0).toFixed(1)}`);
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
