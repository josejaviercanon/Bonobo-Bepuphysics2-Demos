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
import { writeTankControl } from '../../inputRing';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'tank';

const TANK_RENDER_ID_BASE = 1_000;
const TANK_RENDER_ID_STRIDE = 16;
const TANK_COUNT = 1 + 32;
const BODY_CHILD = 0;
const TURRET_CHILD = 1;
const BARREL_CHILD = 2;
const WHEEL_CHILD_BASE = 3;
const WHEEL_CHILD_END = 13;
const BUILDING_RENDER_ID_BASE = 100_000;
const PROJECTILE_RENDER_ID_BASE = 200_000;
const BUILDING_COUNT = 25;
const MAX_PROJECTILES = 256;

const PLANE_WIDTH = 129;
const TERRAIN_SCALE = 6;

declare global {
    interface Window {
        __tank?: () => {
            visibleInstances: number;
            tanks: number;
            projectiles: number;
            buildings: number;
        };
    }
}

/** Heightfield duplicated from the C# fixture (flattened center circle, presentation only). */
function terrainHeight(x: number, z: number): number {
    const terrainPosition = (1 - PLANE_WIDTH) * TERRAIN_SCALE * 0.5;
    const normalizedX = (x - terrainPosition) / TERRAIN_SCALE;
    const normalizedZ = (z - terrainPosition) / TERRAIN_SCALE;
    const octave0 = (Math.sin((normalizedX + 5) * 0.05) + Math.sin((normalizedZ + 11) * 0.05)) * 3.8;
    const octave1 = (Math.sin((normalizedX + 17) * 0.15) + Math.sin((normalizedZ + 47) * 0.15)) * 1.5;
    const octave2 = (Math.sin((normalizedX + 37) * 0.35) + Math.sin((normalizedZ + 93) * 0.35)) * 0.5;
    const octave3 = (Math.sin((normalizedX + 53) * 0.65) + Math.sin((normalizedZ + 131) * 0.65)) * 0.3;
    const octave4 = (Math.sin((normalizedX + 67) * 1.5) + Math.sin((normalizedZ + 13) * 1.5)) * 0.1525;
    const half = Math.floor(PLANE_WIDTH / 2);
    const distanceToEdge = half - Math.max(Math.abs(normalizedX - half), Math.abs(normalizedZ - half));
    const offsetX = PLANE_WIDTH * 0.5 - normalizedX;
    const offsetZ = PLANE_WIDTH * 0.5 - normalizedZ;
    const distanceToCenterSquared = offsetX * offsetX + offsetZ * offsetZ;
    const centerCircleSize = 30;
    const fadeoutBoundary = 50;
    const outsideWeight = Math.min(1, Math.max(0, distanceToCenterSquared - centerCircleSize * centerCircleSize) /
        (fadeoutBoundary * fadeoutBoundary - centerCircleSize * centerCircleSize));
    const edgeRamp = 25 / (5 * distanceToEdge + 1);
    return outsideWeight * (octave0 + octave1 + octave2 + octave3 + octave4 + edgeRamp);
}

function terrainDeformer(vX: number, vY: number): readonly [number, number, number] {
    const terrainPosition = (1 - PLANE_WIDTH) * TERRAIN_SCALE * 0.5;
    const worldX = vX * TERRAIN_SCALE + terrainPosition;
    const worldZ = vY * TERRAIN_SCALE + terrainPosition;
    return [worldX, terrainHeight(worldX, worldZ), worldZ];
}

/**
 * Port of the upstream `TankDemo`: player tank (WASD drive, IJKL aim, Space fire, LShift zoom,
 * B brake through the pinned input ring) plus AI tanks duelling with CCD projectiles. Tank
 * parts route by id range 1000 + tankIndex·16 + child (index 0 = player); projectiles are
 * short-lived records at 200000+.
 */
