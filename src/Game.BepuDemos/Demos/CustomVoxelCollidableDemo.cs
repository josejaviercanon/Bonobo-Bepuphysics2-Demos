using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.CollisionDetection.CollisionTasks;
using Bonobo.Bepuphysics2.CollisionDetection.SweepTasks;
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
///     Custom homogeneous compound collidable: a voxel grid (ported from the upstream
///     <c>CustomVoxelCollidableDemo</c>, BepuPhysics2, Apache-2.0, Ross Nordby). Every child is a
///     <see cref="Box"/>; the object-space <see cref="Tree"/> accelerates overlap and sweep
///     queries. The narrow phase must be told about the new shape type through collision and
///     sweep task registration.
/// </summary>
public struct Voxels : IHomogeneousCompoundShape<Box, BoxWide>
{
    public static int TypeId => 12;

    public Tree Tree;
    public QuickList<Vector3> VoxelIndices;
    public Vector3 VoxelSize;

    public readonly int ChildCount => VoxelIndices.Count;

    public Voxels(QuickList<Vector3> voxelIndices, Vector3 voxelSize, BufferPool pool)
    {
        VoxelIndices = voxelIndices;
        VoxelSize = voxelSize;
        Tree = new Tree(pool, voxelIndices.Count);
        pool.Take(voxelIndices.Count, out Buffer<BoundingBox> bounds);
        for (var i = 0; i < voxelIndices.Count; ++i)
        {
            ref var voxel = ref voxelIndices[i];
            ref var voxelBounds = ref bounds[i];
            // The voxel scale is baked into the tree (unlike Mesh, which shares trees across scales).
            voxelBounds.Min = voxel * VoxelSize;
            voxelBounds.Max = voxelBounds.Min + VoxelSize;
        }

        Tree.SweepBuild(pool, bounds);
        pool.Return(ref bounds);
    }

    public static ShapeBatch CreateShapeBatch(BufferPool pool, int initialCapacity, Shapes shapeBatches) =>
        new HomogeneousCompoundShapeBatch<Voxels, Box, BoxWide>(pool, initialCapacity);

    public readonly void ComputeBounds(Quaternion orientation, out Vector3 min, out Vector3 max)
    {
        Matrix3x3.CreateFromQuaternion(orientation, out var basis);
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (var i = 0; i < VoxelIndices.Count; ++i)
        {
            var localVoxelPosition = (VoxelIndices[i] + new Vector3(0.5f)) * VoxelSize;
            Matrix3x3.Transform(localVoxelPosition, basis, out var rotatedPosition);
            min = Vector3.Min(rotatedPosition, min);
            max = Vector3.Max(rotatedPosition, max);
        }

        // All children share the shape and orientation, so expand the centroid bounds by one box.
        var box = new Box(VoxelSize.X, VoxelSize.Y, VoxelSize.Z);
        box.ComputeBounds(orientation, out var childLocalMin, out var childLocalMax);
        min += childLocalMin;
        max += childLocalMax;
    }

    private unsafe struct HitLeafTester<T> : IRayLeafTester where T : IShapeRayHitHandler
    {
        public QuickList<Vector3> VoxelIndices;
        public Vector3 VoxelSize;
        public Box VoxelShape;
        public T HitHandler;
        public Matrix3x3 Orientation;
        public RayData OriginalRay;

        public void TestLeaf(int leafIndex, RayData* ray, float* maximumT, BufferPool pool)
        {
            ref var voxelIndex = ref VoxelIndices[leafIndex];
            if (VoxelShape.RayTest(voxelIndex + new Vector3(0.5f) * VoxelSize, ray->Origin, ray->Direction, out var t, out var normal) && t <= *maximumT)
            {
                Matrix3x3.Transform(normal, Orientation, out normal);
                HitHandler.OnRayHit(OriginalRay, ref *maximumT, t, normal, leafIndex);
            }
        }
    }

