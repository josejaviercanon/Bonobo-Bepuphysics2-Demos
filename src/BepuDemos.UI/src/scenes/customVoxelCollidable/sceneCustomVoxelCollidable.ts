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
import type { SceneHandle } from '../../types';

const GAME_KEY = 'custom-voxel-collidable';

const GROUND_RENDER_ID = 0;
const BOX_RENDER_ID_BASE = 100;
const VOXEL_RENDER_ID_BASE = 10_000;
const MAX_VOXELS = 6000;

declare global {
    interface Window {
        __customVoxelCollidable?: () => {
            ground: number;
            boxes: number;
            voxels: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `CustomVoxelCollidableDemo`: a custom voxel-grid collidable
 * (`IHomogeneousCompoundShape<Box, BoxWide>` + eight collision/sweep task registrations).
 * Boxes rain onto sine-noise voxel terrain. The grid is reduced 40×30×40 → 20×15×20 and
 * the box count 4096 → 1600 (documented deviations).
 */
export async function createCustomVoxelCollidableScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 - 0.8, Math.PI / 3.2, 150, new Vector3(10, 20, 10), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const boxSet = createShapeSet(scene, 'demo-voxel-boxes', 'box', new Color3(0.95, 0.7, 0.3), 1600, 1024);
    const voxelSet = createShapeSet(scene, 'demo-voxel-terrain', 'box', new Color3(0.75, 0.3, 0.3), MAX_VOXELS, 4096);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCustomVoxelCollidable: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            boxSet.reset();
            voxelSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id >= VOXEL_RENDER_ID_BASE) voxelSet.write(state);
                else if (state.id >= BOX_RENDER_ID_BASE) boxSet.write(state);
            }

            groundSet.commit();
            boxSet.commit();
            voxelSet.commit();
        });
    }

    window.__customVoxelCollidable = () => ({
        ground: groundSet.count,
        boxes: boxSet.count,
        voxels: voxelSet.count,
        visibleInstances: groundSet.count + boxSet.count + voxelSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__customVoxelCollidable = undefined;
            stream?.close();
        },
    };
}
