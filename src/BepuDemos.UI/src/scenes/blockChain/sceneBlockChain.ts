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

const GAME_KEY = 'block-chain';

const GROUND_RENDER_ID = 0;
const BLOCK_RENDER_ID_BASE = 100;
const FORK_COUNT = 20;
const BLOCKS_PER_CHAIN = 20;
const COIN_RENDER_ID_BASE = 1000;
const COIN_COUNT = 128;

declare global {
    interface Window {
        __blockChain?: () => {
            ground: number;
            blocks: number;
            coins: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `BlockChainDemo`: 20 chains of 20 ball-socket boxes with kinematic top
 * blocks, plus the "press Z for an ICO" coin fountain. The Z key is the `ico` GUI button; each
 * ICO replaces the previous coin batch (fixed-capacity signal).
 */
export async function createBlockChainScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 4, Math.PI / 3.2, 90, new Vector3(0, 14, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const blockSet = createShapeSet(scene, 'demo-block-chain-blocks', 'box', new Color3(0.62, 0.68, 0.78), FORK_COUNT * BLOCKS_PER_CHAIN, 512);
    const coinSet = createShapeSet(scene, 'demo-block-chain-coins', 'cylinder', new Color3(0.95, 0.78, 0.25), COIN_COUNT, 128);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneBlockChain: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            blockSet.reset();
            coinSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id >= COIN_RENDER_ID_BASE && state.id < COIN_RENDER_ID_BASE + COIN_COUNT) coinSet.write(state);
                else if (state.id >= BLOCK_RENDER_ID_BASE && state.id < BLOCK_RENDER_ID_BASE + FORK_COUNT * BLOCKS_PER_CHAIN) blockSet.write(state);
            }

            groundSet.commit();
            blockSet.commit();
            coinSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'ICO', verb: 'ico' },
        { label: 'Reset', verb: 'reset' },
    ]);

    window.__blockChain = () => ({
        ground: groundSet.count,
        blocks: blockSet.count,
        coins: coinSet.count,
        visibleInstances: groundSet.count + blockSet.count + coinSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__blockChain = undefined;
            stream?.close();
        },
    };
}
