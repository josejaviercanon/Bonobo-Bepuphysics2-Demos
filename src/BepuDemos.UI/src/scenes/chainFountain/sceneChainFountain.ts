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

const GAME_KEY = 'chain-fountain';

const GROUND_RENDER_ID = 0;
const WALL_RENDER_ID_BASE = 10;
const WALL_COUNT = 2;
const BEAD_RENDER_ID_BASE = 100;
const BEAD_COUNT = 2048;

declare global {
    interface Window {
        __chainFountain?: () => {
            ground: number;
            walls: number;
            beads: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `ChainFountainDemo`: a stiff segmented rope of capsule beads yanks
 * itself out of its container (Newton's beads / the Mould effect). 2048 beads (upstream 4096,
 * documented reduction).
 */
export async function createChainFountainScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 + 0.55, Math.PI / 3.4, 34, new Vector3(2.8, 1.6, -8), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.6;

    const groundSet = createGround(scene);
    const wallSet = createShapeSet(scene, 'demo-chain-fountain-walls', 'box', new Color3(0.5, 0.55, 0.65), WALL_COUNT, 2);
    const beadSet = createShapeSet(scene, 'demo-chain-fountain-beads', 'capsule', new Color3(0.9, 0.72, 0.3), BEAD_COUNT, 2048, {
        capsuleHeight: 0.3 + 2 * 0.05,
        capsuleRadius: 0.05,
    });

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneChainFountain: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            wallSet.reset();
            beadSet.reset();

            for (const state of snapshot.states) {
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id >= WALL_RENDER_ID_BASE && state.id < WALL_RENDER_ID_BASE + WALL_COUNT) wallSet.write(state);
                else if (state.id >= BEAD_RENDER_ID_BASE && state.id < BEAD_RENDER_ID_BASE + BEAD_COUNT) beadSet.write(state);
            }

            groundSet.commit();
            wallSet.commit();
            beadSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__chainFountain = () => ({
        ground: groundSet.count,
        walls: wallSet.count,
        beads: beadSet.count,
        visibleInstances: groundSet.count + wallSet.count + beadSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__chainFountain = undefined;
            stream?.close();
        },
    };
}
