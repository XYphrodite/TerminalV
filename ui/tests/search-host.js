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
    if (message.type === "list-launch-targets" && window.catalogMode !== "manual") {
      queueMicrotask(() => window.sendHost({ type: "launch-targets", requestId: message.requestId,
        targets: window.testLaunchTargets || [
          { id: "powershell", title: "Windows PowerShell", shell: "powershell" },
          { id: "pwsh", title: "PowerShell 7", shell: "pwsh" },
          { id: "cmd", title: "Командная строка", shell: "cmd" },
          { id: "wsl:Ubuntu", title: "Ubuntu", shell: "wsl", wslDistribution: "Ubuntu" }
        ], ...(window.catalogMode === "error" ? { error: "Test WSL unavailable" } : {}) }));
    }
    if (message.type === "persist-profiles" && window.profileSaveMode !== "manual") {
      queueMicrotask(() => window.sendHost({ type: "profiles-saved", requestId: message.requestId,
        ...(window.profileSaveMode === "error" ? { error: "Test database unavailable" } : { profiles: message.profiles }) }));
    }
    if (message.type === "ready") {
      queueMicrotask(() => window.sendHost({
        type: "init", version: "test", shellName: "Test PowerShell", buildNumber: 22621,
        sessions: [
          { id: "first", title: "Muse — поиск", active: true },
          { id: "second", title: "PowerShell — логи" }
        ],
        liveIds: window.testInit?.sessions ? [] : ["first", "second"],
        ...window.testInit
      }));
    }
    const defaultReplay = message.type === "attach" && !window.testInit?.sessions &&
      ["first", "second"].includes(message.id);
    if (message.type === "create" || defaultReplay) {
      const sample = message.id === "first"
        ? "PS C:\\Project> demo\r\nОшибка подключения к серверу\r\nПроверка конфигурации завершена\r\nПовторная ошибка подключения\r\nPS C:\\Project> "
        : "Другая вкладка\r\nunique second\r\nPS C:\\Project> ";
      const data = message.type === "create" ? window.testCreateOutput ?? sample : sample;
      queueMicrotask(() => window.sendHost({ type: "data", id: message.id, data, replay: defaultReplay }));
    }
  }
};
