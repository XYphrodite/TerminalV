window.hostMessages = [];
window.hostErrors = [];
window.addEventListener("error", event => window.hostErrors.push(event.message));
window.addEventListener("unhandledrejection", event => window.hostErrors.push(String(event.reason)));
window.sendHost = message => window.__tvDispatch(JSON.stringify(message));
window.__tvSetBridge({
  async invokeMethodAsync(method, json) {
    if (method !== "HandleMessage") throw new Error(method);
    const message = JSON.parse(json);
    window.hostMessages.push(message);
    if (message.type === "open-connection") {
      let dialog = document.getElementById("mobile-config-host");
      if (!dialog) {
        dialog = document.createElement("div");
        dialog.id = "mobile-config-host";
        dialog.style = "position:fixed;inset:0;z-index:10000;background:#10141a";
        dialog.innerHTML = '<button id="connection-close">Закрыть</button><input id="fixture-connection-input"><button id="fixture-connection-last">Готово</button>';
        document.getElementById("blazor-root").append(dialog);
      }
      dialog.hidden = false;
      window.__tvConnectionConfig(true);
    }
    if (message.type === "close-connection") {
      document.getElementById("mobile-config-host").hidden = true;
      window.__tvConnectionConfig(false);
    }
    if (message.type === "ready") {
      window.sendHost({ type: "app-info", mobile: true, version: "9.8.7", updateSupported: false });
      window.sendHost({
        type: "init", mobile: true, sessions: [], liveIds: [], settings: { hardwareRendering: false }
      });
    }
    if (message.type === "persist-profiles") window.sendHost({
      type: "profiles-saved", requestId: message.requestId, profiles: message.profiles
    });
    if (message.type === "attach") window.sendHost({
      type: "data", id: message.id, data: "ATTACHED-SNAPSHOT\r\n"
    });
  }
});
