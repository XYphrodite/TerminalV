import { readFile, mkdir, copyFile, readdir, rm } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const root = fileURLToPath(new URL("../../", import.meta.url));
const desktop = resolve(root, "src/TerminalV/wwwroot");
const mobile = resolve(root, "src/TerminalV.Mobile/wwwroot/assets");
const html = await readFile(resolve(desktop, "index.html"), "utf8");
const js = html.match(/src="\.\/assets\/([^"]+\.js)"/)?.[1];
const css = html.match(/href="\.\/assets\/([^"]+\.css)"/)?.[1];
if (!js || !css) throw new Error("Desktop UI entry assets are missing. Run npm run build first.");

// Rebuild the mobile asset folder from scratch. Copying the whole desktop
// directory left every previous hashed bundle behind (Vite names change each
// build), so the folder grew to dozens of dead files. Mobile only loads
// mobile.js / mobile.css, plus any non-entry assets (fonts, images).
await rm(mobile, { recursive: true, force: true });
await mkdir(mobile, { recursive: true });

const entries = await readdir(resolve(desktop, "assets"));
for (const name of entries) {
  if (name === js || name === css) continue;
  await copyFile(resolve(desktop, "assets", name), resolve(mobile, name));
}
await copyFile(resolve(desktop, "assets", js), resolve(mobile, "mobile.js"));
await copyFile(resolve(desktop, "assets", css), resolve(mobile, "mobile.css"));
