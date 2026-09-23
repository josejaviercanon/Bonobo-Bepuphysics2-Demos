import { Engine } from '@babylonjs/core/Engines/engine';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { createDancerLikeScene } from '../dancers/dancerSceneShared';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'plump-dancer';
const DANCER_COUNT = 16;
const RENDER_ID_STRIDE = 4096;

declare global {
    interface Window {
        __plumpDancer?: () => {
            dancers: number;
            bodyInstances: number;
            suitNodes: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `PlumpDancerDemo`: the dancer infrastructure plus a weld-connected
 * voxel fat suit on every background dancer. Reduced from the upstream 8×8 grid and 23³ suit
 * detail (documented in docs/compat-review.md).
 */
export async function createPlumpDancerScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    let read = () => ({ bodyInstances: 0, attachmentInstances: 0 });
    const handle = await createDancerLikeScene(engine, canvas, {
        gameKey: GAME_KEY,
        renderIdStride: RENDER_ID_STRIDE,
        namePrefix: 'demo-plump-dancer',
        attachmentColor: new Color3(0.35, 0.7, 0.45),
        attachmentCapacity: 12 * 12 * 12 * DANCER_COUNT,
        onReadout: (reader) => {
            read = reader;
        },
    });

    window.__plumpDancer = () => {
        const { bodyInstances, attachmentInstances } = read();
        return {
            dancers: DANCER_COUNT,
            bodyInstances,
            suitNodes: attachmentInstances,
            visibleInstances: bodyInstances + attachmentInstances,
        };
    };

    const cleanup = handle.cleanup;
    return {
        scene: handle.scene,
        cleanup: () => {
            window.__plumpDancer = undefined;
            cleanup();
        },
    };
}
