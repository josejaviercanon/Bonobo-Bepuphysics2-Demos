import { test as base, expect, type Browser, type BrowserContext, type Page } from '@playwright/test';

/**
 * CDP fixture for the DemoHost (WebView2) host, following the official Playwright WebView2
 * pattern: connect over CDP to the app's debugging port and take the already-loaded page from
 * the first context — never `page.goto()`.
 */
const CDP_ENDPOINT =
  process.env.GAME_WINAPP_CDP ?? `http://127.0.0.1:${process.env.GAME_WINAPP_CDP_PORT ?? 9223}`;

interface WinAppFixtures {
  winAppBrowser: Browser;
  winAppContext: BrowserContext;
  winAppPage: Page;
}

/** WebView2 exposes an initial `about:blank` page; the app page is served from loopback. */
async function findAppPage(context: BrowserContext): Promise<Page> {
  const deadline = Date.now() + 30_000;
  for (;;) {
    const page = context.pages().find((candidate) => candidate.url().includes('127.0.0.1'));
    if (page) return page;
    if (Date.now() > deadline) {
      const open = context.pages().map((candidate) => candidate.url()).join(', ') || '<none>';
      throw new Error(`no app page (127.0.0.1) after 30 s (open pages: ${open})`);
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
}

export const test = base.extend<WinAppFixtures>({
  winAppBrowser: async ({ playwright }, use) => {
    const browser = await playwright.chromium.connectOverCDP(CDP_ENDPOINT);
    await use(browser);
    // Disconnects from the app; the launcher (webServer) owns the process lifetime.
    await browser.close();
  },
  winAppContext: async ({ winAppBrowser }, use) => {
    const context = winAppBrowser.contexts()[0];
    if (!context) throw new Error(`no WebView2 browser context on ${CDP_ENDPOINT}`);
    await use(context);
  },
  winAppPage: async ({ winAppContext }, use) => {
    await use(await findAppPage(winAppContext));
  },
});

export { expect };
