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
///     Port of the upstream <c>DancerDemo</c> (BepuPhysics2, Apache-2.0): a main dancer driven by
///     <see cref="DancerControl"/> servos plus a grid of background dancers, each with its own
///     cosmetic simulation wearing a cloth dress of sphere nodes connected by
///     <see cref="CenterDistanceLimit"/> constraints.
///
///     Test-bed deviations (documented in docs/compat-review.md): 8×8 = 64 background dancers
///     (upstream 16×16 = 256), dress level-of-detail clamped to [1, 1.5] (~15×15 nodes at full
///     detail, upstream 29×29), and the per-dancer simulations step sequentially instead of the
///     upstream <c>ParallelLooper</c> (deterministic solves).
///
///     Render-id ranges: 0 = main-sim floor, 100 + slot = main dancer bodies (12),
///     1000 + dancerIndex·512 + slot = background dancer bodies (12) followed by its dress nodes.
/// </summary>
public sealed class DancerDemo : IDemoSimulation
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int MainDancerRenderIdBase = 100;
    public const int DancerRenderIdBase = 1000;
    public const int DancerRenderIdStride = 512;

    public const int GridWidth = 8;
    public const int GridLength = 8;
    public const int DancerCount = GridWidth * GridLength;

    public const int FullDetailWidthInBodies = 29;
    public const float TargetDressDiameter = 2.6f;
    public const float ClothMassPerBody = 0.01f;
    public const float MinLevelOfDetail = 1f;
    public const float MaxLevelOfDetail = 1.5f;

    /// <summary>Worst-case dress nodes per dancer: the coarsest clamped LOD grid ceiling.</summary>
    public const int MaxDressNodesPerDancer =
        ((FullDetailWidthInBodies + 1) / 2) * ((FullDetailWidthInBodies + 1) / 2);

    public const int MaxRecords = 1 + DancerBodyHandles.Count +
        DancerCount * (DancerBodyHandles.Count + MaxDressNodesPerDancer);
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
        "dancer",
        "dancer",
        BufferCapacity,
        static (config, transport) => new DancerDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

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

    public DancerDemo(
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
        _dancers = new DemoDancers().Initialize<ClothCallbacks, ClothCollisionFilter>(
            GridWidth, GridLength, _simulation, _filters, _bufferPool,
            new SolveDescription(1, 4), DressDancer, new ClothCollisionFilter(0, 0, -1));

        RegisterMainDancerLocked();
    }

    internal int DancerCountActual => _dancers.DancerCount;
    internal int DressNodeCount => _dancerPoses.Sum(poses => Math.Max(0, poses.Count - DancerBodyHandles.Count));
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
            // Advance the main dancer's servo targets, record its motion history and step every
            // background dancer (upstream order: dancers first, then the main simulation).
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
            _mainPoses.AddDynamic(MainDancerRenderIdBase + slot, handles[slot], BodySlotScale(slot));
        }

        _mainPoses.AddStatic(
            GroundRenderId, new Vector3(0f, -1.24f, 0f), Quaternion.Identity, new Vector3(1000f, 1f, 1000f));
    }

    /// <summary>
    ///     Dress-up hook for one background dancer: creates the sphere-node cloth lattice, anchors it
    ///     to the chest and registers the dancer's body + cloth render records.
    /// </summary>
    private void DressDancer(
        Simulation simulation, CollidableProperty<ClothCollisionFilter> filters, DancerBodyHandles bodyHandles,
        int dancerIndex, int dancerGridWidth, float levelOfDetail)
    {
        var poses = PoseSetForDancer(dancerIndex, simulation);
        var renderIdBase = DancerRenderIdBase + dancerIndex * DancerRenderIdStride;
        for (var slot = 0; slot < DancerBodyHandles.Count; slot++)
        {
            poses.AddDynamic(renderIdBase + slot, bodyHandles[slot], BodySlotScale(slot));
        }

        levelOfDetail = MathF.Max(MinLevelOfDetail, MathF.Min(MaxLevelOfDetail, levelOfDetail));
        var spacingAtFullDetail = TargetDressDiameter / FullDetailWidthInBodies;
        var bodyRadius = spacingAtFullDetail / 1.75f;
        var scale = MathF.Pow(2, levelOfDetail);
        var widthInBodies = (int)MathF.Ceiling(FullDetailWidthInBodies / scale);
        var spacing = spacingAtFullDetail * scale;

        var chest = simulation.Bodies[bodyHandles.Chest];
        ref var chestShape = ref simulation.Shapes.GetShape<Capsule>(chest.Collidable.Shape.Index);
        var topOfChestHeight = chest.Pose.Position.Y + chestShape.Radius + bodyRadius;
        var position = new Vector3(0f, topOfChestHeight, 0f) + DemoDancers.GetOffsetForDancer(dancerIndex, dancerGridWidth);

        var bodies = CreateDressBodyGrid(position, widthInBodies, spacing, bodyRadius, ClothMassPerBody, dancerIndex, simulation, filters);
        AttachDressToChest(simulation, chest, chestShape, bodies, spacing);
        CreateDistanceConstraints(bodies, new SpringSettings(60f, 1f), simulation);

        var nextRenderId = renderIdBase + DancerBodyHandles.Count;
        for (var rowIndex = 0; rowIndex < bodies.GetLength(0); ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < bodies.GetLength(1); ++columnIndex)
            {
                var handle = bodies[rowIndex, columnIndex];
                if (handle.Value < 0) continue;
                poses.AddDynamic(nextRenderId++, handle, new Vector3(bodyRadius * 2f));
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

    private static BodyHandle[,] CreateDressBodyGrid(
        Vector3 position, int widthInNodes, float spacing, float bodyRadius, float massPerBody,
        int instanceId, Simulation simulation, CollidableProperty<ClothCollisionFilter> filters)
    {
        var description = BodyDescription.CreateDynamic(
            Quaternion.Identity, new BodyInertia { InverseMass = 1f / massPerBody },
            simulation.Shapes.Add(new Sphere(bodyRadius)), 0.01f);
        var handles = new BodyHandle[widthInNodes, widthInNodes];
        var armHoleCenter = new Vector2(DemoDancers.ArmOffsetX + 0.065f, 0);
        var armHoleRadius = 0.095f;
        var armHoleRadiusSquared = armHoleRadius * armHoleRadius;
        var halfWidth = widthInNodes * spacing / 2;
        var halfWidthSquared = halfWidth * halfWidth;
        var halfWidthOffset = new Vector2(halfWidth);
        for (var rowIndex = 0; rowIndex < widthInNodes; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < widthInNodes; ++columnIndex)
            {
                var horizontalPosition = new Vector2(columnIndex, rowIndex) * spacing - halfWidthOffset;
                var distanceSquared0 = Vector2.DistanceSquared(horizontalPosition, armHoleCenter);
                var distanceSquared1 = Vector2.DistanceSquared(horizontalPosition, -armHoleCenter);
                var centerDistanceSquared = horizontalPosition.LengthSquared();
                if (distanceSquared0 < armHoleRadiusSquared || distanceSquared1 < armHoleRadiusSquared ||
                    centerDistanceSquared > halfWidthSquared)
                {
                    // Too close to an arm or too far from the center, don't create any bodies here.
                    handles[rowIndex, columnIndex] = new BodyHandle { Value = -1 };
                }
                else
                {
                    description.Pose.Position = new Vector3(horizontalPosition.X, 0f, horizontalPosition.Y) + position;
                    var handle = simulation.Bodies.Add(description);
                    handles[rowIndex, columnIndex] = handle;
                    filters.Allocate(handle) = new ClothCollisionFilter(rowIndex, columnIndex, instanceId);
                }
            }
        }

        return handles;
    }

    private static void AttachDressToChest(
        Simulation simulation, BodyReference chest, Capsule chestShape, BodyHandle[,] bodies, float spacing)
    {
        var widthInBodies = bodies.GetLength(0);
        var midpoint = widthInBodies * 0.5f - 0.5f;
        var zRange = chestShape.Radius * 0.65f / spacing;
        var xRange = (chestShape.Radius * 0.5f + chestShape.HalfLength) / spacing;
        var minX = (int)MathF.Ceiling(midpoint - xRange);
        var maxX = (int)(midpoint + xRange);
        var minZ = (int)MathF.Ceiling(midpoint - zRange);
        var maxZ = (int)(midpoint + zRange);
        for (var z = minZ; z <= maxZ; ++z)
        {
            if (z < 0 || z >= widthInBodies) continue;
            for (var x = minX; x <= maxX; ++x)
            {
                if (x < 0 || x >= widthInBodies) continue;
                var clothNodeHandle = bodies[z, x];
                if (clothNodeHandle.Value < 0) continue;
                var clothNodeBody = simulation.Bodies[clothNodeHandle];
                simulation.Solver.Add(chest.Handle, clothNodeBody.Handle, new BallSocket
                {
                    LocalOffsetA = QuaternionEx.Transform(
                        clothNodeBody.Pose.Position - chest.Pose.Position, Quaternion.Conjugate(chest.Pose.Orientation)),
                    SpringSettings = new SpringSettings(30f, 1f),
                });
            }
        }
    }

    private static void CreateDistanceConstraints(BodyHandle[,] bodyHandles, SpringSettings springSettings, Simulation simulation)
    {
        void CreateConstraintBetweenBodies(BodyHandle aHandle, BodyHandle bHandle)
        {
            // Only create a constraint if bodies on both sides of the pair actually exist.
            if (aHandle.Value < 0 || bHandle.Value < 0) return;
            var a = simulation.Bodies[aHandle];
            var b = simulation.Bodies[bHandle];
            // Note the use of a limit; the distance is allowed to go smaller.
            // This helps stop the cloth from having unnatural rigidity.
            var distance = Vector3.Distance(a.Pose.Position, b.Pose.Position);
            simulation.Solver.Add(aHandle, bHandle, new CenterDistanceLimit(distance * 0.15f, distance, springSettings));
        }

        for (var rowIndex = 0; rowIndex < bodyHandles.GetLength(0); ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < bodyHandles.GetLength(1) - 1; ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex, columnIndex + 1]);
            }
        }

        for (var rowIndex = 0; rowIndex < bodyHandles.GetLength(0) - 1; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < bodyHandles.GetLength(1); ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex + 1, columnIndex]);
            }
        }

        for (var rowIndex = 0; rowIndex < bodyHandles.GetLength(0) - 1; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < bodyHandles.GetLength(1) - 1; ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex + 1, columnIndex + 1]);
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex + 1], bodyHandles[rowIndex + 1, columnIndex]);
            }
        }
    }

    /// <summary>Render scale for one dancer body slot (capsules bake aspect client-side; head is a unit sphere).</summary>
    public static Vector3 BodySlotScale(int slot) => BodySlotCapsules[slot].Radius > 0f
        ? Vector3.One
        : new Vector3(0.34f);

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
