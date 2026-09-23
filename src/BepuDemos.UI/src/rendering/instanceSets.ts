import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Transform3DState } from '../signalLayout';
import { writeMatrix } from './thinInstances';

/**
 * One thin-instance mesh driven by a stream of decoded `Transform3DState` records. The
 * matrix buffer grows by doubling up to a fixed cap (mirrors the engine demo scenes); the
 * records carry position + quaternion + scale, so a single unit mesh per shape kind covers
 * every instance.
 */
export class ThinInstanceSet {
    readonly mesh: Mesh;
    private matrices: Float32Array;
    private capacity: number;
    private readonly maxCapacity: number;

    /** Instances written by the last dispatch (becomes `thinInstanceCount` on commit). */
    count = 0;

    constructor(mesh: Mesh, maxCapacity: number, initialCapacity = 128) {
        this.mesh = mesh;
        this.maxCapacity = maxCapacity;
        this.capacity = Math.max(1, Math.min(initialCapacity, maxCapacity));
        this.matrices = new Float32Array(this.capacity * 16);
        mesh.thinInstanceSetBuffer('matrix', this.matrices, 16, false);
        mesh.thinInstanceCount = 0;
    }

    /** Starts a new dispatch. */
    reset(): void {
        this.count = 0;
    }

    write(state: Transform3DState): void {
        if (this.count >= this.maxCapacity) return;
        this.ensureCapacity(this.count + 1);
        this.count = writeMatrix(
            this.matrices, this.count,
            state.x, state.y, state.z,
            state.qx, state.qy, state.qz, state.qw,
            state.sx, state.sy, state.sz);
    }

    /** Publishes the written instances to the GPU. */
    commit(): void {
        this.mesh.thinInstanceCount = this.count;
        this.mesh.thinInstanceBufferUpdated('matrix');
    }

    private ensureCapacity(required: number): void {
        if (required <= this.capacity) return;
        while (this.capacity < required) this.capacity *= 2;
        this.matrices = new Float32Array(this.capacity * 16);
        this.mesh.thinInstanceSetBuffer('matrix', this.matrices, 16, false);
    }
}
