import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { SceneLoader } from '@babylonjs/core/Loading/sceneLoader';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import '@babylonjs/loaders/OBJ';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { ThinInstanceSet } from '../../rendering/instanceSets';
import { createShapeSet } from '../../rendering/shapeSets';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'newt';

const FLOOR_RENDER_ID = 0;
const BALL_RENDER_ID = 1;
const STATIC_SPHERE_RENDER_ID = 2;
const NODE_RENDER_ID_BASE = 100_000;
const NODE_RENDER_ID_STRIDE = 4_096;
const NEWT_COUNT = 8;
const MAX_NODES_PER_NEWT = 2_304;

declare global {
    interface Window {
        __newt?: () => {
            visibleInstances: number;
            newts: number;
            nodes: number;
            ball: number;
        };
    }
}

/**
 * Port of the upstream `NewtDemo`: 8 voxel-tetrahedralized newts (rendered as their node
 * spheres plus a translucent reference copy of `newt.obj` loaded client side through
 * `@babylonjs/loaders`) with a heavy ball dropped on them. Node records route by
 * 100000 + newtIndex·4096 + vertexIndex.
 */
export async function createNewtScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.2, 24, new Vector3(9, 7, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    const floor = CreateBox('demo-newt-floor', { size: 1 }, scene);
    floor.material = new StandardMaterial('newt-floor-mat', scene);
    (floor.material as StandardMaterial).diffuseColor = new Color3(0.2, 0.24, 0.2);
    floor.isPickable = false;
    floor.alwaysSelectAsActiveMesh = true;
    floor.doNotSyncBoundingInfo = true;
    const floorSet = new ThinInstanceSet(floor, 1, 1);
    const staticSphere = createShapeSet(scene, 'demo-newt-static-sphere', 'sphere', new Color3(0.3, 0.3, 0.35), 1, 1);
    const ball = createShapeSet(scene, 'demo-newt-ball', 'sphere', new Color3(0.85, 0.35, 0.2), 1, 1, { sphereSegments: 16 });

    const nodeSets = Array.from({ length: NEWT_COUNT }, (_, index) => {
        const shade = 0.35 + index * 0.05;
        return createShapeSet(
            scene, `demo-newt-nodes-${index}`, 'sphere',
            new Color3(0.25 * shade, 0.9 * shade, 0.35 * shade),
            MAX_NODES_PER_NEWT, 512, { sphereSegments: 6 });
    });

    // Client-side reference copy of the same embedded asset the C# side parses.
    const loaded = await SceneLoader.ImportMeshAsync(null, '/dist/models/', 'newt.obj', scene);
    const ghostMaterial = new StandardMaterial('newt-ghost-mat', scene);
    ghostMaterial.diffuseColor = new Color3(0.55, 0.75, 0.6);
    ghostMaterial.emissiveColor = new Color3(0.1, 0.2, 0.12);
    ghostMaterial.alpha = 0.18;
    ghostMaterial.wireframe = true;
    ghostMaterial.backFaceCulling = false;
    for (const mesh of loaded.meshes) {
        if (mesh.getTotalVertices() === 0) continue;
        for (let i = 0; i < NEWT_COUNT; i++) {
            const ghost = mesh.clone(`newt-ghost-${i}`, null, false);
            if (!ghost) continue;
            ghost.material = ghostMaterial;
            ghost.position.set(i * 3, 5 + i * 1.5, 0);
            ghost.rotation.set(Math.PI * (i * 0.55), 0, 0);
            ghost.isPickable = false;
            ghost.alwaysSelectAsActiveMesh = true;
            ghost.doNotSyncBoundingInfo = true;
        }
        mesh.setEnabled(false);
    }

    let lastSeq = -1;
    let nodeCount = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneNewt: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floorSet.reset();
            staticSphere.reset();
            ball.reset();
            for (const set of nodeSets) set.reset();

            let nodes = 0;
            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floorSet.write(state);
                } else if (state.id === BALL_RENDER_ID) {
                    ball.write(state);
                } else if (state.id === STATIC_SPHERE_RENDER_ID) {
                    staticSphere.write(state);
                } else if (state.id >= NODE_RENDER_ID_BASE) {
                    const newtIndex = Math.floor((state.id - NODE_RENDER_ID_BASE) / NODE_RENDER_ID_STRIDE);
                    if (newtIndex >= 0 && newtIndex < nodeSets.length) {
                        nodeSets[newtIndex].write(state);
                        nodes++;
                    }
                }
            }

            floorSet.commit();
            staticSphere.commit();
            ball.commit();
            for (const set of nodeSets) set.commit();
            nodeCount = nodes;
        });
    }

    window.__newt = () => ({
        newts: NEWT_COUNT,
        nodes: nodeCount,
        ball: ball.count,
        visibleInstances: floorSet.count + staticSphere.count + ball.count + nodeCount,
    });

    return {
        scene,
        cleanup: () => {
            window.__newt = undefined;
            stream?.close();
        },
    };
}
