namespace DemoEngine.ECS;

/// <summary>
///     Plain-data snapshot of one 3D entity transform, serialized into the shared-memory
///     float64 signal buffer. Layout: id + position (x, y, z) + rotation quaternion
///     (qx, qy, qz, qw) + scale (sx, sy, sz) + lifecycle flag = 12 elements.
///     Quaternion component order matches both BepuPhysics2 <c>RigidPose.Orientation</c>
///     and Babylon.js <c>Quaternion</c> (x, y, z, w).
///     Mirrors <c>Game.Engine.ECS.Transform3DState</c> (declared stride 12, float64).
/// </summary>
public record struct Transform3DState(
    int Id,
    double X, double Y, double Z,
    double Qx, double Qy, double Qz, double Qw,
    double Sx, double Sy, double Sz,
    double Lifecycle);

public sealed record Transform3DRenderSignal(
    long Seq,
    int EntityCount,
    double TickMs,
    IReadOnlyList<Transform3DState> States);

/// <summary>
///     Lifecycle flags carried in the 12th scalar of <see cref="Transform3DState"/> so the
///     Babylon.js side can instantiate/dispose meshes without per-entity interop calls.
///     Destroyed entities are emitted one final time with <see cref="Destroyed"/> before the
///     ECS entity is removed. Mirrors the engine demo convention.
/// </summary>
public static class EntityLifecycle3
{
    public const double Active = 0d;
    public const double Spawned = 1d;
    public const double Destroyed = 3d;
}

/// <summary>
///     Encoders for the engine-owned batched render signal (3D transforms) into the
///     shared-memory layout consumed by <see cref="DirectRenderTransport{TSignal,T}"/> and the
///     TypeScript decoders. Mirrors <c>Game.Engine.ECS.SignalBufferEncoders</c>.
/// </summary>
public static class SignalBufferEncoders
{
    public static int ElementLength(Transform3DRenderSignal s) =>
        SignalBuffer.HeaderLength + s.States.Count * SignalBufferLayout.Transform3DStride;

    public static void Encode(Transform3DRenderSignal s, Span<double> f)
    {
        SignalBuffer.WriteHeader(f, s.Seq, 0, s.States.Count,
            SignalBufferLayout.Transform3DStride, (1d / 60d) * 1000d, s.TickMs);

        for (var i = 0; i < s.States.Count; i++)
        {
            var st = s.States[i];
            var dst = f.Slice(SignalBuffer.HeaderLength + i * SignalBufferLayout.Transform3DStride,
                SignalBufferLayout.Transform3DStride);
            dst[0] = st.Id;
            dst[1] = st.X;
            dst[2] = st.Y;
            dst[3] = st.Z;
            dst[4] = st.Qx;
            dst[5] = st.Qy;
            dst[6] = st.Qz;
            dst[7] = st.Qw;
            dst[8] = st.Sx;
            dst[9] = st.Sy;
            dst[10] = st.Sz;
            dst[11] = st.Lifecycle;
        }
    }
}
