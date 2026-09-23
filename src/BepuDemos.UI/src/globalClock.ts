import { GlobalClockStateIndex, type ScalarArray } from './signalLayout';

/** Latest decoded globals clock (sim-time seconds, pause-frozen). */
export interface GlobalClock {
    timeSeconds: number;
    deltaSeconds: number;
    stepCount: number;
    paused: number;
    interpAlpha: number;
    /** Host-side input diagnostics: routed records (reserved0) / dropped records (reserved1). */
    processedInputs: number;
    droppedInputs: number;
}

/** Reads one globals snapshot out of the raw float64 view (valid inside the listener). */
export function readGlobalClock(values: ScalarArray): GlobalClock {
    return {
        timeSeconds: values[GlobalClockStateIndex.timeSeconds],
        deltaSeconds: values[GlobalClockStateIndex.deltaSeconds],
        stepCount: values[GlobalClockStateIndex.stepCount],
        paused: values[GlobalClockStateIndex.paused],
        interpAlpha: values[GlobalClockStateIndex.interpAlpha],
        processedInputs: values[GlobalClockStateIndex.reserved0],
        droppedInputs: values[GlobalClockStateIndex.reserved1],
    };
}
