import {
    BUFFER_HEADER_LENGTH,
    Transform3DStateStride,
    type ScalarArray,
    type Transform3DState,
} from './signalLayout';

/** Decoded header + entity records from one `transform3d`-shaped signal buffer. */
export interface Transform3DSnapshot {
    seq: number;
    entityCount: number;
    stride: number;
    stepMs: number;
    tickMs: number;
    states: Transform3DState[];
}

/**
 * Decodes a transform snapshot from the shared-memory float64 view. The values are only
 * valid inside the listener callback (the host releases the buffer right after dispatch),
 * so the decoder copies everything it keeps into plain objects.
 */
export function decodeTransform3D(values: ScalarArray): Transform3DSnapshot {
    const seq = values[0];
    const entityCount = values[2];
    const stride = values[3];
    const stepMs = values[4];
    const tickMs = values[5];

    const states: Transform3DState[] = [];
    for (let i = 0; i < entityCount; i++) {
        const base = BUFFER_HEADER_LENGTH + i * Transform3DStateStride;
        states.push({
            id: values[base],
            x: values[base + 1],
            y: values[base + 2],
            z: values[base + 3],
            qx: values[base + 4],
            qy: values[base + 5],
            qz: values[base + 6],
            qw: values[base + 7],
            sx: values[base + 8],
            sy: values[base + 9],
            sz: values[base + 10],
            lifecycle: values[base + 11],
        });
    }

    return { seq, entityCount, stride, stepMs, tickMs, states };
}
