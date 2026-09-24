import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { SceneLoader } from '@babylonjs/core/Loading/sceneLoader';
import '@babylonjs/loaders/OBJ';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import { createKeyTracker } from '../../input/keyboardInput';
import { writeCharacterMove } from '../../inputRing';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'character';

const FLOOR_RENDER_ID = 0;
const NEWT_RENDER_ID = 1;
const CHARACTER_RENDER_ID = 2;
const LEGO_RENDER_ID_BASE = 1_000;
const LEGO_COUNT = 144;
const FAN_RENDER_ID_BASE = 10_000;
const FAN_COUNT = 3;
const TONGUE_RENDER_ID = 20_000;
const SEESAW_RENDER_ID_BASE = 20_100;
const PLATFORM_RENDER_ID_BASE = 30_000;
const PLATFORM_COUNT = 16;
const BOX_FIELD_RENDER_ID_BASE = 40_000;
const BOX_FIELD_COUNT = 64;

declare global {
    interface Window {
        __character?: () => {
            visibleInstances: number;
            legos: number;
            platforms: number;
            character: number;
        };
    }
}

/**
 * Port of the upstream `CharacterDemo`: a dynamic capsule character (custom motion constraints)
 * walking over legos, spinning fans, a tongue, a seesaw, moving platforms and a giant static
 * newt. Input is WASD/Space/LShift camera-relative through the pinned input ring; the camera
 * follows the capsule record.
 */
export async function createCharacterScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.1, 70, new Vector3(0, 2, -4), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    const floor = createGround(scene, 'demo-character-floor');
    const characterSet = createShapeSet(scene, 'demo-character-capsule', 'capsule', new Color3(0.95, 0.55, 0.2), 1, 1, { capsuleHeight: 2, capsuleRadius: 0.5 });
    const legoSet = createShapeSet(scene, 'demo-character-legos', 'box', new Color3(0.45, 0.6, 0.85), LEGO_COUNT, LEGO_COUNT);
    const fanSet = createShapeSet(scene, 'demo-character-fans', 'box', new Color3(0.75, 0.75, 0.78), FAN_COUNT * 2, FAN_COUNT * 2);
    const tongueSet = createShapeSet(scene, 'demo-character-tongue', 'box', new Color3(0.85, 0.3, 0.4), 1, 1);
    const seesawSet = createShapeSet(scene, 'demo-character-seesaw', 'box', new Color3(0.6, 0.5, 0.3), 2, 2);
    const platformSet = createShapeSet(scene, 'demo-character-platforms', 'box', new Color3(0.3, 0.7, 0.55), PLATFORM_COUNT, PLATFORM_COUNT);
    const boxFieldSet = createShapeSet(scene, 'demo-character-box-field', 'box', new Color3(0.5, 0.45, 0.6), BOX_FIELD_COUNT, BOX_FIELD_COUNT);

    // Giant static newt (same embedded asset the C# side parses), scaled 15 and placed at (0, 0.5, 0).
    const loaded = await SceneLoader.ImportMeshAsync(null, '/dist/models/', 'newt.obj', scene);
    const newtMaterial = new StandardMaterial('newt-mat', scene);
    newtMaterial.diffuseColor = new Color3(0.35, 0.65, 0.4);
    newtMaterial.emissiveColor = new Color3(0.06, 0.12, 0.07);
    for (const mesh of loaded.meshes) {
        if (mesh.getTotalVertices() === 0) continue;
        const newt = mesh.clone('character-newt', null, false);
        if (newt) {
            newt.material = newtMaterial;
            newt.scaling.setAll(15);
            newt.position.set(0, 0.5, 0);
            newt.isPickable = false;
            newt.alwaysSelectAsActiveMesh = true;
            newt.doNotSyncBoundingInfo = true;
        }
        mesh.setEnabled(false);
    }

    let lastSeq = -1;
    let characterVisible = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCharacter: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floor.reset();
            characterSet.reset();
            legoSet.reset();
            fanSet.reset();
            tongueSet.reset();
            seesawSet.reset();
            platformSet.reset();
            boxFieldSet.reset();

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floor.write(state);
                } else if (state.id === NEWT_RENDER_ID) {
                    // Static newt mesh is placed client side; the record only carries the id.
                } else if (state.id === CHARACTER_RENDER_ID) {
                    characterSet.write(state);
                    camera.setTarget(new Vector3(state.x, state.y + 1.5, state.z));
                } else if (state.id === TONGUE_RENDER_ID) {
                    tongueSet.write(state);
                } else if (state.id >= LEGO_RENDER_ID_BASE && state.id < LEGO_RENDER_ID_BASE + LEGO_COUNT) {
                    legoSet.write(state);
                } else if (state.id >= FAN_RENDER_ID_BASE && state.id < FAN_RENDER_ID_BASE + FAN_COUNT * 2) {
                    fanSet.write(state);
                } else if (state.id >= SEESAW_RENDER_ID_BASE && state.id < SEESAW_RENDER_ID_BASE + 2) {
                    seesawSet.write(state);
                } else if (state.id >= PLATFORM_RENDER_ID_BASE && state.id < PLATFORM_RENDER_ID_BASE + PLATFORM_COUNT) {
                    platformSet.write(state);
                } else if (state.id >= BOX_FIELD_RENDER_ID_BASE) {
                    boxFieldSet.write(state);
                }
            }

            floor.commit();
            characterSet.commit();
            legoSet.commit();
            fanSet.commit();
            tongueSet.commit();
            seesawSet.commit();
            platformSet.commit();
            boxFieldSet.commit();

            characterVisible = characterSet.count;
        });
    }

    const keys = createKeyTracker();
    const publishInput = () => {
        // Camera-relative movement in world XZ (the same convention as the upstream camera input).
        const forward = camera.getDirection(Vector3.Forward());
        forward.y = 0;
        forward.normalize();
        const right = new Vector3(forward.z, 0, -forward.x).scale(-1);
        let moveX = 0;
        let moveZ = 0;
        if (keys.isDown('KeyW')) {
            moveX += forward.x;
            moveZ += forward.z;
        }
        if (keys.isDown('KeyS')) {
            moveX -= forward.x;
            moveZ -= forward.z;
        }
        if (keys.isDown('KeyD')) {
            moveX += right.x;
            moveZ += right.z;
        }
        if (keys.isDown('KeyA')) {
            moveX -= right.x;
            moveZ -= right.z;
        }
        const length = Math.hypot(moveX, moveZ);
        if (length > 1) {
            moveX /= length;
            moveZ /= length;
        }
        writeCharacterMove(moveX, moveZ, keys.isDown('Space'), keys.isDown('ShiftLeft') || keys.isDown('ShiftRight'));
    };
    scene.onBeforeRenderObservable.add(publishInput);

    window.__character = () => ({
        character: characterVisible,
        legos: legoSet.count,
        platforms: platformSet.count,
        visibleInstances:
            floor.count + characterSet.count + legoSet.count + fanSet.count + tongueSet.count +
            seesawSet.count + platformSet.count + boxFieldSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__character = undefined;
            keys.dispose();
            stream?.close();
        },
    };
}
