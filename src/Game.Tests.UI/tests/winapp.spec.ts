import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from './winapp.fixture';
import { clickGuiControl, guiControlExists, guiControlMeasure } from './gui';

const HERE = path.dirname(fileURLToPath(import.meta.url));
// src/Game.Tests.UI/tests -> repo root -> docs/screenshots
const SCREENSHOT_DIR = path.resolve(HERE, '../../../docs/screenshots');

interface SceneMesh {
  name: string;
  isVisible: boolean;
  thinInstanceCount?: number;
}

interface Scene {
  meshes: SceneMesh[];
  textures: Array<{ getControlByName?: (name: string) => unknown }>;
  getEngine: () => { getRenderWidth: () => number; getRenderHeight: () => number };
}

interface Clock {
  timeSeconds: number;
  paused: number;
  processedInputs: number;
  droppedInputs: number;
}

interface SimpleSelfContainedStats {
  seats: number;
  floor: number;
  balls: number;
  markers: number;
  visibleInstances: number;
  clock: Clock | null;
}

interface HostHooks {
  __scene?: Scene;
  __engineClock?: () => Clock | null;
  __simpleSelfContained?: () => SimpleSelfContainedStats;
  __lastFireBall?: { dx: number; dy: number; dz: number; accepted: boolean };
  __inputViews?: { data: Float64Array; head: Int32Array; capacity: number };
  __simCommand?: (path: string) => void;
}

const readStats = (page: import('@playwright/test').Page) =>
  page.evaluate(() => {
    const hooks = window as unknown as HostHooks;
    return hooks.__simpleSelfContained ? hooks.__simpleSelfContained() : null;
  });

/**
 * Ensures the `simple-self-contained` fixture is the active scene (earlier spec files may have
 * left another demo or the main menu active). The menu is the boot scene; demos expose a
 * `Menu` back button, so navigation is: back to menu (if needed) -> live card.
 */
async function ensureSimpleScene(page: import('@playwright/test').Page): Promise<void> {
  await expect
    .poll(
      () =>
        page.evaluate(() => {
          const hooks = window as unknown as HostHooks;
          const scene = hooks.__scene as unknown as {
            textures?: Array<{ getControlByName?: (name: string) => unknown }>;
          } | undefined;
          const hasMenuCard = !!scene?.textures?.some((texture) =>
            texture.getControlByName?.('btn-menu-simple-self-contained'));
          const hasBackButton = !!scene?.textures?.some((texture) =>
            texture.getControlByName?.('btn-scene-menu'));
          return !!hooks.__simpleSelfContained || hasMenuCard || hasBackButton;
        }),
      { timeout: 60_000 }
    )
    .toBe(true);

  const active = await page.evaluate(() => !!(window as unknown as HostHooks).__simpleSelfContained);
  if (active) return;

  if (!(await guiControlExists(page, 'btn-menu-simple-self-contained'))) {
    await clickGuiControl(page, 'btn-scene-menu');
    await expect.poll(() => guiControlExists(page, 'btn-menu-simple-self-contained'), { timeout: 30_000 })
      .toBe(true);
  }

  await clickGuiControl(page, 'btn-menu-simple-self-contained');
  await expect.poll(() => page.evaluate(() => !!(window as unknown as HostHooks).__simpleSelfContained), {
    timeout: 30_000,
  }).toBe(true);
}

const postSimCommand = (page: import('@playwright/test').Page, command: string) =>
  page.evaluate((value) => (window as unknown as HostHooks).__simCommand?.(value), command);

/** Counts distinct colors in a 64×64 downscale of the WebGL canvas (blank = 1-2 colors). */
async function canvasColorCount(page: import('@playwright/test').Page): Promise<number> {
  return page.evaluate(() => {
    const canvas = document.getElementById('render-canvas') as HTMLCanvasElement | null;
    if (!canvas) return 0;
    const scratch = document.createElement('canvas');
    scratch.width = 64;
    scratch.height = 64;
    const context = scratch.getContext('2d');
    if (!context) return 0;
    context.drawImage(canvas, 0, 0, 64, 64);
    const data = context.getImageData(0, 0, 64, 64).data;
    const colors = new Set<string>();
    for (let i = 0; i < data.length; i += 4) {
      colors.add(`${data[i]},${data[i + 1]},${data[i + 2]}`);
    }
    return colors.size;
  });
}

/**
 * DemoHost (WebView2) E2E: the window boots the main menu, the page enters the
 * `simple-self-contained` fixture through its live card, the C# host streams the pinned float64
 * signal into a shared buffer, and Babylon writes the records into thin instances. Commands
 * ride the intentionally low-frequency message path; taps ride the ReadWrite zero-copy input ring.
 */
