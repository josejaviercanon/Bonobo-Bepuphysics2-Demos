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
import { createShapeSet } from '../../rendering/shapeSets';
import { createLineSet } from '../../rendering/lineSets';
import { createDeformedPlane } from '../../rendering/deformedPlane';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'sweep';

const PLANE_RENDER_ID = 0;
const GRID_BOX_RENDER_ID_BASE = 100;
const GRID_CAPSULE_RENDER_ID_BASE = 10_000;
const GRID_SPHERE_RENDER_ID_BASE = 20_000;
const HIT_GHOST_RENDER_ID_BASE = 1_000;
const MISS_GHOST_RENDER_ID_BASE = 2_000;

const GRID_KIND_CAPACITY = 160;
const GHOST_CAPACITY = 320;
const MAX_LINE_SEGMENTS = 32;

declare global {
    interface Window {
        __sweep?: () => {
            grid: number;
            ghostTrails: number;
            impacts: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `SweepDemo`: a 12×3×12 grid of boxes/capsules/spheres falls onto a
 * deformed static plane while 16 box sweeps rotate around the scene. Each sweep draws its
 * ghost trail (20 integrated poses, green id range on hit / red on miss) and two tangent
 * lines mark the impact plane.
 */
export async function createSweepScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.05, 0.06, 0.1, 1);

    const camera = new ArcRotateCamera('Camera', Math.PI / 2, 1.28, 50, new Vector3(0, 2, -10), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.4), scene);
    sun.intensity = 0.5;

    // Presentation duplicate of the deformed static plane (render id 0, skipped in routing).
    createDeformedPlane(scene, 'demo-sweep-plane', 64, 64,
        (x, y) => [x, Math.cos(x / 4) * Math.sin(y / 4), y],
        new Vector3(2, 3, 2),
        { position: new Vector3(-64, -10, -64) });

    const boxSet = createShapeSet(scene, 'demo-sweep-boxes', 'box', new Color3(0.6, 0.66, 0.78), GRID_KIND_CAPACITY);
    const capsuleSet = createShapeSet(scene, 'demo-sweep-capsules', 'capsule', new Color3(0.72, 0.62, 0.5), GRID_KIND_CAPACITY,
        128, { capsuleHeight: 3, capsuleRadius: 1 });
    const sphereSet = createShapeSet(scene, 'demo-sweep-spheres', 'sphere', new Color3(0.55, 0.72, 0.65), GRID_KIND_CAPACITY);
    const hitGhostSet = createShapeSet(scene, 'demo-sweep-hit-ghosts', 'box', new Color3(0.45, 0.95, 0.45), GHOST_CAPACITY);
    const missGhostSet = createShapeSet(scene, 'demo-sweep-miss-ghosts', 'box', new Color3(0.95, 0.4, 0.4), GHOST_CAPACITY);
    const lineSet = createLineSet(scene, 'demo-sweep-lines', MAX_LINE_SEGMENTS);

    let lastSeq = -1;
    let grid = 0;
    let ghostTrails = 0;
    let impacts = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneSweep: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            boxSet.reset();
            capsuleSet.reset();
            sphereSet.reset();
            hitGhostSet.reset();
            missGhostSet.reset();

            for (const state of snapshot.states) {
                if (state.id === PLANE_RENDER_ID) continue;
                if (state.id >= GRID_SPHERE_RENDER_ID_BASE) sphereSet.write(state);
                else if (state.id >= GRID_CAPSULE_RENDER_ID_BASE) capsuleSet.write(state);
                else if (state.id >= MISS_GHOST_RENDER_ID_BASE) missGhostSet.write(state);
                else if (state.id >= HIT_GHOST_RENDER_ID_BASE) hitGhostSet.write(state);
                else if (state.id >= GRID_BOX_RENDER_ID_BASE) boxSet.write(state);
            }

            boxSet.commit();
            capsuleSet.commit();
            sphereSet.commit();
            hitGhostSet.commit();
            missGhostSet.commit();

            lineSet.reset();
            for (const line of snapshot.lines) lineSet.write(line);
            lineSet.commit();

            grid = boxSet.count + capsuleSet.count + sphereSet.count;
            ghostTrails = hitGhostSet.count + missGhostSet.count;
            impacts = lineSet.count;
        });
    }

    window.__sweep = () => ({
        grid,
        ghostTrails,
        impacts,
        visibleInstances: grid + ghostTrails + 1 + (impacts > 0 ? 1 : 0),
    });

    return {
        scene,
        cleanup: () => {
            window.__sweep = undefined;
            stream?.close();
        },
    };
}
