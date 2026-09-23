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
import { createLineSet, type LineSet } from '../../rendering/lineSets';
import { createDeformedPlane } from '../../rendering/deformedPlane';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'ray-casting';

const PLANE_RENDER_ID = 0;
const BOX_RENDER_ID_BASE = 1_000;
const CAPSULE_RENDER_ID_BASE = 10_000;
const SPHERE_RENDER_ID_BASE = 20_000;
const CYLINDER_RENDER_ID_BASE = 30_000;
const HULL_RENDER_ID_BASE = 40_000;

const GRID_CAPACITY = 900;
const MAX_LINE_SEGMENTS = 32_768;

declare global {
    interface Window {
        __rayCasting?: () => {
            collidables: number;
            raySegments: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `RayCastingDemo`: three ray sources (random / frustum / wall) cast
 * 16384 rays each against a 16³ collidable cloud and draw the hits as line segments. Lines
 * carry per-vertex color in the shared-memory record, so a single `LinesMesh` renders the
 * whole set in one draw call (green shaded hits + yellow normals, dark-red misses).
 */
export async function createRayCastingScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.04, 0.05, 0.08, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI * 0.75, 1.15, 40, Vector3.Zero(), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.4), scene);
    sun.intensity = 0.5;

    // Presentation duplicate of the deformed static plane (render id 0, skipped in routing).
    createDeformedPlane(scene, 'demo-ray-plane', 128, 128,
        (x, y) => [x - 64, Math.cos(x / 4) * Math.sin(y / 4), y - 64],
        new Vector3(1, 3, 1),
        { position: new Vector3(0, -10, 0), rotationY: Math.PI / 4 });

    const boxSet = createShapeSet(scene, 'demo-ray-boxes', 'box', new Color3(0.62, 0.68, 0.78), GRID_CAPACITY);
    const capsuleSet = createShapeSet(scene, 'demo-ray-capsules', 'capsule', new Color3(0.7, 0.62, 0.5), GRID_CAPACITY,
        512, { capsuleHeight: 0.6, capsuleRadius: 0.05 });
    const sphereSet = createShapeSet(scene, 'demo-ray-spheres', 'sphere', new Color3(0.55, 0.72, 0.65), GRID_CAPACITY);
    const cylinderSet = createShapeSet(scene, 'demo-ray-cylinders', 'cylinder', new Color3(0.6, 0.6, 0.85), GRID_CAPACITY);
    const hullSet = createShapeSet(scene, 'demo-ray-hulls', 'box', new Color3(0.8, 0.7, 0.4), GRID_CAPACITY);

    const lineSet: LineSet = createLineSet(scene, 'demo-ray-lines', MAX_LINE_SEGMENTS);

    let lastSeq = -1;
    let collidables = 0;
    let raySegments = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneRayCasting: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            boxSet.reset();
            capsuleSet.reset();
            sphereSet.reset();
            cylinderSet.reset();
            hullSet.reset();

            for (const state of snapshot.states) {
                if (state.id === PLANE_RENDER_ID) continue;
                if (state.id >= HULL_RENDER_ID_BASE) hullSet.write(state);
                else if (state.id >= CYLINDER_RENDER_ID_BASE) cylinderSet.write(state);
                else if (state.id >= SPHERE_RENDER_ID_BASE) sphereSet.write(state);
                else if (state.id >= CAPSULE_RENDER_ID_BASE) capsuleSet.write(state);
                else if (state.id >= BOX_RENDER_ID_BASE) boxSet.write(state);
            }

            boxSet.commit();
            capsuleSet.commit();
            sphereSet.commit();
            cylinderSet.commit();
            hullSet.commit();

            lineSet.reset();
            for (const line of snapshot.lines) lineSet.write(line);
            lineSet.commit();

            collidables = boxSet.count + capsuleSet.count + sphereSet.count + cylinderSet.count + hullSet.count;
            raySegments = lineSet.count;
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'Cycle', verb: 'cycle' },
        { label: 'Rotate', verb: 'rotate' },
        { label: 'Reset Rotation', verb: 'reset-rotation' },
        { label: 'Random', verb: 'source-random' },
        { label: 'Frustum', verb: 'source-frustum' },
        { label: 'Wall', verb: 'source-wall' },
    ]);

    window.__rayCasting = () => ({
        collidables,
        raySegments,
        visibleInstances: collidables + 1 + (raySegments > 0 ? 1 : 0),
    });

    return {
        scene,
        cleanup: () => {
            window.__rayCasting = undefined;
            stream?.close();
        },
    };
}
