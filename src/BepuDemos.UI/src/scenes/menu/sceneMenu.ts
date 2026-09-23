import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import { AdvancedDynamicTexture } from '@babylonjs/gui/2D/advancedDynamicTexture';
import { Button } from '@babylonjs/gui/2D/controls/button';
import { Control } from '@babylonjs/gui/2D/controls/control';
import { Grid } from '@babylonjs/gui/2D/controls/grid';
import { TextBlock } from '@babylonjs/gui/2D/controls/textBlock';
import { switchScene } from '../../sceneRunner';
import type { SceneHandle } from '../../types';

const GRID_COLUMNS = 6;
const GRID_ROWS = 5;

interface MenuItem {
    /** DemoSet ordinal from `docs/compat-review.md` (1..30). */
    n: number;
    label: string;
    /** Registered scene key for ported demos; empty for the 26 pending ports. */
    key: string;
}

/**
 * The 30-demo corpus in `docs/compat-review.md` order (DemoSet 29 + SimpleSelfContained).
 * Ported demos carry their scene key; the rest are disabled "soon" cards.
 */
const MENU_ITEMS: MenuItem[] = [
    { n: 1, label: 'Car', key: '' },
    { n: 2, label: 'Tank', key: '' },
    { n: 3, label: 'Character', key: '' },
    { n: 4, label: 'Ragdoll Tube', key: '' },
    { n: 5, label: 'Pyramid', key: 'pyramid' },
    { n: 6, label: 'Colosseum', key: 'colosseum' },
    { n: 7, label: 'Newt', key: '' },
    { n: 8, label: 'Cloth', key: '' },
    { n: 9, label: 'Dancer', key: '' },
    { n: 10, label: 'Plump Dancer', key: '' },
    { n: 11, label: 'Continuous Collision', key: 'continuous-collision-detection' },
    { n: 12, label: 'Planet', key: 'planet' },
    { n: 13, label: 'Per-Body Gravity', key: 'per-body-gravity' },
    { n: 14, label: 'Compound', key: 'compound' },
    { n: 15, label: 'Rope Stability', key: '' },
    { n: 16, label: 'Substepping', key: 'substepping' },
    { n: 17, label: 'Chain Fountain', key: '' },
    { n: 18, label: 'Rope Twist', key: '' },
    { n: 19, label: 'Friction', key: 'friction' },
    { n: 20, label: 'Bounciness', key: 'bounciness' },
    { n: 21, label: 'Ray Casting', key: '' },
    { n: 22, label: 'Sweep', key: '' },
    { n: 23, label: 'Contact Events', key: 'contact-events' },
    { n: 24, label: 'Collision Tracking', key: 'collision-tracking' },
    { n: 25, label: 'Collision Query', key: '' },
    { n: 26, label: 'Solver Contact Enum', key: '' },
    { n: 27, label: 'Custom Voxel', key: 'custom-voxel-collidable' },
    { n: 28, label: 'Block Chain', key: '' },
    { n: 29, label: 'Sponsor', key: '' },
    { n: 30, label: 'Simple Self Contained', key: 'simple-self-contained' },
];

declare global {
    interface Window {
        __menu?: () => {
            total: number;
            live: number;
            placeholders: number;
        };
    }
}

/**
 * Main menu: the 30-demo corpus as a GUI card grid (live cards switch scenes, pending ports
 * are disabled) over a floating sphere backdrop. The scene connects the reserved `menu` game
 * key, which the host resolves as unknown — the previous simulation and its pinned signal
 * buffer are released and the engine stays idle (memory reset on demo switch).
 */
