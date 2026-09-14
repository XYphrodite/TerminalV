import { SESSION_COLORS } from "./session-management.js";

export const PROFILE_SHELLS = {
  auto: "Автоматически", powershell: "Windows PowerShell", pwsh: "PowerShell 7", cmd: "Командная строка"
};

export function validateProfile(record) {
  const profile = {
    id: record.id, title: record.title?.trim(), shell: record.shell,
    cwd: record.cwd?.trim() || null, color: record.color || null,
    startupCommand: record.startupCommand || null
  };
  if (typeof profile.id !== "string" || !profile.id || profile.id.length > 80 ||
      !profile.title || profile.title.length > 80 || /[\x00-\x1f\x7f-\x9f]/.test(profile.title))
    throw new Error("Укажите название профиля до 80 символов.");
  if (!Object.hasOwn(PROFILE_SHELLS, profile.shell)) throw new Error("Выберите оболочку.");
  if (profile.color && !Object.hasOwn(SESSION_COLORS, profile.color)) throw new Error("Выберите цветовую метку.");
  if (profile.cwd && (profile.cwd.length > 2048 || /[\x00-\x1f\x7f-\x9f"<>|*?]/.test(profile.cwd) ||
      !/^(?:[a-z]:[\\/]|\\\\[^\\]+\\[^\\]+)/i.test(profile.cwd)))
    throw new Error("Укажите полный путь к папке без кавычек, например C:\\Projects.");
  if (profile.startupCommand && (profile.startupCommand.length > 4096 || profile.startupCommand.includes("\0")))
    throw new Error("Стартовая команда: максимум 4096 символов, без NUL.");
  if (profile.shell === "cmd" && /[\r\n]/.test(profile.startupCommand || ""))
    throw new Error("Для cmd укажите одну строку; команды можно соединить через &&.");
  return profile;
}

// Copy values, never retain a mutable reference to the saved profile.
export function profileSession(profile) {
  return { title: profile.title, customTitle: profile.title, shell: profile.shell,
    cwd: profile.cwd, color: profile.color, startupCommand: profile.startupCommand };
}

export function createLaunchProfiles({ dialog, post, onLaunch, restoreFocus, onChanged = () => {} }) {
  const find = (name) => dialog.querySelector(`[data-profile-${name}]`);
  const form = dialog.querySelector("form"), fields = dialog.querySelector("fieldset");
  const title = find("title"), shell = find("shell"), cwd = find("cwd"), color = find("color"), command = find("command");
  const error = find("error"), remove = find("delete"), confirmation = find("delete-confirm");
  const cancel = find("cancel");
  let profiles = [], draftId = null, pending = null, loadedCommand = "", displayedCommand = "";
  for (const [id, name] of Object.entries(PROFILE_SHELLS)) shell.add(new Option(name, id));
  for (const [id, value] of Object.entries(SESSION_COLORS)) color.add(new Option(value.name, id));

  function fill(id) {
    const record = profiles.find((item) => item.id === id);
    draftId = record?.id || crypto.randomUUID();
    dialog.querySelector("h2").textContent = record ? "Изменить профиль" : "Создать профиль";
    find("launch").hidden = Boolean(record);
    title.value = record?.title || "";
    shell.value = record?.shell || "auto";
    cwd.value = record?.cwd || "";
    color.value = record?.color || "";
    loadedCommand = record?.startupCommand || "";
    command.value = loadedCommand;
    displayedCommand = command.value; // textarea normalizes CRLF; don't rewrite an untouched script.
    remove.disabled = !record;
    confirmation.hidden = true;
    error.textContent = "";
  }
  function render(id) {
    fill(id);
  }
  function busy(value) {
    fields.disabled = value;
    cancel.disabled = value;
    dialog.setAttribute("aria-busy", String(value));
  }
  function close() {
    if (pending) return;
    if (dialog.open) dialog.close();
    restoreFocus();
  }
  function save(next, action, id) {
    if (pending || !dialog.open) return;
    busy(true);
    error.textContent = "Сохранение…";
    const requestId = crypto.randomUUID();
    const timer = setTimeout(() => {
      if (pending?.requestId !== requestId) return;
      pending = null;
      busy(false);
      error.textContent = "Нет подтверждения сохранения. Профиль не запущен. Закройте и откройте окно приложения, чтобы проверить сохранённые данные.";
    }, 5000);
    pending = { requestId, action, id, timer };
    post({ type: "persist-profiles", requestId, profiles: next });
  }
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (pending) return;
    try {
      const profile = validateProfile({ id: draftId, title: title.value, shell: shell.value,
        cwd: cwd.value, color: color.value,
        startupCommand: command.value === displayedCommand ? loadedCommand : command.value });
      const next = profiles.some((p) => p.id === profile.id)
        ? profiles.map((p) => p.id === profile.id ? profile : p) : [...profiles, profile];
      if (next.length > 100) throw new Error("Допустимо до 100 профилей.");
      const launch = event.submitter?.dataset.profileLaunch !== undefined && !profiles.some(p => p.id === profile.id);
      save(next, launch ? "launch" : "save", profile.id);
    } catch (failure) { error.textContent = failure.message; }
  });
  remove.addEventListener("click", () => {
    confirmation.hidden = false;
    find("delete-name").textContent = profiles.find((p) => p.id === draftId)?.title || "";
    find("keep").focus();
  });
  find("keep").addEventListener("click", () => { confirmation.hidden = true; remove.focus(); });
  find("confirm-delete").addEventListener("click", () => save(profiles.filter((p) => p.id !== draftId), "delete", null));
  cancel.addEventListener("click", close);
  dialog.addEventListener("cancel", (event) => { event.preventDefault(); close(); });
  dialog.addEventListener("keydown", (event) => event.stopPropagation());

  return {
    get isOpen() { return dialog.open; },
    get profiles() { return profiles.map(p => ({ ...p })); },
    setProfiles(records) { if (!pending) { profiles = (records || []).map((p) => ({ ...p })); onChanged(); } },
    open(id = null) {
      if (dialog.open) return;
      render(id);
      dialog.showModal();
      title.focus();
    },
    receive(message) {
      if (!pending || pending.requestId !== message.requestId) return;
      const { action, id, timer } = pending;
      clearTimeout(timer);
      pending = null;
      busy(false);
      if (message.error) { error.textContent = `Не удалось сохранить: ${message.error}`; return; }
      profiles = message.profiles.map((p) => ({ ...p }));
      onChanged();
      render(id);
      if (action === "launch") {
        const profile = profiles.find((p) => p.id === id);
        if (!profile) { error.textContent = "Сохранённый профиль не найден. Запуск отменён."; return; }
        close();
        onLaunch(profileSession(profile));
      } else {
        error.textContent = action === "delete" ? "Профиль удалён. Работающие сессии не изменены." : "Профиль сохранён.";
      }
    }
  };
}
