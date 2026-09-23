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

const GAME_KEY = 'collision-tracking';

const FLOOR_RENDER_ID = 0;
const WALL_RENDER_ID = 1;
const BOX_RENDER_ID = 100;
const CAPSULE_RENDER_ID = 101;
const PARTICLE_RENDER_ID_BASE = 10_000;
const MAX_PARTICLES = 256;

declare global {
    interface Window {
        __collisionTracking?: () => {
            statics: number;
            bodies: number;
            particles: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `CollisionTrackingDemo`: narrow phase manifolds are collected by
 * `CollisionTracker` and analyzed after the timestep (no event control flow); new touching
 * feature ids spawn cyan particles — the same behavior as the contact-events demo.
 */
export async function createCollisionTrackingScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.05, 0.07, 0.12, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.2, 34, new Vector3(0, 4, 4), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.65;

    const floorSet = createGround(scene);
    const staticSet = createShapeSet(scene, 'demo-tracking-statics', 'box', new Color3(0.5, 0.55, 0.65), 2, 2);
    const bodySet = createShapeSet(scene, 'demo-tracking-bodies', 'box', new Color3(0.85, 0.65, 0.35), 2, 2);
    const capsuleSet = createShapeSet(scene, 'demo-tracking-capsule', 'capsule', new Color3(0.4, 0.8, 0.9), 1, 1, {
        capsuleHeight: 1.2,
        capsuleRadius: 0.25,
    });
    const particleSet = createShapeSet(scene, 'demo-tracking-particles', 'sphere', new Color3(0.35, 0.85, 0.95), MAX_PARTICLES, 64, {
        sphereSegments: 8,
    });

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCollisionTracking: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            staticSet.reset();
            bodySet.reset();
            capsuleSet.reset();
            particleSet.reset();

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) floorSet.write(state);
                else if (state.id === WALL_RENDER_ID) staticSet.write(state);
                else if (state.id === BOX_RENDER_ID) bodySet.write(state);
                else if (state.id === CAPSULE_RENDER_ID) capsuleSet.write(state);
                else if (state.id >= PARTICLE_RENDER_ID_BASE) particleSet.write(state);
            }

            floorSet.commit();
            staticSet.commit();
            bodySet.commit();
            capsuleSet.commit();
            particleSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Drop', verb: 'drop' }]);

    window.__collisionTracking = () => ({
        statics: staticSet.count,
        bodies: bodySet.count + capsuleSet.count,
        particles: particleSet.count,
        visibleInstances: floorSet.count + staticSet.count + bodySet.count + capsuleSet.count + particleSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__collisionTracking = undefined;
            stream?.close();
        },
    };
}
