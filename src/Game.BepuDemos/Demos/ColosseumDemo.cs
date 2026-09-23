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
using DemoEngine.Inputs;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>ColosseumDemo</c> (BepuPhysics2, Apache-2.0): concentric ring
///     walls and platforms stacked into a colosseum, hit by large purple hail (bullets) and
///     "super shootie patootie" spheres.
///
///     Test-bed deviations (documented in docs/compat-review.md):
///       * <see cref="LayerCount"/> is reduced from 6 to 3 (≈1857 boxes instead of ≈5000) so the
///         desktop test host stays interactive; ring geometry per layer is upstream.
///       * the upstream Z/X key shots are the click/tap fire-ball input path plus the
///         <c>shoot-big</c> verb.
///
///     Render-id ranges: 0 = static ground, 100+ = ring boxes, 100_000+ = bullets,
///     200_000+ = big shots.
/// </summary>
public sealed class ColosseumDemo : IDemoSimulation, IFireBallSink, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int BoxRenderIdBase = 100;
    public const int BulletRenderIdBase = 100_000;
    public const int BigShotRenderIdBase = 200_000;

    public const int LayerCount = 3;
    public const int MaxBullets = 64;
    public const int MaxBigShots = 16;
    public const int MaxBoxes = 2048;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength
        + (1 + MaxBoxes + MaxBullets + MaxBigShots) * SignalBufferLayout.Transform3DStride;

    public const float InnerRadius = 15f;
    public const float HeightPerPlatform = 3f;
    public const float RingSpacing = 0.5f;
    public const float GroundSize = 500f;

    public const float BulletSpeed = 400f;
    public const float BulletRadius = 0.5f;
    public const float BigShotSpeed = 100f;
    public const float BigShotRadius = 3f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "colosseum",
        "colosseum",
        BufferCapacity,
        static (config, transport) => new ColosseumDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly TypedIndex _ringShape;
    private readonly BodyInertia _ringInertia;
    private readonly TypedIndex _bulletShape;
    private readonly BodyInertia _bulletInertia;
    private readonly TypedIndex _bigShotShape;
    private readonly BodyInertia _bigShotInertia;

    private readonly Queue<int> _bulletRenderIds = new();
    private readonly Queue<int> _bigShotRenderIds = new();
    private int _nextBulletId;
    private int _nextBigShotId;
    private int _boxCount;
    private long _seq;

    public ColosseumDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30f, 1f)),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));

        var ringBox = new Box(0.5f, 1f, 3f);
        _ringShape = _simulation.Shapes.Add(ringBox);
        _ringInertia = ringBox.ComputeInertia(1f);

        var bullet = new Sphere(BulletRadius);
        _bulletShape = _simulation.Shapes.Add(bullet);
        _bulletInertia = bullet.ComputeInertia(0.1f);

        var bigShot = new Sphere(BigShotRadius);
        _bigShotShape = _simulation.Shapes.Add(bigShot);
        _bigShotInertia = bigShot.ComputeInertia(100f);

        _poses = new DemoPoseSet(_world, _simulation);
        BuildColosseumLocked();
    }

    internal int BoxCount => _boxCount;
    internal int ProjectileCount => _bulletRenderIds.Count + _bigShotRenderIds.Count;

    /// <summary>Test probe: the Bepu body handle owned by a ring-box render id.</summary>
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

    /// <summary>Upstream Z-key bullet: sphere 0.5, mass 0.1, speed 400.</summary>
    public void FireBullet(float originX, float originY, float originZ, float dirX, float dirY, float dirZ) =>
        FireProjectileLocked(BulletRadius, BulletSpeed, originX, originY, originZ, dirX, dirY, dirZ,
            _bulletShape, _bulletInertia, BulletRenderIdBase, MaxBullets, _bulletRenderIds, ref _nextBulletId);

    /// <summary>Routes one decoded fire-ball packet (host dispatcher → active simulation).</summary>
    public void OnFireBall(in FireBallInput input) =>
        FireBullet(
            (float)input.OriginX, (float)input.OriginY, (float)input.OriginZ,
            (float)input.DirectionX, (float)input.DirectionY, (float)input.DirectionZ);

    /// <summary>Payload-free verbs exposed through the generic host command path.</summary>
    public bool TryCommand(string verb)
    {
        switch (verb)
        {
            case "shoot-big":
                FireProjectileLocked(BigShotRadius, BigShotSpeed, 0f, 40f, -90f, 0f, 0f, 1f,
                    _bigShotShape, _bigShotInertia, BigShotRenderIdBase, MaxBigShots, _bigShotRenderIds, ref _nextBigShotId);
                return true;
            case "reset":
                Reset();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the colosseum deterministically (bodies and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _bulletRenderIds.Clear();
            _bigShotRenderIds.Clear();
            _boxCount = 0;
            _seq = 0;
            BuildColosseumLocked();
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

    private void FireProjectileLocked(
        float radius, float speed, float originX, float originY, float originZ,
        float dirX, float dirY, float dirZ,
        TypedIndex shape, BodyInertia inertia, int renderIdBase, int maxCount,
        Queue<int> liveIds, ref int nextId)
    {
        var lengthSquared = dirX * dirX + dirY * dirY + dirZ * dirZ;
        if (lengthSquared < 1e-6f) return;
        var inverseLength = 1f / MathF.Sqrt(lengthSquared);
        dirX *= inverseLength;
        dirY *= inverseLength;
        dirZ *= inverseLength;

        lock (_sync)
        {
            if (liveIds.Count >= maxCount)
            {
                var oldest = liveIds.Dequeue();
                _poses.Remove(oldest, removeBody: true);
            }

            var position = new Vector3(originX + dirX * 2f, originY + dirY * 2f, originZ + dirZ * 2f);
            var velocity = new Vector3(dirX * speed, dirY * speed, dirZ * speed);
            var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position), velocity, inertia, shape, 0.01f));

            var renderId = renderIdBase + nextId++;
            liveIds.Enqueue(renderId);
            _poses.AddDynamic(renderId, handle, new Vector3(radius * 2f));
        }
    }

    private void BuildColosseumLocked()
    {
        var layerPosition = Vector3.Zero;
        for (var layerIndex = 0; layerIndex < LayerCount; ++layerIndex)
        {
            var ringCount = LayerCount - layerIndex;
            for (var ringIndex = 0; ringIndex < ringCount; ++ringIndex)
            {
                CreateRingLocked(
                    layerPosition,
                    InnerRadius + ringIndex * (3f + RingSpacing) + layerIndex * (3f - 1f));
            }

            layerPosition.Y += 1f * (1f * HeightPerPlatform + 1f);
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f),
            _simulation.Shapes.Add(new Box(GroundSize, 1f, GroundSize))));

        _poses.AddStatic(
            GroundRenderId, new Vector3(0f, -0.5f, 0f), Quaternion.Identity,
            new Vector3(GroundSize, 1f, GroundSize));
    }

    private void CreateRingLocked(Vector3 position, float radius)
    {
        var halfLength = 1.5f;
        var halfWidth = 0.5f;
        var wallOffset = halfLength - halfWidth;
        CreateRingWallLocked(position, HeightPerPlatform, radius + wallOffset);
        CreateRingWallLocked(position, HeightPerPlatform, radius - wallOffset);
        CreateRingPlatformLocked(position + new Vector3(0f, HeightPerPlatform * 1f, 0f), radius);
    }

    private void CreateRingWallLocked(Vector3 position, float height, float radius)
    {
        var circumference = MathF.PI * 2 * radius;
        var boxCountPerRing = (int)(0.9f * circumference / 3f);
        var increment = MathHelper.TwoPi / boxCountPerRing;
        for (var ringIndex = 0; ringIndex < height; ringIndex++)
        {
            for (var i = 0; i < boxCountPerRing; i++)
            {
                var angle = ((ringIndex & 1) == 0 ? i + 0.5f : i) * increment;
                AddRingBoxLocked(
                    position + new Vector3(-MathF.Cos(angle) * radius, (ringIndex + 0.5f) * 1f, MathF.Sin(angle) * radius),
                    QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, angle));
            }
        }
    }

    private void CreateRingPlatformLocked(Vector3 position, float radius)
    {
        var innerCircumference = MathF.PI * 2 * (radius - 1.5f);
        var boxCount = (int)(0.95f * innerCircumference / 1f);
        var increment = MathHelper.TwoPi / boxCount;
        for (var i = 0; i < boxCount; i++)
        {
            var angle = i * increment;
            AddRingBoxLocked(
                position + new Vector3(-MathF.Cos(angle) * radius, 0.5f, MathF.Sin(angle) * radius),
                QuaternionEx.Concatenate(
                    QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f),
                    QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, angle + MathF.PI * 0.5f)));
        }
    }

    private void AddRingBoxLocked(Vector3 position, Quaternion orientation)
    {
        var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position, orientation), _ringInertia, _ringShape, 0.01f));
        _poses.AddDynamic(BoxRenderIdBase + _boxCount++, handle, new Vector3(0.5f, 1f, 3f));
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
