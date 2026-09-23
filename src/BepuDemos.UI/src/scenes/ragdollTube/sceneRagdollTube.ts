import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import type { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'ragdoll-tube';

const GROUND_RENDER_ID = 0;
const TUBE_RENDER_ID_BASE = 10;
const TUBE_CHILD_COUNT = 13; // 12 panels + spine
const RAGDOLL_RENDER_ID_BASE = 100;
const RAGDOLL_RENDER_ID_STRIDE = 16;
const RAGDOLL_COUNT = 176;

/** Capsule dims per ragdoll render-id slot (mirrors the C# `SlotCapsules`); null = box/sphere. */
const SLOT_CAPSULES: readonly (readonly [number, number])[] = [
    [0.17, 0.25], // hips
    [0.17, 0.22], // abdomen
    [0.21, 0.3], // chest
    [0, 0], // head (sphere)
    [0.1, 0.45], // right upper arm
    [0.09, 0.45], // right lower arm
    [0, 0], // right hand (box)
    [0.1, 0.45], // left upper arm
    [0.09, 0.45], // left lower arm
    [0, 0], // left hand (box)
    [0.12, 0.5], // right upper leg
    [0.11, 0.5], // right lower leg
    [0, 0], // right foot (box)
    [0.12, 0.5], // left upper leg
    [0.11, 0.5], // left lower leg
    [0, 0], // left foot (box)
];

const SLOT_KIND_INDEX: readonly number[] = (() => {
    const kinds = new Map<string, number>();
    return SLOT_CAPSULES.map(([radius, length]) => {
        if (radius === 0) return -1;
        const key = `${radius}:${length}`;
        let index = kinds.get(key);
        if (index === undefined) {
            index = kinds.size;
            kinds.set(key, index);
        }
        return index;
    });
})();

const KIND_CAPSULES = [...new Map(
    SLOT_CAPSULES.filter(([radius]) => radius > 0).map(([r, l]) => [`${r}:${l}`, [r, l] as const])).values()];

declare global {
    interface Window {
        __ragdollTube?: () => {
            ground: number;
            tube: number;
            bodies: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `RagdollTubeDemo`: 176 subgroup-filtered capsule ragdolls tumble inside
 * a spinning kinematic tube. Reduced from 4×4×44 ragdolls and 20 tube panels (documented in
 * docs/compat-review.md).
 */
export async function createRagdollTubeScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.25, Math.PI / 2.2, 30, new Vector3(0, 3.5, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.55;

    const groundSet = createGround(scene);
    const tubeSet = createShapeSet(scene, 'demo-ragdoll-tube-shell', 'box', new Color3(0.45, 0.55, 0.7), TUBE_CHILD_COUNT, TUBE_CHILD_COUNT);
    // Semi-transparent shell (presentation-only): the ragdolls tumbling inside stay visible.
    const tubeMaterial = tubeSet.mesh.material as StandardMaterial;
    tubeMaterial.alpha = 0.22;
    tubeMaterial.backFaceCulling = false;
    const bodySets = KIND_CAPSULES.map(([radius, length], index) =>
        createShapeSet(scene, `demo-ragdoll-tube-capsule-${index}`, 'capsule', new Color3(0.72, 0.58, 0.5), RAGDOLL_COUNT, 256, {
            capsuleHeight: length + radius * 2,
            capsuleRadius: radius,
        }));
    const headSet = createShapeSet(scene, 'demo-ragdoll-tube-heads', 'sphere', new Color3(0.88, 0.72, 0.6), RAGDOLL_COUNT, 256);
    const limbSet = createShapeSet(scene, 'demo-ragdoll-tube-limbs', 'box', new Color3(0.6, 0.5, 0.42), RAGDOLL_COUNT * 4, 1024);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneRagdollTube: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            tubeSet.reset();
            for (const set of bodySets) set.reset();
            headSet.reset();
            limbSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) {
                    groundSet.write(state);
                    continue;
                }

                if (state.id >= TUBE_RENDER_ID_BASE && state.id < TUBE_RENDER_ID_BASE + TUBE_CHILD_COUNT) {
                    tubeSet.write(state);
                    continue;
                }

                const index = state.id - RAGDOLL_RENDER_ID_BASE;
                if (index < 0 || index >= RAGDOLL_COUNT * RAGDOLL_RENDER_ID_STRIDE) continue;
                const slot = index % RAGDOLL_RENDER_ID_STRIDE;
                if (slot >= SLOT_CAPSULES.length) continue;
                const kind = SLOT_KIND_INDEX[slot];
                if (kind >= 0) bodySets[kind].write(state);
                else if (slot === 3) headSet.write(state);
                else limbSet.write(state);
            }

            groundSet.commit();
            tubeSet.commit();
            for (const set of bodySets) set.commit();
            headSet.commit();
            limbSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__ragdollTube = () => {
        const bodies = bodySets.reduce((sum, set) => sum + set.count, 0) + headSet.count + limbSet.count;
        return {
            ground: groundSet.count,
            tube: tubeSet.count,
            bodies,
            visibleInstances: groundSet.count + tubeSet.count + bodies,
        };
    };

    return {
        scene,
        cleanup: () => {
            window.__ragdollTube = undefined;
            stream?.close();
        },
    };
}
