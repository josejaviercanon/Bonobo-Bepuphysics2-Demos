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

const GAME_KEY = 'substepping';

const GROUND_RENDER_ID = 0;
const ROPE_RENDER_ID_BASE = 100;
const ROPE_LINK_COUNT = 13;
const WRECKING_BALL_RENDER_ID = 200;
const STACK_RENDER_ID_BASE = 300;
const STACK_BOX_COUNT = 20;
const CAPSTONE_RENDER_ID = 400;
const CHAIN_RENDER_ID_BASE = 500;

declare global {
    interface Window {
        __substepping?: () => {
            ground: number;
            rope: number;
            wreckingBall: number;
            stack: number;
            chains: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `SubsteppingDemo`: a 10000:1 rope + wrecking ball, a 20-box stack
 * with a heavy capstone and four motorized hinge chains. The upstream Z/X/C/V key solver
 * mutations are the four GUI buttons (documented deviation).
 */
export async function createSubsteppingScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.5, Math.PI / 3.4, 120, new Vector3(0, 25, 10), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const ropeSet = createShapeSet(scene, 'demo-substep-rope', 'sphere', new Color3(0.75, 0.75, 0.8), ROPE_LINK_COUNT, 16);
    const wreckingBallSet = createShapeSet(scene, 'demo-substep-wrecking-ball', 'sphere', new Color3(0.35, 0.4, 0.5), 1, 1);
    const stackSet = createShapeSet(scene, 'demo-substep-stack', 'box', new Color3(0.62, 0.68, 0.78), STACK_BOX_COUNT + 1, 32);
    const chainSet = createShapeSet(scene, 'demo-substep-chains', 'box', new Color3(0.9, 0.7, 0.3), 64, 64);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneSubstepping: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            ropeSet.reset();
            wreckingBallSet.reset();
            stackSet.reset();
            chainSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id === WRECKING_BALL_RENDER_ID) wreckingBallSet.write(state);
                else if (state.id >= CHAIN_RENDER_ID_BASE) chainSet.write(state);
                else if (state.id >= CAPSTONE_RENDER_ID) stackSet.write(state);
                else if (state.id >= STACK_RENDER_ID_BASE) stackSet.write(state);
                else if (state.id >= ROPE_RENDER_ID_BASE && state.id < ROPE_RENDER_ID_BASE + ROPE_LINK_COUNT) ropeSet.write(state);
            }

            groundSet.commit();
            ropeSet.commit();
            wreckingBallSet.commit();
            stackSet.commit();
            chainSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'Substeps +', verb: 'substeps-more' },
        { label: 'Substeps -', verb: 'substeps-less' },
        { label: 'Iters +', verb: 'iters-more' },
        { label: 'Iters -', verb: 'iters-less' },
        { label: 'Reset', verb: 'reset' },
    ]);

    window.__substepping = () => ({
        ground: groundSet.count,
        rope: ropeSet.count,
        wreckingBall: wreckingBallSet.count,
        stack: stackSet.count,
        chains: chainSet.count,
        visibleInstances: groundSet.count + ropeSet.count + wreckingBallSet.count + stackSet.count + chainSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__substepping = undefined;
            stream?.close();
        },
    };
}