    public readonly void RayTest<TRayHitHandler>(in RigidPose pose, in RayData ray, ref float maximumT, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        HitLeafTester<TRayHitHandler> leafTester;
        leafTester.VoxelIndices = VoxelIndices;
        leafTester.VoxelSize = VoxelSize;
        leafTester.VoxelShape = new Box(VoxelSize.X, VoxelSize.Y, VoxelSize.Z);
        leafTester.HitHandler = hitHandler;
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out leafTester.Orientation);
        leafTester.OriginalRay = ray;
        Matrix3x3.TransformTranspose(ray.Origin - pose.Position, leafTester.Orientation, out var localOrigin);
        Matrix3x3.TransformTranspose(ray.Direction, leafTester.Orientation, out var localDirection);
        Tree.RayCast(localOrigin, localDirection, ref maximumT, pool, ref leafTester);
        hitHandler = leafTester.HitHandler;
    }

    public readonly unsafe void RayTest<TRayHitHandler>(in RigidPose pose, ref RaySource rays, BufferPool pool, ref TRayHitHandler hitHandler)
        where TRayHitHandler : struct, IShapeRayHitHandler
    {
        HitLeafTester<TRayHitHandler> leafTester;
        leafTester.VoxelIndices = VoxelIndices;
        leafTester.VoxelSize = VoxelSize;
        leafTester.VoxelShape = new Box(VoxelSize.X, VoxelSize.Y, VoxelSize.Z);
        leafTester.HitHandler = hitHandler;
        Matrix3x3.CreateFromQuaternion(pose.Orientation, out leafTester.Orientation);
        Matrix3x3.Transpose(leafTester.Orientation, out var inverseOrientation);
        for (var i = 0; i < rays.RayCount; ++i)
        {
            rays.GetRay(i, out var ray, out var maximumT);
            leafTester.OriginalRay = *ray;
            Matrix3x3.Transform(ray->Origin - pose.Position, inverseOrientation, out var localOrigin);
            Matrix3x3.Transform(ray->Direction, inverseOrientation, out var localDirection);
            Tree.RayCast(localOrigin, localDirection, ref *maximumT, pool, ref leafTester);
        }

        hitHandler = leafTester.HitHandler;
    }

    public readonly void GetLocalChild(int childIndex, out Box childShape)
    {
        var halfSize = VoxelSize * 0.5f;
        childShape.HalfWidth = halfSize.X;
        childShape.HalfHeight = halfSize.Y;
        childShape.HalfLength = halfSize.Z;
    }

    public readonly void GetPosedLocalChild(int childIndex, out Box childShape, out RigidPose childPose)
    {
        GetLocalChild(childIndex, out childShape);
        childPose = VoxelIndices[childIndex] + new Vector3(0.5f) * VoxelSize;
    }

    public readonly void GetLocalChild(int childIndex, ref BoxWide shapeWide)
    {
        var halfSize = VoxelSize * 0.5f;
        GatherScatter.GetFirst(ref shapeWide.HalfWidth) = halfSize.X;
        GatherScatter.GetFirst(ref shapeWide.HalfHeight) = halfSize.Y;
        GatherScatter.GetFirst(ref shapeWide.HalfLength) = halfSize.Z;
    }

    public readonly unsafe void FindLocalOverlaps<TOverlaps, TSubpairOverlaps>(ref Buffer<OverlapQueryForPair> pairs, BufferPool pool, Shapes shapes, ref TOverlaps overlaps)
        where TOverlaps : struct, ICollisionTaskOverlaps<TSubpairOverlaps>
        where TSubpairOverlaps : struct, ICollisionTaskSubpairOverlaps
    {
        ShapeTreeOverlapEnumerator<TSubpairOverlaps> enumerator;
        enumerator.Pool = pool;
        for (var i = 0; i < pairs.Length; ++i)
        {
            ref var pair = ref pairs[i];
            ref var voxelsSet = ref Unsafe.AsRef<Voxels>(pair.Container);
            enumerator.Overlaps = Unsafe.AsPointer(ref overlaps.GetOverlapsForPair(i));
            voxelsSet.Tree.GetOverlaps(pair.Min, pair.Max, pool, ref enumerator);
        }
    }

    public readonly void FindLocalOverlaps<TEnumerator>(Vector3 min, Vector3 max, BufferPool pool, Shapes shapes, ref TEnumerator enumerator)
        where TEnumerator : IBreakableForEach<int>
    {
        Tree.GetOverlaps(min, max, pool, ref enumerator);
    }

    public readonly unsafe void FindLocalOverlaps<TOverlaps>(Vector3 min, Vector3 max, Vector3 sweep, float maximumT, BufferPool pool, Shapes shapes, void* overlaps)
        where TOverlaps : ICollisionTaskSubpairOverlaps
    {
        ShapeTreeSweepLeafTester<TOverlaps> enumerator;
        enumerator.Pool = pool;
        enumerator.Overlaps = overlaps;
        Tree.Sweep(min, max, sweep, maximumT, pool, ref enumerator);
    }

    public void Dispose(BufferPool pool)
    {
        Tree.Dispose(pool);
        VoxelIndices.Dispose(pool);
    }
}

