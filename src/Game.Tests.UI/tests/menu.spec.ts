import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from './winapp.fixture';
import { clickGuiControl, guiControlEnabled, guiControlExists, guiControlMeasure } from './gui';

const HERE = path.dirname(fileURLToPath(import.meta.url));
// src/Game.Tests.UI/tests -> repo root -> docs/screenshots
const SCREENSHOT_DIR = path.resolve(HERE, '../../../docs/screenshots');

interface MenuStats {
  total: number;
  live: number;
  placeholders: number;
}

interface HostHooks {
  __menu?: () => MenuStats;
  __engineClock?: () => { timeSeconds: number } | null;
  __pyramid?: () => { visibleInstances: number };
  __bounciness?: () => { visibleInstances: number };
  __planet?: () => { visibleInstances: number };
  __simpleSelfContained?: () => { visibleInstances: number };
  __friction?: () => { visibleInstances: number };
  __perBodyGravity?: () => { visibleInstances: number };
  __colosseum?: () => { visibleInstances: number };
  __continuousCollisionDetection?: () => { visibleInstances: number };
  __substepping?: () => { visibleInstances: number };
  __compound?: () => { visibleInstances: number };
  __contactEvents?: () => { visibleInstances: number };
  __collisionTracking?: () => { visibleInstances: number };
  __customVoxelCollidable?: () => { visibleInstances: number };
  __ropeStability?: () => { visibleInstances: number };
  __ropeTwist?: () => { visibleInstances: number };
  __chainFountain?: () => { visibleInstances: number };
  __blockChain?: () => { visibleInstances: number };
  __ragdollTube?: () => { visibleInstances: number };
  __dancer?: () => { visibleInstances: number };
  __plumpDancer?: () => { visibleInstances: number };
  __rayCasting?: () => { visibleInstances: number };
  __sweep?: () => { visibleInstances: number };
  __collisionQuery?: () => { visibleInstances: number };
  __solverContactEnumeration?: () => { visibleInstances: number };
  __car?: () => { visibleInstances: number };
  __tank?: () => { visibleInstances: number };
  __newt?: () => { visibleInstances: number };
  __character?: () => { visibleInstances: number };
  __sponsor?: () => { visibleInstances: number };
  __cloth?: () => { visibleInstances: number };
}

const DEMOS = [
  { key: 'pyramid', hook: '__pyramid' },
  { key: 'bounciness', hook: '__bounciness' },
  { key: 'planet', hook: '__planet' },
  { key: 'simple-self-contained', hook: '__simpleSelfContained' },
  { key: 'friction', hook: '__friction' },
  { key: 'per-body-gravity', hook: '__perBodyGravity' },
  { key: 'colosseum', hook: '__colosseum' },
  { key: 'continuous-collision-detection', hook: '__continuousCollisionDetection' },
  { key: 'substepping', hook: '__substepping' },
  { key: 'compound', hook: '__compound' },
  { key: 'contact-events', hook: '__contactEvents' },
  { key: 'collision-tracking', hook: '__collisionTracking' },
  { key: 'custom-voxel-collidable', hook: '__customVoxelCollidable' },
  { key: 'rope-stability', hook: '__ropeStability' },
  { key: 'rope-twist', hook: '__ropeTwist' },
  { key: 'chain-fountain', hook: '__chainFountain' },
  { key: 'block-chain', hook: '__blockChain' },
  { key: 'ragdoll-tube', hook: '__ragdollTube' },
  { key: 'dancer', hook: '__dancer' },
  { key: 'plump-dancer', hook: '__plumpDancer' },
  { key: 'ray-casting', hook: '__rayCasting' },
  { key: 'sweep', hook: '__sweep' },
  { key: 'collision-query', hook: '__collisionQuery' },
  { key: 'solver-contact-enumeration', hook: '__solverContactEnumeration' },
  { key: 'car', hook: '__car' },
  { key: 'tank', hook: '__tank' },
  { key: 'newt', hook: '__newt' },
  { key: 'character', hook: '__character' },
  { key: 'sponsor', hook: '__sponsor' },
  { key: 'cloth', hook: '__cloth' },
] as const;

const readMenu = (page: import('@playwright/test').Page) =>
  page.evaluate(() => {
    const hooks = window as unknown as HostHooks;
    return hooks.__menu ? hooks.__menu() : null;
  });

