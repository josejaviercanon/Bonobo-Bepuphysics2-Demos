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

const GAME_KEY = 'rope-stability';

const GROUND_RENDER_ID = 0;
const ROPE_RENDER_ID_BASE = 100;
const ROPE_RENDER_ID_STRIDE = 20;
const ROPE_CONFIG_COUNT = 7;
const ROPE_LINK_COUNT = 13;
const SKIP_ROPE_RENDER_ID_BASE = 300;
const SKIP_ROPE_LINK_COUNT = 101;
const BALL_RENDER_ID_BASE = 500;
const BALL_COUNT = 8;
const WRAP_POST_RENDER_ID = 600;

const ROPE_RECORD_CAPACITY = ROPE_CONFIG_COUNT * ROPE_LINK_COUNT + SKIP_ROPE_LINK_COUNT;

declare global {
    interface Window {
        __ropeStability?: () => {
            ground: number;
            ropes: number;
            balls: number;
            post: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `RopeStabilityDemo`: seven rope configurations (naive light/heavy
 * wrecking balls, softer springs, mass boost, inertia boost, zero lever arm, direct cheat
 * constraint) plus a 100-link skip-constraint rope, swinging near a static capsule wrap post.
 */
export async function createRopeStabilityScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.4, 110, new Vector3(-15, 45, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const ropeSet = createShapeSet(scene, 'demo-rope-stability-ropes', 'sphere', new Color3(0.78, 0.78, 0.85), ROPE_RECORD_CAPACITY, 256);
    const ballSet = createShapeSet(scene, 'demo-rope-stability-balls', 'sphere', new Color3(0.35, 0.4, 0.5), BALL_COUNT, 8);
    const postSet = createShapeSet(scene, 'demo-rope-stability-post', 'capsule', new Color3(0.7, 0.55, 0.3), 1, 1, {
        capsuleHeight: 64 + 2 * 8,
        capsuleRadius: 8,
    });

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneRopeStability: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            ropeSet.reset();
            ballSet.reset();
            postSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id === WRAP_POST_RENDER_ID) postSet.write(state);
                else if (state.id >= BALL_RENDER_ID_BASE && state.id < BALL_RENDER_ID_BASE + BALL_COUNT) ballSet.write(state);
                else if (state.id >= SKIP_ROPE_RENDER_ID_BASE && state.id < SKIP_ROPE_RENDER_ID_BASE + SKIP_ROPE_LINK_COUNT) ropeSet.write(state);
                else if (state.id >= ROPE_RENDER_ID_BASE &&
                    state.id < ROPE_RENDER_ID_BASE + ROPE_CONFIG_COUNT * ROPE_RENDER_ID_STRIDE) ropeSet.write(state);
            }

            groundSet.commit();
            ropeSet.commit();
            ballSet.commit();
            postSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__ropeStability = () => ({
        ground: groundSet.count,
        ropes: ropeSet.count,
        balls: ballSet.count,
        post: postSet.count,
        visibleInstances: groundSet.count + ropeSet.count + ballSet.count + postSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__ropeStability = undefined;
            stream?.close();
        },
    };
}