/// <summary>Continuation handler combining voxel child manifolds through a nonconvex reduction.</summary>
public struct ConvexVoxelsContinuations : IConvexCompoundContinuationHandler<NonconvexReduction>
{
    public CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    public ref NonconvexReduction CreateContinuation<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, int childCount, in BoundsTestedPair pair, in OverlapQueryForPair pairQuery, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks
    {
        return ref collisionBatcher.NonconvexReductions.CreateContinuation(childCount, collisionBatcher.Pool, out continuationIndex);
    }

    public static unsafe void GetChildData<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReductionChild continuationChild,
        in BoundsTestedPair pair, int shapeTypeA, int childIndexB, out RigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var voxels = ref Unsafe.AsRef<Voxels>(pair.B);
        ref var voxelIndex = ref voxels.VoxelIndices[childIndexB];
        var localPosition = (voxelIndex + new Vector3(0.5f)) * voxels.VoxelSize;
        QuaternionEx.TransformWithoutOverlap(localPosition, pair.OrientationB, out childPoseB.Position);
        childPoseB.Orientation = Quaternion.Identity;
        childTypeB = Box.Id;
        var halfSize = voxels.VoxelSize * 0.5f;
        collisionBatcher.CacheShapeB(shapeTypeA, childTypeB, Unsafe.AsPointer(ref halfSize), 12, out childShapeDataB);
    }

    public unsafe void ConfigureContinuationChild<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int shapeTypeA, int childIndexB,
        out RigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var continuationChild = ref continuation.Children[continuationChildIndex];
        GetChildData(ref collisionBatcher, ref continuationChild, pair, shapeTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        if (pair.FlipMask < 0)
        {
            continuationChild.ChildIndexA = childIndexB;
            continuationChild.ChildIndexB = 0;
            continuationChild.OffsetA = childPoseB.Position;
            continuationChild.OffsetB = default;
        }
        else
        {
            continuationChild.ChildIndexA = 0;
            continuationChild.ChildIndexB = childIndexB;
            continuationChild.OffsetA = default;
            continuationChild.OffsetB = childPoseB.Position;
        }
    }
}

/// <summary>Continuation handler for compound-vs-voxels pairs.</summary>
public unsafe struct CompoundVoxelsContinuations<TCompoundA> : ICompoundPairContinuationHandler<NonconvexReduction>
    where TCompoundA : ICompoundShape
{
    public CollisionContinuationType CollisionContinuationType => CollisionContinuationType.NonconvexReduction;

    public ref NonconvexReduction CreateContinuation<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, int totalChildCount, ref Buffer<ChildOverlapsCollection> pairOverlaps, ref Buffer<OverlapQueryForPair> pairQueries, in BoundsTestedPair pair, out int continuationIndex)
        where TCallbacks : struct, ICollisionCallbacks
    {
        return ref collisionBatcher.NonconvexReductions.CreateContinuation(totalChildCount, collisionBatcher.Pool, out continuationIndex);
    }

    public void GetChildAData<TCallbacks>(ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, in BoundsTestedPair pair, int childIndexA,
        out RigidPose childPoseA, out int childTypeA, out void* childShapeDataA)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var compoundA = ref Unsafe.AsRef<TCompoundA>(pair.A);
        ref var compoundChildA = ref compoundA.GetChild(childIndexA);
        Compound.GetRotatedChildPose(compoundChildA.AsPose(), pair.OrientationA, out childPoseA);
        childTypeA = compoundChildA.ShapeIndex.Type;
        collisionBatcher.Shapes[childTypeA].GetShapeData(compoundChildA.ShapeIndex.Index, out childShapeDataA, out _);
    }

    public void ConfigureContinuationChild<TCallbacks>(
        ref CollisionBatcher<TCallbacks> collisionBatcher, ref NonconvexReduction continuation, int continuationChildIndex, in BoundsTestedPair pair, int childIndexA, int childTypeA, int childIndexB, in RigidPose childPoseA,
        out RigidPose childPoseB, out int childTypeB, out void* childShapeDataB)
        where TCallbacks : struct, ICollisionCallbacks
    {
        ref var continuationChild = ref continuation.Children[continuationChildIndex];
        ConvexVoxelsContinuations.GetChildData(ref collisionBatcher, ref continuationChild, pair, childTypeA, childIndexB, out childPoseB, out childTypeB, out childShapeDataB);
        if (pair.FlipMask < 0)
        {
            continuationChild.ChildIndexA = childIndexB;
            continuationChild.ChildIndexB = childIndexA;
            continuationChild.OffsetA = childPoseB.Position;
            continuationChild.OffsetB = childPoseA.Position;
        }
        else
        {
            continuationChild.ChildIndexA = childIndexA;
            continuationChild.ChildIndexB = childIndexB;
            continuationChild.OffsetA = childPoseA.Position;
            continuationChild.OffsetB = childPoseB.Position;
        }
    }
}

