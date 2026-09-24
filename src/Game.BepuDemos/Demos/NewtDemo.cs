using System.Diagnostics;
using System.Numerics;
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
///     Port of the upstream <c>NewtDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby): squishy
///     newts built from a voxel-tetrahedralized OBJ mesh, welded together with springy
///     <c>Weld</c> constraints plus <c>VolumeConstraint</c> tetrahedra, with a heavy ball
///     dropped on them.
///
///     Test-bed deviations (documented in docs/compat-review.md): the OBJ is parsed by the
///     in-repo span parser (<see cref="ObjMeshParser"/>) from an embedded resource instead of
///     the <c>ObjLoader</c> NuGet + content archive; the tetrahedralizer uses managed
///     collections (deterministic insertion order); newt count reduced from 8 to
///     <see cref="NewtCount"/> so the deterministic unit tests stay fast.
///
///     Render-id ranges: 0 = floor static, 1 = heavy ball, 2 = static sphere bump,
///     100000 + newtIndex·4096 + vertexIndex = deformable node spheres.
/// </summary>
public sealed class NewtDemo : IDemoSimulation
{
    public const int FloorRenderId = 0;
    public const int BallRenderId = 1;
    public const int StaticSphereRenderId = 2;
    public const int NodeRenderIdBase = 100_000;
    public const int NodeRenderIdStride = 4_096;

    public const int NewtCount = 8;
    public const float CellSize = 0.1f;
    public const int MaxNodesPerNewt = 2_304;

    public const int MaxTransformCount = 3 + NewtCount * MaxNodesPerNewt;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "newt",
        "newt",
        BufferCapacity,
        static (config, transport) => new NewtDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    /// <summary>Unordered vertex pair used to deduplicate the weld lattice (<c>Edge</c> upstream).</summary>
    private struct Edge : IEquatable<Edge>
    {
        public int A;
        public int B;

        public bool Equals(Edge other) => (A == other.A && B == other.B) || (A == other.B && B == other.A);

        public override bool Equals(object? obj) => obj is Edge other && Equals(other);

        public override int GetHashCode() => A + B;
    }

    /// <summary>Insertion-ordered unique edge set (managed replacement for <c>QuickSet&lt;Edge, Edge&gt;</c>).</summary>
    private sealed class EdgeSet
    {
        private readonly List<Edge> _items;
        private readonly HashSet<Edge> _seen;

        public EdgeSet(int capacity)
        {
            _items = new List<Edge>(capacity);
            _seen = new HashSet<Edge>(capacity);
        }

        public int Count => _items.Count;

        public Edge this[int index] => _items[index];

        public bool Add(Edge edge) => _seen.Add(edge) && Append(edge);

        private bool Append(Edge edge)
        {
            _items.Add(edge);
            return true;
        }
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly CollidableProperty<DeformableCollisionFilter> _filters = new();

    private long _seq;

