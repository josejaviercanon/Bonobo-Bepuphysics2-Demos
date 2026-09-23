import { defineConfig } from '@playwright/test';

/**
 * E2E suite for the desktop host (`DemoHost.WinApp`: WinUI 3 + WebView2 + Native AOT).
 *
 * The harness launches the published unpackaged exe with `--remote-debugging-port` (via
 * `scripts/launch-demohost.mjs`) and the spec attaches over CDP with
 * `chromium.connectOverCDP()` (the official Playwright WebView2 pattern). The page is never
 * navigated — tests grab the already-loaded `http://127.0.0.1:<port>/index.html` tab.
 *
 * Run: `npm run test:winapp` (publishes the Release host when missing; set
 * GAME_WINAPP_PUBLISH=1 to force a fresh publish, GAME_WINAPP_REUSE=1 to attach to a running
 * app, GAME_WINAPP_CDP_PORT to move the debugging port).
 */
const PORT = Number(process.env.GAME_WINAPP_CDP_PORT ?? 9223);
const CDP_READY_URL = `http://127.0.0.1:${PORT}/json/version`;

export default defineConfig({
  testDir: './tests',
  timeout: 120_000,
  expect: { timeout: 20_000 },
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never', outputFolder: '../../output/playwright/report' }]],
  outputDir: '../../output/playwright/artifacts',
  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `node scripts/launch-demohost.mjs${process.env.GAME_WINAPP_PUBLISH ? ' --publish' : ''}`,
    url: CDP_READY_URL,
    reuseExistingServer: process.env.GAME_WINAPP_REUSE === '1',
    timeout: 600_000,
  },
});
