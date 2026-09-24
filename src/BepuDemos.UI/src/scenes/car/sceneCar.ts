import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createShapeSet } from '../../rendering/shapeSets';
import { createDeformedPlane } from '../../rendering/deformedPlane';
import { createKeyTracker } from '../../input/keyboardInput';
import { writeVehicleControl } from '../../inputRing';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'car';

const CAR_RENDER_ID_BASE = 100;
const CAR_RENDER_ID_STRIDE = 8;
const BODY_CHILD = 0;
const CABIN_CHILD = 1;
const WHEEL_CHILDREN = [2, 3, 4, 5];
const BUILDING_RENDER_ID_BASE = 100_000;
const AI_CAR_COUNT = 64;
const BUILDING_COUNT = 100;

const PLANE_WIDTH = 129;
const TERRAIN_SCALE = 6;
const TRACK_QUADRANT_RADIUS = (PLANE_WIDTH - 32) * TERRAIN_SCALE * 0.25;

declare global {
    interface Window {
        __car?: () => {
            visibleInstances: number;
            player: number;
            aiCars: number;
            buildings: number;
        };
    }
}

/** Race-track deformer duplicated from the C# scene fixture (presentation-only terrain). */
function terrainDeformer(vX: number, vY: number): readonly [number, number, number] {
    const octave0 = (Math.sin((vX + 5) * 0.05) + Math.sin((vY + 11) * 0.05)) * 1.8;
    const octave1 = (Math.sin((vX + 17) * 0.15) + Math.sin((vY + 19) * 0.15)) * 0.9;
    const octave2 = (Math.sin((vX + 37) * 0.35) + Math.sin((vY + 93) * 0.35)) * 0.4;
    const octave3 = (Math.sin((vX + 53) * 0.65) + Math.sin((vY + 47) * 0.65)) * 0.2;
    const octave4 = (Math.sin((vX + 67) * 1.5) + Math.sin((vY + 13) * 1.5)) * 0.125;
    const half = Math.floor(PLANE_WIDTH / 2);
    const distanceToEdge = half - Math.max(Math.abs(vX - half), Math.abs(vY - half));
    const edgeRamp = 25 / (distanceToEdge + 1);
    const terrainHeight = octave0 + octave1 + octave2 + octave3 + octave4;
    const worldX = vX * TERRAIN_SCALE + (1 - PLANE_WIDTH) * TERRAIN_SCALE * 0.5;
    const worldZ = vY * TERRAIN_SCALE + (1 - PLANE_WIDTH) * TERRAIN_SCALE * 0.5;
    const distanceToTrack = raceTrackDistance(worldX, worldZ);
    const trackWeight = Math.min(1, 3 / (distanceToTrack * 0.1 + 1));
    const height = trackWeight * -10 + terrainHeight * (1 - trackWeight);
    return [worldX, height + edgeRamp, worldZ];
}

/** Mirrors the C# `RaceTrack.GetDistance` quadrant-circle math (including the zero-distance branch). */
function raceTrackDistance(x: number, z: number): number {
    const radius = TRACK_QUADRANT_RADIUS;
    const quadrantX = x < 0 ? -radius : radius;
    const quadrantZ = z < 0 ? -radius : radius;
    const dx = x - quadrantX;
    const dz = z - quadrantZ;
    const distanceToQuadrantCenter = Math.hypot(dx, dz);
    const directionX = distanceToQuadrantCenter > 0 ? dx / distanceToQuadrantCenter : radius;
    const directionZ = distanceToQuadrantCenter > 0 ? dz / distanceToQuadrantCenter : 0;
    const closestX = quadrantX + radius * directionX;
    const closestZ = quadrantZ + radius * directionZ;
    return Math.hypot(closestX - x, closestZ - z);
}

/**
 * Port of the upstream `CarDemo`: player car (driven through the pinned input ring with
 * WASD / LShift zoom / Space brake) plus AI cars racing a quarter-circle track on a
 * presentation-only heightfield. Car records route by id range: 100 + carIndex·8 + child.
 */
export async function createCarScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.4, 220, new Vector3(0, 10, -60), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    createDeformedPlane(scene, 'car-terrain', PLANE_WIDTH, PLANE_WIDTH, terrainDeformer, new Vector3(1, 1, 1), {
        position: new Vector3(0, -15, 0),
        rotationY: Math.PI / 2,
        color: new Color3(0.22, 0.3, 0.24),
    });

    const playerBody = createShapeSet(scene, 'demo-car-player-body', 'box', new Color3(0.95, 0.4, 0.12), 2, 1);
    const playerCabin = createShapeSet(scene, 'demo-car-player-cabin', 'box', new Color3(0.98, 0.65, 0.2), 2, 1);
    const aiBodies = createShapeSet(scene, 'demo-car-ai-body', 'box', new Color3(0.25, 0.55, 0.9), AI_CAR_COUNT, 64);
    const aiCabins = createShapeSet(scene, 'demo-car-ai-cabin', 'box', new Color3(0.4, 0.7, 0.95), AI_CAR_COUNT, 64);
    const wheels = createShapeSet(scene, 'demo-car-wheels', 'cylinder', new Color3(0.15, 0.15, 0.18), (AI_CAR_COUNT + 1) * 4, 128);
    const buildings = createShapeSet(scene, 'demo-car-buildings', 'box', new Color3(0.45, 0.42, 0.4), BUILDING_COUNT, 100);

    let lastSeq = -1;
    let playerVisible = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCar: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            playerBody.reset();
            playerCabin.reset();
            aiBodies.reset();
            aiCabins.reset();
            wheels.reset();
            buildings.reset();

            for (const state of snapshot.states) {
                if (state.id >= BUILDING_RENDER_ID_BASE) {
                    buildings.write(state);
                    continue;
                }

                const carIndex = Math.floor((state.id - CAR_RENDER_ID_BASE) / CAR_RENDER_ID_STRIDE);
                const child = (state.id - CAR_RENDER_ID_BASE) % CAR_RENDER_ID_STRIDE;
                const isPlayer = carIndex === 0;

                if (child === BODY_CHILD) {
                    (isPlayer ? playerBody : aiBodies).write(state);
                } else if (child === CABIN_CHILD) {
                    (isPlayer ? playerCabin : aiCabins).write(state);
                } else if (WHEEL_CHILDREN.includes(child)) {
                    wheels.write(state);
                }

                if (isPlayer && child === BODY_CHILD) {
                    camera.setTarget(new Vector3(state.x, state.y + 3, state.z));
                }
            }

            playerBody.commit();
            playerCabin.commit();
            aiBodies.commit();
            aiCabins.commit();
            wheels.commit();
            buildings.commit();

            playerVisible = playerBody.count + playerCabin.count;
        });
    }

    const keys = createKeyTracker();
    const publishInput = () => {
        const throttle = keys.isDown('KeyW') ? 1 : keys.isDown('KeyS') ? -1 : 0;
        const steer = keys.isDown('KeyA') ? 1 : keys.isDown('KeyD') ? -1 : 0;
        writeVehicleControl(throttle, steer, keys.isDown('ShiftLeft') || keys.isDown('ShiftRight'), keys.isDown('Space'));
    };
    scene.onBeforeRenderObservable.add(publishInput);

    window.__car = () => ({
        player: playerVisible,
        aiCars: aiBodies.count + aiCabins.count,
        buildings: buildings.count,
        visibleInstances:
            playerBody.count + playerCabin.count + aiBodies.count + aiCabins.count + wheels.count + buildings.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__car = undefined;
            keys.dispose();
            stream?.close();
        },
    };
}
