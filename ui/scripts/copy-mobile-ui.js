import { readFile, mkdir, cp, copyFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const root = fileURLToPath(new URL("../../", import.meta.url));
const desktop = resolve(root, "src/TerminalV/wwwroot");
const mobile = resolve(root, "src/TerminalV.Mobile/wwwroot/assets");
const html = await readFile(resolve(desktop, "index.html"), "utf8");
const js = html.match(/src="\.\/assets\/([^"]+\.js)"/)?.[1];
const css = html.match(/href="\.\/assets\/([^"]+\.css)"/)?.[1];
if (!js || !css) throw new Error("Desktop UI entry assets are missing. Run npm run build first.");
await mkdir(mobile, { recursive: true });
await cp(resolve(desktop, "assets"), mobile, { recursive: true });
await copyFile(resolve(desktop, "assets", js), resolve(mobile, "mobile.js"));
await copyFile(resolve(desktop, "assets", css), resolve(mobile, "mobile.css"));
