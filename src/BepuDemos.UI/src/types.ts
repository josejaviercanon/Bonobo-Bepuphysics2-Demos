import type { Scene } from '@babylonjs/core/scene';

/** Handle returned by a scene factory; `cleanup` must release listeners and GPU resources. */
export interface SceneHandle {
    scene: Scene;
    cleanup: () => void;
}

/** Test hooks exposed on `window` by scenes (E2E probes). */
export interface SceneTestHooks {
    [key: string]: unknown;
}
