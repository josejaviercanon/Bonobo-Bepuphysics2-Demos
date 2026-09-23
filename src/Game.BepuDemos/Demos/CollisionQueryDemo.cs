using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>CollisionQueryDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby):
///     a 5×5 grid of shape queries collects broad phase overlaps and hands every candidate
///     pair to a <see cref="CollisionBatcher{TCallbacks}"/>; queries with a positive-depth
///     contact are routed to the green id range, the rest to the red range. 128 boxes fall
///     through the query grid and a deformed static plane sits underneath.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream raw-pointer
///     <c>AddDirectly</c>/<c>CacheShapeB</c>/<c>GetShapeData</c> path is replaced by the
///     managed <c>CollisionBatcher.Add(TypedIndex, TypedIndex, …)</c> overload (both the
///     candidate shape and the registered query box live in <c>Simulation.Shapes</c>);
///     color routing replaces the upstream per-shape render color.
///
///     Render-id ranges: 0 = ground static, 1 = deformed plane static, 100+ = falling boxes,
///     1000+ = touched queries (green), 2000+ = untouched queries (red).
/// </summary>
public sealed class CollisionQueryDemo : IDemoSimulation
{
    public const int GroundRenderId = 0;
    public const int PlaneRenderId = 1;
    public const int BoxRenderIdBase = 100;
    public const int TouchedQueryRenderIdBase = 1000;
    public const int UntouchedQueryRenderIdBase = 2000;

    public const int BoxCount = 128;
    public const int QueryGridWidth = 5;
    public const int QueryCount = QueryGridWidth * QueryGridWidth;

