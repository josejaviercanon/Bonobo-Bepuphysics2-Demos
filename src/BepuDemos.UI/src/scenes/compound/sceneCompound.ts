import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import { buildCommandButtons } from '../../gui/commandButtons';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'compound';

const GROUND_RENDER_ID = 0;
const STATIC_SPHERE_RENDER_ID = 10;
const PLANE_RENDER_ID = 20;
const SPHERE_CHILD_RENDER_ID_BASE = 1000;
const CAPSULE_CHILD_RENDER_ID_BASE = 2000;
const BOX_CHILD_RENDER_ID_BASE = 3000;

// Deformed plane (mirrors `CompoundDemo` + `DemoMeshHelper.CreateDeformedPlane`).
const PLANE_WIDTH = 48;
const PLANE_HEIGHT = 48;
const PLANE_SCALE = new Vector3(2, 1, 2);
const PLANE_POSITION = new Vector3(64, 4, 32);

declare global {
    interface Window {
        __compound?: () => {
            ground: number;
            plane: number;
            spheres: number;
            capsules: number;
            boxes: number;
            visibleInstances: number;
        };
    }
}

/**
 * Port of the upstream `CompoundDemo`: compound shapes (capsule+box, sphere grids, tables,
 * a clamp) and tree-accelerated `BigCompound`s (128 children each). Every compound child
 * arrives as its own transform record — the C# side composes parent ∘ local — so each child
 * is rendered as a primitive. The deformed-plane static mesh is rebuilt here from the same
 * formula (presentation only).
 */
export async function createCompoundScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2 - 0.8, Math.PI / 3.2, 160, new Vector3(20, 8, 16), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.75;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.65;

    const groundSet = createGround(scene);
    const sphereSet = createShapeSet(scene, 'demo-compound-spheres', 'sphere', new Color3(0.35, 0.6, 0.95), 512, 256);
    const capsuleSet = createShapeSet(scene, 'demo-compound-capsules', 'capsule', new Color3(0.4, 0.85, 0.5), 64, 16, {
        capsuleHeight: 1.5,
        capsuleRadius: 0.5,
    });
    const boxSet = createShapeSet(scene, 'demo-compound-boxes', 'box', new Color3(0.85, 0.62, 0.35), 2048, 1024);

    const planeMesh = buildDeformedPlane(scene);

    let lastSeq = -1;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCompound: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            groundSet.reset();
            sphereSet.reset();
            capsuleSet.reset();
            boxSet.reset();

            for (const state of snapshot.states) {
                if (state.id === PLANE_RENDER_ID) continue;
                if (state.id === GROUND_RENDER_ID) groundSet.write(state);
                else if (state.id === STATIC_SPHERE_RENDER_ID) sphereSet.write(state);
                else if (state.id >= BOX_CHILD_RENDER_ID_BASE) boxSet.write(state);
                else if (state.id >= CAPSULE_CHILD_RENDER_ID_BASE) capsuleSet.write(state);
                else if (state.id >= SPHERE_CHILD_RENDER_ID_BASE) sphereSet.write(state);
            }

            groundSet.commit();
            sphereSet.commit();
            capsuleSet.commit();
            boxSet.commit();
        });
    }

    buildCommandButtons(scene, GAME_KEY, [{ label: 'Reset', verb: 'reset' }]);

    window.__compound = () => ({
        ground: groundSet.count,
        plane: planeMesh.isEnabled() ? 1 : 0,
        spheres: sphereSet.count,
        capsules: capsuleSet.count,
        boxes: boxSet.count,
        visibleInstances: groundSet.count + (planeMesh.isEnabled() ? 1 : 0) + sphereSet.count + capsuleSet.count + boxSet.count,
    });

    return {
        scene,
        cleanup: () => {
            window.__compound = undefined;
            stream?.close();
        },
    };
}

/** Presentation duplicate of the C# `DemoMeshHelper.CreateDeformedPlane` surface. */
function buildDeformedPlane(scene: Scene): Mesh {
    const positions: number[] = [];
    const indices: number[] = [];

    const halfWidth = PLANE_WIDTH / 2;
    const halfHeight = PLANE_HEIGHT / 2;
    for (let i = 0; i < PLANE_WIDTH; i++) {
        for (let j = 0; j < PLANE_HEIGHT; j++) {
            const dx = i - halfWidth;
            const dz = j - halfHeight;
            const y = Math.cos(i / 4) * Math.sin(j / 4) - 0.01 * (dx * dx + dz * dz);
            positions.push(dx * PLANE_SCALE.x, y * PLANE_SCALE.y, dz * PLANE_SCALE.z);
        }
    }

    const quadWidth = PLANE_WIDTH - 1;
    for (let i = 0; i < quadWidth; i++) {
        for (let j = 0; j < PLANE_HEIGHT - 1; j++) {
            const v00 = j * PLANE_WIDTH + i;
            const v01 = j * PLANE_WIDTH + i + 1;
            const v10 = (j + 1) * PLANE_WIDTH + i;
            const v11 = (j + 1) * PLANE_WIDTH + i + 1;
            indices.push(v00, v01, v10, v01, v11, v10);
        }
    }

    const normals: number[] = [];
    VertexData.ComputeNormals(positions, indices, normals);

    const mesh = new Mesh('demo-compound-plane', scene);
    const vertexData = new VertexData();
    vertexData.positions = positions;
    vertexData.indices = indices;
    vertexData.normals = normals;
    vertexData.applyToMesh(mesh);

    const material = new StandardMaterial('demo-compound-plane-mat', scene);
    material.diffuseColor = new Color3(0.35, 0.45, 0.35);
    material.emissiveColor = new Color3(0.04, 0.06, 0.04);
    material.backFaceCulling = false;
    mesh.material = material;
    mesh.position.copyFrom(PLANE_POSITION);
    mesh.rotation.y = Math.PI / 2;
    mesh.isPickable = false;
    return mesh;
}
