import type { Scene } from '@babylonjs/core/scene';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import type { Vector3 } from '@babylonjs/core/Maths/math.vector';

export interface DeformedPlaneOptions {
    position?: Vector3;
    rotationY?: number;
    color?: Color3;
}

/**
 * Presentation duplicate of the C# `DemoMeshHelper.CreateDeformedPlane` surface: the same
 * width×height vertex grid (`index = width·j + i`), the same two triangles per quad and the
 * same vertex scaling, so a ported demo's static mesh renders exactly where the simulation
 * tests it. Used by the P2c debug-visual scenes.
 */
export function createDeformedPlane(
    scene: Scene,
    name: string,
    width: number,
    height: number,
    deformer: (x: number, y: number) => readonly [number, number, number],
    scaling: Vector3,
    options: DeformedPlaneOptions = {}): Mesh {
    const positions: number[] = new Array(width * height * 3);
    for (let i = 0; i < width; i++) {
        for (let j = 0; j < height; j++) {
            const [x, y, z] = deformer(i, j);
            const base = (width * j + i) * 3;
            positions[base] = x * scaling.x;
            positions[base + 1] = y * scaling.y;
            positions[base + 2] = z * scaling.z;
        }
    }

    const indices: number[] = [];
    for (let i = 0; i < width - 1; i++) {
        for (let j = 0; j < height - 1; j++) {
            const v00 = width * j + i;
            const v01 = width * j + i + 1;
            const v10 = width * (j + 1) + i;
            const v11 = width * (j + 1) + i + 1;
            indices.push(v00, v01, v10, v01, v11, v10);
        }
    }

    const normals: number[] = [];
    VertexData.ComputeNormals(positions, indices, normals);

    const mesh = new Mesh(name, scene);
    const vertexData = new VertexData();
    vertexData.positions = positions;
    vertexData.indices = indices;
    vertexData.normals = normals;
    vertexData.applyToMesh(mesh);

    const material = new StandardMaterial(`${name}-mat`, scene);
    const color = options.color ?? new Color3(0.35, 0.45, 0.35);
    material.diffuseColor = color;
    material.emissiveColor = color.scale(0.15);
    material.backFaceCulling = false;
    mesh.material = material;
    if (options.position) mesh.position.copyFrom(options.position);
    if (options.rotationY) mesh.rotation.y = options.rotationY;
    mesh.isPickable = false;
    mesh.alwaysSelectAsActiveMesh = true;
    mesh.doNotSyncBoundingInfo = true;
    return mesh;
}
