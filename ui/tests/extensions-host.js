// Adds extension traffic to the existing test-only terminal host.
(() => {
  const original = window.chrome.webview.postMessage;
  let enabled = true, count = 0;
  const catalog = () => [
    { id: "test.hello", name: "Hello <img>", version: "1.0.0", enabled, active: true,
      valid: true, restartRequired: !enabled, uiUrl: new URL("/tests/extensions-module.js", document.baseURI).href },
    { id: "test.invalid", name: "Invalid", enabled: false, active: false, valid: false, error: "Invalid manifest" }
  ];
  window.chrome.webview.postMessage = message => {
    original(message);
    if (!message.type.startsWith("extensions:")) return;
    queueMicrotask(() => {
      let result;
      if (message.type === "extensions:list") result = catalog();
      if (message.type === "extensions:set-enabled") { enabled = message.enabled; result = catalog(); }
      if (message.type === "extensions:invoke") {
        if (message.method === "increment") {
          count++;
          window.sendHost({ type: "extensions:event", extensionId: message.extensionId,
            event: "changed", data: { count } });
        }
        result = { count };
      }
      window.sendHost({ type: "extensions:result", requestId: message.requestId,
        extensionId: message.extensionId, result });
    });
  };
})();
