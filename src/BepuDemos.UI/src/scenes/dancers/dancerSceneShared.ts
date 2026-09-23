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

const GROUND_RENDER_ID = 0;
const MAIN_DANCER_RENDER_ID_BASE = 100;
const DANCER_RENDER_ID_BASE = 1000;
const BODIES_PER_DANCER = 12;

/** Capsule dims per dancer body slot (radius, inner length); slot 11 is the head sphere. */
const SLOT_CAPSULES: readonly (readonly [number, number])[] = [
    [0.11, 0.5], // upper left leg
    [0.1, 0.5], // lower left leg
    [0.11, 0.5], // upper right leg
    [0.1, 0.5], // lower right leg
    [0.08, 0.39], // upper left arm
    [0.075, 0.39], // lower left arm
    [0.08, 0.39], // upper right arm
    [0.075, 0.39], // lower right arm
    [0.14, 0.27], // hips
    [0.13, 0.216], // abdomen
    [0.165, 0.216], // chest
];

/** Deduplicated capsule kind index for every body slot (backs one thin-instance set per kind). */
const SLOT_KIND_INDEX: readonly number[] = (() => {
    const kinds = new Map<string, number>();
    return SLOT_CAPSULES.map(([radius, length]) => {
        const key = `${radius}:${length}`;
        let index = kinds.get(key);
        if (index === undefined) {
            index = kinds.size;
            kinds.set(key, index);
        }
        return index;
    });
})();

const KIND_CAPSULES: readonly (readonly [number, number])[] = [...new Set(SLOT_CAPSULES.map(([r, l]) => `${r}:${l}`))]
    .map((key) => key.split(':').map(Number) as unknown as readonly [number, number]);

export interface DancerSceneReadout {
    bodyInstances: number;
    attachmentInstances: number;
}

export interface DancerLikeSceneOptions {
    gameKey: string;
    /** Render-id stride between background dancers (512 for the dress demo, 4096 for the suit demo). */
    renderIdStride: number;
    /** Name prefix for the generated mesh sets. */
    namePrefix: string;
    attachmentColor: Color3;
    attachmentCapacity: number;
    /** Receives a reader returning the latest dispatch's instance counts. */
    onReadout: (read: () => DancerSceneReadout) => void;
}

/**
 * Shared factory for the DancerDemo / PlumpDancerDemo scenes: both stream the same render-id
 * layout (main dancer block at 100, background dancers at `1000 + index·stride`, 12 body slots
 * per dancer followed by its attachment nodes). Skeleton capsules bake their aspect into the
 * mesh (record scale 1); heads are unit spheres (record scale 0.34) and attachment nodes are
 * unit spheres carrying their diameter in the record scale.
 */
export async function createDancerLikeScene(
    engine: Engine,
    canvas: HTMLCanvasElement,
    options: DancerLikeSceneOptions): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.2, 22, new Vector3(0, 1.4, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.55;

    const groundSet = createGround(scene);
    const bodySets = KIND_CAPSULES.map(([radius, length], index) => {
        return createShapeSet(scene, `${options.namePrefix}-capsule-${index}`, 'capsule', new Color3(0.55, 0.45, 0.75), 256, 256, {
            capsuleHeight: length + radius * 2,
            capsuleRadius: radius,
        });
    });
    const headSet = createShapeSet(scene, `${options.namePrefix}-heads`, 'sphere', new Color3(0.85, 0.7, 0.6), 256, 256);
    const attachmentSet = createShapeSet(
        scene, `${options.namePrefix}-nodes`, 'sphere', options.attachmentColor, options.attachmentCapacity, 1024);

    let lastSeq = -1;
    const readout: DancerSceneReadout = { bodyInstances: 0, attachmentInstances: 0 };
    options.onReadout(() => ({ ...readout }));

    const stream = connectSignalStream(`/api/${options.gameKey}/stream`);
    if (!stream) {
        console.error(`[bepu-demos] ${options.gameKey} scene: no signal stream (host bridge missing)`);
    } else {
        stream.addBufferListener(options.gameKey, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            for (const set of bodySets) set.reset();
            headSet.reset();
            attachmentSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) {
                    groundSet.write(state);
                    continue;
                }

                const isMainDancer = state.id >= MAIN_DANCER_RENDER_ID_BASE &&
                    state.id < MAIN_DANCER_RENDER_ID_BASE + BODIES_PER_DANCER;
                const dancerIndex = isMainDancer
                    ? -1
                    : Math.floor((state.id - DANCER_RENDER_ID_BASE) / options.renderIdStride);
                if (dancerIndex < -1) continue;
                const slot = isMainDancer
                    ? state.id - MAIN_DANCER_RENDER_ID_BASE
                    : state.id - DANCER_RENDER_ID_BASE - dancerIndex * options.renderIdStride;
                if (slot < 0) continue;

                if (slot < BODIES_PER_DANCER) {
                    if (slot === 11) headSet.write(state);
                    else bodySets[SLOT_KIND_INDEX[slot]].write(state);
                } else {
                    attachmentSet.write(state);
                }
            }

            groundSet.commit();
            for (const set of bodySets) set.commit();
            headSet.commit();
            attachmentSet.commit();
            readout.bodyInstances = bodySets.reduce((sum, set) => sum + set.count, 0) + headSet.count;
            readout.attachmentInstances = attachmentSet.count;
        });
    }

    return {
        scene,
        cleanup: () => {
            stream?.close();
        },
    };
}
