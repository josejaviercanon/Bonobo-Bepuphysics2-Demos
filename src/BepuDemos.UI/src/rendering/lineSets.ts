import type { Scene } from '@babylonjs/core/scene';
import { LinesMesh } from '@babylonjs/core/Meshes/linesMesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { VertexBuffer } from '@babylonjs/core/Buffers/buffer';
import type { LineState } from '../signalLayout';

/**
 * One `LinesMesh` driven by a stream of decoded `LineState` records (P2c debug visuals:
 * rays, sweep impacts). The mesh is created once with a fixed segment capacity — Babylon
 * forbids changing the point count of an updatable line system — so unused slots are kept
 * invisible by writing alpha 0 (and a degenerate zero-length segment). Per-vertex colors
 * carry the per-segment rgba, which removes any need for palette bucketing or one mesh per
 * color; the whole system renders in a single draw call.
 */
export class LineSet {
    readonly mesh: LinesMesh;
    private readonly positions: Float32Array;
    private readonly colors: Float32Array;
    private readonly maxSegments: number;

    /** Segments written by the last dispatch. */
    count = 0;

    constructor(scene: Scene, name: string, maxSegments: number) {
        this.maxSegments = Math.max(1, maxSegments);
        this.positions = new Float32Array(this.maxSegments * 6);
        this.colors = new Float32Array(this.maxSegments * 8);

        const indices = new Uint32Array(this.maxSegments * 2);
        for (let i = 0; i < indices.length; i++) indices[i] = i;

        const mesh = new LinesMesh(name, scene, null, undefined, undefined, true, true);
        const vertexData = new VertexData();
        vertexData.positions = this.positions;
        vertexData.colors = this.colors;
        vertexData.indices = indices;
        vertexData.applyToMesh(mesh, true);
        mesh.isPickable = false;
        mesh.alwaysSelectAsActiveMesh = true;
        mesh.doNotSyncBoundingInfo = true;
        this.mesh = mesh;
    }

    /** Starts a new dispatch; clears colors so stale segments from the previous frame vanish. */
    reset(): void {
        this.count = 0;
        this.colors.fill(0);
    }

    write(line: LineState): void {
        if (this.count >= this.maxSegments) return;

        const p = this.count * 6;
        this.positions[p] = line.ax;
        this.positions[p + 1] = line.ay;
        this.positions[p + 2] = line.az;
        this.positions[p + 3] = line.bx;
        this.positions[p + 4] = line.by;
        this.positions[p + 5] = line.bz;

        const c = this.count * 8;
        this.colors[c] = line.r;
        this.colors[c + 1] = line.g;
        this.colors[c + 2] = line.b;
        this.colors[c + 3] = line.a;
        this.colors[c + 4] = line.r;
        this.colors[c + 5] = line.g;
        this.colors[c + 6] = line.b;
        this.colors[c + 7] = line.a;

        this.count++;
    }

    /** Publishes the written segments to the GPU. */
    commit(): void {
        this.mesh.updateVerticesData(VertexBuffer.PositionKind, this.positions, false, false);
        this.mesh.updateVerticesData(VertexBuffer.ColorKind, this.colors, false, false);
    }
}

/** Creates a fixed-capacity colored line system for one demo scene. */
export function createLineSet(scene: Scene, name: string, maxSegments: number): LineSet {
    return new LineSet(scene, name, maxSegments);
}
