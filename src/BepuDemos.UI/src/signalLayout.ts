// Shared-memory ABI mirror — the TypeScript half of the C# constants in
// `src/DemoEngine/ECS/SignalBuffer.cs` and `src/DemoEngine/Inputs/Inputs.cs`.
//
// The Bonobo engine generates this file; this repo is self-contained and pins the
// values instead: `Game.BepuDemos.Tests/AbiPinTests.cs` asserts the C# side and
// `npm run typecheck` + the E2E suite cover the TS side. Never hand-edit a value here
// without changing the pinned test and `docs/compat-review.md`.

/** Standard signal header: [seq, epoch, entityCount, stride, stepMs, tickMs]. */
export const BUFFER_HEADER_LENGTH = 6;

/** Every signal scalar is an 8-byte double (`Float64Array` on every host). */
export const ScalarSize = 8;

/** `Transform3DState`: id + position(3) + quaternion(4) + scale(3) + lifecycle. */
export const Transform3DStateStride = 12;

/** The typed-array view every host hands to the page. */
export type ScalarArray = Float64Array;

export interface Transform3DState {
    id: number;
    x: number;
    y: number;
    z: number;
    qx: number;
    qy: number;
    qz: number;
    qw: number;
    sx: number;
    sy: number;
    sz: number;
    lifecycle: number;
}

/** Lifecycle flags carried in the 12th scalar of `Transform3DState`. */
export const EntityLifecycle = {
    active: 0,
    spawned: 1,
    destroyed: 3,
} as const;

/** Host-scope "globals" clock block (8 doubles), written by the C# host every tick. */
export const GlobalClockStateIndex = {
    seq: 0,
    timeSeconds: 1,
    deltaSeconds: 2,
    stepCount: 3,
    paused: 4,
    interpAlpha: 5,
    reserved0: 6,
    reserved1: 7,
} as const;

/** Input ring ABI (mirrors `DemoEngine.Inputs`). */
export const InputSlotSize = 8;
export const InputQueueCapacity = 100;

/** Packet ids and field slot indexes (slot 0 = id, slots 1..7 = fields in C# order). */
export const PacketType = {
    clickMove: 1,
    fireBall: 2,
    sceneLoaded: 3,
} as const;

export const ClickMovePacketFields = {
    targetEntityId: 1,
    x: 2,
    y: 3,
    z: 4,
} as const;

export const FireBallPacketFields = {
    originX: 1,
    originY: 2,
    originZ: 3,
    directionX: 4,
    directionY: 5,
    directionZ: 6,
} as const;

export const SceneLoadedPacketFields = {
    ok: 1,
} as const;
