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
///     Port of the upstream <c>PlumpDancerDemo</c> (BepuPhysics2, Apache-2.0): the
///     <see cref="DancerDemo"/> infrastructure plus a fat suit built from a voxel lattice of
///     sphere bodies welded to the dancer's capsules (<see cref="Weld"/> constraints), with
///     interior nodes stripped of collidables.
///
///     Test-bed deviations (documented in docs/compat-review.md): 4×4 = 16 background dancers
///     (upstream 8×8 = 64), suit level-of-detail clamped to [1, 1.4] (~12³ nodes at full detail,
///     upstream 23³), sequential per-dancer solves instead of the upstream <c>ParallelLooper</c>.
///
///     Render-id ranges: 0 = main-sim floor, 100 + slot = main dancer bodies (12),
///     1000 + dancerIndex·4096 + slot = background dancer bodies (12) followed by its suit nodes.
/// </summary>
public sealed class PlumpDancerDemo : IDemoSimulation
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int MainDancerRenderIdBase = 100;
    public const int DancerRenderIdBase = 1000;
    public const int DancerRenderIdStride = 4096;

    public const int GridWidth = 4;
    public const int GridLength = 4;
    public const int DancerCount = GridWidth * GridLength;

    public const int FullDetailAxisBodyCount = 23;
    public const float MinLevelOfDetail = 1f;
    public const float MaxLevelOfDetail = 1.4f;
    public const float SuitSize = 1f;

    /// <summary>Worst-case suit nodes per dancer: the 12³ clamped-LOD lattice ceiling.</summary>
    public const int MaxSuitNodesPerDancer = 12 * 12 * 12;

    public const int MaxRecords = 1 + DancerBodyHandles.Count +
        DancerCount * (DancerBodyHandles.Count + MaxSuitNodesPerDancer);
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    /// <summary>Capsule dimensions per dancer body slot (radius, inner length); (0, 0) for the head sphere.</summary>
    public static readonly (float Radius, float Length)[] BodySlotCapsules =
    {
        (0.11f, 0.5f), // upper left leg
        (0.1f, 0.5f), // lower left leg
        (0.11f, 0.5f), // upper right leg
        (0.1f, 0.5f), // lower right leg
        (0.08f, 0.39f), // upper left arm
        (0.075f, 0.39f), // lower left arm
        (0.08f, 0.39f), // upper right arm
        (0.075f, 0.39f), // lower right arm
        (0.14f, 0.27f), // hips
        (0.13f, 0.216f), // abdomen
        (0.165f, 0.216f), // chest
        (0f, 0f), // head (sphere)
    };

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "plump-dancer",
        "plump-dancer",
        BufferCapacity,
        static (config, transport) => new PlumpDancerDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>Precomputed capsule for the fat-suit distance test.</summary>
    private struct TestCapsule
    {
        public Vector3 Start;
        public Vector3 Direction;
        public float Length;
        public float Radius;
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<SubgroupCollisionFilter> _filters = new();
    private readonly DemoPoseSet _mainPoses;
    private readonly List<DemoPoseSet> _dancerPoses = new();
    private readonly DemoDancers _dancers;
    private long _seq;

    public PlumpDancerDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new SubgroupFilteredCallbacks(_filters),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, 0f, 0f)),
            new SolveDescription(8, 1));

        _mainPoses = new DemoPoseSet(_world, _simulation);
        // The fat-suit welds are soft, so the per-dancer solve can be extremely minimal.
        _dancers = new DemoDancers().Initialize<DeformableCallbacks, DeformableCollisionFilter>(
            GridWidth, GridLength, _simulation, _filters, _bufferPool,
            new SolveDescription(1, 1), CreateFatSuit, new DeformableCollisionFilter(0, 0, 0, -1));

        RegisterMainDancerLocked();
    }

    internal int DancerCountActual => _dancers.DancerCount;
    internal int SuitNodeCount => _dancerPoses.Sum(poses => Math.Max(0, poses.Count - DancerBodyHandles.Count));
    internal int RecordCount => _mainPoses.Count + _dancerPoses.Sum(poses => poses.Count);
    internal int ConstraintCount => _simulation.Solver.CountConstraints() + _dancers.ConstraintCount;

    /// <summary>Test probe: the Bepu body handle owned by a render id (main sim or dancer sim).</summary>
    internal bool TryGetPhysicsBody(int renderId, out PhysicsBody body)
    {
        lock (_sync)
        {
            body = default;
            if (_mainPoses.TryGetBody(renderId, out var handle))
            {
                body = new PhysicsBody(handle);
                return true;
            }

            var dancerIndex = (renderId - DancerRenderIdBase) / DancerRenderIdStride;
            if (dancerIndex < 0 || dancerIndex >= _dancerPoses.Count) return false;
            if (!_dancerPoses[dancerIndex].TryGetBody(renderId, out handle)) return false;
            body = new PhysicsBody(handle);
            return true;
        }
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            _dancers.UpdateTargets(_simulation);
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _mainPoses.Sync();
            for (var i = 0; i < _dancerPoses.Count; i++) _dancerPoses[i].Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void RegisterMainDancerLocked()
    {
        var handles = _dancers.SourceBodyHandles;
        for (var slot = 0; slot < DancerBodyHandles.Count; slot++)
        {
            _mainPoses.AddDynamic(MainDancerRenderIdBase + slot, handles[slot], DancerDemo.BodySlotScale(slot));
        }

        _mainPoses.AddStatic(
            GroundRenderId, new Vector3(0f, -1.24f, 0f), Quaternion.Identity, new Vector3(1000f, 1f, 1000f));
    }

    /// <summary>Fat-suit hook for one background dancer: builds the voxel lattice and registers records.</summary>
    private void CreateFatSuit(
        Simulation simulation, CollidableProperty<DeformableCollisionFilter> filters, DancerBodyHandles bodyHandles,
        int dancerIndex, int dancerGridWidth, float levelOfDetail)
    {
        var poses = PoseSetForDancer(dancerIndex, simulation);
        var renderIdBase = DancerRenderIdBase + dancerIndex * DancerRenderIdStride;
        for (var slot = 0; slot < DancerBodyHandles.Count; slot++)
        {
            poses.AddDynamic(renderIdBase + slot, bodyHandles[slot], DancerDemo.BodySlotScale(slot));
        }

        levelOfDetail = MathF.Max(MinLevelOfDetail, MathF.Min(MaxLevelOfDetail, levelOfDetail));
        var suitSize = new Vector3(SuitSize, SuitSize, SuitSize);
        var fullDetailAxisBodyCounts = new Int3 { X = FullDetailAxisBodyCount, Y = FullDetailAxisBodyCount, Z = FullDetailAxisBodyCount };
        var scale = MathF.Pow(2, levelOfDetail);
        var axisBodyCounts = new Int3
        {
            X = (int)MathF.Ceiling(fullDetailAxisBodyCounts.X / scale),
            Y = (int)MathF.Ceiling(fullDetailAxisBodyCounts.Y / scale),
            Z = (int)MathF.Ceiling(fullDetailAxisBodyCounts.Z / scale),
        };
        var bodyRadius = MathF.Min(suitSize.X / axisBodyCounts.X, MathF.Min(suitSize.Y / axisBodyCounts.Y, suitSize.Z / axisBodyCounts.Z));

        var chest = simulation.Bodies[bodyHandles.Chest];
        ref var chestShape = ref simulation.Shapes.GetShape<Capsule>(chest.Collidable.Shape.Index);
        var topOfChestHeight = chest.Pose.Position.Y + chestShape.Radius;
        var topOfChestPosition = new Vector3(0f, topOfChestHeight, 0f) + DemoDancers.GetOffsetForDancer(dancerIndex, dancerGridWidth);
        var suitMinimum = topOfChestPosition - suitSize * new Vector3(0.5f, 1f, 0.5f);
        var suitMaximum = suitMinimum + suitSize;

        var nodeHandles = CreateBodyGrid(
            bodyHandles, axisBodyCounts, suitMinimum, suitMaximum, bodyRadius, 0.01f, dancerIndex, simulation, filters);

        var nextRenderId = renderIdBase + DancerBodyHandles.Count;
        for (var x = 0; x < nodeHandles.GetLength(0); ++x)
        {
            for (var y = 0; y < nodeHandles.GetLength(1); ++y)
            {
                for (var z = 0; z < nodeHandles.GetLength(2); ++z)
                {
                    var handle = nodeHandles[x, y, z];
                    if (handle.Value < 0) continue;
                    poses.AddDynamic(nextRenderId++, handle, new Vector3(bodyRadius * 2f));
                }
            }
        }
    }

    private DemoPoseSet PoseSetForDancer(int dancerIndex, Simulation simulation)
    {
        while (_dancerPoses.Count <= dancerIndex)
        {
            _dancerPoses.Add(new DemoPoseSet(_world, simulation));
        }

        return _dancerPoses[dancerIndex];
    }

    private static TestCapsule CreateTestCapsule(Simulation simulation, BodyHandle handle)
    {
        var body = simulation.Bodies[handle];
        ref var shape = ref simulation.Shapes.GetShape<Capsule>(body.Collidable.Shape.Index);
        var pose = body.Pose;
        TestCapsule toReturn;
        QuaternionEx.TransformUnitY(pose.Orientation, out toReturn.Direction);
        toReturn.Start = pose.Position - toReturn.Direction * shape.HalfLength;
        toReturn.Radius = shape.Radius;
        toReturn.Length = shape.HalfLength * 2;
        return toReturn;
    }

    private static BodyHandle[,,] CreateBodyGrid(
        DancerBodyHandles bodyHandles, Int3 axisSizeInBodies, Vector3 gridMinimum, Vector3 gridMaximum,
        float bodyRadius, float massPerBody, int instanceId, Simulation simulation,
        CollidableProperty<DeformableCollisionFilter> filters)
    {
        var shape = new Sphere(bodyRadius);
        var shapeIndex = simulation.Shapes.Add(shape);
        // Unlike the dress nodes, the deformable sub-bodies can rotate: the Weld constraints
        // control all six degrees of freedom.
        var description = BodyDescription.CreateDynamic(
            Quaternion.Identity, shape.ComputeInertia(massPerBody), shapeIndex, 0.01f);
        var handles = new BodyHandle[axisSizeInBodies.X, axisSizeInBodies.Y, axisSizeInBodies.Z];
        var nearestHandles = new BodyHandle[axisSizeInBodies.X, axisSizeInBodies.Y, axisSizeInBodies.Z];
        var gridSpan = gridMaximum - gridMinimum;
        var gridSpacing = gridSpan / new Vector3(axisSizeInBodies.X - 1, axisSizeInBodies.Y - 1, axisSizeInBodies.Z - 1);
        Span<TestCapsule> testCapsules = stackalloc TestCapsule[11];

        // DancerBodyHandles stores the head last, so the first 11 bodies are all capsules.
        for (var i = 0; i < 11; ++i)
        {
            testCapsules[i] = CreateTestCapsule(simulation, bodyHandles[i]);
        }

        var center = (gridMinimum + gridMaximum) * 0.5f;

        for (var x = 0; x < axisSizeInBodies.X; ++x)
        {
            for (var y = 0; y < axisSizeInBodies.Y; ++y)
            {
                for (var z = 0; z < axisSizeInBodies.Z; ++z)
                {
                    var position = gridMinimum + gridSpacing * new Vector3(x, y, z);
                    var minimumDistance = float.MaxValue;
                    var minimumIndex = 0;
                    for (var i = 0; i < testCapsules.Length; ++i)
                    {
                        var testCapsule = testCapsules[i];
                        var distance = Vector3.Distance(
                            position,
                            testCapsule.Start + MathF.Max(0, MathF.Min(testCapsule.Length, Vector3.Dot(position - testCapsule.Start, testCapsule.Direction))) * testCapsule.Direction) - testCapsule.Radius;
                        if (distance < minimumDistance)
                        {
                            minimumDistance = distance;
                            minimumIndex = i;
                        }
                    }

                    nearestHandles[x, y, z] = bodyHandles[minimumIndex];

                    var maximumDistanceForCreatingNodes = MathF.Max(0.1f, 0.8f - 1.5f * Vector3.Distance(position, center));
                    if (minimumDistance < bodyRadius)
                    {
                        // Intersecting; don't create a body. -2 marks the slot as intersecting.
                        handles[x, y, z] = new BodyHandle { Value = -2 };
                    }
                    else if (minimumDistance > maximumDistanceForCreatingNodes)
                    {
                        // -1 means too far.
                        handles[x, y, z] = new BodyHandle { Value = -1 };
                    }
                    else
                    {
                        // Nearby: create it.
                        description.Pose.Position = position;
                        var handle = simulation.Bodies.Add(description);
                        handles[x, y, z] = handle;
                        filters.Allocate(handle) = new DeformableCollisionFilter(x, y, z, instanceId);
                    }
                }
            }
        }

        for (var x = 0; x < axisSizeInBodies.X; ++x)
        {
            for (var y = 0; y < axisSizeInBodies.Y; ++y)
            {
                for (var z = 0; z < axisSizeInBodies.Z; ++z)
                {
                    // For every node exposed to the air (a neighbor slot flagged -1), make sure it
                    // has a collidable; interior nodes don't need one.
                    var handle = handles[x, y, z];
                    if (handle.Value < 0) continue;

                    var needsAnchor =
                        (x != 0 && handles[x - 1, y, z].Value == -2) ||
                        (x != handles.GetLength(0) - 1 && handles[x + 1, y, z].Value == -2) ||
                        (y != 0 && handles[x, y - 1, z].Value == -2) ||
                        (y != handles.GetLength(1) - 1 && handles[x, y + 1, z].Value == -2) ||
                        (z != 0 && handles[x, y, z - 1].Value == -2) ||
                        (z != handles.GetLength(2) - 1 && handles[x, y, z + 1].Value == -2);
                    var source = simulation.Bodies[handle];
                    if (needsAnchor)
                    {
                        var nearestHandle = nearestHandles[x, y, z];
                        var nearestPose = simulation.Bodies[nearestHandle].Pose;
                        var conjugate = Quaternion.Conjugate(nearestPose.Orientation);
                        simulation.Solver.Add(nearestHandle, handle, new Weld
                        {
                            LocalOffset = QuaternionEx.Transform(source.Pose.Position - nearestPose.Position, conjugate),
                            LocalOrientation = conjugate,
                            SpringSettings = new SpringSettings(6f, 0.4f),
                        });
                    }

                    var needsCollidable =
                        x == 0 || handles[x - 1, y, z].Value == -1 || x == handles.GetLength(0) - 1 || handles[x + 1, y, z].Value == -1 ||
                        y == 0 || handles[x, y - 1, z].Value == -1 || y == handles.GetLength(1) - 1 || handles[x, y + 1, z].Value == -1 ||
                        z == 0 || handles[x, y, z - 1].Value == -1 || z == handles.GetLength(2) - 1 || handles[x, y, z + 1].Value == -1;
                    if (!needsCollidable)
                    {
                        source.SetShape(default);
                    }

                    if (x < handles.GetLength(0) - 1) TryAddWeld(simulation, source, handles[x + 1, y, z]);
                    if (y < handles.GetLength(1) - 1) TryAddWeld(simulation, source, handles[x, y + 1, z]);
                    if (z < handles.GetLength(2) - 1) TryAddWeld(simulation, source, handles[x, y, z + 1]);
                }
            }
        }

        return handles;

        static void TryAddWeld(Simulation simulation, BodyReference source, BodyHandle targetHandle)
        {
            if (targetHandle.Value < 0) return;
            var target = simulation.Bodies[targetHandle];
            simulation.Solver.Add(source.Handle, targetHandle, new Weld
            {
                LocalOffset = target.Pose.Position - source.Pose.Position,
                LocalOrientation = Quaternion.Identity,
                SpringSettings = new SpringSettings(6f, 0.4f),
            });
        }
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        _seq++;
        var states = new List<Transform3DState>(RecordCount);
        _mainPoses.Emit(states);
        for (var i = 0; i < _dancerPoses.Count; i++) _dancerPoses[i].Emit(states);
        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _dancers.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
