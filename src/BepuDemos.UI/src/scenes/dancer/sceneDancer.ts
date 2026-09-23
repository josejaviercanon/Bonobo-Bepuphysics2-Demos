import { Engine } from '@babylonjs/core/Engines/engine';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { createDancerLikeScene } from '../dancers/dancerSceneShared';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'dancer';
const DANCER_COUNT = 64;
const RENDER_ID_STRIDE = 512;

declare global {
    interface Window {
        __dancer?: () => {
            dancers: number;
            bodyInstances: number;
            dressNodes: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `DancerDemo`: a servo-driven main dancer plus 64 background dancers,
 * each with its own cosmetic simulation wearing a sphere-node cloth dress. Reduced from the
 * upstream 16×16 grid and 29×29 dress detail (documented in docs/compat-review.md).
 */
export async function createDancerScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    let read = () => ({ bodyInstances: 0, attachmentInstances: 0 });
    const handle = await createDancerLikeScene(engine, canvas, {
        gameKey: GAME_KEY,
        renderIdStride: RENDER_ID_STRIDE,
        namePrefix: 'demo-dancer',
        attachmentColor: new Color3(0.85, 0.3, 0.45),
        attachmentCapacity: 15 * 15 * DANCER_COUNT,
        onReadout: (reader) => {
            read = reader;
        },
    });

    window.__dancer = () => {
        const { bodyInstances, attachmentInstances } = read();
        return {
            dancers: DANCER_COUNT,
            bodyInstances,
            dressNodes: attachmentInstances,
            visibleInstances: bodyInstances + attachmentInstances,
        };
    };

    const cleanup = handle.cleanup;
    return {
        scene: handle.scene,
        cleanup: () => {
            window.__dancer = undefined;
            cleanup();
        },
    };
}
