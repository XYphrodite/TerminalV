import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, extname, join, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const uiRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

async function runBrowserFixture(t, fixture) {
  const browser = [
    process.env.TERMINALV_TEST_BROWSER,
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Google/Chrome/Application/chrome.exe"
  ].find((path) => path && existsSync(path));
  assert.ok(browser, "Set TERMINALV_TEST_BROWSER to a Chromium browser executable");

  const server = createServer(async (request, response) => {
    try {
      const path = resolve(uiRoot, "." + decodeURIComponent(new URL(request.url, "http://localhost").pathname));
      if (!path.startsWith(uiRoot + sep)) {
        response.writeHead(403).end();
        return;
      }
      const content = await readFile(path);
      const mime = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css" };
      response.setHeader("Content-Type", (mime[extname(path)] || "application/octet-stream") + "; charset=utf-8");
      response.end(content);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  const profile = await mkdtemp(join(tmpdir(), "terminalv-copy-test-"));
  try {
    const screenshot = fixture === "paste-confirmation" && process.env.TERMINALV_TEST_SCREENSHOT;
    const output = await new Promise((resolve, reject) => {
      const child = spawn(browser, [
        "--headless", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
        "--disable-extensions", "--disable-background-networking", "--disable-component-update",
        `--user-data-dir=${profile}`, "--dump-dom", "--virtual-time-budget=5000",
        ...(screenshot ? [`--screenshot=${screenshot}`, "--window-size=800,600"] : []),
        `http://127.0.0.1:${server.address().port}/tests/${fixture}.fixture.html${screenshot ? "?preview" : ""}`
      ], { windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", (chunk) => { stdout += chunk; });
      child.stderr.on("data", (chunk) => { stderr += chunk; });
      const timer = setTimeout(() => {
        child.kill();
        reject(new Error(`${fixture} browser test timed out\n${stderr}`));
      }, 30000);
      child.once("error", (error) => { clearTimeout(timer); reject(error); });
      child.once("close", (code) => {
        clearTimeout(timer);
        if (code === 0) resolve(stdout);
        else reject(new Error(`Browser exited ${code}\n${stderr}`));
      });
    });
    const result = output.match(/data-test-result="([A-Za-z0-9+/=]+)"/);
    assert.ok(result, `Browser did not finish ${fixture} tests\n${output}`);
    const checks = JSON.parse(Buffer.from(result[1], "base64").toString("utf8"));
    assert.ok(checks.length > 0);
    for (const check of checks) {
      await t.test(check.name, () => assert.equal(check.ok, true, check.error));
    }
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
    // Only the isolated profile created by this test is removed.
    await rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

test("copy selection in xterm.js", (t) => runBrowserFixture(t, "selection"));
test("multiline paste confirmation in xterm.js", (t) => runBrowserFixture(t, "paste-confirmation"));
