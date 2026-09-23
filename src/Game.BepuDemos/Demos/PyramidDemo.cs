using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Inputs;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>PyramidDemo</c> (BepuPhysics2, Apache-2.0): a row of box
///     pyramids and a click-launched sphere cannonball with randomized radius.
///
///     Test-bed deviations (documented in docs/compat-review.md):
///       * <see cref="PyramidCount"/> is reduced from 40 to 12 to keep the desktop test host
///         comfortably interactive; the per-pyramid geometry is the upstream 20-row stack.
///       * the upstream Z-key launch is the click/tap fire-ball input path.
///
///     Render-id ranges: 0 = static floor, 100+ = pyramid boxes, 10_000+ = projectiles.
/// </summary>
public sealed class PyramidDemo : IDemoSimulation, IFireBallSink, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int BoxRenderIdBase = 100;
    public const int BallRenderIdBase = 10_000;

    public const int PyramidCount = 12;
    public const int RowCount = 20;
    public const int BoxesPerPyramid = RowCount * (RowCount + 1) / 2;
    public const int MaxProjectiles = 64;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength
        + (1 + PyramidCount * BoxesPerPyramid + MaxProjectiles) * SignalBufferLayout.Transform3DStride;

    public const float BoxSize = 1f;
    public const float FloorRenderScale = 2500f;
    public const float ProjectileSpeed = 150f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "pyramid",
        "pyramid",
        BufferCapacity,
        static (config, transport) => new PyramidDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly TypedIndex _boxShape;
    private readonly BodyInertia _boxInertia;
    private readonly TypedIndex _sphereShape;
    private readonly Dictionary<int, Entity> _entityByRenderId = new();
    private readonly List<int> _boxRenderIds = new();
    private readonly List<Projectile> _projectiles = new();

    private readonly Random _random = new(5);
    private int _nextProjectileId;
    private long _seq;

    private readonly record struct Projectile(int RenderId, BodyHandle Handle, float Radius);

    public PyramidDemo(
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

        var box = new Box(BoxSize, BoxSize, BoxSize);
        _boxShape = _simulation.Shapes.Add(box);
        _boxInertia = box.ComputeInertia(1f);
        _sphereShape = _simulation.Shapes.Add(new Sphere(1f));

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f),
            _simulation.Shapes.Add(new Box(FloorRenderScale, 1f, FloorRenderScale))));

        BuildPyramidsLocked();
    }

    internal int BoxCount => _boxRenderIds.Count;

    internal int ProjectileCount
    {
        get
        {
            lock (_sync) return _projectiles.Count;
        }
    }

    /// <summary>Test probe: the Bepu body handle owned by a pyramid box render id.</summary>
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

    /// <summary>Upstream Z-key cannonball: radius 0.5..5.5 from the demo's seeded generator.</summary>
    public void FireRandomProjectile(float originX, float originY, float originZ, float dirX, float dirY, float dirZ)
    {
        var lengthSquared = dirX * dirX + dirY * dirY + dirZ * dirZ;
        if (lengthSquared < 1e-6f) return;
        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        dirX *= inverseLength;
        dirY *= inverseLength;
        dirZ *= inverseLength;

        lock (_sync)
        {
            if (_projectiles.Count >= MaxProjectiles)
            {
                var oldest = _projectiles[0];
                _projectiles.RemoveAt(0);
                RemoveProjectileLocked(oldest);
            }

            var radius = 0.5f + 5f * _random.NextSingle();
            var shape = new Sphere(radius);
            var shapeIndex = _simulation.Shapes.Add(shape);
            var position = new Vector3(
                originX + dirX * 2f, originY + dirY * 2f, originZ + dirZ * 2f);
            var velocity = new Vector3(dirX * ProjectileSpeed, dirY * ProjectileSpeed, dirZ * ProjectileSpeed);
            var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position), velocity, shape.ComputeInertia(1f), shapeIndex, 0.01f));

            _projectiles.Add(new Projectile(BallRenderIdBase + _nextProjectileId++, handle, radius));
        }
    }

    /// <summary>Routes one decoded fire-ball packet (generated dispatcher → active simulation).</summary>
    public void OnFireBall(in FireBallInput input) =>
        FireRandomProjectile(
            (float)input.OriginX, (float)input.OriginY, (float)input.OriginZ,
            (float)input.DirectionX, (float)input.DirectionY, (float)input.DirectionZ);

    /// <summary>Payload-free verbs exposed through the generic host command path.</summary>
    public bool TryCommand(string verb)
    {
        switch (verb)
        {
            case "shoot":
                FireRandomProjectile(0f, 8f, -140f, 0f, 0f, 1f);
                return true;
            case "clear-projectiles":
                lock (_sync)
                {
                    foreach (var projectile in _projectiles)
                        RemoveProjectileLocked(projectile);
                    _projectiles.Clear();
                }

                return true;
            default:
                return false;
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

    private void BuildPyramidsLocked()
    {
        for (var pyramidIndex = 0; pyramidIndex < PyramidCount; pyramidIndex++)
        {
            for (var rowIndex = 0; rowIndex < RowCount; rowIndex++)
            {
                var columnCount = RowCount - rowIndex;
                for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
                {
                    var position = new Vector3(
                        (-columnCount * 0.5f + columnIndex) * BoxSize,
                        (rowIndex + 0.5f) * BoxSize,
                        (pyramidIndex - PyramidCount * 0.5f) * (BoxSize + 4f));

                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(position), _boxInertia, _boxShape, 0.01f));

                    var renderId = BoxRenderIdBase + _boxRenderIds.Count;
                    _boxRenderIds.Add(renderId);
                    _entityByRenderId[renderId] = _world.Create(
                        new Position3(position.X, position.Y, position.Z),
                        Rotation3.Identity,
                        Scale3.One,
                        new RenderId(renderId),
                        new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
                        new PhysicsBody(handle));
                }
            }
        }
    }

    private void RemoveProjectileLocked(Projectile projectile)
    {
        _simulation.Bodies.Remove(projectile.Handle);
    }

    private void SyncPhysicsPosesLocked()
    {
        foreach (var (renderId, entity) in _entityByRenderId)
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
        var states = new List<Transform3DState>(1 + _boxRenderIds.Count + _projectiles.Count);

        states.Add(new Transform3DState(
            FloorRenderId, 0d, -0.5d, 0d, 0d, 0d, 0d, 1d,
            FloorRenderScale, 1d, FloorRenderScale, EntityLifecycle3.Active));

        foreach (var renderId in _boxRenderIds)
        {
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) continue;
            if (!_world.IsAlive(entity)) continue;

            var position = _world.Get<Position3>(entity);
            var rotation = _world.Get<Rotation3>(entity);
            states.Add(new Transform3DState(
                renderId,
                position.X, position.Y, position.Z,
                rotation.Qx, rotation.Qy, rotation.Qz, rotation.Qw,
                BoxSize, BoxSize, BoxSize,
                EntityLifecycle3.Active));
        }

        foreach (var projectile in _projectiles)
        {
            var body = _simulation.Bodies[projectile.Handle];
            var position = body.Pose.Position;
            var orientation = body.Pose.Orientation;
            var diameter = projectile.Radius * 2f;
            states.Add(new Transform3DState(
                projectile.RenderId,
                position.X, position.Y, position.Z,
                orientation.X, orientation.Y, orientation.Z, orientation.W,
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
