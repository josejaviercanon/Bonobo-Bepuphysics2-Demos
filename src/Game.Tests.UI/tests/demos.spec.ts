import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from './winapp.fixture';
import { clickGuiControl, guiControlExists } from './gui';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const SCREENSHOT_DIR = path.resolve(HERE, '../../../docs/screenshots');

interface HostWindow {
  __pyramid?: () => { floor: number; boxes: number; projectiles: number; visibleInstances: number };
  __bounciness?: () => { floor: number; balls: number; visibleInstances: number; meanHeight: number };
  __planet?: () => { planet: number; balls: number; visibleInstances: number; meanDistance: number };
}

const SCENES = [
  { key: 'pyramid', hook: '__pyramid' },
  { key: 'bounciness', hook: '__bounciness' },
  { key: 'planet', hook: '__planet' },
] as const;

async function visibleInstances(page: import('@playwright/test').Page, hook: string): Promise<number> {
  return page.evaluate((hookName) => {
    const hooks = window as unknown as Record<string, (() => { visibleInstances: number }) | undefined>;
    return hooks[hookName]?.().visibleInstances ?? 0;
  }, hook);
}

/**
 * Navigates to a demo through the main menu. Handles all three entry states: already on the
 * target demo, on the menu, or on another demo (click the `Menu` back button first).
 */
async function openDemoFromMenu(page: import('@playwright/test').Page, key: string): Promise<void> {
  await expect
    .poll(
      async () =>
        (await guiControlExists(page, `btn-menu-${key}`)) ||
        (await guiControlExists(page, 'btn-scene-menu')),
      { timeout: 60_000 }
    )
    .toBe(true);

  if (!(await guiControlExists(page, `btn-menu-${key}`))) {
    await clickGuiControl(page, 'btn-scene-menu');
  }

  await expect.poll(() => guiControlExists(page, `btn-menu-${key}`), { timeout: 30_000 }).toBe(true);
  await page.waitForTimeout(300);
  await clickGuiControl(page, `btn-menu-${key}`);
}

/**
 * Scene-menu smoke tests for the ported demo set: each demo connects its C# fixture, streams
 * the pinned float64 signal and renders thin instances — screenshots are committed for human
 * review (`docs/screenshots/<game-key>.png`). Every demo is entered through the main menu and
 * left through the `Menu` back button, which releases the previous sim + pinned buffer.
 */
test.describe('ported demo scenes', () => {
  for (const scene of SCENES) {
    test(`renders the ${scene.key} demo`, async ({ winAppPage: page }) => {
      const errors: string[] = [];
      page.on('console', (msg) => {
        if (msg.type() === 'error') errors.push(msg.text());
      });
      page.on('pageerror', (error) => errors.push(String(error)));

      await openDemoFromMenu(page, scene.key);

      await expect.poll(() => visibleInstances(page, scene.hook), { timeout: 60_000 }).toBeGreaterThan(0);

      // Let the scene settle before the human-review screenshot (gravity demos need motion).
      await page.waitForTimeout(scene.key === 'planet' ? 4000 : 2500);
      await page.screenshot({ path: path.join(SCREENSHOT_DIR, `${scene.key}.png`), fullPage: false });

      // Fixture-specific sanity: floor/planet record present and instances flowing.
      const stats = await page.evaluate((hookName) => {
        const hooks = window as unknown as Record<string, (() => Record<string, number>) | undefined>;
        return hooks[hookName]?.() ?? null;
      }, scene.hook);
      expect(stats).not.toBeNull();
      expect(stats!.visibleInstances).toBeGreaterThan(0);

      expect(errors.filter((message) => !message.includes('favicon'))).toEqual([]);
    });
  }
});