export async function createTankScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 2.7, 80, new Vector3(0, 8, -20), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    createDeformedPlane(scene, 'tank-terrain', PLANE_WIDTH, PLANE_WIDTH, terrainDeformer, new Vector3(1, 1, 1), {
        position: new Vector3(0, 0, 0),
        color: new Color3(0.24, 0.28, 0.22),
    });

    const playerBody = createShapeSet(scene, 'demo-tank-player-body', 'box', new Color3(0.85, 0.3, 0.2), 1, 1);
    const playerTurret = createShapeSet(scene, 'demo-tank-player-turret', 'box', new Color3(0.95, 0.45, 0.3), 1, 1);
    const playerBarrel = createShapeSet(scene, 'demo-tank-player-barrel', 'box', new Color3(0.7, 0.25, 0.15), 1, 1);
    const aiBodies = createShapeSet(scene, 'demo-tank-ai-body', 'box', new Color3(0.35, 0.42, 0.3), TANK_COUNT, 33);
    const aiTurrets = createShapeSet(scene, 'demo-tank-ai-turret', 'box', new Color3(0.45, 0.52, 0.38), TANK_COUNT, 33);
    const aiBarrels = createShapeSet(scene, 'demo-tank-ai-barrel', 'box', new Color3(0.28, 0.32, 0.24), TANK_COUNT, 33);
    const wheels = createShapeSet(scene, 'demo-tank-wheels', 'cylinder', new Color3(0.13, 0.13, 0.15), TANK_COUNT * 10, 128);
    const buildings = createShapeSet(scene, 'demo-tank-buildings', 'box', new Color3(0.5, 0.46, 0.42), BUILDING_COUNT, BUILDING_COUNT);
    const projectiles = createShapeSet(scene, 'demo-tank-projectiles', 'sphere', new Color3(1, 0.85, 0.2), MAX_PROJECTILES, 64, { sphereSegments: 6 });

    let lastSeq = -1;
    let tankCount = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneTank: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            playerBody.reset();
            playerTurret.reset();
            playerBarrel.reset();
            aiBodies.reset();
            aiTurrets.reset();
            aiBarrels.reset();
            wheels.reset();
            buildings.reset();
            projectiles.reset();

            for (const state of snapshot.states) {
                if (state.id >= PROJECTILE_RENDER_ID_BASE) {
                    projectiles.write(state);
                    continue;
                }

                if (state.id >= BUILDING_RENDER_ID_BASE) {
                    buildings.write(state);
                    continue;
                }

                if (state.id < TANK_RENDER_ID_BASE) continue;

                const tankIndex = Math.floor((state.id - TANK_RENDER_ID_BASE) / TANK_RENDER_ID_STRIDE);
                const child = (state.id - TANK_RENDER_ID_BASE) % TANK_RENDER_ID_STRIDE;
                const isPlayer = tankIndex === 0;

                if (child === BODY_CHILD) {
                    (isPlayer ? playerBody : aiBodies).write(state);
                    if (isPlayer) camera.setTarget(new Vector3(state.x, state.y + 2.5, state.z));
                } else if (child === TURRET_CHILD) {
                    (isPlayer ? playerTurret : aiTurrets).write(state);
                } else if (child === BARREL_CHILD) {
                    (isPlayer ? playerBarrel : aiBarrels).write(state);
                } else if (child >= WHEEL_CHILD_BASE && child < WHEEL_CHILD_END) {
                    wheels.write(state);
                }
            }

            playerBody.commit();
            playerTurret.commit();
            playerBarrel.commit();
            aiBodies.commit();
            aiTurrets.commit();
            aiBarrels.commit();
            wheels.commit();
            buildings.commit();
            projectiles.commit();

            tankCount = playerBody.count + playerTurret.count + playerBarrel.count +
                aiBodies.count + aiTurrets.count + aiBarrels.count;
        });
    }

    const keys = createKeyTracker();
    const publishInput = () => {
        const move = keys.isDown('KeyW') ? 1 : keys.isDown('KeyS') ? -1 : 0;
        const turn = keys.isDown('KeyA') ? 1 : keys.isDown('KeyD') ? -1 : 0;
        const aimHorizontal = keys.isDown('KeyJ') ? -1 : keys.isDown('KeyL') ? 1 : 0;
        const aimVertical = keys.isDown('KeyI') ? 1 : keys.isDown('KeyK') ? -1 : 0;
        writeTankControl(
            move, turn, aimHorizontal, aimVertical,
            keys.isDown('Space'), keys.isDown('ShiftLeft') || keys.isDown('ShiftRight'), keys.isDown('KeyB'));
    };
    scene.onBeforeRenderObservable.add(publishInput);

    window.__tank = () => ({
        tanks: tankCount,
        projectiles: projectiles.count,
        buildings: buildings.count,
        visibleInstances:
            tankCount + wheels.count + buildings.count + projectiles.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__tank = undefined;
            keys.dispose();
            stream?.close();
        },
    };
}
