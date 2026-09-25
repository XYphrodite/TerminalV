export async function activate(api) {
  window.extensionActivations = (window.extensionActivations ?? 0) + 1;
  const panel = api.ui.createSidebarPanel({ id: "hello", title: "Hello <script>" });
  const settings = api.ui.createSettingsPanel({ id: "options", title: "Example settings" });
  settings.element.textContent = "Settings body";
  const render = state => { panel.element.textContent = `Count: ${state.count}`; panel.setBadge(state.count); };
  api.host.on("changed", render);
  api.commands.register("increment", "Increment", async () => {
    render(await api.host.invoke("increment"));
    api.ui.showNotification("Saved <img>");
  });
  render(await api.host.invoke("getState"));
}
