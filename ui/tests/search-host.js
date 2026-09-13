// Test-only WebView host. No real shell, account or system clipboard is used.
window.hostMessages = [];
window.hostErrors = [];
window.addEventListener("error", (event) => window.hostErrors.push(event.message));
window.addEventListener("unhandledrejection", (event) => window.hostErrors.push(String(event.reason)));
let receive;
window.sendHost = (data) => receive({ data });
window.chrome ??= {};
window.chrome.webview = {
  addEventListener(type, listener) { if (type === "message") receive = listener; },
  postMessage(message) {
    window.hostMessages.push(message);
    if (message.type === "ready") {
      queueMicrotask(() => window.sendHost({
        type: "init", version: "test", shellName: "Test PowerShell", buildNumber: 22621,
        sessions: [
          { id: "first", title: "Muse — поиск", active: true },
          { id: "second", title: "PowerShell — логи" }
        ]
      }));
    }
    if (message.type === "create") {
      const data = message.id === "first"
        ? "PS C:\\Project> demo\r\nОшибка подключения к серверу\r\nПроверка конфигурации завершена\r\nПовторная ошибка подключения\r\nPS C:\\Project> "
        : "Другая вкладка\r\nunique second\r\nPS C:\\Project> ";
      queueMicrotask(() => window.sendHost({ type: "data", id: message.id, data }));
    }
  }
};