    public NewtDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DeformableCallbacks(_filters, new PairMaterialProperties(1f, 2f, new SpringSettings(30, 1))),
            new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0), 0, 0),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        // Embedded newt.obj -> triangles -> voxel tetrahedralization (shared by every newt).
        var mesh = ObjMeshParser.Parse(DemoContent.NewtObjText);
        var triangles = new Triangle[mesh.Indices.Length / 3];
        for (var i = 0; i < triangles.Length; ++i)
        {
            var baseIndex = i * 3;
            triangles[i] = new Triangle(
                mesh.Vertices[mesh.Indices[baseIndex]],
                mesh.Vertices[mesh.Indices[baseIndex + 1]],
                mesh.Vertices[mesh.Indices[baseIndex + 2]]);
        }

        DumbTetrahedralizer.Tetrahedralize(
            triangles, CellSize,
            out var vertices, out var vertexSpatialIndices, out var cellVertexIndices, out var tetrahedraVertexIndices);

        var weldSpringiness = new SpringSettings(30f, 1f);
        var volumeSpringiness = new SpringSettings(30, 1);

        for (var i = 0; i < NewtCount; ++i)
        {
            CreateDeformable(
                _simulation, new Vector3(i * 3, 5 + i * 1.5f, 0),
                QuaternionEx.CreateFromAxisAngle(new Vector3(1, 0, 0), MathF.PI * (i * 0.55f)),
                1f, CellSize, weldSpringiness, volumeSpringiness, i, _filters,
                vertices, vertexSpatialIndices, cellVertexIndices, tetrahedraVertexIndices, i);
        }

        // Drop something heavy on one of the newts. The newt probably won't mind.
        var ballShape = new Sphere(5);
        var ballHandle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(new Vector3(0, 100, -0.5f)), ballShape.ComputeInertia(10), _simulation.Shapes.Add(ballShape), 0.01f));
        _poses.AddDynamic(BallRenderId, ballHandle, new Vector3(10, 10, 10));

        var floorShape = new Box(750, 0.5f, 750);
        var floorPosition = new Vector3(0, -0.5f, 0);
        _simulation.Statics.Add(new StaticDescription(floorPosition, _simulation.Shapes.Add(floorShape)));
        _poses.AddStatic(FloorRenderId, floorPosition, Quaternion.Identity, new Vector3(1500, 1, 1500));

        var staticSphere = new Sphere(3);
        var staticSpherePosition = new Vector3(0, -1.5f, 0);
        _simulation.Statics.Add(new StaticDescription(staticSpherePosition, _simulation.Shapes.Add(staticSphere)));
        _poses.AddStatic(StaticSphereRenderId, staticSpherePosition, Quaternion.Identity, new Vector3(6, 6, 6));
    }

    /// <summary>Test probe: deformable node count of the first newt.</summary>
    internal int NodeCount { get; private set; }

    /// <summary>Test probe: total weld + volume constraint count.</summary>
    internal int ConstraintCount { get; private set; }

    private void CreateDeformable(
        Simulation simulation, Vector3 position, Quaternion orientation, float density, float cellSize,
        SpringSettings weldSpringiness, SpringSettings volumeSpringiness, int instanceId,
        CollidableProperty<DeformableCollisionFilter> filters,
        Vector3[] vertices, Cell[] vertexSpatialIndices, CellVertexIndices[] cellVertexIndices,
        TetrahedronVertices[] tetrahedraVertexIndices, int newtIndex)
    {
        var vertexEdgeCounts = new int[vertices.Length];
        var edges = new EdgeSet(vertices.Length * 3);
        var edgeCountForInternalVertex = CreateHexahedralUniqueEdgesList(cellVertexIndices, vertexEdgeCounts, edges);

        var vertexHandles = new BodyHandle[vertices.Length];
        var vertexShape = new Sphere(cellSize * 0.7f);
        var massPerVertex = density * (cellSize * cellSize * cellSize);
        var vertexInertia = vertexShape.ComputeInertia(massPerVertex);
        var vertexShapeIndex = simulation.Shapes.Add(vertexShape);
        for (var i = 0; i < vertices.Length; ++i)
        {
            vertexHandles[i] = simulation.Bodies.Add(BodyDescription.CreateDynamic(
                new RigidPose(position + QuaternionEx.Transform(vertices[i], orientation), orientation),
                vertexInertia,
                // Bodies don't have to have collidables; take advantage of that for internal vertices.
                vertexEdgeCounts[i] == edgeCountForInternalVertex ? new CollidableDescription() : new CollidableDescription(vertexShapeIndex, 0.1f),
                0.01f));
            ref var vertexSpatialIndex = ref vertexSpatialIndices[i];
            filters.Allocate(vertexHandles[i]) = new DeformableCollisionFilter(vertexSpatialIndex.X, vertexSpatialIndex.Y, vertexSpatialIndex.Z, instanceId);
            _poses.AddDynamic(
                NodeRenderIdBase + newtIndex * NodeRenderIdStride + i,
                vertexHandles[i],
                new Vector3(cellSize * 1.4f, cellSize * 1.4f, cellSize * 1.4f));
        }

        for (var i = 0; i < edges.Count; ++i)
        {
            var edge = edges[i];
            var offset = vertices[edge.B] - vertices[edge.A];
            simulation.Solver.Add(vertexHandles[edge.A], vertexHandles[edge.B], new Weld
            {
                LocalOffset = offset,
                LocalOrientation = Quaternion.Identity,
                SpringSettings = weldSpringiness,
            });
        }

        // Volume constraints add a fairly subtle effect on top of the already stiff welds.
        for (var i = 0; i < tetrahedraVertexIndices.Length; ++i)
        {
            ref var tetrahedron = ref tetrahedraVertexIndices[i];
            simulation.Solver.Add(
                vertexHandles[tetrahedron.A], vertexHandles[tetrahedron.B], vertexHandles[tetrahedron.C], vertexHandles[tetrahedron.D],
                new VolumeConstraint(vertices[tetrahedron.A], vertices[tetrahedron.B], vertices[tetrahedron.C], vertices[tetrahedron.D], volumeSpringiness));
        }

        if (newtIndex == 0)
        {
            NodeCount = vertices.Length;
            ConstraintCount = _simulation.Solver.CountConstraints();
        }
    }

    private static int CreateHexahedralUniqueEdgesList(CellVertexIndices[] cellVertexIndices, int[] vertexEdgeCounts, EdgeSet edges)
    {
        for (var i = 0; i < cellVertexIndices.Length; ++i)
        {
            ref var cell = ref cellVertexIndices[i];
            TryAddEdge(cell.V000, cell.V001, edges, vertexEdgeCounts);
            TryAddEdge(cell.V000, cell.V010, edges, vertexEdgeCounts);
            TryAddEdge(cell.V000, cell.V100, edges, vertexEdgeCounts);
            TryAddEdge(cell.V001, cell.V011, edges, vertexEdgeCounts);
            TryAddEdge(cell.V001, cell.V101, edges, vertexEdgeCounts);
            TryAddEdge(cell.V010, cell.V011, edges, vertexEdgeCounts);
            TryAddEdge(cell.V010, cell.V110, edges, vertexEdgeCounts);
            TryAddEdge(cell.V011, cell.V111, edges, vertexEdgeCounts);
            TryAddEdge(cell.V100, cell.V101, edges, vertexEdgeCounts);
            TryAddEdge(cell.V100, cell.V110, edges, vertexEdgeCounts);
            TryAddEdge(cell.V101, cell.V111, edges, vertexEdgeCounts);
            TryAddEdge(cell.V110, cell.V111, edges, vertexEdgeCounts);
        }

        return 6;
    }

    private static void TryAddEdge(int a, int b, EdgeSet edges, int[] vertexEdgeCounts)
    {
        if (edges.Add(new Edge { A = a, B = b }))
        {
            ++vertexEdgeCounts[a];
            ++vertexEdgeCounts[b];
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

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;
        var states = new List<Transform3DState>(3 + NodeCount * NewtCount);
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