test.describe('DemoHost (WebView2)', () => {
  test('renders the simple-self-contained fixture entered from the menu', async ({ winAppPage: page }) => {
    const errors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') errors.push(msg.text());
    });
    page.on('pageerror', (error) => errors.push(String(error)));

    expect(page.url()).toContain('127.0.0.1');
    expect(await page.evaluate(() => globalThis.crossOriginIsolated)).toBe(true);

    await ensureSimpleScene(page);

    // ReadWrite input mapping arrives on the `input-hello` handshake.
    await expect
      .poll(() => page.evaluate(() => !!(window as unknown as HostHooks).__inputViews), { timeout: 30_000 })
      .toBe(true);

    // The boot scene connects the fixture sim and the signal reaches the page.
    await expect.poll(async () => (await readStats(page))?.visibleInstances ?? 0, { timeout: 30_000 })
      .toBeGreaterThan(0);

    const stats = await readStats(page);
    expect(stats!.floor).toBe(1);
    expect(stats!.markers).toBe(6);
    expect(stats!.balls).toBeGreaterThanOrEqual(1);

    // Host clock is flowing (fixed-step accumulator pumped by the dispatcher timer).
    await expect
      .poll(async () => (await readStats(page))?.clock?.timeSeconds ?? 0, { timeout: 20_000 })
      .toBeGreaterThan(0);

    // Real pixels: a blank/cleared canvas has one color; the fixture draws floor + spheres.
    await expect.poll(() => canvasColorCount(page), { timeout: 20_000 }).toBeGreaterThan(4);

    // Debug stats overlay laid out with live values.
    const overlay = await guiControlMeasure(page, 'debug-stats-text');
    expect(overlay, 'stats text control not found').not.toBeNull();
    expect(overlay!.text).toMatch(/FPS: \d+/);
    expect(overlay!.width).toBeGreaterThan(0);

    // Human-review artifact.
    await page.waitForTimeout(2500);
    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'simple-self-contained.png'), fullPage: false });

    expect(errors.filter((message) => !message.includes('favicon'))).toEqual([]);
  });

  test('runs commands and routes a tap through the zero-copy input ring', async ({ winAppPage: page }) => {
    const errors: string[] = [];
    page.on('console', (msg) => {
      if (msg.type() === 'error') errors.push(msg.text());
    });
    page.on('pageerror', (error) => errors.push(String(error)));

    await ensureSimpleScene(page);

    // Previous test may have left the fixed-step loop running; ensure a live baseline.
    await postSimCommand(page, '/api/simple-self-contained/resume');
    await expect.poll(async () => (await readStats(page))?.balls ?? 0, { timeout: 30_000 })
      .toBeGreaterThanOrEqual(1);
    const baseline = (await readStats(page))!.balls;

    // Payload-free verb over the message path: `command:{game}:spawn-ball`.
    await clickGuiControl(page, 'btn-spawn-ball');
    await expect.poll(async () => (await readStats(page))?.balls ?? 0, { timeout: 20_000 })
      .toBe(baseline + 1);

    // Tap fires a Bepu ball: the intent rides the ReadWrite ring, the host drains it and the
    // projectile comes back through the same pinned signal.
    const canvas = page.locator('#render-canvas');
    const canvasBox = await canvas.boundingBox();
    await page.mouse.click(canvasBox!.x + canvasBox!.width / 2, canvasBox!.y + canvasBox!.height * 0.45);
    await expect
      .poll(() => page.evaluate(() => (window as unknown as HostHooks).__lastFireBall?.accepted ?? false), {
        timeout: 15_000,
      })
      .toBe(true);
    await expect.poll(async () => (await readStats(page))?.balls ?? 0, { timeout: 20_000 })
      .toBe(baseline + 2);

    // The drain counter in the "globals" block proves the host consumed the ring record.
    await expect
      .poll(async () => (await readStats(page))?.clock?.processedInputs ?? 0, { timeout: 15_000 })
      .toBeGreaterThan(0);

    await page.screenshot({ path: path.join(SCREENSHOT_DIR, 'simple-self-contained-input.png'), fullPage: false });
    expect(errors.filter((message) => !message.includes('favicon'))).toEqual([]);
  });

  test('pause freezes sim time and resume restarts it', async ({ winAppPage: page }) => {
    await ensureSimpleScene(page);
    await postSimCommand(page, '/api/simple-self-contained/pause');
    await expect.poll(async () => (await readStats(page))?.clock?.paused ?? 0, { timeout: 15_000 }).toBe(1);

    const frozen = (await readStats(page))!.clock!.timeSeconds;
    await page.waitForTimeout(700);
    const still = (await readStats(page))!.clock!.timeSeconds;
    expect(Math.abs(still - frozen)).toBeLessThan(0.02);

    await postSimCommand(page, '/api/simple-self-contained/resume');
    await expect.poll(async () => (await readStats(page))?.clock?.paused ?? 1, { timeout: 15_000 }).toBe(0);
    await expect
      .poll(async () => (await readStats(page))?.clock?.timeSeconds ?? 0, { timeout: 15_000 })
      .toBeGreaterThan(still);
  });
});
