using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.Bepuphysics2.Trees;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>RayCastingDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby):
///     16384 rays per source are cast against a 16×16×16 collidable cloud every step and
///     drawn as colored line segments (hit = shaded green + yellow normal, miss = dark red).
///     Three sources cycle (random / frustum / wall) and the whole ray set rotates around Y.
///
///     Test-bed deviations (documented in docs/compat-review.md): the upstream
///     batched-vs-unbatched <c>SimulationRayBatcher</c> timing comparison is dropped (the
///     package exposes no public batcher constructor and there is no text-overlay channel);
///     the zero-radius upstream capsule is kept in the simulation but rendered with a small
///     baked radius; convex-hull collidables are rendered as unit boxes (no hull mesh client
///     side).
///
///     Render-id ranges: 0 = deformed plane static, 1000+ = cloud boxes, 10000+ = cloud
///     capsules, 20000+ = cloud spheres, 30000+ = cloud cylinders, 40000+ = cloud convex
///     hulls (rendered as unit boxes), 100000+ = ray line segments (2 per hit, 1 per miss).
/// </summary>
public sealed class RayCastingDemo : IDemoSimulation, IDemoCommands
{
    public const int PlaneRenderId = 0;
    public const int BoxRenderIdBase = 1_000;
    public const int CapsuleRenderIdBase = 10_000;
    public const int SphereRenderIdBase = 20_000;
    public const int CylinderRenderIdBase = 30_000;
    public const int HullRenderIdBase = 40_000;
    public const int RayLineRenderIdBase = 100_000;

    public const int GridWidth = 16;
    public const int GridHeight = 16;
    public const int GridLength = 16;
    public const int GridCount = GridWidth * GridHeight * GridLength;

    public const int RandomRayCount = 1 << 14;
    public const int FrustumRayWidth = 128;
    public const int FrustumRayHeight = 128;
    public const int FrustumRayCount = FrustumRayWidth * FrustumRayHeight;
    public const int WallRayWidth = 128;
    public const int WallRayHeight = 128;
    public const int WallRayCount = WallRayWidth * WallRayHeight;
    public const int MaxRayCount = RandomRayCount;
    public const int MaxLineCount = MaxRayCount * 2;

    public const int MaxTransformCount = 1 + GridCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength
        + MaxTransformCount * SignalBufferLayout.Transform3DStride
        + MaxLineCount * SignalBufferLayout.LineStateStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "ray-casting",
        "ray-casting",
        BufferCapacity,
        static (config, transport) => new RayCastingDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>All-false narrow phase: the demo only exercises ray queries.</summary>
    public struct NoCollisionCallbacks : INarrowPhaseCallbacks
    {
        public void Initialize(Simulation simulation)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            pairMaterial = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => false;

        public void Dispose()
        {
        }
    }

    private struct TestRay
    {
        public Vector3 Origin;
        public Vector3 Direction;
        public float MaximumT;
    }

    private struct RayHit
    {
        public Vector3 Normal;
        public float T;
        public bool Hit;
    }

    /// <summary>Single-threaded hit collector: keeps the closest hit per ray id.</summary>
    private struct HitHandler : IRayHitHandler
    {
        public RayHit[] Hits;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal, CollidableReference collidable, int childIndex)
        {
            maximumT = t;
            ref var hit = ref Hits[ray.Id];
            if (t < hit.T)
            {
                hit.Normal = normal;
                hit.T = t;
                hit.Hit = true;
            }
        }
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;

    private readonly TestRay[] _randomRays = new TestRay[RandomRayCount];
    private readonly TestRay[] _frustumRays = new TestRay[FrustumRayCount];
    private readonly TestRay[] _wallRays = new TestRay[WallRayCount];
    private readonly TestRay[] _testRays = new TestRay[MaxRayCount];
    private readonly RayHit[] _hits = new RayHit[MaxRayCount];

    private int _testRayCount;
    private int _raySourceIndex = 1;
    private int _frameCount;
    private float _rotation;
    private bool _shouldCycle = true;
    private bool _shouldRotate = true;
    private long _seq;

