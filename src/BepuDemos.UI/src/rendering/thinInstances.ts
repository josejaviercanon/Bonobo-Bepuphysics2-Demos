/**
 * Writes one composed transform (position + quaternion + scale) into a thin-instance matrix
 * buffer. Row-vector layout, translation at floats 12..14 — same convention as
 * `Matrix.Compose`, without allocating a `Matrix` per instance. Mirrors the engine's
 * helper (`src/Game.Examples.UI/scenes/boxpile/sceneBoxPile.ts`).
 */
export function writeMatrix(
    target: Float32Array, index: number,
    x: number, y: number, z: number,
    qx: number, qy: number, qz: number, qw: number,
    sx: number, sy: number, sz: number): number {
    const offset = index * 16;
    const x2 = qx + qx, y2 = qy + qy, z2 = qz + qz;
    const xx = qx * x2, xy = qx * y2, xz = qx * z2;
    const yy = qy * y2, yz = qy * z2, zz = qz * z2;
    const wx = qw * x2, wy = qw * y2, wz = qw * z2;

    target[offset] = (1 - (yy + zz)) * sx;
    target[offset + 1] = (xy + wz) * sx;
    target[offset + 2] = (xz - wy) * sx;
    target[offset + 3] = 0;
    target[offset + 4] = (xy - wz) * sy;
    target[offset + 5] = (1 - (xx + zz)) * sy;
    target[offset + 6] = (yz + wx) * sy;
    target[offset + 7] = 0;
    target[offset + 8] = (xz + wy) * sz;
    target[offset + 9] = (yz - wx) * sz;
    target[offset + 10] = (1 - (xx + yy)) * sz;
    target[offset + 11] = 0;
    target[offset + 12] = x;
    target[offset + 13] = y;
    target[offset + 14] = z;
    target[offset + 15] = 1;

    return index + 1;
}