export async function createMenuScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.02, 0.03, 0.06, 1);

    const camera = new ArcRotateCamera('menu-camera', -Math.PI / 2, 1.25, 34, new Vector3(0, -2, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;
    camera.useAutoRotationBehavior = true;
    if (camera.autoRotationBehavior) camera.autoRotationBehavior.idleRotationSpeed = 0.12;

    const light = new HemisphericLight('menu-light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.9;

    // Backdrop: one sphere per demo slot on a slow-orbiting constellation below the cards.
    const liveMaterial = new StandardMaterial('menu-live-mat', scene);
    liveMaterial.emissiveColor = new Color3(0.1, 0.42, 0.6);
    liveMaterial.specularColor = new Color3(0.15, 0.15, 0.15);

    const soonMaterial = new StandardMaterial('menu-soon-mat', scene);
    soonMaterial.emissiveColor = new Color3(0.07, 0.09, 0.13);

    MENU_ITEMS.forEach((item, index) => {
        const live = item.key.length > 0;
        const sphere = CreateSphere(`menu-slot-${item.n}`, { diameter: live ? 1.4 : 0.8, segments: 12 }, scene);
        sphere.material = live ? liveMaterial : soonMaterial;
        const angle = (index / MENU_ITEMS.length) * Math.PI * 2;
        const radius = 21 + (index % 3) * 3;
        sphere.position.set(
            Math.cos(angle) * radius,
            -2 + Math.sin(angle * 2) * 3.5,
            Math.sin(angle) * radius
        );
        sphere.isPickable = false;
    });

    buildMenuGui(scene);

    const live = MENU_ITEMS.filter((item) => item.key.length > 0).length;
    window.__menu = () => ({
        total: MENU_ITEMS.length,
        live,
        placeholders: MENU_ITEMS.length - live,
    });

    return {
        scene,
        cleanup: () => {
            window.__menu = undefined;
        },
    };
}

/** Card grid + title overlay. Card names: `btn-menu-{key}` (live) / `btn-menu-slot-{n}` (soon). */
function buildMenuGui(scene: Scene): void {
    const gui = AdvancedDynamicTexture.CreateFullscreenUI('menu', true, scene);
    gui.idealWidth = 1920;

    const live = MENU_ITEMS.filter((item) => item.key.length > 0).length;

    const title = new TextBlock('menu-title', 'Bonobo BepuPhysics2 Demos');
    title.color = '#e2e8f0';
    title.fontSize = 34;
    title.fontWeight = 'bold';
    title.resizeToFit = true;
    title.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_CENTER;
    title.verticalAlignment = Control.VERTICAL_ALIGNMENT_TOP;
    title.top = '26px';
    gui.addControl(title);

    const subtitle = new TextBlock(
        'menu-subtitle',
        `${live} of ${MENU_ITEMS.length} demos ported — click a live card to run it`
    );
    subtitle.color = '#94a3b8';
    subtitle.fontSize = 18;
    subtitle.resizeToFit = true;
    subtitle.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_CENTER;
    subtitle.verticalAlignment = Control.VERTICAL_ALIGNMENT_TOP;
    subtitle.top = '74px';
    gui.addControl(subtitle);

    const grid = new Grid('menu-grid');
    grid.width = `${GRID_COLUMNS * 300}px`;
    grid.height = `${GRID_ROWS * 86}px`;
    grid.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_CENTER;
    grid.verticalAlignment = Control.VERTICAL_ALIGNMENT_CENTER;
    for (let column = 0; column < GRID_COLUMNS; column++) grid.addColumnDefinition(1 / GRID_COLUMNS);
    for (let row = 0; row < GRID_ROWS; row++) grid.addRowDefinition(1 / GRID_ROWS);

    MENU_ITEMS.forEach((item, index) => {
        const isLive = item.key.length > 0;
        const name = isLive ? `btn-menu-${item.key}` : `btn-menu-slot-${item.n}`;
        const button = Button.CreateSimpleButton(name, `${item.n}. ${item.label}${isLive ? '' : ' (soon)'}`);
        button.width = '280px';
        button.height = '70px';
        button.color = isLive ? '#0f172a' : '#64748b';
        button.background = isLive ? '#38bdf8' : '#111827';
        button.cornerRadius = 10;
        button.thickness = isLive ? 2 : 1;
        button.fontSize = 16;
        button.paddingLeft = '12px';
        button.paddingRight = '12px';

        if (isLive) {
            button.onPointerClickObservable.add(() => {
                void switchScene(item.key);
            });
        } else {
            button.isEnabled = false;
        }

        grid.addControl(button, Math.floor(index / GRID_COLUMNS), index % GRID_COLUMNS);
    });

    gui.addControl(grid);
}
