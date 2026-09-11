import { defineConfig } from "vite";
import { resolve } from "node:path";

export default defineConfig({
  root: ".",
  base: "./",
  build: {
    outDir: resolve(__dirname, "../src/TerminalV/wwwroot"),
    emptyOutDir: true,
    assetsDir: "assets"
  }
});
