import { Scene } from '@babylonjs/core/scene';
import { Engine } from '@babylonjs/core/Engines/engine';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { VertexBuffer } from '@babylonjs/core/Buffers/buffer';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color';
import { connectSignalStream } from '../../signalSource';
import { decodeTransform3D } from '../../decodeTransform3D';
import { createGround } from '../../rendering/ground';
import { createShapeSet } from '../../rendering/shapeSets';
import type { SceneHandle } from '../../types';

const GAME_KEY = 'cloth';

const FLOOR_RENDER_ID = 0;
const BAR_RENDER_ID_BASE = 1;
const CLOTH_NODE_RENDER_ID_BASE = 10_000;
const CLOTH_NODE_RENDER_ID_STRIDE = 4_096;

/** Panel geometry duplicated from the C# fixture: 4 curtains (10×30) plus the sheet (48×48). */
const PANELS = [
    { width: 10, height: 30, color: new Color3(0.4, 0.65, 0.9) },
    { width: 10, height: 30, color: new Color3(0.45, 0.8, 0.6) },
    { width: 10, height: 30, color: new Color3(0.85, 0.6, 0.4) },
    { width: 10, height: 30, color: new Color3(0.8, 0.5, 0.75) },
    { width: 48, height: 48, color: new Color3(0.65, 0.65, 0.85) },
];

declare global {
    interface Window {
        __cloth?: () => {
            visibleInstances: number;
            panels: number;
            nodes: number;
            bars: number;
        };
    }
}

interface ClothPanel {
    width: number;
    height: number;
    positions: Float32Array;
    normals: Float32Array;
    indices: number[];
    mesh: Mesh;
    count: number;
}

/**
 * Port of the upstream `ClothDemo`: four 10×30 curtain lattices and one 48×48 fully dynamic
 * sheet. Vertex-level records (stride-12 transfrom records, id = panel base + row·width +
 * column) rebuild each panel's `VertexData` every frame — the design note is recorded in
 * `docs/compat-review.md`.
 */
export async function createClothScene(engine: Engine, canvas: HTMLCanvasElement): Promise<SceneHandle> {
    const scene = new Scene(engine);
    scene.clearColor = new Color4(0.06, 0.08, 0.13, 1);

    const camera = new ArcRotateCamera('Camera', -Math.PI / 2, Math.PI / 3.2, 300, new Vector3(-20, 22, 0), scene);
    camera.attachControl(canvas, true);
    camera.wheelDeltaPercentage = 0.02;

    const light = new HemisphericLight('light', new Vector3(0.3, 1, 0.2), scene);
    light.intensity = 0.8;
    const sun = new DirectionalLight('sun', new Vector3(-0.25, -1, 0.3), scene);
    sun.intensity = 0.7;

    const floor = createGround(scene, 'demo-cloth-floor');
    // Capsule contract: one baked mesh per (radius, length) pair; records emit scale 1.
    const bar0 = createShapeSet(scene, 'demo-cloth-bar-0', 'capsule', new Color3(0.6, 0.6, 0.65), 1, 1, { capsuleHeight: 136, capsuleRadius: 8 });
    const bar1 = createShapeSet(scene, 'demo-cloth-bar-1', 'capsule', new Color3(0.6, 0.6, 0.65), 1, 1, { capsuleHeight: 76, capsuleRadius: 8 });

    const panels: ClothPanel[] = PANELS.map((panel, index) => {
        const vertexCount = panel.width * panel.height;
        const positions = new Float32Array(vertexCount * 3);
        const normals = new Float32Array(vertexCount * 3);
        const indices: number[] = [];
        for (let row = 0; row < panel.height - 1; row++) {
            for (let column = 0; column < panel.width - 1; column++) {
                const v00 = row * panel.width + column;
                const v01 = v00 + 1;
                const v10 = (row + 1) * panel.width + column;
                const v11 = v10 + 1;
                indices.push(v00, v01, v10, v01, v11, v10);
            }
        }

        const mesh = new Mesh(`cloth-panel-${index}`, scene);
        const vertexData = new VertexData();
        vertexData.positions = positions;
        vertexData.indices = indices;
        vertexData.normals = normals;
        vertexData.applyToMesh(mesh, true);
        const material = new StandardMaterial(`cloth-panel-mat-${index}`, scene);
        material.diffuseColor = panel.color;
        material.emissiveColor = panel.color.scale(0.18);
        material.backFaceCulling = false;
        mesh.material = material;
        mesh.isPickable = false;
        mesh.alwaysSelectAsActiveMesh = true;
        mesh.doNotSyncBoundingInfo = true;

        return { width: panel.width, height: panel.height, positions, normals, indices, mesh, count: 0 };
    });

    let lastSeq = -1;
    let nodeCount = 0;

    const stream = connectSignalStream(`/api/${GAME_KEY}/stream`);
    if (!stream) {
        console.error('[bepu-demos] sceneCloth: no signal stream (host bridge missing)');
    } else {
        stream.addBufferListener(GAME_KEY, (values) => {
            const snapshot = decodeTransform3D(values);
            if (snapshot.seq === lastSeq) return;
            lastSeq = snapshot.seq;

            floor.reset();
            bar0.reset();
            bar1.reset();
            for (const panel of panels) panel.count = 0;

            for (const state of snapshot.states) {
                if (state.id === FLOOR_RENDER_ID) {
                    floor.write(state);
                } else if (state.id >= BAR_RENDER_ID_BASE && state.id < CLOTH_NODE_RENDER_ID_BASE) {
                    (state.id === BAR_RENDER_ID_BASE ? bar0 : bar1).write(state);
                } else if (state.id >= CLOTH_NODE_RENDER_ID_BASE) {
                    const panelIndex = Math.floor((state.id - CLOTH_NODE_RENDER_ID_BASE) / CLOTH_NODE_RENDER_ID_STRIDE);
                    if (panelIndex < 0 || panelIndex >= panels.length) continue;
                    const panel = panels[panelIndex];
                    const vertexIndex = (state.id - CLOTH_NODE_RENDER_ID_BASE) % CLOTH_NODE_RENDER_ID_STRIDE;
                    if (vertexIndex >= panel.width * panel.height) continue;
                    panel.positions[vertexIndex * 3] = state.x;
                    panel.positions[vertexIndex * 3 + 1] = state.y;
                    panel.positions[vertexIndex * 3 + 2] = state.z;
                    panel.count++;
                }
            }

            floor.commit();
            bar0.commit();
            bar1.commit();

            let nodes = 0;
            for (const panel of panels) {
                if (panel.count === 0) continue;
                VertexData.ComputeNormals(panel.positions, panel.indices, panel.normals);
                panel.mesh.updateVerticesData(VertexBuffer.PositionKind, panel.positions);
                panel.mesh.updateVerticesData(VertexBuffer.NormalKind, panel.normals);
                nodes += panel.count;
            }
            nodeCount = nodes;
        });
    }

    window.__cloth = () => ({
        panels: panels.length,
        nodes: nodeCount,
        bars: bar0.count + bar1.count,
        visibleInstances: floor.count + bar0.count + bar1.count + nodeCount,
    });

    return {
        scene,
        cleanup: () => {
            window.__cloth = undefined;
            stream?.close();
        },
    };
}
