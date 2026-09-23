using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using Bonobo.ECS.Systems;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Inputs;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>SimpleSelfContainedDemo</c> (BepuPhysics2, Apache-2.0): one
///     dynamic sphere on a huge static box floor, gravity only, no shared demo types. The port
///     keeps the upstream setup and the deterministic null-dispatcher solve, and adds the
///     test-bed scaffolding this repo reviews:
///       * the sphere is a BonoboECS entity carrying its <see cref="PhysicsBody"/> handle and
///         mirrored pose components;
///       * click/tap fires a projectile ball through the input ring (<see cref="IFireBallSink"/>);
///       * <c>spawn-ball</c> / <c>reset</c> payload-free verbs;
///       * pure-ECS orbit markers (no physics body) driven by a generated <c>[Query]</c> system,
///         proving the ECS-only path alongside the Bepu-authoritative path.
///
///     Render-id ranges (the scene maps ids to meshes):
///       0                  static floor
///       100 .. 100+MaxBalls    dynamic spheres (Bepu-authoritative)
///       10_000 + n            orbit markers (ECS-authoritative, no physics body)
/// </summary>
public sealed class SimpleSelfContainedDemo : IDemoSimulation, IFireBallSink, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int BallRenderIdBase = 100;
    public const int MarkerRenderIdBase = 10_000;

    public const int MaxBalls = 64;
    public const int MarkerCount = 6;
    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + MaxBalls + MarkerCount) * SignalBufferLayout.Transform3DStride;

    public const float FloorHalfExtent = 250f;
    public const float FloorThickness = 1f;
    public const float BallRadius = 1f;
    public const float BallMass = 1f;
    public const float BallSpeed = 30f;
    public const float MarkerRadius = 0.5f;
    private const float SleepThreshold = 0.01f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "simple-self-contained",
        "simple-self-contained",
        BufferCapacity,
        static (config, transport) => new SimpleSelfContainedDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Group<double> _systems;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly TypedIndex _sphereShape;
    private readonly BodyInertia _sphereInertia;

    private readonly Dictionary<int, Entity> _entityByRenderId = new();
    private readonly List<int> _ballRenderIds = new();

    private int _nextBallRenderId;
    private long _seq;
    private double _elapsedSeconds;

    public SimpleSelfContainedDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        var world = config ?? DemoWorldConfig.Default;
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new DemoPoseIntegratorCallbacks(new Vector3((float)world.GravityX, (float)world.GravityY, (float)world.GravityZ)),
            new SolveDescription(8, 1));

        var sphere = new Sphere(BallRadius);
        _sphereShape = _simulation.Shapes.Add(sphere);
        _sphereInertia = sphere.ComputeInertia(BallMass);

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, 0f, 0f),
            _simulation.Shapes.Add(new Box(FloorHalfExtent * 2f, FloorThickness, FloorHalfExtent * 2f))));

        SpawnBallLocked(0f, 5f, 0f);

        var orbitSystem = new OrbitSystem(_world);
        _systems = new Group<double>("SimpleSelfContained", orbitSystem);
        _systems.Initialize();
        SpawnMarkers();
    }

    /// <summary>Live projectile/entity balls (excluding orbit markers).</summary>
    internal int BallCount
    {
        get
        {
            lock (_sync) return _ballRenderIds.Count;
        }
    }

    /// <summary>Test probe: the Bepu body handle owned by a ball render id.</summary>
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

    /// <summary>Test probe: world-space position of an ECS entity by render id.</summary>
    internal bool TryGetPosition(int renderId, out Position3 position)
    {
        lock (_sync)
        {
            position = default;
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) return false;
            if (!_world.IsAlive(entity) || !_world.Has<Position3>(entity)) return false;
            position = _world.Get<Position3>(entity);
            return true;
        }
    }

    /// <summary>Queues one extra ball when the cap allows it.</summary>
    public bool SpawnBall()
    {
        lock (_sync)
        {
            if (_ballRenderIds.Count >= MaxBalls) return false;
            var index = _ballRenderIds.Count + 1;
            SpawnBallLocked(2f + index * 0.75f, 6f + index * 0.5f, 0f);
            return true;
        }
    }

    /// <summary>Routes one decoded fire-ball packet (generated dispatcher → active simulation).</summary>
    public void OnFireBall(in FireBallInput input)
    {
        var dx = input.DirectionX;
        var dy = input.DirectionY;
        var dz = input.DirectionZ;
        var lengthSquared = dx * dx + dy * dy + dz * dz;
        if (lengthSquared < 1e-6d) return;

        var inverseLength = 1d / Math.Sqrt(lengthSquared);
        dx *= inverseLength;
        dy *= inverseLength;
        dz *= inverseLength;

        lock (_sync)
        {
            if (_ballRenderIds.Count >= MaxBalls)
                RemoveBallLocked(_ballRenderIds[0]);

            var renderId = BallRenderIdBase + _nextBallRenderId++;
            var position = new Vector3(
                (float)(input.OriginX + dx * 2d),
                (float)(input.OriginY + dy * 2d),
                (float)(input.OriginZ + dz * 2d));
            var velocity = new BodyVelocity(new Vector3(
                (float)(dx * BallSpeed), (float)(dy * BallSpeed), (float)(dz * BallSpeed)));

            var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position), velocity, _sphereInertia, _sphereShape, SleepThreshold));

            RegisterBallLocked(renderId, handle, position);
        }
    }

    /// <summary>Payload-free verbs exposed through the generic host command path.</summary>
    public bool TryCommand(string verb)
    {
        switch (verb)
        {
            case "spawn-ball":
                return SpawnBall();
            case "reset":
                Reset();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the scene deterministically: disposes every body and respawns the fixture.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            foreach (var entity in EnumerateEntitiesLocked())
            {
                if (!_world.IsAlive(entity)) continue;
                if (_world.Has<PhysicsBody>(entity))
                    _simulation.Bodies.Remove(_world.Get<PhysicsBody>(entity).Handle);
                _world.Destroy(entity);
            }

            _entityByRenderId.Clear();
            _ballRenderIds.Clear();
            _nextBallRenderId = 0;
            _elapsedSeconds = 0d;
            _seq = 0;

            SpawnBallLocked(0f, 5f, 0f);
            SpawnMarkers();
        }
    }

    /// <summary>
    ///     Advances the authoritative physics world one fixed step, mirrors Bepu poses into the
    ///     ECS components, runs the ECS-only systems and pushes the batched transform signal.
    /// </summary>
    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            // Deterministic single-threaded solve: never pass a ThreadDispatcher.
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _elapsedSeconds += deltaSeconds;

            SyncPhysicsPosesLocked();
            _systems.BeforeUpdate(in deltaSeconds);
            _systems.Update(in deltaSeconds);
            _systems.AfterUpdate(in deltaSeconds);

            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void SpawnBallLocked(float x, float y, float z)
    {
        var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(new Vector3(x, y, z)), _sphereInertia, _sphereShape, SleepThreshold));
        RegisterBallLocked(BallRenderIdBase + _nextBallRenderId++, handle, new Vector3(x, y, z));
    }

    private void RegisterBallLocked(int renderId, BodyHandle handle, Vector3 position)
    {
        var entity = _world.Create(
            new Position3(position.X, position.Y, position.Z),
            Rotation3.Identity,
            Scale3.One,
            new RenderId(renderId),
            new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
            new PhysicsBody(handle));

        _entityByRenderId[renderId] = entity;
        _ballRenderIds.Add(renderId);
    }

    private void RemoveBallLocked(int renderId)
    {
        if (!_entityByRenderId.TryGetValue(renderId, out var entity)) return;

        if (_world.IsAlive(entity) && _world.Has<PhysicsBody>(entity))
            _simulation.Bodies.Remove(_world.Get<PhysicsBody>(entity).Handle);
        if (_world.IsAlive(entity))
            _world.Destroy(entity);

        _entityByRenderId.Remove(renderId);
        _ballRenderIds.Remove(renderId);
    }

    /// <summary>
    ///     ECS-only orbit markers: no Bepu body, no collision — the generated <see cref="OrbitSystem"/>
    ///     <c>[Query]</c> drives their position each step. Proves the ECS-authoritative path
    ///     reaches the same render signal as the physics path.
    /// </summary>
    private void SpawnMarkers()
    {
        for (var i = 0; i < MarkerCount; i++)
        {
            var renderId = MarkerRenderIdBase + i;
            var radius = 6f + i * 1.5f;
            var angle = i * (MathF.Tau / MarkerCount);
            var entity = _world.Create(
                new Position3(MathF.Cos(angle) * radius, 2f + i * 0.4f, MathF.Sin(angle) * radius),
                Rotation3.Identity,
                new Scale3(1f, 1f, 1f),
                new RenderId(renderId),
                new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
                new OrbitState(radius, 2f + i * 0.4f, 0.6f + i * 0.15f, angle));
            _entityByRenderId[renderId] = entity;
        }
    }

    private IEnumerable<Entity> EnumerateEntitiesLocked()
    {
        var entities = new Entity[_world.Size];
        _world.GetEntities(new QueryDescription(), entities.AsSpan());
        return entities;
    }

    /// <summary>Mirrors authoritative Bepu poses into the ECS components read by the signal encoder.</summary>
    private void SyncPhysicsPosesLocked()
    {
        foreach (var (renderId, entity) in _entityByRenderId)
        {
            if (renderId >= MarkerRenderIdBase) continue;
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

        var states = new List<Transform3DState>(1 + _ballRenderIds.Count + MarkerCount);

        // Static floor: stable reserved id, identity rotation, full-extent scale.
        states.Add(new Transform3DState(
            FloorRenderId, 0d, 0d, 0d, 0d, 0d, 0d, 1d,
            FloorHalfExtent * 2d, FloorThickness, FloorHalfExtent * 2d, EntityLifecycle3.Active));

        foreach (var renderId in _ballRenderIds)
        {
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) continue;
            if (!_world.IsAlive(entity)) continue;
            AddEntityStateLocked(states, entity, renderId);
        }

        for (var i = 0; i < MarkerCount; i++)
        {
            var renderId = MarkerRenderIdBase + i;
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) continue;
            if (!_world.IsAlive(entity)) continue;
            AddEntityStateLocked(states, entity, renderId);
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    private void AddEntityStateLocked(List<Transform3DState> states, Entity entity, int renderId)
    {
        var position = _world.Get<Position3>(entity);
        var rotation = _world.Has<Rotation3>(entity) ? _world.Get<Rotation3>(entity) : Rotation3.Identity;
        var scale = _world.Has<Scale3>(entity) ? _world.Get<Scale3>(entity) : Scale3.One;
        var lifecycle = _world.Has<RenderLifecycle3>(entity)
            ? _world.Get<RenderLifecycle3>(entity).State
            : EntityLifecycle3.Active;

        // Unit-sphere mesh convention: the scene scales by the record's Sx/Sy/Sz.
        var diameter = renderId >= MarkerRenderIdBase ? MarkerRadius * 2f : BallRadius * 2f;
        var sx = scale.X * diameter;
        var sy = scale.Y * diameter;
        var sz = scale.Z * diameter;

        states.Add(new Transform3DState(
            renderId,
            position.X, position.Y, position.Z,
            rotation.Qx, rotation.Qy, rotation.Qz, rotation.Qw,
            sx, sy, sz,
            lifecycle));
    }

    public void Dispose()
    {
        _systems.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
