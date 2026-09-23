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
  __friction?: () => { floor: number; boxes: number; visibleInstances: number; meanX: number };
  __perBodyGravity?: () => { floor: number; spheres: number; capsules: number; boxes: number; visibleInstances: number };
  __colosseum?: () => { ground: number; boxes: number; projectiles: number; visibleInstances: number };
  __continuousCollisionDetection?: () => { ground: number; discrete: number; passive: number; continuous: number; spinners: number; visibleInstances: number };
  __substepping?: () => { ground: number; rope: number; wreckingBall: number; stack: number; chains: number; visibleInstances: number };
  __compound?: () => { ground: number; plane: number; spheres: number; capsules: number; boxes: number; visibleInstances: number };
  __contactEvents?: () => { statics: number; bodies: number; particles: number; visibleInstances: number };
  __collisionTracking?: () => { statics: number; bodies: number; particles: number; visibleInstances: number };
  __customVoxelCollidable?: () => { ground: number; boxes: number; voxels: number; visibleInstances: number };
  __ropeStability?: () => { ground: number; ropes: number; balls: number; post: number; visibleInstances: number };
  __ropeTwist?: () => { ground: number; ropes: number; ball: number; visibleInstances: number };
  __chainFountain?: () => { ground: number; walls: number; beads: number; visibleInstances: number };
  __blockChain?: () => { ground: number; blocks: number; coins: number; visibleInstances: number };
  __ragdollTube?: () => { ground: number; tube: number; bodies: number; visibleInstances: number };
  __dancer?: () => { dancers: number; bodyInstances: number; dressNodes: number; visibleInstances: number };
  __plumpDancer?: () => { dancers: number; bodyInstances: number; suitNodes: number; visibleInstances: number };
}

const SCENES = [
  { key: 'pyramid', hook: '__pyramid' },
  { key: 'bounciness', hook: '__bounciness' },
  { key: 'planet', hook: '__planet' },
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
] as const;

/** Contact demos re-drop their bodies just before the screenshot so particles are visible. */
const DROP_DEMOS = new Set(['contact-events', 'collision-tracking']);

/** Heavier fixtures get a longer settle window before the human-review screenshot. */
const LONG_SETTLE = new Set([
  'planet',
  'colosseum',
  'continuous-collision-detection',
  'substepping',
  'compound',
  'custom-voxel-collidable',
  'chain-fountain',
  'ragdoll-tube',
  'dancer',
  'plump-dancer',
]);

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
      await page.waitForTimeout(LONG_SETTLE.has(scene.key) ? 4000 : 2500);

      if (DROP_DEMOS.has(scene.key)) {
        // Re-drop the listened bodies and catch the fresh contact particles.
        await clickGuiControl(page, 'btn-drop');
        await page.waitForTimeout(1200);
      }

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
