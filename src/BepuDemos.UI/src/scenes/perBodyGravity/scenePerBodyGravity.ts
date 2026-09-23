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

const GAME_KEY = 'per-body-gravity';

const FLOOR_RENDER_ID = 0;
const BODY_RENDER_ID_BASE = 100;

// Fixture layout (mirrors `PerBodyGravityDemo`): 20×4×20 bodies, gravity by (i + k) % 3.
const WIDTH = 20;
const HEIGHT = 4;

declare global {
    interface Window {
        __perBodyGravity?: () => {
            floor: number;
            spheres: number;
            capsules: number;
            boxes: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `PerBodyGravityDemo`: spheres fall slowly (-0.1), capsules in
 * between (-3), boxes quickly (-10). The 20×20×20 upstream grid is reduced to 20×20×4
 * (documented deviation). Capsule mesh matches Bepu `Capsule(1, 1)` (total height 3).
 */
export async function createPerBodyGravityScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.6, Math.PI / 3.2, 170, new Vector3(0, 30, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const floorSet = createGround(scene);
    const sphereSet = createShapeSet(scene, 'demo-gravity-spheres', 'sphere', new Color3(0.3, 0.6, 0.95), 1024, 256);
    const capsuleSet = createShapeSet(scene, 'demo-gravity-capsules', 'capsule', new Color3(0.35, 0.85, 0.45), 1024, 256, {
        capsuleHeight: 3,
        capsuleRadius: 1,
    });
    const boxSet = createShapeSet(scene, 'demo-gravity-boxes', 'box', new Color3(0.95, 0.6, 0.25), 1024, 256);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] scenePerBodyGravity: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            sphereSet.reset();
            capsuleSet.reset();
            boxSet.reset();

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorSet.write(state);
                    continue;
                }

                const index = state.id - BODY_RENDER_ID_BASE;
                if (index < 0) continue;
                const kind = (Math.floor(index / (HEIGHT * WIDTH)) + (index % WIDTH)) % 3;
                if (kind === 0) sphereSet.write(state);
                else if (kind === 1) capsuleSet.write(state);
                else boxSet.write(state);
            }

            floorSet.commit();
            sphereSet.commit();
            capsuleSet.commit();
            boxSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__perBodyGravity = () => ({
        floor: floorSet.count,
        spheres: sphereSet.count,
        capsules: capsuleSet.count,
        boxes: boxSet.count,
        visibleInstances: floorSet.count + sphereSet.count + capsuleSet.count + boxSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__perBodyGravity = undefined;
            stream?.close();
        },
    };
}