/// <summary>
///     Port of the upstream <c>CustomVoxelCollidableDemo</c> (BepuPhysics2, Apache-2.0): a custom
///     voxel-grid collidable registered with the narrow phase through six convex and two compound
///     collision tasks plus six convex and two compound sweep tasks. Boxes rain onto a sine-noise
///     voxel terrain.
///
///     Test-bed deviation (documented in docs/compat-review.md): the voxel grid is reduced from
///     40×30×40 to 20×15×20 and the falling box count from 4096 to 1600 so the desktop test host
///     stays interactive; the noise function and task registration are upstream.
///
///     Render-id ranges: 0 = static ground, 100+ = falling boxes, 10_000+ = voxels.
/// </summary>
public sealed class CustomVoxelCollidableDemo : IDemoSimulation
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int BoxRenderIdBase = 100;
    public const int VoxelRenderIdBase = 10_000;
    public const int BoxCount = 1600;

    public const int GridWidth = 20;
    public const int GridHeight = 15;
    public const int GridDepth = 20;
    public const int MaxVoxels = GridWidth * GridHeight * GridDepth;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + BoxCount + MaxVoxels) * SignalBufferLayout.Transform3DStride;

    public const float GroundSize = 300f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "custom-voxel-collidable",
        "custom-voxel-collidable",
        BufferCapacity,
        static (config, transport) => new CustomVoxelCollidableDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private Voxels _voxels;
    private long _seq;

    public CustomVoxelCollidableDemo(
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

        RegisterVoxelTasks();
        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int VoxelCount => _voxels.ChildCount;
    internal int FallingBoxCount => BoxCount;

    /// <summary>Test probe: the Bepu body handle owned by a falling-box render id.</summary>
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

    /// <summary>Registers the custom collidable's collision and sweep tasks with the narrow phase.</summary>
    private void RegisterVoxelTasks()
    {
        var collisionTasks = _simulation.NarrowPhase.CollisionTaskRegistry;
        collisionTasks.Register(new ConvexCompoundCollisionTask<Sphere, Voxels, ConvexCompoundOverlapFinder<Sphere, SphereWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new ConvexCompoundCollisionTask<Capsule, Voxels, ConvexCompoundOverlapFinder<Capsule, CapsuleWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new ConvexCompoundCollisionTask<Box, Voxels, ConvexCompoundOverlapFinder<Box, BoxWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new ConvexCompoundCollisionTask<Triangle, Voxels, ConvexCompoundOverlapFinder<Triangle, TriangleWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new ConvexCompoundCollisionTask<Cylinder, Voxels, ConvexCompoundOverlapFinder<Cylinder, CylinderWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new ConvexCompoundCollisionTask<ConvexHull, Voxels, ConvexCompoundOverlapFinder<ConvexHull, ConvexHullWide, Voxels>, ConvexVoxelsContinuations, NonconvexReduction>());
        collisionTasks.Register(new CompoundPairCollisionTask<Compound, Voxels, CompoundPairOverlapFinder<Compound, Voxels>, CompoundVoxelsContinuations<Compound>, NonconvexReduction>());
        collisionTasks.Register(new CompoundPairCollisionTask<BigCompound, Voxels, CompoundPairOverlapFinder<BigCompound, Voxels>, CompoundVoxelsContinuations<BigCompound>, NonconvexReduction>());

        var sweepTasks = _simulation.NarrowPhase.SweepTaskRegistry;
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<Sphere, SphereWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Sphere, Voxels>>());
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<Capsule, CapsuleWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Capsule, Voxels>>());
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<Box, BoxWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Box, Voxels>>());
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<Triangle, TriangleWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Triangle, Voxels>>());
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<Cylinder, CylinderWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<Cylinder, Voxels>>());
        sweepTasks.Register(new ConvexHomogeneousCompoundSweepTask<ConvexHull, ConvexHullWide, Voxels, Box, BoxWide, ConvexCompoundSweepOverlapFinder<ConvexHull, Voxels>>());
        sweepTasks.Register(new CompoundHomogeneousCompoundSweepTask<Compound, Voxels, Box, BoxWide, CompoundPairSweepOverlapFinder<Compound, Voxels>>());
        sweepTasks.Register(new CompoundHomogeneousCompoundSweepTask<BigCompound, Voxels, Box, BoxWide, CompoundPairSweepOverlapFinder<BigCompound, Voxels>>());
    }

    private void BuildSceneLocked()
    {
        var voxelIndices = new QuickList<Vector3>(MaxVoxels, _bufferPool);
        for (var i = 0; i < GridWidth; ++i)
        {
            for (var j = 0; j < GridHeight; ++j)
            {
                for (var k = 0; k < GridDepth; ++k)
                {
                    // Sine-wave noise (upstream constants): density > 0 leaves a voxel.
                    var octave0 = MathF.Cos((i + 78) * 0.8f) + MathF.Cos((j + 37) * 0.8f) + MathF.Cos((k + 131) * 0.8f);
                    var octave1 = MathF.Cos((i + 59) * 0.4f) + MathF.Cos((j + 100) * 0.4f) + MathF.Cos((k + 131) * 0.4f);
                    var octave2 = MathF.Cos((i + 43) * 0.1f) + MathF.Cos((j + 200) * 0.1f) + MathF.Cos((k + 281) * 0.1f);
                    var octave3 = MathF.Cos((i + 647) * 0.025f) + MathF.Cos((j + 1553) * 0.025f) + MathF.Cos((k + 53) * 0.025f);
                    var density = octave0 + octave1 + octave2 + octave3;
                    if (density > 0) voxelIndices.AllocateUnsafely() = new Vector3(i, j, k);
                }
            }
        }

        _voxels = new Voxels(voxelIndices, new Vector3(1f, 1f, 1f), _bufferPool);
        _simulation.Statics.Add(new StaticDescription(new Vector3(0f, 0f, 0f), _simulation.Shapes.Add(_voxels)));

        for (var i = 0; i < _voxels.ChildCount; i++)
        {
            var voxel = _voxels.VoxelIndices[i];
            _poses.AddStatic(
                VoxelRenderIdBase + i,
                voxel + new Vector3(0.5f),
                Quaternion.Identity,
                new Vector3(1f, 1f, 1f));
        }

        var random = new Random(5);
        var shapeToDrop = new Box(1f, 1f, 1f);
        var descriptionToDrop = BodyDescription.CreateDynamic(
            RigidPose.Identity, shapeToDrop.ComputeInertia(1f), _simulation.Shapes.Add(shapeToDrop), 0.01f);
        for (var i = 0; i < BoxCount; ++i)
        {
            descriptionToDrop.Pose.Position = new Vector3(
                15f + 10f * random.NextSingle(),
                45f + 150f * random.NextSingle(),
                15f + 10f * random.NextSingle());
            var handle = _simulation.Bodies.Add(descriptionToDrop);
            _poses.AddDynamic(BoxRenderIdBase + i, handle, Vector3.One);
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f), _simulation.Shapes.Add(new Box(GroundSize, 1f, GroundSize))));
        _poses.AddStatic(
            GroundRenderId, new Vector3(0f, -0.5f, 0f), Quaternion.Identity,
            new Vector3(GroundSize, 1f, GroundSize));
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
