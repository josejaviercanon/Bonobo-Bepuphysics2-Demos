using System.Diagnostics;
using System.Numerics;
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
///     Port of the upstream <c>RopeStabilityDemo</c> (BepuPhysics2, Apache-2.0): seven rope
///     stability configurations (naive light/heavy wrecking balls, softer springs, mass boost,
///     inertia boost, zero lever arm, direct cheat constraint) plus a 100-link rope with skip
///     constraints, all swinging next to a static capsule wrap post. No input in the upstream
///     demo; this port exposes <c>reset</c> only.
///
///     Render-id ranges: 0 = static ground, 100 + config·20 = rope links (13 each),
///     300 + = skip-rope links (101), 500 + = wrecking balls (8), 600 = static wrap post.
/// </summary>
public sealed class RopeStabilityDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int RopeRenderIdBase = 100;
    public const int RopeRenderIdStride = 20;
    public const int RopeConfigCount = 7;
    public const int RopeLinkCount = 13;
    public const int SkipRopeRenderIdBase = 300;
    public const int SkipRopeLinkCount = 101;
    public const int WreckingBallRenderIdBase = 500;
    public const int WreckingBallCount = 8;
    public const int WrapPostRenderId = 600;

    public const int MaxRecords =
        1 + RopeConfigCount * RopeLinkCount + SkipRopeLinkCount + WreckingBallCount + 1;
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float BodyRadius = 0.5f;
    public const float BodySpacing = 0.3f;
    public const float SmallBallRadius = 1f;
    public const float BigBallRadius = 3f;
    public const int SkipConstraintsPerBody = 4;
    public const float WrapPostRadius = 8f;
    public const float WrapPostLength = 64f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "rope-stability",
        "rope-stability",
        BufferCapacity,
        static (config, transport) => new RopeStabilityDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private long _seq;

    public RopeStabilityDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30f, 1f), 20f),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int RecordCount => _poses.Count;

    /// <summary>Test probe: the Bepu body handle owned by a render id.</summary>
    internal bool TryGetPhysicsBody(int renderId, out PhysicsBody body)
    {
        lock (_sync)
        {
            body = default;
            if (!_poses.TryGetBody(renderId, out var handle)) return false;
            body = new PhysicsBody(handle);
            return true;
        }
    }

    /// <summary>Payload-free verbs exposed through the generic host command path.</summary>
    public bool TryCommand(string verb)
    {
        switch (verb)
        {
            case "reset":
                Reset();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the fixture deterministically (bodies, constraints and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _seq = 0;
            BuildSceneLocked();
        }
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
        var smallWreckingBall = new Sphere(SmallBallRadius);
        var smallWreckingBallInertia = smallWreckingBall.ComputeInertia(5f);
        var smallWreckingBallIndex = _simulation.Shapes.Add(smallWreckingBall);

        var bigWreckingBall = new Sphere(BigBallRadius);
        var bigWreckingBallInertia = bigWreckingBall.ComputeInertia(100f);
        var bigWreckingBallIndex = _simulation.Shapes.Add(bigWreckingBall);

        // Each config: the naive light/heavy-ball pair, then the four stabilization tricks.
        Span<float> starts = stackalloc float[7];
        starts[0] = -55f;
        starts[1] = -35f;
        starts[2] = -15f;
        starts[3] = -5f;
        starts[4] = 5f;
        starts[5] = 15f;
        starts[6] = 25f;
        Span<float> ropeMasses = stackalloc float[7];
        ropeMasses[0] = 1f;
        ropeMasses[1] = 1f;
        ropeMasses[2] = 1f;
        ropeMasses[3] = 20f;
        ropeMasses[4] = 5f;
        ropeMasses[5] = 1f;
        ropeMasses[6] = 1f;
        Span<float> inertiaScales = stackalloc float[7];
        inertiaScales[0] = 1f;
        inertiaScales[1] = 1f;
        inertiaScales[2] = 1f;
        inertiaScales[3] = 1f;
        inertiaScales[4] = 0.2f;
        inertiaScales[5] = 0f;
        inertiaScales[6] = 0f;
        Span<float> constraintOffsets = stackalloc float[7];
        constraintOffsets[0] = BodyRadius;
        constraintOffsets[1] = BodyRadius;
        constraintOffsets[2] = BodyRadius;
        constraintOffsets[3] = BodyRadius;
        constraintOffsets[4] = BodyRadius;
        constraintOffsets[5] = 0f;
        constraintOffsets[6] = 0f;
        Span<float> springFrequencies = stackalloc float[7];
        springFrequencies[0] = 30f;
        springFrequencies[1] = 30f;
        springFrequencies[2] = 3f;
        springFrequencies[3] = 30f;
        springFrequencies[4] = 30f;
        springFrequencies[5] = 30f;
        springFrequencies[6] = 30f;
        Span<bool> useSmallBall = stackalloc bool[7];
        useSmallBall[0] = true;

        for (var configIndex = 0; configIndex < RopeConfigCount; ++configIndex)
        {
            var startLocation = new Vector3(starts[configIndex], 35f, 0f);
            var springSettings = new SpringSettings(springFrequencies[configIndex], 1f);
            var bodyHandles = RopeHelpers.BuildRope(
                _simulation, startLocation, RopeLinkCount - 1, BodyRadius, BodySpacing,
                constraintOffsets[configIndex], ropeMasses[configIndex], inertiaScales[configIndex], springSettings);

            var useSmall = useSmallBall[configIndex];
            var ballInertia = useSmall ? smallWreckingBallInertia : bigWreckingBallInertia;
            var ballRadius = useSmall ? smallWreckingBall.Radius : bigWreckingBall.Radius;
            var ballShapeIndex = useSmall ? smallWreckingBallIndex : bigWreckingBallIndex;
            var wreckingBallHandle = RopeHelpers.AttachWreckingBall(
                _simulation, bodyHandles, BodyRadius, BodySpacing, constraintOffsets[configIndex],
                ballRadius, ballInertia, ballShapeIndex, springSettings);

            if (configIndex == 6)
            {
                // The "cheat" constraint: attach the wrecking ball directly to the kinematic top link.
                var wreckingBallConnectionOffset = new Vector3(0, ballRadius, 0);
                var maximumDistance = Vector3.Distance(
                    _simulation.Bodies[bodyHandles[0]].Pose.Position,
                    _simulation.Bodies[wreckingBallHandle].Pose.Position + wreckingBallConnectionOffset);
                _simulation.Solver.Add(bodyHandles[0], wreckingBallHandle,
                    new DistanceLimit(default, wreckingBallConnectionOffset, 0.01f, maximumDistance, springSettings));
            }

            for (var i = 0; i < bodyHandles.Length; i++)
            {
                _poses.AddDynamic(
                    RopeRenderIdBase + configIndex * RopeRenderIdStride + i,
                    bodyHandles[i], new Vector3(BodyRadius * 2f));
            }

            _poses.AddDynamic(WreckingBallRenderIdBase + configIndex, wreckingBallHandle, new Vector3(ballRadius * 2f));
        }

        // The skip-constraint rope: impulses propagate along multiple shortcut paths.
        {
            var startLocation = new Vector3(35f, 140f, 0f);
            var springSettings = new SpringSettings(30f, 1f);
            var bodyHandles = RopeHelpers.BuildRopeBodies(
                _simulation, startLocation, SkipRopeLinkCount - 1, BodyRadius, BodySpacing, 1f, 0f);

            bool TryCreateConstraint(int handleIndexA, int handleIndexB)
            {
                if (handleIndexA >= bodyHandles.Length || handleIndexB >= bodyHandles.Length) return false;
                var maximumDistance = Vector3.Distance(
                    _simulation.Bodies[bodyHandles[handleIndexA]].Pose.Position,
                    _simulation.Bodies[bodyHandles[handleIndexB]].Pose.Position);
                _simulation.Solver.Add(bodyHandles[handleIndexA], bodyHandles[handleIndexB],
                    new DistanceLimit(default, default, 0.01f, maximumDistance, springSettings));
                return true;
            }

            for (var i = 0; i < bodyHandles.Length - 1; ++i)
            {
                for (var j = 1; j <= SkipConstraintsPerBody; ++j)
                {
                    if (!TryCreateConstraint(i, i + j)) break;
                }
            }

            var wreckingBallHandle = RopeHelpers.CreateWreckingBall(
                _simulation, bodyHandles, BodyRadius, BodySpacing, BigBallRadius, bigWreckingBallInertia, bigWreckingBallIndex);
            var wreckingBallConnectionOffset = new Vector3(0, BigBallRadius, 0);
            for (var i = 1; i <= SkipConstraintsPerBody; ++i)
            {
                var targetBodyHandleIndex = bodyHandles.Length - i;
                if (targetBodyHandleIndex < 0) break;
                var maximumDistance = Vector3.Distance(
                    _simulation.Bodies[bodyHandles[targetBodyHandleIndex]].Pose.Position,
                    _simulation.Bodies[wreckingBallHandle].Pose.Position + wreckingBallConnectionOffset);
                _simulation.Solver.Add(bodyHandles[targetBodyHandleIndex], wreckingBallHandle,
                    new DistanceLimit(default, wreckingBallConnectionOffset, 0.01f, maximumDistance, springSettings));
            }

            for (var i = 0; i < bodyHandles.Length; i++)
            {
                _poses.AddDynamic(SkipRopeRenderIdBase + i, bodyHandles[i], new Vector3(BodyRadius * 2f));
            }

            _poses.AddDynamic(WreckingBallRenderIdBase + RopeConfigCount, wreckingBallHandle, new Vector3(BigBallRadius * 2f));
        }

        _simulation.Statics.Add(new StaticDescription(Vector3.Zero, _simulation.Shapes.Add(new Box(200f, 1f, 200f))));
        _poses.AddStatic(GroundRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(200f, 1f, 200f));

        // The static capsule the ropes wrap around (upstream keeps it high on +X).
        var wrapPostPosition = new Vector3(100f, 70f, 0f);
        var wrapPostOrientation = QuaternionEx.CreateFromAxisAngle(new Vector3(1f, 0f, 0f), MathF.PI * 0.5f);
        _simulation.Statics.Add(new StaticDescription(
            wrapPostPosition, wrapPostOrientation, _simulation.Shapes.Add(new Capsule(WrapPostRadius, WrapPostLength))));
        _poses.AddStatic(WrapPostRenderId, wrapPostPosition, wrapPostOrientation, Vector3.One);
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        _seq++;
        var states = new List<Transform3DState>(_poses.Count);
        _poses.Emit(states);
        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
