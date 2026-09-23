using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>RopeTwistDemo</c> (BepuPhysics2, Apache-2.0): two ropes tied to a
///     10000-mass wrecking ball spinning around Y; hard no-friction contacts and heavy substepping
///     keep the worst-case mass ratio stable.
///
///     Test-bed deviations: 2 ropes × 65 links (upstream 4 × 131) and 30 substeps (upstream 60)
///     keep the desktop host interactive — both documented in docs/compat-review.md.
///
///     Render-id ranges: 0 = static ground, 100 + rope·100 = rope links (66 each), 500 = ball.
/// </summary>
public sealed class RopeTwistDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int RopeRenderIdBase = 100;
    public const int RopeRenderIdStride = 100;
    public const int RopeCount = 2;
    public const int RopeLinkCount = 66;
    public const int WreckingBallRenderId = 500;

    public const int MaxRecords = 1 + RopeCount * RopeLinkCount + 1;
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float RopeBodyRadius = 0.1f;
    public const float RopeBodySpacing = -0.1f;
    public const float RopeDistributionRadius = 1f;
    public const float WreckingBallRadius = 3f;
    public const int SubstepCount = 30;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "rope-twist",
        "rope-twist",
        BufferCapacity,
        static (config, transport) => new RopeTwistDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<RopeFilter> _filters = new();
    private readonly DemoPoseSet _poses;
    private long _seq;

    public RopeTwistDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new RopeNarrowPhaseCallbacks(_filters, new PairMaterialProperties(0.0f, float.MaxValue, new SpringSettings(1200f, 1f))),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(1, SubstepCount));

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int RecordCount => _poses.Count;
    internal float RopeBodySpacingUsed => RopeBodySpacing;

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
        var startLocation = new Vector3(0f, 30f, 0f);

        var bigWreckingBall = new Sphere(WreckingBallRadius);
        var bigWreckingBallInertia = bigWreckingBall.ComputeInertia(10000f);
        var bigWreckingBallIndex = _simulation.Shapes.Add(bigWreckingBall);
        var wreckingBallPosition = startLocation - new Vector3(
            0f, RopeBodyRadius + (RopeBodyRadius * 2 + RopeBodySpacing) * (RopeLinkCount - 1) + bigWreckingBall.Radius, 0f);
        var wreckingBallHandle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(wreckingBallPosition, Quaternion.Identity), bigWreckingBallInertia, bigWreckingBallIndex, 0.01f));
        _simulation.Bodies[wreckingBallHandle].Velocity.Angular = new Vector3(0f, 20f, 0f);
        _filters.Allocate(wreckingBallHandle) = new RopeFilter { RopeIndex = 16384, IndexInRope = RopeLinkCount };

        var springSettings = new SpringSettings(600f, 100f);
        for (var ropeIndex = 0; ropeIndex < RopeCount; ++ropeIndex)
        {
            var angle = ropeIndex * MathF.PI * 2 / RopeCount;
            var horizontalOffset = RopeDistributionRadius * new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle));
            var ropeStartLocation = startLocation + horizontalOffset;

            var bodyHandles = RopeHelpers.BuildRopeBodies(
                _simulation, ropeStartLocation, RopeLinkCount - 1, RopeBodyRadius, RopeBodySpacing, 1f, 0f);
            for (var i = 0; i < bodyHandles.Length; ++i)
            {
                _filters.Allocate(bodyHandles[i]) = new RopeFilter { RopeIndex = (short)ropeIndex, IndexInRope = (short)i };
            }

            for (var i = 0; i < bodyHandles.Length - 1; ++i)
            {
                var maximumDistance = Vector3.Distance(
                    _simulation.Bodies[bodyHandles[i]].Pose.Position,
                    _simulation.Bodies[bodyHandles[i + 1]].Pose.Position);
                _simulation.Solver.Add(bodyHandles[i], bodyHandles[i + 1],
                    new DistanceLimit(default, default, 0.01f, maximumDistance, springSettings));
            }

            var wreckingBallConnectionOffset = horizontalOffset + new Vector3(0f, bigWreckingBall.Radius, 0f);
            var ropeConnectionToBall = _simulation.Bodies[wreckingBallHandle].Pose.Position + wreckingBallConnectionOffset;
            var maximumDistanceToBall = Vector3.Distance(_simulation.Bodies[bodyHandles[^1]].Pose.Position, ropeConnectionToBall);
            _simulation.Solver.Add(bodyHandles[^1], wreckingBallHandle,
                new DistanceLimit(default, wreckingBallConnectionOffset, 0.01f, maximumDistanceToBall, springSettings));

            for (var i = 0; i < bodyHandles.Length; i++)
            {
                _poses.AddDynamic(
                    RopeRenderIdBase + ropeIndex * RopeRenderIdStride + i,
                    bodyHandles[i], new Vector3(RopeBodyRadius * 2f));
            }
        }

        _poses.AddDynamic(WreckingBallRenderId, wreckingBallHandle, new Vector3(WreckingBallRadius * 2f));

        _simulation.Statics.Add(new StaticDescription(Vector3.Zero, _simulation.Shapes.Add(new Box(200f, 1f, 200f))));
        _poses.AddStatic(GroundRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(200f, 1f, 200f));
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
