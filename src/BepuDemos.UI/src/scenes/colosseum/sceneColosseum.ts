import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { PointerEventTypes } from '@babylonjs/core/Events/pointerEvents';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import '@babylonjs/core/Culling/ray';
import { connectSignalStream } from '../../signalSource';
import { writeFireBall } from '../../inputRing';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'colosseum';

const GROUND_RENDER_ID = 0;
const BOX_RENDER_ID_BASE = 100;
const BULLET_RENDER_ID_BASE = 100_000;
const BIG_SHOT_RENDER_ID_BASE = 200_000;
const MAX_BOXES = 2048;
const MAX_PROJECTILES = 64;
const MAX_BIG_SHOTS = 16;

declare global {
    interface Window {
        __colosseum?: () => {
            ground: number;
            boxes: number;
            projectiles: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `ColosseumDemo`: ring walls/platforms stacked into a colosseum, hit
 * by click-launched bullets and the `Shoot Big` verb (the upstream Z/X keys). The layer
 * count is reduced 6 → 3 (documented deviation).
 */
export async function createColosseumScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.07, 0.09, 0.14, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 - 0.9, Math.PI / 3.1, 150, new Vector3(0, 12, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.7;
    const sun = new DirectionalLight('sun', new Vector3(-0.3, -1, 0.25), scene);
    sun.intensity = 0.7;

    const groundSet = createGround(scene);
    const boxSet = createShapeSet(scene, 'demo-colosseum-boxes', 'box', new Color3(0.72, 0.68, 0.6), MAX_BOXES, 1024);
    const bulletSet = createShapeSet(scene, 'demo-colosseum-bullets', 'sphere', new Color3(0.95, 0.55, 0.2), MAX_PROJECTILES, 32);
    const bigShotSet = createShapeSet(scene, 'demo-colosseum-big', 'sphere', new Color3(0.7, 0.35, 0.95), MAX_BIG_SHOTS, 8);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneColosseum: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            boxSet.reset();
            bulletSet.reset();
            bigShotSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id >= BIG_SHOT_RENDER_ID_BASE) bigShotSet.write(state);
                else if (state.id >= BULLET_RENDER_ID_BASE) bulletSet.write(state);
                else if (state.id >= BOX_RENDER_ID_BASE) boxSet.write(state);
            }

            groundSet.commit();
            boxSet.commit();
            bulletSet.commit();
            bigShotSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'Shoot Big', verb: 'shoot-big' },
        { label: 'Reset', verb: 'reset' },
    ]);

    scene.onPointerObservable.add((info) => {
        if (info.type !== PointerEventTypes.POINTERTAP && info.type !== PointerEventTypes.POINTERDOUBLETAP) return;
        if (info.event.button !== undefined && info.event.button !== 0) return;

        const ray = scene.createPickingRay(scene.pointerX, scene.pointerY, null, camera);
        const direction = ray.direction.clone().normalize();
        writeFireBall(ray.origin.x, ray.origin.y, ray.origin.z, direction.x, direction.y, direction.z);
    });

    window.__colosseum = () => ({
        ground: groundSet.count,
        boxes: boxSet.count,
        projectiles: bulletSet.count + bigShotSet.count,
        visibleInstances: groundSet.count + boxSet.count + bulletSet.count + bigShotSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__colosseum = undefined;
            stream?.close();
        },
    };
}
