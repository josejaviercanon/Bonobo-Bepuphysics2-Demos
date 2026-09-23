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

const GAME_KEY = 'rope-twist';

const GROUND_RENDER_ID = 0;
const ROPE_RENDER_ID_BASE = 100;
const ROPE_RENDER_ID_STRIDE = 100;
const ROPE_COUNT = 2;
const ROPE_LINK_COUNT = 66;
const WRECKING_BALL_RENDER_ID = 500;

declare global {
    interface Window {
        __ropeTwist?: () => {
            ground: number;
            ropes: number;
            ball: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `RopeTwistDemo`: two ropes tied to a 10000-mass wrecking ball spinning
 * around Y, stabilized by 30 substeps (upstream 4 ropes / 60 substeps, documented reduction).
 */
export async function createRopeTwistScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.6, Math.PI / 3.2, 45, new Vector3(0, 12, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const ropeSet = createShapeSet(scene, 'demo-rope-twist-ropes', 'sphere', new Color3(0.78, 0.78, 0.85), ROPE_COUNT * ROPE_LINK_COUNT, 256);
    const ballSet = createShapeSet(scene, 'demo-rope-twist-ball', 'sphere', new Color3(0.35, 0.4, 0.5), 1, 1);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneRopeTwist: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            ropeSet.reset();
            ballSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id === WRECKING_BALL_RENDER_ID) ballSet.write(state);
                else if (state.id >= ROPE_RENDER_ID_BASE &&
                    state.id < ROPE_RENDER_ID_BASE + ROPE_COUNT * ROPE_RENDER_ID_STRIDE) ropeSet.write(state);
            }

            groundSet.commit();
            ropeSet.commit();
            ballSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__ropeTwist = () => ({
        ground: groundSet.count,
        ropes: ropeSet.count,
        ball: ballSet.count,
        visibleInstances: groundSet.count + ropeSet.count + ballSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__ropeTwist = undefined;
            stream?.close();
        },
    };
}
