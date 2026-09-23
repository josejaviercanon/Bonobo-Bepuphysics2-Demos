import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { GridMaterial } from '@babylonjs/materials/grid';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import { PointerEventTypes } from '@babylonjs/core/Events/pointerEvents';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import '@babylonjs/core/Culling/ray';
import { connectSignalStream } from '../../signalSource';
import { writeFireBall } from '../../inputRing';
import { decodeTransform3D } from '../../decodeTransform3D';
import { ThinInstanceSet } from '../../rendering/instanceSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'pyramid';

const FLOOR_RENDER_ID = 0;
const BALL_RENDER_ID_BASE = 10_000;
const MAX_BOXES = 4096;
const MAX_PROJECTILES = 64;

declare global {
    interface Window {
        __pyramid?: () => {
            floor: number;
            boxes: number;
            projectiles: number;
            visibleInstances: number;
        };
    }
}

/** Port of the upstream `PyramidDemo`: 12 box pyramids + click-launched cannonballs. */
export async function createPyramidScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.07, 0.1, 0.16, 1);

    // The pyramids are laid out along Z, so look at the row from a 3/4 angle.
    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 1.0, Math.PI / 3.6, 190, new Vector3(0, 8, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.7;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.25), scene);
    sun.intensity = 0.75;

    const ground = CreateBox('demo-floor', { size: 1 }, scene);
    ground.material = new GridMaterial('floor-mat', scene);
    (ground.material as GridMaterial).gridRatio = 1;
    (ground.material as GridMaterial).majorUnitFrequency = 5;
    (ground.material as GridMaterial).minorUnitVisibility = 0.35;
    (ground.material as GridMaterial).mainColor = new Color3(0.11, 0.14, 0.2);
    (ground.material as GridMaterial).lineColor = new Color3(0.32, 0.42, 0.58);
    ground.isPickable = false;
    ground.alwaysSelectAsActiveMesh = true;
    ground.doNotSyncBoundingInfo = true;
    const floorSet = new ThinInstanceSet(ground, 1, 1);

    const boxMaterial = new StandardMaterial('box-mat', scene);
    boxMaterial.diffuseColor = new Color3(0.62, 0.68, 0.78);
    boxMaterial.emissiveColor = new Color3(0.05, 0.06, 0.09);

    const boxesMesh = CreateBox('demo-boxes', { size: 1 }, scene);
    boxesMesh.material = boxMaterial;
    boxesMesh.isPickable = false;
    boxesMesh.alwaysSelectAsActiveMesh = true;
    boxesMesh.doNotSyncBoundingInfo = true;
    const boxSet = new ThinInstanceSet(boxesMesh, MAX_BOXES, 4096);

    const shotMaterial = new StandardMaterial('shot-mat', scene);
    shotMaterial.diffuseColor = new Color3(0.95, 0.45, 0.18);
    shotMaterial.emissiveColor = new Color3(0.2, 0.07, 0.02);

    const shotsMesh = CreateSphere('demo-shots', { diameter: 1, segments: 16 }, scene);
    shotsMesh.material = shotMaterial;
    shotsMesh.isPickable = false;
    shotsMesh.alwaysSelectAsActiveMesh = true;
    shotsMesh.doNotSyncBoundingInfo = true;
    const shotSet = new ThinInstanceSet(shotsMesh, MAX_PROJECTILES, 64);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] scenePyramid: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            boxSet.reset();
            shotSet.reset();

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) floorSet.write(state);
                else if (state.id >= BALL_RENDER_ID_BASE) shotSet.write(state);
                else boxSet.write(state);
            }

            floorSet.commit();
            boxSet.commit();
            shotSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'Shoot', verb: 'shoot' },
        { label: 'Clear Shots', verb: 'clear-projectiles' },
    ]);

    scene.onPointerObservable.add((info) => {
        if (info.type !== PointerEventTypes.POINTERTAP && info.type !== PointerEventTypes.POINTERDOUBLETAP) return;
        if (info.event.button !== undefined && info.event.button !== 0) return;

        const ray = scene.createPickingRay(scene.pointerX, scene.pointerY, null, camera);
        const direction = ray.direction.clone().normalize();
        writeFireBall(ray.origin.x, ray.origin.y, ray.origin.z, direction.x, direction.y, direction.z);
    });

    window.__pyramid = () => ({
        floor: floorSet.count,
        boxes: boxSet.count,
        projectiles: shotSet.count,
        visibleInstances: floorSet.count + boxSet.count + shotSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__pyramid = undefined;
            stream?.close();
        },
    };
}
