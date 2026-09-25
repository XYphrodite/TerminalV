// Real animation frames are required for scrollbar geometry assertions.
// Chromium's --dump-dom --virtual-time-budget can finish timers without
// running xterm's pending requestAnimationFrame callbacks.
export async function runFrameFixture(browser, profile, url) {
  const { chromium } = await import("playwright-core");
  const context = await chromium.launchPersistentContext(profile, {
    executablePath: browser,
    headless: true,
    timeout: 15000,
    args: ["--disable-gpu", "--disable-extensions", "--disable-background-networking", "--disable-component-update"]
  });
  try {
    const page = await context.newPage();
    await page.goto(url, { timeout: 15000 });
    await page.waitForFunction(() => document.body.dataset.testResult, null, { timeout: 15000 });
    return await page.content();
  } finally {
    await context.close();
  }
}
