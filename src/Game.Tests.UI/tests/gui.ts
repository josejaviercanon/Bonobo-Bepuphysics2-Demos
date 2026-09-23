import { expect, Page } from '@playwright/test';

/**
 * Babylon GUI control helpers for E2E. Buttons are located through the live
 * `_currentMeasure` of the control instead of hard-coded pixel offsets, so adding or
 * resizing a button never silently moves the click target (the GUI scales with
 * `AdvancedDynamicTexture.idealWidth`).
 */

export async function guiControlPoint(page: Page, controlName: string): Promise<{ x: number; y: number }> {
  const point = await page.evaluate((name) => {
    const scene = (window as unknown as { __scene?: { textures: Array<{ getControlByName?: (n: string) => { _currentMeasure: { left: number; top: number; width: number; height: number } } | null }> } })
      .__scene;
    if (!scene) return null;

    for (const texture of scene.textures) {
      const control = texture.getControlByName?.(name);
      if (!control) continue;
      const measure = control._currentMeasure;
      return { x: measure.left + measure.width / 2, y: measure.top + measure.height / 2 };
    }
    return null;
  }, controlName);

  expect(point, `GUI control '${controlName}' not found`).not.toBeNull();
  return point!;
}

/** Clicks the center of a GUI control in page coordinates (canvas offset + ideal-ratio scale applied). */
export async function clickGuiControl(page: Page, controlName: string): Promise<void> {
  const point = await guiControlPoint(page, controlName);
  const canvasBox = await page.locator('#render-canvas').boundingBox();
  expect(canvasBox, 'render canvas has no bounding box').not.toBeNull();

  const renderWidth = await page.evaluate(() => {
    const scene = (window as unknown as { __scene?: { getEngine: () => { getRenderWidth: () => number } } }).__scene;
    return scene ? scene.getEngine().getRenderWidth() : 0;
  });
  expect(renderWidth, 'engine render width unavailable').toBeGreaterThan(0);

  const scale = canvasBox!.width / renderWidth;
  await page.mouse.click(canvasBox!.x + point.x * scale, canvasBox!.y + point.y * scale);
}

/** True while the named GUI control is visible (used for the F2 stats overlay). */
export async function guiControlVisible(page: Page, controlName: string): Promise<boolean | null> {
  return page.evaluate((name) => {
    const scene = (window as unknown as { __scene?: { textures: Array<{ getControlByName?: (n: string) => { isVisible: boolean } | null }> } })
      .__scene;
    if (!scene) return null;

    for (const texture of scene.textures) {
      const control = texture.getControlByName?.(name);
      if (control) return control.isVisible;
    }
    return null;
  }, controlName);
}

/** Enabled state of the named GUI control (the pending-port menu cards are disabled). */
export async function guiControlEnabled(page: Page, controlName: string): Promise<boolean | null> {
  return page.evaluate((name) => {
    const scene = (window as unknown as { __scene?: { textures: Array<{ getControlByName?: (n: string) => { isEnabled: boolean } | null }> } })
      .__scene;
    if (!scene) return null;

    for (const texture of scene.textures) {
      const control = texture.getControlByName?.(name);
      if (control) return control.isEnabled;
    }
    return null;
  }, controlName);
}

/** True when any live GUI texture exposes a control with the given name. */
export async function guiControlExists(page: Page, controlName: string): Promise<boolean> {
  return page.evaluate((name) => {
    const scene = (window as unknown as { __scene?: { textures: Array<{ getControlByName?: (n: string) => unknown }> } })
      .__scene;
    return !!scene?.textures?.some((texture) => !!texture.getControlByName?.(name));
  }, controlName);
}

/** Text content of the named GUI control (test-only readout for the stats overlay). */
export async function guiControlText(page: Page, controlName: string): Promise<string | null> {
  return page.evaluate((name) => {
    const scene = (window as unknown as { __scene?: { textures: Array<{ getControlByName?: (n: string) => { text?: string } | null }> } })
      .__scene;
    if (!scene) return null;

    for (const texture of scene.textures) {
      const control = texture.getControlByName?.(name);
      if (control) return control.text ?? null;
    }
    return null;
  }, controlName);
}

export interface GuiControlMeasure {
  text: string | null;
  isVisible: boolean;
  left: number;
  top: number;
  width: number;
  height: number;
}

/**
 * Live layout measure of a GUI control. Reading `.text` alone cannot tell whether a control
 * was actually laid out: a percentage-sized child of a StackPanel gets a zero measure while
 * its text property is intact (Babylon warns and skips it), which renders nothing.
 */
export async function guiControlMeasure(page: Page, controlName: string): Promise<GuiControlMeasure | null> {
  return page.evaluate((name) => {
    const scene = (window as unknown as {
      __scene?: { textures: Array<{ getControlByName?: (n: string) => {
        text?: string;
        isVisible: boolean;
        _currentMeasure: { left: number; top: number; width: number; height: number };
      } | null }> };
    }).__scene;
    if (!scene) return null;

    for (const texture of scene.textures) {
      const control = texture.getControlByName?.(name);
      if (!control) continue;
      const measure = control._currentMeasure;
      return {
        text: control.text ?? null,
        isVisible: control.isVisible,
        left: measure.left,
        top: measure.top,
        width: measure.width,
        height: measure.height,
      };
    }
    return null;
  }, controlName);
}
