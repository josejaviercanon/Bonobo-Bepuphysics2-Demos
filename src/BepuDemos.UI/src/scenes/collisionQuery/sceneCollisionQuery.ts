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
import { createDeformedPlane } from '../../rendering/deformedPlane';
import { createGround } from '../../rendering/ground';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'collision-query';

const GROUND_RENDER_ID = 0;
const PLANE_RENDER_ID = 1;
const BOX_RENDER_ID_BASE = 100;
const TOUCHED_QUERY_RENDER_ID_BASE = 1_000;
const UNTOUCHED_QUERY_RENDER_ID_BASE = 2_000;

const BOX_CAPACITY = 128;
const QUERY_CAPACITY = 25;

declare global {
    interface Window {
        __collisionQuery?: () => {
            boxes: number;
            touchedQueries: number;
            untouchedQueries: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `CollisionQueryDemo`: a 5×5 grid of box queries collects broad phase
 * overlaps and hands each candidate pair to a `CollisionBatcher`; queries with a
 * positive-depth contact route to the green id range, the rest to the red range. 128 boxes
 * fall through the query grid above a deformed static plane.
 */
export async function createCollisionQueryScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.05, 0.07, 0.1, 1);

    const camera = new ArcRotateCamera('Camera', Math.PI / 2, 1.2, 26, new Vector3(0, 4, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.4), scene);
    sun.intensity = 0.5;

    const groundSet = createGround(scene);
    createDeformedPlane(scene, 'demo-query-plane', 20, 20,
        (x, y) => [x * 5 - 50, 3 * Math.sin(x) * Math.sin(y), y * 5 - 50],
        Vector3.One());

    const boxSet = createShapeSet(scene, 'demo-query-boxes', 'box', new Color3(0.62, 0.68, 0.78), BOX_CAPACITY);
    const touchedSet = createShapeSet(scene, 'demo-query-touched', 'box', new Color3(0.35, 0.9, 0.4), QUERY_CAPACITY);
    const untouchedSet = createShapeSet(scene, 'demo-query-untouched', 'box', new Color3(0.9, 0.35, 0.35), QUERY_CAPACITY);

    let lastSeq = -1;
    let boxes = 0;
    let touchedQueries = 0;
    let untouchedQueries = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCollisionQuery: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            boxSet.reset();
            touchedSet.reset();
            untouchedSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id === PLANE_RENDER_ID) continue;
                else if (state.id >= UNTOUCHED_QUERY_RENDER_ID_BASE) untouchedSet.write(state);
                else if (state.id >= TOUCHED_QUERY_RENDER_ID_BASE) touchedSet.write(state);
                else if (state.id >= BOX_RENDER_ID_BASE) boxSet.write(state);
            }

            groundSet.commit();
            boxSet.commit();
            touchedSet.commit();
            untouchedSet.commit();

            boxes = boxSet.count;
            touchedQueries = touchedSet.count;
            untouchedQueries = untouchedSet.count;
        });
    }

    window.__collisionQuery = () => ({
        boxes,
        touchedQueries,
        untouchedQueries,
        visibleInstances: boxes + touchedQueries + untouchedQueries + 2,
    });

    return {
        scene,
        cleanup: () => {
            window.__collisionQuery = undefined;
            stream?.close();
        },
    };
}