const readInstances = (page: import('@playwright/test').Page, hook: string) =>
  page.evaluate((hookName) => {
    const hooks = window as unknown as Record<string, (() => { visibleInstances: number }) | undefined>;
    return hooks[hookName]?.().visibleInstances ?? 0;
  }, hook);

/** Waits for the main menu scene to be active (hook + live card laid out). */
async function waitForMenu(page: import('@playwright/test').Page): Promise<void> {
  await expect.poll(() => readMenu(page), { timeout: 60_000 }).not.toBeNull();
  await expect.poll(() => guiControlExists(page, 'btn-menu-pyramid'), { timeout: 30_000 }).toBe(true);
}

/** Navigates from wherever the app is back to the menu. */
async function goToMenu(page: import('@playwright/test').Page): Promise<void> {
  if (await guiControlExists(page, 'btn-menu-pyramid')) return;
  await expect.poll(() => guiControlExists(page, 'btn-scene-menu'), { timeout: 30_000 }).toBe(true);
  await clickGuiControl(page, 'btn-scene-menu');
  await waitForMenu(page);
}

/**
 * Main-menu E2E: the boot scene is the fully ported 30-demo card grid. Every card is live (each
 * connects its C# fixture through the reserved `menu` -> demo key switch). Screenshots are
 * committed for human review (`docs/screenshots/menu*.png`).
 */
test.describe('main menu scene', () => {
  test('renders the 30-demo menu grid', async ({ winAppPage: page }) => {
    const errors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') errors.push(msg.text());
    });
    page.on('pageerror', (error) => errors.push(String(error)));

    await goToMenu(page);

    const menu = await readMenu(page);
    expect(menu).toEqual({ total: 30, live: 30, placeholders: 0 });

    // Every demo slot has an enabled card; no placeholders remain.
    for (const demo of DEMOS) {
      expect(await guiControlExists(page, `btn-menu-${demo.key}`), `${demo.key} card missing`).toBe(true);
      expect(await guiControlEnabled(page, `btn-menu-${demo.key}`)).toBe(true);
    }
    expect(await guiControlExists(page, 'btn-menu-slot-1')).toBe(false);
    expect(await guiControlExists(page, 'btn-menu-slot-29')).toBe(false);

    // The overlay actually laid out (a zero measure would render nothing).
    const title = await guiControlMeasure(page, 'menu-title');
    expect(title, 'menu title control not found').not.toBeNull();
    expect(title!.text).toContain('Bonobo BepuPhysics2 Demos');
    expect(title!.width).toBeGreaterThan(0);

    // The host is idle on the menu (no sim key) but the globals clock block keeps streaming.
    await expect.poll(async () => page.evaluate(() => {
      const hooks = window as unknown as HostHooks;
      return hooks.__engineClock?.() ?? null;
    }), { timeout: 20_000 }).not.toBeNull();

    await page.waitForTimeout(1500);
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'menu.png'), fullPage: false });

    expect(errors.filter((message) => !message.includes('favicon'))).toEqual([]);
  });

  test('round-trips every live demo back to the menu (memory reset)', async ({ winAppPage: page }) => {
    const errors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') errors.push(msg.text());
    });
    page.on('pageerror', (error) => errors.push(String(error)));

    await goToMenu(page);

    for (const demo of DEMOS) {
      await clickGuiControl(page, `btn-menu-${demo.key}`);
      await expect.poll(() => readInstances(page, demo.hook), { timeout: 60_000 }).toBeGreaterThan(0);

      // Back to the menu: the demo scene is disposed (hook cleared) and the host released the
      // demo simulation + its pinned signal buffer (the `menu` key is unknown by design).
      await clickGuiControl(page, 'btn-scene-menu');
      await waitForMenu(page);
      expect(await readMenu(page)).toEqual({ total: 30, live: 30, placeholders: 0 });
      expect(await page.evaluate((hookName) => {
        const hooks = window as unknown as Record<string, unknown>;
        return hooks[hookName] === undefined;
      }, demo.hook)).toBe(true);
    }

    await page.waitForTimeout(1000);
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'menu-after-roundtrip.png'), fullPage: false });

    expect(errors.filter((message) => !message.includes('favicon'))).toEqual([]);
  });
});
