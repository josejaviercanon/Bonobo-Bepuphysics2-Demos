import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'continuous-collision-detection';

const GROUND_RENDER_ID = 0;
const BOX_RENDER_ID_BASE = 100;
const BOXES_PER_GRID = 100;
const SPINNER_RENDER_ID_BASE = 1000;

declare global {
    interface Window {
        __continuousCollisionDetection?: () => {
            ground: number;
            discrete: number;
            passive: number;
            continuous: number;
            spinners: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `ContinuousCollisionDetectionDemo`: three 10×10 box grids show
 * discrete mode (tiny margin), unlimited speculative margins (passive) and explicit swept
 * continuous detection; two spinner pairs show angular CCD. Colours mark each mode.
 */
export async function createContinuousCollisionDetectionScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.75, Math.PI / 3.4, 130, new Vector3(0, 10, -10), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.2, -1, 0.35), scene);
    sun.intensity = 0.65;

    const groundSet = createGround(scene);
    const discreteSet = createShapeSet(scene, 'demo-ccd-discrete', 'box', new Color3(0.95, 0.35, 0.3), BOXES_PER_GRID, 128);
    const passiveSet = createShapeSet(scene, 'demo-ccd-passive', 'box', new Color3(0.65, 0.72, 0.85), BOXES_PER_GRID, 128);
    const continuousSet = createShapeSet(scene, 'demo-ccd-continuous', 'box', new Color3(0.35, 0.85, 0.5), BOXES_PER_GRID, 128);
    const spinnerBaseSet = createShapeSet(scene, 'demo-ccd-spinner-bases', 'box', new Color3(0.5, 0.45, 0.4), 8, 8);
    const spinnerBladeSet = createShapeSet(scene, 'demo-ccd-spinner-blades', 'box', new Color3(0.85, 0.55, 0.95), 8, 8);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneContinuousCollisionDetection: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            discreteSet.reset();
            passiveSet.reset();
            continuousSet.reset();
            spinnerBaseSet.reset();
            spinnerBladeSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) {
                    groundSet.write(state);
                    continue;
                }

                if (state.id >= SPINNER_RENDER_ID_BASE) {
                    if ((state.id - SPINNER_RENDER_ID_BASE) % 2 === 0) spinnerBaseSet.write(state);
                    else spinnerBladeSet.write(state);
                    continue;
                }

                const index = state.id - BOX_RENDER_ID_BASE;
                if (index < 0 || index >= BOXES_PER_GRID * 3) continue;
                if (index < BOXES_PER_GRID) discreteSet.write(state);
                else if (index < BOXES_PER_GRID * 2) passiveSet.write(state);
                else continuousSet.write(state);
            }

            groundSet.commit();
            discreteSet.commit();
            passiveSet.commit();
            continuousSet.commit();
            spinnerBaseSet.commit();
            spinnerBladeSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__continuousCollisionDetection = () => ({
        ground: groundSet.count,
        discrete: discreteSet.count,
        passive: passiveSet.count,
        continuous: continuousSet.count,
        spinners: spinnerBaseSet.count + spinnerBladeSet.count,
        visibleInstances: groundSet.count + discreteSet.count + passiveSet.count + continuousSet.count +
            spinnerBaseSet.count + spinnerBladeSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__continuousCollisionDetection = undefined;
            stream?.close();
        },
    };
}
