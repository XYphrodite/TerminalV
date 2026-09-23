import { icon } from "./icons.js";
import { PROFILE_SHELLS, profileSession } from "./launch-profiles.js";

export function createLaunchMenu({ dialog, trigger, post, getProfiles, onLaunch, onEdit }) {
  const profilesEl = dialog.querySelector("[data-launch-profiles]");
  const targetsEl = dialog.querySelector("[data-launch-targets]");
  const status = dialog.querySelector("[data-launch-status]");
  const refresh = dialog.querySelector("[data-launch-refresh]");
  let targets = [], pending = null, loaded = false;
  let activeTrigger = trigger;

  function position() {
    if (!dialog.open) return;
    const anchor = (activeTrigger || trigger).getBoundingClientRect();
    const height = dialog.getBoundingClientRect().height;
    const width = dialog.getBoundingClientRect().width;
    dialog.style.left = `${Math.max(8, Math.min(anchor.left, innerWidth - width - 8))}px`;
    dialog.style.top = `${Math.max(8, Math.min(anchor.top - height - 8, innerHeight - height - 8))}px`;
  }
  function close(focus = true) {
    if (!dialog.open) return;
    dialog.close();
    (activeTrigger || trigger).setAttribute("aria-expanded", "false");
    if (focus) (activeTrigger || trigger).focus({ preventScroll: true });
    activeTrigger = trigger;
  }
  function run(action) {
    if (!dialog.open) return; // A queued second click cannot create a second session.
    close(false);
    action();
  }
  function row(key, title, detail, launch, edit, shell) {
    const element = document.createElement("div");
    element.className = "launch-row";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "launch-item";
    button.dataset.launchKey = key;
    button.dataset.shell = shell || "auto";
    button.setAttribute("aria-label", `Запустить ${title}`);
    button.title = detail ? `${title} — ${detail}` : title;
    const labels = document.createElement("span");
    labels.className = "launch-labels";
    const name = document.createElement("span"), meta = document.createElement("span");
    name.textContent = title;
    meta.textContent = detail;
    labels.append(name, meta);
    const hint = document.createElement("span");
    hint.className = "launch-hint";
    hint.textContent = "Запустить";
    button.append(icon(shell === "wsl" ? "linux" : "terminal"), labels, hint);
    button.addEventListener("click", () => run(launch));
    element.append(button);
    if (edit) {
      const editButton = document.createElement("button");
      editButton.type = "button";
      editButton.className = "launch-edit icon-btn";
      editButton.dataset.editProfile = key.slice("profile:".length);
      editButton.dataset.launchKey = "edit:" + key;
      editButton.title = `Изменить ${title}`;
      editButton.setAttribute("aria-label", editButton.title);
      editButton.append(icon("edit"));
      editButton.addEventListener("click", () => run(edit));
      element.append(editButton);
    }
    return element;
  }
  function render() {
    const focused = dialog.contains(document.activeElement) ? document.activeElement?.dataset.launchKey : null;
    const profiles = getProfiles();
    profilesEl.replaceChildren(...profiles.map(profile => row("profile:" + profile.id, profile.title,
      [PROFILE_SHELLS[profile.shell], profile.cwd].filter(Boolean).join(" · "),
      () => onLaunch(profileSession(profile)), () => onEdit(profile.id), profile.shell)));
    if (!profiles.length) {
      const empty = document.createElement("p");
      empty.className = "launch-empty";
      empty.textContent = "Нет сохранённых профилей";
      profilesEl.append(empty);
    }
    targetsEl.replaceChildren(...targets.map(target => row(target.id, target.title,
      target.shell === "wsl" ? "WSL · домашняя папка Linux" : "Оболочка",
      () => onLaunch({ title: target.title, customTitle: target.title, shell: target.shell,
        cwd: null, wslDistribution: target.wslDistribution || undefined }), null, target.shell)));
    if (dialog.open && focused) {
      const next = [...dialog.querySelectorAll("[data-launch-key]")].find(button => button.dataset.launchKey === focused);
      (next || profilesEl.querySelector("button") || targetsEl.querySelector("button") || dialog.querySelector("[data-launch-new]")).focus({ preventScroll: true });
    }
    position();
  }
  function request() {
    if (pending) return;
    const requestId = crypto.randomUUID();
    status.textContent = "Поиск оболочек и дистрибутивов WSL…";
    refresh.disabled = true;
    const timer = setTimeout(() => {
      if (pending?.requestId !== requestId) return;
      pending = null;
      refresh.disabled = false;
      status.textContent = "Нет ответа. Обновите список или перезапустите окно приложения.";
      position();
    }, 7000);
    pending = { requestId, timer };
    post({ type: "list-launch-targets", requestId });
    position();
  }
  refresh.addEventListener("click", request);
  dialog.querySelector("[data-launch-new]").addEventListener("click", () => run(() => onEdit(null)));
  dialog.querySelector("[data-launch-close]").addEventListener("click", () => close());
  dialog.addEventListener("cancel", event => { event.preventDefault(); close(); });
  dialog.addEventListener("click", event => {
    const bounds = dialog.getBoundingClientRect();
    if (event.target === dialog && (event.clientX < bounds.left || event.clientX > bounds.right ||
        event.clientY < bounds.top || event.clientY > bounds.bottom)) close();
  });
  dialog.addEventListener("keydown", event => {
    event.stopPropagation();
    if (event.isComposing) return;
    if (event.repeat && ["Enter", " "].includes(event.key)) { event.preventDefault(); return; }
    if (event.ctrlKey || event.altKey || event.metaKey) return;
    if (event.key === "Tab") {
      const buttons = [...dialog.querySelectorAll("button:not(:disabled)")];
      if (event.shiftKey && document.activeElement === buttons[0]) { event.preventDefault(); buttons.at(-1)?.focus(); }
      else if (!event.shiftKey && document.activeElement === buttons.at(-1)) { event.preventDefault(); buttons[0]?.focus(); }
      return;
    }
    if (["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key)) {
      event.preventDefault();
      const buttons = [...dialog.querySelectorAll("button:not(:disabled)")];
      const current = buttons.indexOf(document.activeElement);
      const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 :
        (current + (event.key === "ArrowDown" ? 1 : -1) + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }
  });
  window.addEventListener("resize", position);
  return {
    get isOpen() { return dialog.open; },
    refreshProfiles: render,
    open(anchor = null) {
      const nextTrigger = anchor || trigger;
      if (dialog.open) {
        if (activeTrigger === nextTrigger) { close(); return; }
        activeTrigger.setAttribute("aria-expanded", "false");
        activeTrigger = nextTrigger;
        render();
        activeTrigger.setAttribute("aria-expanded", "true");
        position();
        (profilesEl.querySelector("button") || targetsEl.querySelector("button") || dialog.querySelector("[data-launch-new]")).focus();
        if (!loaded) request();
        return;
      }
      activeTrigger = nextTrigger;
      render();
      dialog.showModal();
      activeTrigger.setAttribute("aria-expanded", "true");
      position();
      (profilesEl.querySelector("button") || targetsEl.querySelector("button") || dialog.querySelector("[data-launch-new]")).focus();
      if (!loaded) request();
    },
    receive(message) {
      if (!pending || pending.requestId !== message.requestId) return;
      clearTimeout(pending.timer);
      pending = null;
      refresh.disabled = false;
      if (!message.error) {
        targets = (Array.isArray(message.targets) ? message.targets : []).filter(target => target && typeof target.id === "string" &&
          typeof target.title === "string" && ["powershell", "pwsh", "cmd", "wsl"].includes(target.shell) &&
          (target.shell !== "wsl" || typeof target.wslDistribution === "string"));
        loaded = true;
      }
      status.textContent = message.error || message.notice || "";
      render();
    }
  };
}