    public RayCastingDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new NoCollisionCallbacks(),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
    }

    /// <summary>Payload-free verbs exposed through the generic host command path.</summary>
    public bool TryCommand(string verb)
    {
        lock (_sync)
        {
            switch (verb)
            {
                case "cycle":
                    _shouldCycle = !_shouldCycle;
                    return true;
                case "rotate":
                    _shouldRotate = !_shouldRotate;
                    return true;
                case "reset-rotation":
                    _rotation = 0f;
                    return true;
                case "source-random":
                    _shouldCycle = false;
                    _raySourceIndex = 1;
                    return true;
                case "source-frustum":
                    _shouldCycle = false;
                    _raySourceIndex = 2;
                    return true;
                case "source-wall":
                    _shouldCycle = false;
                    _raySourceIndex = 3;
                    return true;
                default:
                    return false;
            }
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

            UpdateRaysLocked((float)deltaSeconds);
            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildSceneLocked()
    {
        var random = new Random(5);

        // Convex hull collidable (upstream draws 16 random points in the unit cube).
        const int pointCount = 16;
        var points = new QuickList<Vector3>(pointCount, _bufferPool);
        for (var i = 0; i < pointCount; ++i)
        {
            points.AllocateUnsafely() = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
        }

        var hull = new ConvexHull(points.Span.Slice(0, points.Count), _bufferPool, out _);

        var sphereIndex = _simulation.Shapes.Add(new Sphere(0.5f));
        // Upstream uses a zero-radius capsule; keep the collidable but render it with a small
        // baked radius (the client capsule mesh cannot be built from radius 0).
        var capsuleIndex = _simulation.Shapes.Add(new Capsule(0f, 0.5f));
        var boxIndex = _simulation.Shapes.Add(new Box(0.5f, 1.5f, 1f));
        var cylinderIndex = _simulation.Shapes.Add(new Cylinder(0.5f, 1f));
        var hullIndex = _simulation.Shapes.Add(hull);

        var spacing = new Vector3(2.01f);
        const float randomizationSubset = 0.9f;
        var randomizationSpan = (spacing - new Vector3(1f)) * randomizationSubset;
        var randomizationBase = randomizationSpan * -0.5f;

        var boxCount = 0;
        var capsuleCount = 0;
        var sphereCount = 0;
        var cylinderCount = 0;
        var hullCount = 0;

        for (var i = 0; i < GridWidth; ++i)
        {
            for (var j = 0; j < GridHeight; ++j)
            {
                for (var k = 0; k < GridLength; ++k)
                {
                    var r = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
                    var location = spacing * (new Vector3(i, j, k) + new Vector3(-GridWidth, -GridHeight, -GridLength) * 0.5f)
                                   + randomizationBase + r * randomizationSpan;

                    Quaternion orientation;
                    orientation.X = -1f + 2f * random.NextSingle();
                    orientation.Y = -1f + 2f * random.NextSingle();
                    orientation.Z = -1f + 2f * random.NextSingle();
                    orientation.W = 0.01f + random.NextSingle();
                    QuaternionEx.Normalize(ref orientation);

                    var shapeIndex = ((i + j + k) % 5) switch
                    {
                        0 => boxIndex,
                        1 => capsuleIndex,
                        2 => sphereIndex,
                        3 => cylinderIndex,
                        _ => hullIndex,
                    };
                    // Render scale carries the full shape dimensions (Bonobo box/cylinder
                    // constructors take full width/height/length; capsules bake their aspect).
                    var (scale, renderId) = ((i + j + k) % 5) switch
                    {
                        0 => (new Vector3(0.5f, 1.5f, 1f), BoxRenderIdBase + boxCount++),
                        1 => (Vector3.One, CapsuleRenderIdBase + capsuleCount++),
                        2 => (Vector3.One, SphereRenderIdBase + sphereCount++),
                        3 => (new Vector3(1f, 1f, 1f), CylinderRenderIdBase + cylinderCount++),
                        _ => (Vector3.One, HullRenderIdBase + hullCount++),
                    };

                    if ((i + j + k) % 2 == 1)
                    {
                        var handle = _simulation.Bodies.Add(BodyDescription.CreateKinematic((location, orientation), shapeIndex, -0.1f));
                        _poses.AddDynamic(renderId, handle, scale);
                    }
                    else
                    {
                        _simulation.Statics.Add(new StaticDescription(location, orientation, shapeIndex));
                        _poses.AddStatic(renderId, location, orientation, scale);
                    }
                }
            }
        }

        const int planeWidth = 128;
        const int planeHeight = 128;
        var planeMesh = DemoMeshHelper.CreateDeformedPlane(planeWidth, planeHeight,
            static (x, y) => new Vector3(x - planeWidth / 2, MathF.Cos(x / 4f) * MathF.Sin(y / 4f), y - planeHeight / 2),
            new Vector3(1f, 3f, 1f), _bufferPool);
        var planeOrientation = QuaternionEx.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 4f);
        var planePosition = new Vector3(0f, -10f, 0f);
        _simulation.Statics.Add(new StaticDescription(planePosition, planeOrientation, _simulation.Shapes.Add(planeMesh)));
        _poses.AddStatic(PlaneRenderId, planePosition, planeOrientation, new Vector3(planeWidth, 1f, planeHeight));

        BuildRaySources(random);
    }

    private void BuildRaySources(Random random)
    {
        // Random rays spewed from inside the shape cloud.
        for (var i = 0; i < RandomRayCount; ++i)
        {
            _ = GetDirection(random); // upstream consumes one direction here; keep the sequence
            var originScale = (float)Math.Sqrt(random.NextDouble());
            _randomRays[i] = new TestRay
            {
                Origin = originScale * GetDirection(random) * GridWidth * 0.25f * 2.01f,
                Direction = GetDirection(random),
                MaximumT = 50f,
            };
        }

        // Rays matching a planar projection (frustum).
        const float aspectRatio = 1.6f;
        var verticalFov = MathHelper.Pi * 0.16f;
        var unitZScreenHeight = 2f * MathF.Tan(verticalFov / 2f);
        var unitZScreenWidth = unitZScreenHeight * aspectRatio;
        var unitZSpacing = new Vector2(unitZScreenWidth / FrustumRayWidth, unitZScreenHeight / FrustumRayHeight);
        var unitZBase = (unitZSpacing - new Vector2(unitZScreenWidth, unitZScreenHeight)) * 0.5f;
        var frustumOrigin = new Vector3(0f, 0f, -50f);
        for (var i = 0; i < FrustumRayWidth; ++i)
        {
            for (var j = 0; j < FrustumRayHeight; ++j)
            {
                var direction = new Vector3(unitZBase.X + i * unitZSpacing.X, unitZBase.Y + j * unitZSpacing.Y, 1f);
                _frustumRays[i * FrustumRayHeight + j] = new TestRay
                {
                    Origin = frustumOrigin + direction * 10f,
                    Direction = direction,
                    MaximumT = 100f,
                };
            }
        }

        // Rays matching an orthographic projection (wall).
        var wallOrigin = new Vector3(0f, 0f, -50f);
        var wallSpacing = new Vector2(0.1f);
        var wallBase = 0.5f * (wallSpacing - wallSpacing * new Vector2(WallRayWidth, WallRayHeight));
        for (var i = 0; i < WallRayWidth; ++i)
        {
            for (var j = 0; j < WallRayHeight; ++j)
            {
                _wallRays[i * WallRayHeight + j] = new TestRay
                {
                    Origin = wallOrigin + new Vector3(wallBase.X + wallSpacing.X * i, wallBase.Y + wallSpacing.Y * j, 0f),
                    Direction = new Vector3(0f, 0f, 1f),
                    MaximumT = 100f,
                };
            }
        }
    }

    private static Vector3 GetDirection(Random random)
    {
        Vector3 direction;
        float length;
        do
        {
            direction = 2f * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) - Vector3.One;
            length = direction.Length();
        }
        while (length < 1e-7f);

        direction /= length;
        return direction;
    }

    private void UpdateRaysLocked(float deltaSeconds)
    {
        ++_frameCount;
        if (_frameCount > 1 << 20) _frameCount = 0;

        if (_shouldRotate) _rotation += MathF.PI * 1e-2f * deltaSeconds % (2f * MathF.PI);
        if (_shouldCycle) _raySourceIndex = 1 + _frameCount / 256 % 3;

        var (source, count) = _raySourceIndex switch
        {
            1 => (_randomRays, RandomRayCount),
            2 => (_frustumRays, FrustumRayCount),
            _ => (_wallRays, WallRayCount),
        };

        _testRayCount = count;
        var transform = Matrix3x3.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), _rotation);
        for (var i = 0; i < count; ++i)
        {
            Matrix3x3.Transform(source[i].Origin, transform, out _testRays[i].Origin);
            Matrix3x3.Transform(source[i].Direction, transform, out _testRays[i].Direction);
            _testRays[i].MaximumT = source[i].MaximumT;
        }

        for (var i = 0; i < MaxRayCount; ++i)
        {
            _hits[i].T = float.MaxValue;
            _hits[i].Hit = false;
        }

        var handler = new HitHandler { Hits = _hits };
        for (var i = 0; i < _testRayCount; ++i)
        {
            ref var ray = ref _testRays[i];
            _simulation.RayCast(ray.Origin, ray.Direction, ray.MaximumT, _bufferPool, ref handler, i);
        }
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;

        var states = new List<Transform3DState>(MaxTransformCount);
        _poses.Emit(states);

        var lines = new List<LineState>(_testRayCount);
        for (var i = 0; i < _testRayCount; ++i)
        {
            ref var result = ref _hits[i];
            ref var ray = ref _testRays[i];
            if (result.Hit)
            {
                var end = ray.Origin + ray.Direction * result.T;
                var diffuseLight = Vector3.Dot(result.Normal, new Vector3(0.57735f));
                if (diffuseLight < 0) diffuseLight = -0.5f * diffuseLight;
                var shade = 0.2f + 0.8f * diffuseLight;
                lines.Add(new LineState(
                    RayLineRenderIdBase + i * 2,
                    ray.Origin.X, ray.Origin.Y, ray.Origin.Z,
                    end.X, end.Y, end.Z,
                    0d, shade, 0d, 1d));
                var normalEnd = end + result.Normal;
                lines.Add(new LineState(
                    RayLineRenderIdBase + i * 2 + 1,
                    end.X, end.Y, end.Z,
                    normalEnd.X, normalEnd.Y, normalEnd.Z,
                    1d, 1d, 0d, 1d));
            }
            else
            {
                var end = ray.Origin + ray.Direction * ray.MaximumT;
                lines.Add(new LineState(
                    RayLineRenderIdBase + i * 2,
                    ray.Origin.X, ray.Origin.Y, ray.Origin.Z,
                    end.X, end.Y, end.Z,
                    0.25d, 0d, 0d, 1d));
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
