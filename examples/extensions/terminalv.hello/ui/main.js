export async function activate(api) {
  const panel = api.ui.createSidebarPanel({ id: "hello", title: "Hello extension" });
  const text = document.createElement("p");
  const button = document.createElement("button");
  button.textContent = "+1";
  button.type = "button";
  panel.element.append(text, button);

  function render(state) {
    if (api.signal.aborted) return;
    text.textContent = `Счётчик: ${state.count} · ${new Date(state.updatedAt).toLocaleTimeString()}`;
    panel.setBadge(state.count);
  }

  async function increment() {
    const state = await api.host.invoke("increment");
    render(state);
    api.ui.showNotification(`Сохранено: ${state.count}`);
  }

  api.host.on("changed", render);
  api.commands.register("increment", "Увеличить счётчик", increment);
  button.addEventListener("click", () => {
    increment().catch(error => { if (!api.signal.aborted) api.ui.showNotification(error.message); });
  }, { signal: api.signal });

  const settings = api.ui.createSettingsPanel({ id: "info", title: "Hello extension" });
  settings.element.textContent = "Счётчик хранится в C#. Фоновое событие обновляет время каждые 30 секунд.";
  // Events are not replayed. Request the current snapshot after subscribing.
  render(await api.host.invoke("getState"));
}
