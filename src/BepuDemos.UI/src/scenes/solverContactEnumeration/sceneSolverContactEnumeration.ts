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
import type { SceneHandle } from '../../types';

const GAME_KEY = 'solver-contact-enumeration';

const PLANE_RENDER_ID = 0;
const SENSOR_RENDER_ID = 1;
const PYRAMID_RENDER_ID_BASE = 100;
const GREEN_CONTACT_RENDER_ID_BASE = 1_000;
const BLUE_CONTACT_RENDER_ID_BASE = 2_000;

const PYRAMID_CAPACITY = 210;
const CONTACT_CAPACITY = 1024;

declare global {
    interface Window {
        __solverContactEnumeration?: () => {
            pyramid: number;
            contacts: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `SolverContactEnumerationDemo`: a 20-row box pyramid drops onto a
 * large sensor box; every step the solver contacts connected to the sensor are extracted
 * and drawn as cylinders whose length follows the penetration impulse and radius the
 * friction impulse. Touching contacts route to the green id range, speculative contacts
 * (negative depth) to the blue range.
 */
export async function createSolverContactEnumerationScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.05, 0.06, 0.09, 1);

    const camera = new ArcRotateCamera('Camera', Math.PI / 2, 1.32, 30, new Vector3(0, 3, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.4), scene);
    sun.intensity = 0.5;

    // Presentation duplicate of the deformed static plane (render id 0, skipped in routing).
    createDeformedPlane(scene, 'demo-contacts-plane', 128, 128,
        (x, y) => [x - 64, Math.cos(x / 2) * Math.sin(y / 2), y - 64],
        new Vector3(2, 1, 2),
        { position: new Vector3(0, -2, 0), rotationY: Math.PI / 2 });

    const sensorSet = createShapeSet(scene, 'demo-contacts-sensor', 'box', new Color3(0.55, 0.58, 0.68), 1, 1);
    const pyramidSet = createShapeSet(scene, 'demo-contacts-pyramid', 'box', new Color3(0.62, 0.68, 0.78), PYRAMID_CAPACITY);
    const greenSet = createShapeSet(scene, 'demo-contacts-green', 'cylinder', new Color3(0.3, 0.95, 0.35), CONTACT_CAPACITY,
        64, { cylinderTessellation: 12 });
    const blueSet = createShapeSet(scene, 'demo-contacts-blue', 'cylinder', new Color3(0.35, 0.5, 1), CONTACT_CAPACITY,
        64, { cylinderTessellation: 12 });

    let lastSeq = -1;
    let pyramid = 0;
    let contacts = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneSolverContactEnumeration: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            sensorSet.reset();
            pyramidSet.reset();
            greenSet.reset();
            blueSet.reset();

            for (const state of snapshot.states) {
                if (state.id === PLANE_RENDER_ID) continue;
                if (state.id === SENSOR_RENDER_ID) sensorSet.write(state);
                else if (state.id >= BLUE_CONTACT_RENDER_ID_BASE) blueSet.write(state);
                else if (state.id >= GREEN_CONTACT_RENDER_ID_BASE) greenSet.write(state);
                else if (state.id >= PYRAMID_RENDER_ID_BASE) pyramidSet.write(state);
            }

            sensorSet.commit();
            pyramidSet.commit();
            greenSet.commit();
            blueSet.commit();

            pyramid = pyramidSet.count;
            contacts = greenSet.count + blueSet.count;
        });
    }

    window.__solverContactEnumeration = () => ({
        pyramid,
        contacts,
        visibleInstances: pyramid + contacts + 2,
    });

    return {
        scene,
        cleanup: () => {
            window.__solverContactEnumeration = undefined;
            stream?.close();
        },
    };
}
