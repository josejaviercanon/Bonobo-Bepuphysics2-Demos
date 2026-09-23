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
///     Port of the upstream <c>BouncinessDemo</c> (BepuPhysics2, Apache-2.0): contact springs
///     instead of a coefficient of restitution. A sphere grid drops onto a static floor;
///     from left to right the spring frequency increases, from far to near the damping ratio
///     increases. Per-collidable materials come from a <c>CollidableProperty&lt;SimpleMaterial&gt;</c>.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream 100×100 grid is
///     reduced to 40×40 = 1600 spheres so the desktop test host stays interactive. The
///     material sweep (frequency/damping per column/row) is preserved.
///
///     Render-id ranges: 0 = static floor, 100+ = spheres.
/// </summary>
public sealed class BouncinessDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int BallRenderIdBase = 100;

    public const int GridColumns = 40;
    public const int GridRows = 40;
    public const int BallCount = GridColumns * GridRows;
    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + BallCount) * SignalBufferLayout.Transform3DStride;

    public const float BallRadius = 1f;
    public const float FloorHalfExtent = 1250f;
    public const float FloorHalfThickness = 15f;
    public const float FloorRenderScaleX = FloorHalfExtent * 2f;
    public const float FloorRenderScaleY = FloorHalfThickness * 2f;

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
    public struct BounceCallbacks : INarrowPhaseCallbacks
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
        "bounciness",
        "bounciness",
        BufferCapacity,
        static (config, transport) => new BouncinessDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<SimpleMaterial> _materials = new();
    private readonly TypedIndex _sphereShape;
    private readonly BodyInertia _sphereInertia;
    private readonly Dictionary<int, Entity> _entityByRenderId = new();
    private readonly List<int> _ballRenderIds = new();

    private long _seq;

    public BouncinessDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        // Bounciness needs substeps (upstream: 8) so brief contacts are integrated properly.
        _simulation = Simulation.Create(
            _bufferPool,
            new BounceCallbacks { CollidableMaterials = _materials },
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f), 0f, 0f),
            new SolveDescription(1, 8));

        var sphere = new Sphere(BallRadius);
        _sphereShape = _simulation.Shapes.Add(sphere);
        _sphereInertia = sphere.ComputeInertia(1f);

        BuildGridLocked();
    }

    internal int BallEntityCount => _ballRenderIds.Count;

    /// <summary>Test probe: the Bepu body handle owned by a sphere render id.</summary>
    internal bool TryGetPhysicsBody(int renderId, out PhysicsBody body)
    {
        lock (_sync)
        {
            body = default;
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) return false;
            if (!_world.IsAlive(entity) || !_world.Has<PhysicsBody>(entity)) return false;
            body = _world.Get<PhysicsBody>(entity);
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

    /// <summary>Rebuilds the grid deterministically (bodies and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            foreach (var (_, entity) in _entityByRenderId)
            {
                if (!_world.IsAlive(entity)) continue;
                if (_world.Has<PhysicsBody>(entity))
                    _simulation.Bodies.Remove(_world.Get<PhysicsBody>(entity).Handle);
                _world.Destroy(entity);
            }

            _entityByRenderId.Clear();
            _ballRenderIds.Clear();
            _seq = 0;
            BuildGridLocked();
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

            SyncPhysicsPosesLocked();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildGridLocked()
    {
        var ballDescription = BodyDescription.CreateDynamic(
            RigidPose.Identity, _sphereInertia, _sphereShape, 1e-2f);

        for (var i = 0; i < GridColumns; i++)
        {
            for (var j = 0; j < GridRows; j++)
            {
                // Upstream layout (scaled to the reduced grid): higher spring frequency along
                // +x, higher damping ratio along +z.
                ballDescription.Pose.Position = new Vector3(
                    i * 3f - (GridColumns - 1) * 3f / 2f,
                    100f,
                    j * 3f - GridRows * 3f * 0.75f);

                var handle = _simulation.Bodies.Add(ballDescription);
                _materials.Allocate(handle) = new SimpleMaterial
                {
                    FrictionCoefficient = 1f,
                    MaximumRecoveryVelocity = float.MaxValue,
                    SpringSettings = new SpringSettings(5f + 0.25f * i, j * j / (float)(GridRows * GridRows) * 1.6f),
                };

                var renderId = BallRenderIdBase + _ballRenderIds.Count;
                _ballRenderIds.Add(renderId);
                _entityByRenderId[renderId] = _world.Create(
                    new Position3(ballDescription.Pose.Position.X, ballDescription.Pose.Position.Y, ballDescription.Pose.Position.Z),
                    Rotation3.Identity,
                    Scale3.One,
                    new RenderId(renderId),
                    new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
                    new PhysicsBody(handle));
            }
        }

        _materials.Allocate(_simulation.Statics.Add(new StaticDescription(
                new Vector3(0f, -FloorHalfThickness, 0f),
                _simulation.Shapes.Add(new Box(FloorRenderScaleX, FloorRenderScaleY, FloorRenderScaleX))))) =
            new SimpleMaterial
            {
                FrictionCoefficient = 1f,
                MaximumRecoveryVelocity = 2f,
                SpringSettings = new SpringSettings(30f, 1f),
            };
    }

    private void SyncPhysicsPosesLocked()
    {
        foreach (var (_, entity) in _entityByRenderId)
        {
            if (!_world.IsAlive(entity) || !_world.Has<PhysicsBody>(entity)) continue;

            var body = _simulation.Bodies[_world.Get<PhysicsBody>(entity).Handle];
            var position = body.Pose.Position;
            var orientation = body.Pose.Orientation;

            ref var pos = ref _world.Get<Position3>(entity);
            pos.X = position.X;
            pos.Y = position.Y;
            pos.Z = position.Z;

            ref var rot = ref _world.Get<Rotation3>(entity);
            rot.Qx = orientation.X;
            rot.Qy = orientation.Y;
            rot.Qz = orientation.Z;
            rot.Qw = orientation.W;
        }
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        _seq++;
        var states = new List<Transform3DState>(1 + _ballRenderIds.Count);

        states.Add(new Transform3DState(
            FloorRenderId, 0d, -FloorHalfThickness, 0d, 0d, 0d, 0d, 1d,
            FloorRenderScaleX, FloorRenderScaleY, FloorRenderScaleX, EntityLifecycle3.Active));

        var diameter = BallRadius * 2f;
        foreach (var renderId in _ballRenderIds)
        {
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) continue;
            if (!_world.IsAlive(entity)) continue;

            var position = _world.Get<Position3>(entity);
            var rotation = _world.Get<Rotation3>(entity);
            states.Add(new Transform3DState(
                renderId,
                position.X, position.Y, position.Z,
                rotation.Qx, rotation.Qy, rotation.Qz, rotation.Qw,
                diameter, diameter, diameter,
                EntityLifecycle3.Active));
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
