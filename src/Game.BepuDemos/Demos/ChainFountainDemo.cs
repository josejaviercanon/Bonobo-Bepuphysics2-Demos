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
///     Port of the upstream <c>ChainFountainDemo</c> (BepuPhysics2, Apache-2.0): a stiff
///     segmented rope of capsule beads coils inside a container, then yanks itself over the lip
///     (Newton's beads / the Mould effect). Per-link <see cref="BallSocket"/> + swing limits.
///
///     Test-bed deviation: 2048 beads (upstream 4096) — documented in docs/compat-review.md.
///
///     Render-id ranges: 0 = container floor, 10 + = container walls, 100 + = beads.
/// </summary>
public sealed class ChainFountainDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int WallRenderIdBase = 10;
    public const int BeadRenderIdBase = 100;

    /// <summary>Upstream uses 4096; halved for desktop host interactivity.</summary>
    public const int BeadCount = 2048;

    public const int MaxRecords = 1 + 2 + BeadCount;
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float BeadRadius = 0.05f;
    public const float BeadSpacing = 0.3f;
    public const float SpiralRadius = 2.5f;
    public const float ContainerHalfWidth = 5.65f;
    public const float ContainerFloorThickness = 0.2f;
    public const float ContainerLength = 40f;
    public const float WallThickness = 0.4f;
    public const float WallHeight = 1f;
    public const int InitialSpeedupBeads = 32;
    public const float InitialSpeedX = 20f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "chain-fountain",
        "chain-fountain",
        BufferCapacity,
        static (config, transport) => new ChainFountainDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<RopeFilter> _filters = new();
    private readonly DemoPoseSet _poses;
    private readonly Vector3 _beadScale;
    private long _seq;

    public ChainFountainDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new RopeNarrowPhaseCallbacks(
                _filters, new PairMaterialProperties(0.1f, float.MaxValue, new SpringSettings(240f, 0f)), 3),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(1, 12));

        _beadScale = Vector3.One;
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

    /// <summary>Rebuilds the coil deterministically (bodies, constraints and ECS entities).</summary>
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
        var beadShape = new Capsule(BeadRadius, BeadSpacing);
        var beadInertia = beadShape.ComputeInertia(1f);
        var beadShapeIndex = _simulation.Shapes.Add(beadShape);

        var anglePerIteration = 2 * MathF.Asin(BeadSpacing / (2 * SpiralRadius));
        var heightPerIteration = beadShape.Radius * 2 / (MathF.PI * 2 / anglePerIteration);
        var handles = new BodyHandle[BeadCount];
        for (var i = 0; i < BeadCount; ++i)
        {
            var angle = MathF.PI + i * anglePerIteration;
            var nextAngle = MathF.PI + (i + 1) * anglePerIteration;

            var currentPosition = new Vector3(
                2.8f + MathF.Sin(angle) * SpiralRadius,
                0.5f + heightPerIteration * i,
                -15f + MathF.Cos(angle) * SpiralRadius);
            var nextPosition = new Vector3(
                2.8f + MathF.Sin(nextAngle) * SpiralRadius,
                0.5f + heightPerIteration * (i + 1),
                -15f + MathF.Cos(nextAngle) * SpiralRadius);
            // The constraints were built along the local Y axis, so get the shortest rotation from Y to the current orientation.
            var offset = currentPosition - nextPosition;
            var cross = Vector3.Cross(Vector3.Normalize(offset), new Vector3(0, 1, 0));
            var crossLength = cross.Length();
            var orientation = crossLength > 1e-8f
                ? QuaternionEx.CreateFromAxisAngle(cross / crossLength, (float)Math.Asin(crossLength))
                : Quaternion.Identity;

            var description = BodyDescription.CreateDynamic(
                new RigidPose(currentPosition + new Vector3(0f, 0f, i * 0.006f), orientation),
                beadInertia, beadShapeIndex, 0.01f);
            // Throw the tip of the rope off the edge.
            if (i > BeadCount - InitialSpeedupBeads)
            {
                description.Velocity.Linear = new Vector3(InitialSpeedX, 0f, 0f);
            }

            handles[i] = _simulation.Bodies.Add(description);
            _filters.Allocate(handles[i]) = new RopeFilter { RopeIndex = 1, IndexInRope = (short)i };

            if (i > 0)
            {
                _simulation.Solver.Add(handles[i - 1], handles[i], new BallSocket
                {
                    LocalOffsetA = new Vector3(0f, BeadSpacing * 0.5f, 0f),
                    LocalOffsetB = new Vector3(0f, BeadSpacing * -0.5f, 0f),
                    SpringSettings = new SpringSettings(120f, 1f),
                });
                _simulation.Solver.Add(handles[i - 1], handles[i], new SwingLimit
                {
                    AxisLocalA = Vector3.UnitY,
                    AxisLocalB = Vector3.UnitY,
                    SpringSettings = new SpringSettings(120f, 1f),
                    MaximumSwingAngle = MathF.PI * 0.05f,
                });
            }

            _poses.AddDynamic(BeadRenderIdBase + i, handles[i], _beadScale);
        }

        _simulation.Statics.Add(new StaticDescription(
            Vector3.Zero, _simulation.Shapes.Add(new Box(ContainerHalfWidth * 2f, ContainerFloorThickness, ContainerLength))));
        _poses.AddStatic(
            GroundRenderId, Vector3.Zero, Quaternion.Identity,
            new Vector3(ContainerHalfWidth * 2f, ContainerFloorThickness, ContainerLength));

        var wallShape = new Box(WallThickness, WallHeight, ContainerLength);
        var wallShapeIndex = _simulation.Shapes.Add(wallShape);
        var wallPosition = new Vector3(ContainerHalfWidth, 2.4f - 2f, 0f);
        _simulation.Statics.Add(new StaticDescription(wallPosition, wallShapeIndex));
        _simulation.Statics.Add(new StaticDescription(new Vector3(-ContainerHalfWidth, 2.4f - 2f, 0f), wallShapeIndex));
        _poses.AddStatic(WallRenderIdBase, wallPosition, Quaternion.Identity, new Vector3(WallThickness, WallHeight, ContainerLength));
        _poses.AddStatic(WallRenderIdBase + 1, new Vector3(-ContainerHalfWidth, 2.4f - 2f, 0f), Quaternion.Identity, new Vector3(WallThickness, WallHeight, ContainerLength));

        // Kill floor far below — physics only, never rendered.
        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -500f, 0f), _simulation.Shapes.Add(new Box(500f, 1f, 500f))));
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
