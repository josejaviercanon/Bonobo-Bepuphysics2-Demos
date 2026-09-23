import {
    BUFFER_HEADER_LENGTH,
    HeaderEntityCountIndex,
    HeaderLineCountIndex,
    HeaderLineStrideIndex,
    HeaderStrideIndex,
    Transform3DStateStride,
    type LineState,
    type ScalarArray,
    type Transform3DState,
} from './signalLayout';

/** Decoded header + transform and line records from one signal buffer. */
export interface Transform3DSnapshot {
    seq: number;
    entityCount: number;
    stride: number;
    stepMs: number;
    tickMs: number;
    lineCount: number;
    lineStride: number;
    states: Transform3DState[];
    lines: LineState[];
}

/**
 * Decodes a transform + line snapshot from the shared-memory float64 view. The values are
 * only valid inside the listener callback (the host releases the buffer right after
 * dispatch), so the decoder copies everything it keeps into plain objects. `lines` is empty
 * for every demo that predates the P2c debug-visual ports.
 */
export function decodeTransform3D(values: ScalarArray): Transform3DSnapshot {
    const seq = values[0];
    const entityCount = values[HeaderEntityCountIndex];
    const stride = values[HeaderStrideIndex];
    const stepMs = values[4];
    const tickMs = values[5];
    const lineCount = values[HeaderLineCountIndex];
    const lineStride = values[HeaderLineStrideIndex];

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

    const lines: LineState[] = [];
    const lineBase = BUFFER_HEADER_LENGTH + entityCount * stride;
    for (let i = 0; i < lineCount; i++) {
        const base = lineBase + i * lineStride;
        lines.push({
            id: values[base],
            ax: values[base + 1],
            ay: values[base + 2],
            az: values[base + 3],
            bx: values[base + 4],
            by: values[base + 5],
            bz: values[base + 6],
            r: values[base + 7],
            g: values[base + 8],
            b: values[base + 9],
            a: values[base + 10],
            reserved: values[base + 11],
        });
    }

    return { seq, entityCount, stride, stepMs, tickMs, lineCount, lineStride, states, lines };
}
