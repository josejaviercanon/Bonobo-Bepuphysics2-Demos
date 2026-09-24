/** Tracks physical key state by `KeyboardEvent.code`; consumed by the gameplay scenes. */
export interface KeyTracker {
    isDown(code: string): boolean;
    dispose(): void;
}

/**
 * Minimal keydown/keyup tracker feeding the pinned input ring. Physical codes ('KeyW',
 * 'ShiftLeft', ...) keep the layout stable; every gameplay packet still goes through
 * `writeCharacterMove`/`writeVehicleControl`/`writeTankControl` (no DOM event path into the
 * simulation).
 */
export function createKeyTracker(target: Window = window): KeyTracker {
    const down = new Set<string>();
    const onKeyDown = (event: KeyboardEvent) => {
        down.add(event.code);
    };
    const onKeyUp = (event: KeyboardEvent) => {
        down.delete(event.code);
    };
    const onBlur = () => {
        down.clear();
    };

    target.addEventListener('keydown', onKeyDown);
    target.addEventListener('keyup', onKeyUp);
    target.addEventListener('blur', onBlur);

    return {
        isDown: (code: string) => down.has(code),
        dispose: () => {
            target.removeEventListener('keydown', onKeyDown);
            target.removeEventListener('keyup', onKeyUp);
            target.removeEventListener('blur', onBlur);
            down.clear();
        },
    };
}
