import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { Vector3, Quaternion } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreatePlane } from '@babylonjs/core/Meshes/Builders/planeBuilder';
import { SceneLoader } from '@babylonjs/core/Loading/sceneLoader';
import '@babylonjs/loaders/OBJ';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createShapeSet } from '../../rendering/shapeSets';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'sponsor';

const FLOOR_RENDER_ID = 0;
const WALL_RENDER_ID_BASE = 1;
const OVERLORD_RENDER_ID = 5;
const NEWT_RENDER_ID_BASE = 10_000;
const NEWT_COUNT = 8;
const CHARACTER_RENDER_ID_BASE = 20_000;
const CHARACTER_COUNT = 150;
const HUT_RENDER_ID_BASE = 30_000;
const MAX_HUT_BODIES = 5_376;

const SPONSOR_IMAGES = [
    'angerybite', 'beaverboss', 'bedtimeforopossum', 'behattedpenguin', 'borb', 'bork',
    'contentsluginthevoid', 'disturbingkoala', 'goose', 'handicat', 'healthyostrich',
    'ifiwereatardigrade', 'ladybugwouldpreferlandvaluetax', 'marmottourism', 'mcmonkey',
    'normalgiraffe', 'physquirrel', 'pleasedfrog', 'raisondetre', 'scootybun', 'smugturt',
    'spide', 'spooky', 'squidhanginout', 'tarbeenus', 'tootbush', 'waryoctopus',
];

declare global {
    interface Window {
        __sponsor?: () => {
            visibleInstances: number;
            newts: number;
            characters: number;
            huts: number;
            billboards: number;
        };
    }
}

/**
 * Port of the upstream `SponsorDemo`: hopping kinematic sponsor newts chase 150 AI characters
 * (dynamic character controllers) through box huts inside a walled arena, watched over by a
 * giant static newt. The 27 sponsor PNGs are billboards (the upstream screen-space text tiers
 * need a text-overlay channel this ABI does not have). Newt meshes are OBJ clones driven by
 * their body records.
 */
export async function createSponsorScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', Math.PI / 2, Math.PI / 3.4, 460, new Vector3(0, 10, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    const floorSet = createShapeSet(scene, 'demo-sponsor-floor', 'box', new Color3(0.24, 0.28, 0.2), 1, 1);
    const wallSet = createShapeSet(scene, 'demo-sponsor-walls', 'box', new Color3(0.35, 0.33, 0.3), 4, 4);
    const characterSet = createShapeSet(scene, 'demo-sponsor-characters', 'capsule', new Color3(0.9, 0.6, 0.25), CHARACTER_COUNT, CHARACTER_COUNT, { capsuleHeight: 2, capsuleRadius: 0.5 });
    const hutSet = createShapeSet(scene, 'demo-sponsor-huts', 'box', new Color3(0.55, 0.45, 0.35), MAX_HUT_BODIES, 1024);

    // Hopping newt meshes: one OBJ clone per kinematic body, driven by its record.
    const loaded = await SceneLoader.ImportMeshAsync(null, '/dist/models/', 'newt.obj', scene);
    const newtMaterial = new StandardMaterial('sponsor-newt-mat', scene);
    newtMaterial.diffuseColor = new Color3(0.35, 0.6, 0.35);
    newtMaterial.emissiveColor = new Color3(0.05, 0.1, 0.06);
    const newtMeshes: Mesh[] = [];
    const overlord = (() => {
        for (const mesh of loaded.meshes) {
            if (mesh.getTotalVertices() === 0) continue;
            for (let i = 0; i < NEWT_COUNT; i++) {
                const newt = mesh.clone(`sponsor-newt-${i}`, null, false) as Mesh | null;
                if (!newt) continue;
                newt.material = newtMaterial;
                newt.scaling.set(-10, 10, -10);
                newt.rotationQuaternion = Quaternion.Identity();
                newt.isPickable = false;
                newt.alwaysSelectAsActiveMesh = true;
                newt.doNotSyncBoundingInfo = true;
                newtMeshes.push(newt);
            }

            const big = mesh.clone('sponsor-overlord', null, false) as Mesh | null;
            if (big) {
                big.material = newtMaterial;
                big.scaling.setAll(60);
                big.position.set(0, 10, -190);
                big.isPickable = false;
                big.alwaysSelectAsActiveMesh = true;
                big.doNotSyncBoundingInfo = true;
            }
            mesh.setEnabled(false);
            return big;
        }
        return null;
    })();
    if (!overlord) console.error('[bepu-demos] sceneSponsor: newt.obj has no renderable mesh');

    // Sponsor billboards around the arena (3 rows of 9).
    const billboardRoots = SPONSOR_IMAGES.map((name, index) => {
        const row = Math.floor(index / 9);
        const column = index % 9;
        const plane = CreatePlane(`sponsor-billboard-${name}`, { size: 26 }, scene);
        const material = new StandardMaterial(`sponsor-billboard-mat-${name}`, scene);
        material.diffuseTexture = new Texture(`/dist/sponsors/${name}.png`, scene);
        material.emissiveColor = new Color3(0.35, 0.35, 0.35);
        material.backFaceCulling = false;
        plane.material = material;
        plane.billboardMode = Mesh.BILLBOARDMODE_ALL;
        plane.position.set(-160 + column * 40, 30 + row * 32, 155);
        plane.isPickable = false;
        plane.alwaysSelectAsActiveMesh = true;
        plane.doNotSyncBoundingInfo = true;
        return plane;
    });

    let lastSeq = -1;
    let characterCount = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneSponsor: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            wallSet.reset();
            characterSet.reset();
            hutSet.reset();

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorSet.write(state);
                } else if (state.id >= WALL_RENDER_ID_BASE && state.id < OVERLORD_RENDER_ID) {
                    wallSet.write(state);
                } else if (state.id === OVERLORD_RENDER_ID) {
                    // Static overlord mesh placed client side.
                } else if (state.id >= NEWT_RENDER_ID_BASE && state.id < CHARACTER_RENDER_ID_BASE) {
                    const index = state.id - NEWT_RENDER_ID_BASE;
                    const newt = newtMeshes[index];
                    if (newt) {
                        newt.position.set(state.x, state.y, state.z);
                        newt.rotationQuaternion = new Quaternion(state.qx, state.qy, state.qz, state.qw);
                    }
                } else if (state.id >= CHARACTER_RENDER_ID_BASE && state.id < HUT_RENDER_ID_BASE) {
                    characterSet.write(state);
                } else if (state.id >= HUT_RENDER_ID_BASE) {
                    hutSet.write(state);
                }
            }

            floorSet.commit();
            wallSet.commit();
            characterSet.commit();
            hutSet.commit();
            characterCount = characterSet.count;
        });
    }

    window.__sponsor = () => ({
        newts: newtMeshes.length,
        characters: characterCount,
        huts: hutSet.count,
        billboards: billboardRoots.length,
        visibleInstances:
            floorSet.count + wallSet.count + characterCount + hutSet.count + newtMeshes.length + billboardRoots.length + (overlord ? 1 : 0),
    });

    return {
        scene,
        cleanup: () => {
            window.__sponsor = undefined;
            stream?.close();
        },
    };
}
