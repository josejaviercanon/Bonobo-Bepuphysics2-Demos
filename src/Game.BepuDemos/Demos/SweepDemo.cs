using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>SweepDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby): a
///     12×3×12 grid of box/capsule/sphere bodies falls onto a deformed static plane while 16
///     box sweeps rotate around the scene; each sweep draws its ghost trail (20 poses) and,
///     on impact, two tangent lines marking the contact plane.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream background
///     pairwise shape-vs-shape sweep matrix uses the raw-pointer
///     <c>SweepTaskRegistry.Sweep(void*)</c> API and is dropped (no unsafe code in ported
///     demos); the foreground simulation-wide sweeps, ghost trails and impact markers are
///     preserved. Ghost trails are solid-colored per hit/miss id range instead of the
///     upstream per-step fade.
///
///     Render-id ranges: 0 = deformed plane static, 100+ = falling grid boxes, 10000+ =
///     falling grid capsules, 20000+ = falling grid spheres, 1000+ = ghost trails of hit
///     sweeps, 2000+ = ghost trails of miss sweeps, 100000+ = impact tangent lines.
/// </summary>
public sealed class SweepDemo : IDemoSimulation
{
    public const int PlaneRenderId = 0;
    public const int GridBoxRenderIdBase = 100;
    public const int GridCapsuleRenderIdBase = 10_000;
    public const int GridSphereRenderIdBase = 20_000;
    public const int SweepHitGhostRenderIdBase = 1000;
    public const int SweepMissGhostRenderIdBase = 2000;
    public const int ImpactLineRenderIdBase = 100_000;

    public const int GridWidth = 12;
    public const int GridHeight = 3;
    public const int GridLength = 12;
    public const int GridCount = GridWidth * GridHeight * GridLength;

    public const int SweepCount = 16;
    public const int SweepGhostSteps = 20;
    public const int GhostCount = SweepCount * SweepGhostSteps;
    public const int MaxLineCount = SweepCount * 2;

