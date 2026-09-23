import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { GridMaterial } from '@babylonjs/materials/grid';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { CreateSphere } from '@babylonjs/core/Meshes/Builders/sphereBuilder';
import { PointerEventTypes } from '@babylonjs/core/Events/pointerEvents';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import '@babylonjs/core/Culling/ray';
import { connectSignalStream } from '../../signalSource';
import { readGlobalClock } from '../../globalClock';
import { writeFireBall } from '../../inputRing';
import { decodeTransform3D } from '../../decodeTransform3D';
import { writeMatrix } from '../../rendering/thinInstances';
import type { SceneHandle } from '../../types';
import { buildCommandButtons } from '../../gui/commandButtons';

const GAME_KEY = 'simple-self-contained';

/** Render-id ranges mirroring `SimpleSelfContainedDemo` (C#). */
const FLOOR_RENDER_ID = 0;
const BALL_RENDER_ID_BASE = 100;
const MARKER_RENDER_ID_BASE = 10_000;
const MAX_BALLS = 64;
const MARKER_COUNT = 6;
const INITIAL_INSTANCES = 128;

declare global {
    interface Window {
        /** Test/agent hook: live instance counters for this scene. */
        __simpleSelfContained?: () => {
            seats: number;
            floor: number;
            balls: number;
            markers: number;
            visibleInstances: number;
            clock: ReturnType<typeof readGlobalClock> | null;
        };
        /** Last fire-ball intent published into the input ring (test/agent hook). */
        __lastFireBall?: { dx: number; dy: number; dz: number; accepted: boolean };
    }
}

/**
 * Port of the upstream `SimpleSelfContainedDemo`: one sphere on a huge static box floor plus
 * ECS-only orbit markers. All transforms arrive batched through the pinned
 * "simple-self-contained" signal as pure float64 and are written straight into
 * thin-instance matrix buffers — zero per-entity nodes, zero per-entity interop.
 *
 * Taps publish a fire-ball intent through the zero-copy input ring; the projectile is a Bepu
 * body in the C# simulation, never client physics.
 */
