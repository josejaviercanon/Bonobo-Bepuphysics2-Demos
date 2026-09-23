using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
///     Port of the upstream <c>FrictionDemo</c> (BepuPhysics2, Apache-2.0): 100 boxes with a
///     sideways velocity slide across a static floor; the friction coefficient ramps from 0 to
///     0.75 along the line, so the boxes slide different distances. Materials come from a
///     <c>CollidableProperty&lt;SimpleMaterial&gt;</c> blended multiplicatively per pair — the same
///     material-property pattern as <see cref="BouncinessDemo"/>.
///
///     Render-id ranges: 0 = static floor, 100+ = boxes.
/// </summary>
public sealed class FrictionDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int BoxRenderIdBase = 100;

    public const int BoxCount = 100;
    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + BoxCount) * SignalBufferLayout.Transform3DStride;

    public const float BoxSize = 1f;
    public const float MaximumFriction = 0.75f;
    public const float FloorSize = 2500f;
    public const float FloorThickness = 30f;
    public const float InitialVelocityX = 20f;

    public struct SimpleMaterial
    {
        public SpringSettings SpringSettings;
        public float FrictionCoefficient;
        public float MaximumRecoveryVelocity;
    }

    /// <summary>
    ///     Per-pair material blending (upstream): multiplicative friction, spring chosen by the
    ///     higher maximum recovery velocity. Public for the unit tests' contact setup probes.
    /// </summary>
    public struct FrictionCallbacks : INarrowPhaseCallbacks
    {
        public CollidableProperty<SimpleMaterial> CollidableMaterials;

        public void Initialize(Simulation simulation) => CollidableMaterials.Initialize(simulation);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
            a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            var a = CollidableMaterials[pair.A];
            var b = CollidableMaterials[pair.B];
            pairMaterial.FrictionCoefficient = a.FrictionCoefficient * b.FrictionCoefficient;
            pairMaterial.MaximumRecoveryVelocity = MathF.Max(a.MaximumRecoveryVelocity, b.MaximumRecoveryVelocity);
            pairMaterial.SpringSettings = pairMaterial.MaximumRecoveryVelocity == a.MaximumRecoveryVelocity
                ? a.SpringSettings
                : b.SpringSettings;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

        public void Dispose()
        {
        }
    }

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "friction",
        "friction",
        BufferCapacity,
        static (config, transport) => new FrictionDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<SimpleMaterial> _materials = new();
    private readonly DemoPoseSet _poses;
    private readonly TypedIndex _boxShape;
    private readonly BodyInertia _boxInertia;

    private long _seq;

    public FrictionDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new FrictionCallbacks { CollidableMaterials = _materials },
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f), 0f, 0f),
            new SolveDescription(4, 1));

        var box = new Box(BoxSize, BoxSize, BoxSize);
        _boxShape = _simulation.Shapes.Add(box);
        _boxInertia = box.ComputeInertia(1f);
        _poses = new DemoPoseSet(_world, _simulation);

        BuildLineLocked();
    }

    internal int BoxEntityCount => BoxCount;

    /// <summary>Test probe: the Bepu body handle owned by a box render id.</summary>
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

    /// <summary>Rebuilds the line deterministically (bodies and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _seq = 0;
            BuildLineLocked();
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

    private void BuildLineLocked()
    {
        var boxDescription = BodyDescription.CreateDynamic(
            RigidPose.Identity, new Vector3(InitialVelocityX, 0f, 0f), _boxInertia, _boxShape, 1e-2f);

        for (var i = 0; i < BoxCount; i++)
        {
            boxDescription.Pose.Position = new Vector3(-80f, 0.5f, i * 1.2f);
            var handle = _simulation.Bodies.Add(boxDescription);
            _materials.Allocate(handle) = new SimpleMaterial
            {
                FrictionCoefficient = MaximumFriction * i / (BoxCount - 1f),
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30f, 1f),
            };

            _poses.AddDynamic(BoxRenderIdBase + i, handle, new Vector3(BoxSize, BoxSize, BoxSize));
        }

        _materials.Allocate(_simulation.Statics.Add(new StaticDescription(
                new Vector3(0f, -15f, 0f),
                _simulation.Shapes.Add(new Box(FloorSize, FloorThickness, FloorSize))))) =
            new SimpleMaterial { FrictionCoefficient = 1f, MaximumRecoveryVelocity = 2f, SpringSettings = new SpringSettings(30f, 1f) };

        _poses.AddStatic(
            FloorRenderId, new Vector3(0f, -15f, 0f), Quaternion.Identity,
            new Vector3(FloorSize, FloorThickness, FloorSize));
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
