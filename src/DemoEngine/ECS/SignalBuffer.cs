namespace DemoEngine.ECS;

/// <summary>
///     Canonical shared-memory signal header — the C# half of the shared-memory signal
///     contract. Mirrors <c>Game.Engine.ECS.SignalBuffer</c> 1:1 (header shape, index order,
///     scalar type). Constants are pinned by unit tests.
///
///     Every signal buffer starts with an eight-element standard header:
///         [0] seq, [1] epoch, [2] entityCount, [3] stride, [4] stepMs, [5] tickMs,
///         [6] lineCount, [7] lineStride
///     followed by scene-specific scalar extras, then entityCount × stride entity records,
///     then lineCount × lineStride line records. Line records are a local extension for the
///     P2c debug-visual demos (rays/sweeps); demos that do not emit lines write
///     lineCount = 0. Scalar elements are always 8-byte doubles (<c>Float64Array</c> on every
///     host) — the ABI carries no scalar-size field. Booleans are encoded as 0 / 1.
/// </summary>
public static class SignalBuffer
{
    public const int HeaderLength = 8;
    public const int HeaderSeq = 0;
    public const int HeaderEpoch = 1;
    public const int HeaderEntityCount = 2;
    public const int HeaderStride = 3;
    public const int HeaderStepMs = 4;
    public const int HeaderTickMs = 5;
    public const int HeaderLineCount = 6;
    public const int HeaderLineStride = 7;

    public static void WriteHeader(
        Span<double> f, long seq, long epoch, int entityCount, int stride, double stepMs, double tickMs,
        int lineCount = 0, int lineStride = 0)
    {
        f[HeaderSeq] = seq;
        f[HeaderEpoch] = epoch;
        f[HeaderEntityCount] = entityCount;
        f[HeaderStride] = stride;
        f[HeaderStepMs] = stepMs;
        f[HeaderTickMs] = tickMs;
        f[HeaderLineCount] = lineCount;
        f[HeaderLineStride] = lineStride;
    }

    /// <summary>
    ///     Global engine clock block ("globals" signal) — one fixed-capacity block owned by
    ///     <c>SimulationHost</c> and rewritten on every host tick, independently of the
    ///     per-scene signal cadence. Scalar elements are 8-byte doubles like every other
    ///     signal. Semantics: simulation time is the accumulated fixed-step time (frozen while
    ///     paused, deterministic on every host); <c>interpAlpha</c> is the residual accumulator
    ///     fraction used by client render interpolation
    ///     (<c>P_render = P_prev + (P_curr − P_prev) × α</c>).
    ///     Mirrors <c>Game.Engine.ECS.SignalBuffer.GlobalClock*</c>.
    /// </summary>
    public const int GlobalClockLength = 8;
    public const int GlobalClockSeq = 0;
    public const int GlobalClockTimeSeconds = 1;
    public const int GlobalClockDeltaSeconds = 2;
    public const int GlobalClockStepCount = 3;
    public const int GlobalClockPaused = 4;
    public const int GlobalClockInterpAlpha = 5;
    public const int GlobalClockReserved0 = 6;
    public const int GlobalClockReserved1 = 7;
}

/// <summary>
///     Per-signal buffer geometry: element stride and scalar element size. Mirrors
///     <c>Game.Engine.ECS.SignalBufferLayout</c> for the layouts this repo uses. Pinned by
///     unit tests; there is no source generator here by design (see docs/compat-review.md).
/// </summary>
public static class SignalBufferLayout
{
    // transform3d: no extras, Transform3DState record (id, xyz, quat xyzw, scale xyz, lifecycle) — float64.
    public const int Transform3DStride = 12;
    public const int Transform3DScalarSize = 8;
    public const int Transform3DByteLength = Transform3DStride * Transform3DScalarSize;

    // lines: LineState record (id, axyz, bxyz, rgba, reserved) — float64, appended after the
    // transform region in the same signal buffer (P2c debug-visual demos only).
    public const int LineStateStride = 12;
    public const int LineStateScalarSize = 8;
    public const int LineStateByteLength = LineStateStride * LineStateScalarSize;

    // globals: GlobalClockState record (seq, time, delta, stepCount, paused, interpAlpha,
    // reserved ×2) — float64. Host-scope block, not scene-scope.
    public const int GlobalClockStride = 8;
    public const int GlobalClockScalarSize = 8;
    public const int GlobalClockByteLength = GlobalClockStride * GlobalClockScalarSize;
}
