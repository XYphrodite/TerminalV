// Load the current UI sources in browser fixtures without a Vite build.
const packages = [
  ["@xterm/xterm", "xterm", "Terminal", "window"],
  ["@xterm/addon-fit", "addon-fit", "FitAddon", "window.FitAddon"],
  ["@xterm/addon-web-links", "addon-web-links", "WebLinksAddon", "window.WebLinksAddon"],
  ["@xterm/addon-webgl", "addon-webgl", "WebglAddon", "window.WebglAddon"],
  ["@xterm/addon-serialize", "addon-serialize", "SerializeAddon", "window.SerializeAddon"],
  ["@xterm/addon-search", "addon-search", "SearchAddon", "window.SearchAddon"]
];
let source = await (await fetch("/src/main.js")).text();
for (const [pkg, file, name, global] of packages) {
  await new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = `/node_modules/${pkg}/lib/${file}.js`;
    script.onload = resolve;
    script.onerror = () => reject(new Error(`Unable to load ${pkg}`));
    document.head.append(script);
  });
  source = source.replace(`import { ${name} } from "${pkg}";`, `const { ${name} } = ${global};`);
}
source = source.replace(/^import "[^"\n]+\.css";\r?\n/gm, "")
  .replace(/from "(\.[^"\n]+)"(\s+with\s+\{[^}]+\})?/g, (_, path, withClause) => `from "${new URL(path, new URL("/src/main.js", document.baseURI)).href}"${withClause || ""}`);
source += "\nwindow.__sourceTabs = tabs;\n";
const url = URL.createObjectURL(new Blob([source], { type: "text/javascript" }));
try { await import(url); }
finally { URL.revokeObjectURL(url); }
