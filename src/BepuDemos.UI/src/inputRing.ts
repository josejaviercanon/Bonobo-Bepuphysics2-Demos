import { getLocalBufferProvider } from './signalSource';
import {
    CharacterMovePacketFields,
    ClickMovePacketFields,
    FireBallPacketFields,
    InputQueueCapacity,
    InputSlotSize,
    PacketType,
    SceneLoadedPacketFields,
    TankControlPacketFields,
    VehicleControlPacketFields,
} from './signalLayout';

/**
 * Producer half of the zero-copy input ring. The page writes packets straight into the
 * pinned WebView2 ReadWrite shared mapping (`provider.getInputViews()`) and publishes them
 * with a release `Atomics.store` into the head counter. No interop call per input, no JSON,
 * no allocation. Mirrors `@bonoboengine/core`'s producer.
 */
let localHead = 0;

function beginRecord(): { data: Float64Array; base: number; head: Int32Array } | null {
    const views = getLocalBufferProvider()?.getInputViews?.();
    if (!views) return null;
    return { data: views.data, base: (localHead % InputQueueCapacity) * InputSlotSize, head: views.head };
}

function publish(head: Int32Array): void {
    localHead += 1;
    Atomics.store(head, 0, localHead);
}

/** Publishes one click-move intent (steer the entity with the render id toward a point). */
export function writeClickMove(targetEntityId: number, x: number, y: number, z: number): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.clickMove;
    record.data[record.base + ClickMovePacketFields.targetEntityId] = targetEntityId;
    record.data[record.base + ClickMovePacketFields.x] = x;
    record.data[record.base + ClickMovePacketFields.y] = y;
    record.data[record.base + ClickMovePacketFields.z] = z;
    publish(record.head);
    return true;
}

/** Publishes one fire-ball intent (world-space origin + direction). */
export function writeFireBall(
    originX: number, originY: number, originZ: number,
    directionX: number, directionY: number, directionZ: number): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.fireBall;
    record.data[record.base + FireBallPacketFields.originX] = originX;
    record.data[record.base + FireBallPacketFields.originY] = originY;
    record.data[record.base + FireBallPacketFields.originZ] = originZ;
    record.data[record.base + FireBallPacketFields.directionX] = directionX;
    record.data[record.base + FireBallPacketFields.directionY] = directionY;
    record.data[record.base + FireBallPacketFields.directionZ] = directionZ;
    publish(record.head);
    return true;
}

/** Publishes the scene-load outcome (1 = loaded cleanly, 0 = failed). */
export function writeSceneLoaded(ok: boolean): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.sceneLoaded;
    record.data[record.base + SceneLoadedPacketFields.ok] = ok ? 1 : 0;
    publish(record.head);
    return true;
}

/** Publishes one character move intent (world-space camera-relative direction + buttons). */
export function writeCharacterMove(moveX: number, moveZ: number, jump: boolean, sprint: boolean): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.characterMove;
    record.data[record.base + CharacterMovePacketFields.moveX] = moveX;
    record.data[record.base + CharacterMovePacketFields.moveZ] = moveZ;
    record.data[record.base + CharacterMovePacketFields.jump] = jump ? 1 : 0;
    record.data[record.base + CharacterMovePacketFields.sprint] = sprint ? 1 : 0;
    publish(record.head);
    return true;
}

/** Publishes one vehicle-control intent (throttle/steer in -1..1 plus zoom/brake buttons). */
export function writeVehicleControl(throttle: number, steer: number, zoom: boolean, brake: boolean): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.vehicleControl;
    record.data[record.base + VehicleControlPacketFields.throttle] = throttle;
    record.data[record.base + VehicleControlPacketFields.steer] = steer;
    record.data[record.base + VehicleControlPacketFields.zoom] = zoom ? 1 : 0;
    record.data[record.base + VehicleControlPacketFields.brake] = brake ? 1 : 0;
    publish(record.head);
    return true;
}

/** Publishes one tank-control intent (move/turn/aim + fire/zoom/brake buttons). */
export function writeTankControl(
    move: number, turn: number,
    aimHorizontal: number, aimVertical: number,
    fire: boolean, zoom: boolean, brake: boolean): boolean {
    const record = beginRecord();
    if (!record) return false;

    record.data[record.base] = PacketType.tankControl;
    record.data[record.base + TankControlPacketFields.move] = move;
    record.data[record.base + TankControlPacketFields.turn] = turn;
    record.data[record.base + TankControlPacketFields.aimHorizontal] = aimHorizontal;
    record.data[record.base + TankControlPacketFields.aimVertical] = aimVertical;
    record.data[record.base + TankControlPacketFields.fire] = fire ? 1 : 0;
    record.data[record.base + TankControlPacketFields.zoom] = zoom ? 1 : 0;
    record.data[record.base + TankControlPacketFields.brake] = brake ? 1 : 0;
    publish(record.head);
    return true;
}