    public const int MaxTransformCount = 2 + BoxCount + QueryCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "collision-query",
        "collision-query",
        BufferCapacity,
        static (config, transport) => new CollisionQueryDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>Boolean contact test per query id (upstream `BatcherCallbacks`).</summary>
    public struct BatcherCallbacks : ICollisionCallbacks
    {
        /// <summary>Set to true for a pair id if a nonnegative depth was detected.</summary>
        public Buffer<bool> QueryWasTouched;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowCollisionTesting(int pairId, int childA, int childB) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnChildPairCompleted(int pairId, int childA, int childB, ref ConvexContactManifold manifold)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnPairCompleted<TManifold>(int pairId, ref TManifold manifold)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            for (var i = 0; i < manifold.Count; ++i)
            {
                if (manifold.GetDepth(i) >= 0)
                {
                    QueryWasTouched[pairId] = true;
                    break;
                }
            }
        }
    }

    /// <summary>Collects broad phase overlap candidates (upstream `BroadPhaseOverlapEnumerator`).</summary>
    private struct BroadPhaseOverlapEnumerator : IBreakableForEach<CollidableReference>
    {
        public QuickList<CollidableReference> References;
        public BufferPool Pool;

        public bool LoopBody(CollidableReference reference)
        {
            References.Allocate(Pool) = reference;
            return true;
        }
    }

    private struct Query
    {
        public RigidPose Pose;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly Query[] _queries = new Query[QueryCount];
    private readonly TypedIndex _boxShape;
    private readonly Buffer<bool> _queryWasTouched;
    private long _seq;

    /// <summary>Test probe: broad phase overlaps collected by the last query pass.</summary>
    internal int LastOverlapCount { get; private set; }

    /// <summary>Test probe: candidate pairs added to the collision batcher by the last pass.</summary>
    internal int LastAddedPairCount { get; private set; }

    public CollisionQueryDemo(
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

        var box = new Box(1f, 1f, 1f);
        _boxShape = _simulation.Shapes.Add(box);

        _bufferPool.Take<bool>(QueryCount, out _queryWasTouched);

        BuildSceneLocked(box);
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

    private void BuildSceneLocked(Box box)
    {
        // Ground.
        _simulation.Statics.Add(new StaticDescription(new Vector3(), _simulation.Shapes.Add(new Box(100f, 1f, 100f))));
        _poses.AddStatic(GroundRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(100f, 1f, 100f));

        // Falling boxes.
        var random = new Random(5);
        var boxInertia = box.ComputeInertia(1f);
        var description = BodyDescription.CreateDynamic(new Vector3(), boxInertia, _boxShape, 0.01f);
        for (var i = 0; i < BoxCount; ++i)
        {
            description.Pose.Position = new Vector3(
                -5f + 10f * random.NextSingle(),
                45f + 150f * random.NextSingle(),
                -5f + 10f * random.NextSingle());
            var handle = _simulation.Bodies.Add(description);
            _poses.AddDynamic(BoxRenderIdBase + i, handle, Vector3.One);
        }

        // Deformed static plane (client rebuilds the same surface for presentation).
        const int planeWidth = 20;
        const int planeHeight = 20;
        var planeMesh = DemoMeshHelper.CreateDeformedPlane(planeWidth, planeHeight,
            static (x, y) => new Vector3(x * 5f - 50f, 3f * MathF.Sin(x) * MathF.Sin(y), y * 5f - 50f),
            Vector3.One, _bufferPool);
        _simulation.Statics.Add(new StaticDescription(new Vector3(), _simulation.Shapes.Add(planeMesh)));
        _poses.AddStatic(PlaneRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(100f, 1f, 100f));

        // Query grid.
        var querySpacing = new Vector3(3f, 0f, 3f);
        var basePosition = new Vector3(0f, 2f, 0f) - new Vector3(QueryGridWidth - 1) * querySpacing * 0.5f;
        for (var i = 0; i < QueryGridWidth; ++i)
        {
            for (var j = 0; j < QueryGridWidth; ++j)
            {
                var pose = new RigidPose(
                    basePosition + querySpacing * new Vector3(i, 0f, j),
                    Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(i - 2.5f, 0f, j - 2.5f)), i * j + 0.7457f));
                box.ComputeBounds(pose.Orientation, out var boundsMin, out var boundsMax);
                _queries[i * QueryGridWidth + j] = new Query
                {
                    Pose = pose,
                    BoundsMin = boundsMin + pose.Position,
                    BoundsMax = boundsMax + pose.Position,
                };
            }
        }
    }

    /// <summary>
    ///     Runs the query grid against the current broad phase. A fresh batcher is created per
    ///     pass (upstream does the same): a batcher cannot be reused across flushes once
    ///     nonconvex shapes (the deformed mesh) have been added.
    /// </summary>
    private void RunQueriesLocked()
    {
        _queryWasTouched.Clear(0, _queryWasTouched.Length);
        LastOverlapCount = 0;
        LastAddedPairCount = 0;

        var batcher = new CollisionBatcher<BatcherCallbacks>(
            _bufferPool, _simulation.Shapes, _simulation.NarrowPhase.CollisionTaskRegistry, 0f,
            new BatcherCallbacks { QueryWasTouched = _queryWasTouched });

        for (var queryIndex = 0; queryIndex < QueryCount; ++queryIndex)
        {
            ref var query = ref _queries[queryIndex];
            var enumerator = new BroadPhaseOverlapEnumerator
            {
                Pool = _bufferPool,
                References = new QuickList<CollidableReference>(16, _bufferPool),
            };
            _simulation.BroadPhase.GetOverlaps(query.BoundsMin, query.BoundsMax, _bufferPool, ref enumerator);
            LastOverlapCount += enumerator.References.Count;

            for (var overlapIndex = 0; overlapIndex < enumerator.References.Count; ++overlapIndex)
            {
                var reference = enumerator.References[overlapIndex];
                RigidPose candidatePose;
                TypedIndex candidateShape;
                if (reference.Mobility == CollidableMobility.Static)
                {
                    var collidable = _simulation.Statics[reference.StaticHandle];
                    candidatePose = collidable.Pose;
                    candidateShape = collidable.Shape;
                }
                else
                {
                    var body = _simulation.Bodies[reference.BodyHandle];
                    candidatePose = body.Pose;
                    candidateShape = body.Collidable.Shape;
                }

                var continuation = new PairContinuation(queryIndex);
                batcher.Add(
                    candidateShape, _boxShape,
                    query.Pose.Position - candidatePose.Position,
                    candidatePose.Orientation, query.Pose.Orientation,
                    0f, continuation);
                LastAddedPairCount++;
            }

            enumerator.References.Dispose(_bufferPool);
        }

        batcher.Flush();
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;
        RunQueriesLocked();

        var states = new List<Transform3DState>(MaxTransformCount);
        _poses.Emit(states);

        for (var i = 0; i < QueryCount; ++i)
        {
            ref var query = ref _queries[i];
            var renderId = _queryWasTouched[i] ? TouchedQueryRenderIdBase + i : UntouchedQueryRenderIdBase + i;
            states.Add(new Transform3DState(
                renderId,
                query.Pose.Position.X, query.Pose.Position.Y, query.Pose.Position.Z,
                query.Pose.Orientation.X, query.Pose.Orientation.Y, query.Pose.Orientation.Z, query.Pose.Orientation.W,
                1d, 1d, 1d,
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