export async function createSimpleSelfContainedScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.05, 0.07, 0.11, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2.6, Math.PI / 3.8, 30, new Vector3(0, 1.5, 0), scene);
    camera.attachControl(canvas, true);
    camera.lowerRadiusLimit = 6;
    camera.upperRadiusLimit = 220;
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;

    const sun = new DirectionalLight('sun', new Vector3(-0.4, -1, 0.3), scene);
    sun.intensity = 0.7;

    const floorMaterial = new GridMaterial('floor-mat', scene);
    floorMaterial.gridRatio = 1;
    floorMaterial.majorUnitFrequency = 5;
    floorMaterial.minorUnitVisibility = 0.35;
    floorMaterial.mainColor = new Color3(0.11, 0.14, 0.2);
    floorMaterial.lineColor = new Color3(0.32, 0.42, 0.58);

    const ballMaterial = new StandardMaterial('ball-mat', scene);
    ballMaterial.diffuseColor = new Color3(0.95, 0.5, 0.18);
    ballMaterial.emissiveColor = new Color3(0.18, 0.07, 0.02);

    const markerMaterial = new StandardMaterial('marker-mat', scene);
    markerMaterial.diffuseColor = new Color3(0.25, 0.85, 0.95);
    markerMaterial.emissiveColor = new Color3(0.08, 0.3, 0.36);

    // Unit meshes; per-instance scale in the signal carries the real dimensions
    // (floor extents, sphere diameter).
    const floor = CreateBox('demo-floor', { size: 1 }, scene);
    floor.material = floorMaterial;
    floor.isPickable = false;
    floor.alwaysSelectAsActiveMesh = true;
    floor.doNotSyncBoundingInfo = true;
    const floorMatrices = new Float32Array(16);
    floor.thinInstanceSetBuffer('matrix', floorMatrices, 16, false);

    const balls = CreateSphere('demo-balls', { diameter: 1, segments: 20 }, scene);
    balls.material = ballMaterial;
    balls.isPickable = false;
    balls.alwaysSelectAsActiveMesh = true;
    balls.doNotSyncBoundingInfo = true;
    let ballCapacity = INITIAL_INSTANCES;
    let ballMatrices = new Float32Array(ballCapacity * 16);
    balls.thinInstanceSetBuffer('matrix', ballMatrices, 16, false);
    balls.thinInstanceCount = 0;

    const markers = CreateSphere('demo-markers', { diameter: 1, segments: 12 }, scene);
    markers.material = markerMaterial;
    markers.isPickable = false;
    markers.alwaysSelectAsActiveMesh = true;
    markers.doNotSyncBoundingInfo = true;
    const markerMatrices = new Float32Array(MARKER_COUNT * 16);
    markers.thinInstanceSetBuffer('matrix', markerMatrices, 16, false);
    markers.thinInstanceCount = 0;

    let floorCount = 0;
    let ballCount = 0;
    let markerCount = 0;
    let lastSeq = -1;
    let clock: ReturnType<typeof readGlobalClock> | null = null;

    const ensureBallCapacity = (required: number): void => {
        if (required > MAX_BALLS) required = MAX_BALLS;
        if (required <= ballCapacity) return;
        while (ballCapacity < required) ballCapacity *= 2;
        ballMatrices = new Float32Array(ballCapacity * 16);
        balls.thinInstanceSetBuffer('matrix', ballMatrices, 16, false);
    };

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneSimpleSelfContained: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener('globals', (values) => {
            clock = readGlobalClock(values);
        });

        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorCount = 0;
            ballCount = 0;
            markerCount = 0;
            ensureBallCapacity(snapshot.states.length);

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorCount = writeMatrix(floorMatrices, 0,
                        state.x, state.y, state.z, state.qx, state.qy, state.qz, state.qw,
                        state.sx, state.sy, state.sz);
                    continue;
                }

                if (state.id >= MARKER_RENDER_ID_BASE) {
                    if (markerCount < MARKER_COUNT) {
                        markerCount = writeMatrix(markerMatrices, markerCount,
                            state.x, state.y, state.z, state.qx, state.qy, state.qz, state.qw,
                            state.sx, state.sy, state.sz);
                    }
                    continue;
                }

                if (state.id >= BALL_RENDER_ID_BASE && ballCount < MAX_BALLS) {
                    ballCount = writeMatrix(ballMatrices, ballCount,
                        state.x, state.y, state.z, state.qx, state.qy, state.qz, state.qw,
                        state.sx, state.sy, state.sz);
                }
            }

            floor.thinInstanceCount = floorCount > 0 ? 1 : 0;
            floor.thinInstanceBufferUpdated('matrix');
            balls.thinInstanceCount = ballCount;
            balls.thinInstanceBufferUpdated('matrix');
            markers.thinInstanceCount = markerCount;
            markers.thinInstanceBufferUpdated('matrix');
        });
    }

    buildCommandButtons(scene, GAME_KEY, [
        { label: 'Spawn Ball', verb: 'spawn-ball' },
        { label: 'Reset', verb: 'reset' },
    ]);

    // Fire-ball intent: one input-ring record; the C# simulation owns the projectile.
    scene.onPointerObservable.add((info) => {
        if (info.type !== PointerEventTypes.POINTERTAP && info.type !== PointerEventTypes.POINTERDOUBLETAP) return;
        if (info.event.button !== undefined && info.event.button !== 0) return;

        const ray = scene.createPickingRay(scene.pointerX, scene.pointerY, null, camera);
        const direction = ray.direction.clone().normalize();
        const accepted = writeFireBall(
            ray.origin.x, ray.origin.y, ray.origin.z,
            direction.x, direction.y, direction.z);

        window.__lastFireBall = {
            dx: direction.x, dy: direction.y, dz: direction.z, accepted,
        };
    });

    window.__simpleSelfContained = () => ({
        seats: balls.thinInstanceCount + markers.thinInstanceCount + floor.thinInstanceCount,
        floor: floor.thinInstanceCount,
        balls: balls.thinInstanceCount,
        markers: markers.thinInstanceCount,
        visibleInstances: balls.thinInstanceCount + markers.thinInstanceCount + floor.thinInstanceCount,
        clock,
    });

    return {
        scene,
        cleanup: () => {
            window.__simpleSelfContained = undefined;
            window.__lastFireBall = undefined;
            stream?.close();
        },
    };
}
