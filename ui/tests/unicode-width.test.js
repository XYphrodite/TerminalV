import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const main = await readFile(new URL("../src/main.js", import.meta.url), "utf8");
const pkg = JSON.parse(await readFile(new URL("../package.json", import.meta.url), "utf8"));

test("terminals use Unicode 11 widths so emoji take two cells like TUIs expect", () => {
  assert.ok(pkg.dependencies["@xterm/addon-unicode11"], "addon is a dependency");
  assert.match(main, /import \{ Unicode11Addon \} from "@xterm\/addon-unicode11";/);
  const load = main.indexOf("term.loadAddon(new Unicode11Addon());");
  const activate = main.indexOf('term.unicode.activeVersion = "11";');
  const open = main.indexOf("term.open(hostEl);");
  assert.ok(load > 0 && activate > load, "addon is loaded, then activated");
  assert.ok(open > activate, "version is active before the terminal opens and receives output");
});
