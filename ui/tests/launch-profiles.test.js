import assert from "node:assert/strict";
import test from "node:test";
import { validateProfile, profileSession } from "../src/launch-profiles.js";

const base = { id: "test", title: "Мой проект", shell: "powershell", cwd: "C:\\Проект O'Brien [test]", color: "green",
  startupCommand: "Write-Output 'Первая строка'\r\nWrite-Output 'Вторая строка'" };
test("profile validation preserves literal paths, Cyrillic and multiline PowerShell exactly", () => {
  assert.deepEqual(validateProfile(base), base);
  assert.equal(validateProfile({ ...base, cwd: "  " }).cwd, null);
  assert.equal(validateProfile({ ...base, cwd: "\\\\server\\share\\папка" }).cwd, "\\\\server\\share\\папка");
});
test("invalid profile names, shells, paths and commands are rejected without truncation", () => {
  for (const invalid of [
    { title: " " }, { title: "x".repeat(81) }, { title: "new\ntitle" }, { shell: "unknown" }, { shell: "__proto__" },
    { color: "toString" }, { cwd: "C:relative" }, { cwd: '"C:\\Test"' }, { cwd: "C:\\bad\npath" },
    { startupCommand: "x".repeat(4097) }, { startupCommand: "echo\0bad" }, { shell: "cmd" }
  ]) assert.throws(() => validateProfile({ ...base, ...invalid }));
  assert.equal(validateProfile({ ...base, shell: "cmd", startupCommand: 'echo "quotes & literal" && echo ok' }).shell, "cmd");
});
test("session launch is an independent snapshot and never includes a saved profile id", () => {
  const profile = validateProfile(base), options = profileSession(profile);
  profile.title = "Edited";
  profile.startupCommand = "new command";
  assert.equal(options.title, base.title);
  assert.equal(options.customTitle, base.title);
  assert.equal(options.startupCommand, base.startupCommand);
  assert.equal(Object.hasOwn(options, "id"), false);
});