    public const int MaxTransformCount = 1 + GridCount + GhostCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength
        + MaxTransformCount * SignalBufferLayout.Transform3DStride
        + MaxLineCount * SignalBufferLayout.LineStateStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "sweep",
        "sweep",
        BufferCapacity,
        static (config, transport) => new SweepDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>Collects the closest scene-wide sweep hit (upstream `SceneSweepHitHandler`).</summary>
    private struct SceneSweepHitHandler : ISweepHitHandler
    {
        public Vector3 HitLocation;
        public Vector3 HitNormal;
        public float T;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int child) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHit(ref float maximumT, float t, Vector3 hitLocation, Vector3 hitNormal, CollidableReference collidable)
        {
            if (t < maximumT) maximumT = t;
            if (t < T)
            {
                T = t;
                HitLocation = hitLocation;
                HitNormal = hitNormal;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            maximumT = 0f;
            T = 0f;
            HitLocation = default;
            HitNormal = default;
        }
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly Box _sweepShape = new(1f, 2f, 1.5f);
    private long _seq;

    public SweepDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildSceneLocked()
    {
        var box = new Box(2f, 2f, 2f);
        var capsule = new Capsule(1f, 1f);
        var sphere = new Sphere(1.5f);
        var boxInertia = box.ComputeInertia(1f);
        var capsuleInertia = capsule.ComputeInertia(1f);
        var sphereInertia = sphere.ComputeInertia(1f);
        var boxIndex = _simulation.Shapes.Add(box);
        var capsuleIndex = _simulation.Shapes.Add(capsule);
        var sphereIndex = _simulation.Shapes.Add(sphere);

        var boxCount = 0;
        var capsuleCount = 0;
        var sphereCount = 0;
        for (var i = 0; i < GridWidth; ++i)
        {
            for (var j = 0; j < GridHeight; ++j)
            {
                for (var k = 0; k < GridLength; ++k)
                {
                    var location = new Vector3(5f, 5f, 5f) * new Vector3(i, j, k)
                                   + new Vector3(-GridWidth * 2.5f, 2.5f, -GridLength * 2.5f);

                    BodyDescription description;
                    Vector3 scale;
                    int renderId;
                    switch (j % 3)
                    {
                        case 0:
                            description = BodyDescription.CreateDynamic(location, boxInertia, boxIndex, 0.1f);
                            scale = new Vector3(2f, 2f, 2f);
                            renderId = GridBoxRenderIdBase + boxCount++;
                            break;
                        case 1:
                            description = BodyDescription.CreateDynamic(location, capsuleInertia, capsuleIndex, 0.1f);
                            scale = Vector3.One;
                            renderId = GridCapsuleRenderIdBase + capsuleCount++;
                            break;
                        default:
                            description = BodyDescription.CreateDynamic(location, sphereInertia, sphereIndex, 0.1f);
                            scale = new Vector3(3f);
                            renderId = GridSphereRenderIdBase + sphereCount++;
                            break;
                    }

                    var handle = _simulation.Bodies.Add(description);
                    _poses.AddDynamic(renderId, handle, scale);
                }
            }
        }

        const int planeWidth = 64;
        const int planeHeight = 64;
        var planeMesh = DemoMeshHelper.CreateDeformedPlane(planeWidth, planeHeight,
            static (x, y) => new Vector3(x, MathF.Cos(x / 4f) * MathF.Sin(y / 4f), y),
            new Vector3(2f, 3f, 2f), _bufferPool);
        var planePosition = new Vector3(-64f, -10f, -64f);
        _simulation.Statics.Add(new StaticDescription(planePosition, _simulation.Shapes.Add(planeMesh)));
        _poses.AddStatic(PlaneRenderId, planePosition, Quaternion.Identity, new Vector3(planeWidth, 1f, planeHeight));
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;

        var states = new List<Transform3DState>(MaxTransformCount);
        _poses.Emit(states);

        var lines = new List<LineState>(MaxLineCount);
        var ghostScale = new Vector3(1f, 2f, 1.5f);
        var localOrigin = new Vector3(-25f, 15f, 0f);
        var localDirection = new Vector3(7f, -10f, 0f);
        var inverse = 1f / (SweepGhostSteps - 1);

        for (var i = 0; i < SweepCount; ++i)
        {
            Matrix3x3.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), i * MathHelper.TwoPi / SweepCount, out var rotation);
            Matrix3x3.Transform(localOrigin, rotation, out var sweepOrigin);
            Matrix3x3.Transform(localDirection, rotation, out var sweepDirection);

            var hitHandler = default(SceneSweepHitHandler);
            hitHandler.T = float.MaxValue;
            var initialPose = new RigidPose { Position = sweepOrigin, Orientation = Quaternion.Identity };
            var sweepVelocity = new BodyVelocity { Linear = sweepDirection };
            _simulation.Sweep(_sweepShape, initialPose, sweepVelocity, 10f, _bufferPool, ref hitHandler);

            var intersected = hitHandler.T < float.MaxValue;
            var ghostBase = intersected ? SweepHitGhostRenderIdBase : SweepMissGhostRenderIdBase;
            var visualizedT = intersected ? hitHandler.T : 10f;

            for (var step = SweepGhostSteps - 1; step >= 0; --step)
            {
                var stepT = step * inverse * visualizedT;
                PoseIntegration.Integrate(initialPose, sweepVelocity, stepT, out var stepPose);
                states.Add(new Transform3DState(
                    ghostBase + i * SweepGhostSteps + step,
                    stepPose.Position.X, stepPose.Position.Y, stepPose.Position.Z,
                    stepPose.Orientation.X, stepPose.Orientation.Y, stepPose.Orientation.Z, stepPose.Orientation.W,
                    ghostScale.X, ghostScale.Y, ghostScale.Z,
                    EntityLifecycle3.Active));
            }

            if (intersected && hitHandler.T > 0f)
            {
                Helpers.BuildOrthonormalBasis(hitHandler.HitNormal, out var tangent1, out var tangent2);
                var hit = hitHandler.HitLocation;
                lines.Add(new LineState(
                    ImpactLineRenderIdBase + i * 2,
                    hit.X - tangent1.X, hit.Y - tangent1.Y, hit.Z - tangent1.Z,
                    hit.X + tangent1.X, hit.Y + tangent1.Y, hit.Z + tangent1.Z,
                    0d, 1d, 0d, 1d));
                lines.Add(new LineState(
                    ImpactLineRenderIdBase + i * 2 + 1,
                    hit.X - tangent2.X, hit.Y - tangent2.Y, hit.Z - tangent2.Z,
                    hit.X + tangent2.X, hit.Y + tangent2.Y, hit.Z + tangent2.Z,
                    0d, 1d, 0d, 1d));
            }
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states, lines);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
