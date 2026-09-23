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

const GAME_KEY = 'friction';

const FLOOR_RENDER_ID = 0;
const BOX_RENDER_ID_BASE = 100;
const BOX_COUNT = 100;
const BAND_COUNT = 4;

declare global {
    interface Window {
        __friction?: () => {
            floor: number;
            boxes: number;
            visibleInstances: number;
            meanX: number;
        };
    }
}

/**
 * Port of the upstream `FrictionDemo`: 100 boxes with a sideways velocity slide across the
 * floor; the friction coefficient ramps 0 → 0.75 along the line, so the boxes stop at
 * different distances. Boxes are tinted per friction band (client-side only).
 */
export async function createFrictionScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.4, 150, new Vector3(-40, 2, 60), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.2, -1, 0.35), scene);
    sun.intensity = 0.65;

    const floorSet = createGround(scene);

    const bands = Array.from({ length: BAND_COUNT }, (_, index) => {
        const t = index / (BAND_COUNT - 1);
        const color = Color3.Lerp(new Color3(0.25, 0.55, 0.95), new Color3(0.95, 0.3, 0.25), t);
        return createShapeSet(scene, `demo-friction-boxes-${index}`, 'box', color, BOX_COUNT, 32);
    });

    let lastSeq = -1;
    let meanX = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneFriction: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            for (const band of bands) band.reset();

            let sum = 0;
            let count = 0;
            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorSet.write(state);
                    continue;
                }

                const index = state.id - BOX_RENDER_ID_BASE;
                if (index < 0 || index >= BOX_COUNT) continue;
                const band = Math.min(BAND_COUNT - 1, Math.floor((index / BOX_COUNT) * BAND_COUNT));
                bands[band].write(state);
                sum += state.x;
                count += 1;
            }

            floorSet.commit();
            for (const band of bands) band.commit();
            meanX = count > 0 ? sum / count : 0;
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__friction = () => {
        const boxes = bands.reduce((total, band) => total + band.count, 0);
        return { floor: floorSet.count, boxes, visibleInstances: floorSet.count + boxes, meanX };
    };

    return {
        scene,
        cleanup: () => {
            window.__friction = undefined;
            stream?.close();
        },
    };
}
