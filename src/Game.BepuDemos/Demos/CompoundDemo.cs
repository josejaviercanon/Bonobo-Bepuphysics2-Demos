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
///     Port of the upstream <c>CompoundDemo</c> (BepuPhysics2, Apache-2.0): <c>Compound</c> (no
///     acceleration structure, few children) vs <c>BigCompound</c> (128-child tree-accelerated),
///     built through <c>CompoundBuilder</c> including the recentering overload. Fixture: a
///     capsule+box compound, four 3×3 sphere-grid compounds, a 17-table stack family, a
///     clamp-shaped compound, eight 128-child big compounds and a deformed-plane static mesh.
///
///     Rendering note (documented in docs/compat-review.md): every compound child is emitted as
///     its own <c>Transform3DState</c> record — the parent Bepu pose composed with the child's
///     local pose — so the client renders one primitive per child with no new signal type. The
///     deformed plane is rebuilt client-side from the same formula (presentation only).
///
///     Render-id ranges: 0 = ground, 10 = static sphere, 20 = deformed plane,
///     1000+ = sphere children, 2000+ = capsule children, 3000+ = box children.
/// </summary>
public sealed class CompoundDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int StaticSphereRenderId = 10;
    public const int PlaneRenderId = 20;
    public const int SphereChildRenderIdBase = 1000;
    public const int CapsuleChildRenderIdBase = 2000;
    public const int BoxChildRenderIdBase = 3000;

    public const int PlaneWidth = 48;
    public const int PlaneHeight = 48;
    public const float PlaneScaleX = 2f;
    public const float PlaneScaleY = 1f;
    public const float PlaneScaleZ = 2f;

    public const int BufferCapacity = SignalBuffer.HeaderLength + 4096 * SignalBufferLayout.Transform3DStride;

    public const int ShapeKindSphere = 0;
    public const int ShapeKindCapsule = 1;
    public const int ShapeKindBox = 2;

    private readonly record struct ChildTemplate(int Kind, Vector3 Scale);

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "compound",
        "compound",
        BufferCapacity,
        static (config, transport) => new CompoundDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private int _nextSphereChildId;
    private int _nextCapsuleChildId;
    private int _nextBoxChildId;
    private int _compoundBodyCount;
    private long _seq;

    public CompoundDemo(
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

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int CompoundBodyCount => _compoundBodyCount;

    /// <summary>Test probe: the Bepu body handle owned by a child render id.</summary>
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

    /// <summary>Rebuilds the scene deterministically (bodies, shapes and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _nextSphereChildId = 0;
            _nextCapsuleChildId = 0;
            _nextBoxChildId = 0;
            _compoundBodyCount = 0;
            _seq = 0;
            BuildSceneLocked();
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

    private void BuildSceneLocked()
    {
        using (var compoundBuilder = new CompoundBuilder(_bufferPool, _simulation.Shapes, 8))
        {
            // Capsule + box, recentered: children are far from the origin and the builder
            // outputs the computed center used as the body pose.
            {
                var capsuleChildShape = new Capsule(0.5f, 0.5f);
                var capsuleLocalPose = new RigidPose { Position = new Vector3(-0.5f, 4f, 4f), Orientation = Quaternion.Identity };
                var boxChildShape = new Box(0.5f, 1f, 1.5f);
                var boxLocalPose = new RigidPose { Position = new Vector3(0.5f, 4f, 4f), Orientation = Quaternion.Identity };

                var templates = new List<ChildTemplate>();
                compoundBuilder.Add(capsuleChildShape, capsuleLocalPose, 1);
                templates.Add(new ChildTemplate(ShapeKindCapsule, new Vector3(1f)));
                compoundBuilder.Add(boxChildShape, boxLocalPose, 1);
                templates.Add(new ChildTemplate(ShapeKindBox, new Vector3(0.5f, 1f, 1.5f)));

                compoundBuilder.BuildDynamicCompound(out var compoundChildren, out var compoundInertia, out var compoundCenter);
                compoundBuilder.Reset();
                var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                    compoundCenter, compoundInertia, _simulation.Shapes.Add(new Compound(compoundChildren)), 0.01f));
                RegisterChildrenLocked(handle, compoundChildren, templates);
            }

            // A stack of 3×3 sphere-grid compounds (manifold reduction stress).
            {
                var gridShape = new Sphere(0.5f);
                const float gridSpacing = 1.5f;
                const int gridWidth = 3;
                var gridShapeIndex = _simulation.Shapes.Add(gridShape);
                var gridBoxInertia = gridShape.ComputeInertia(1f);
                var localPoseOffset = -0.5f * gridSpacing * (gridWidth - 1);
                var gridTemplates = new List<ChildTemplate>();
                for (var i = 0; i < gridWidth; ++i)
                {
                    for (var j = 0; j < gridWidth; ++j)
                    {
                        compoundBuilder.Add(
                            gridShapeIndex,
                            new RigidPose(new Vector3(localPoseOffset, 0f, localPoseOffset) + new Vector3(gridSpacing) * new Vector3(i, 0, j)),
                            gridBoxInertia.InverseInertiaTensor, 1);
                        gridTemplates.Add(new ChildTemplate(ShapeKindSphere, new Vector3(1f)));
                    }
                }

                compoundBuilder.BuildDynamicCompound(out var gridChildren, out var gridInertia, out _);
                compoundBuilder.Reset();
                var gridCompoundIndex = _simulation.Shapes.Add(new Compound(gridChildren));
                for (var i = 0; i < 4; ++i)
                {
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(new Vector3(0f, 2f + i * 3f, 0f)), gridInertia, gridCompoundIndex, 0.01f));
                    RegisterChildrenLocked(handle, gridChildren, gridTemplates);
                }
            }

            // Tables: four legs plus a top, stacked, on a sphere and with a clamp on top.
            {
                var tableTemplates = new List<ChildTemplate>();
                var legShape = new Box(0.2f, 1f, 0.2f);
                var legInverseInertia = legShape.ComputeInertia(1f);
                var legShapeIndex = _simulation.Shapes.Add(legShape);
                var legPoses = new[]
                {
                    new RigidPose(new Vector3(-1.5f, 0f, -1.5f), Quaternion.Identity),
                    new RigidPose(new Vector3(-1.5f, 0f, 1.5f), Quaternion.Identity),
                    new RigidPose(new Vector3(1.5f, 0f, -1.5f), Quaternion.Identity),
                    new RigidPose(new Vector3(1.5f, 0f, 1.5f), Quaternion.Identity),
                };
                foreach (var legPose in legPoses)
                {
                    compoundBuilder.Add(legShapeIndex, legPose, legInverseInertia.InverseInertiaTensor, 1);
                    tableTemplates.Add(new ChildTemplate(ShapeKindBox, new Vector3(0.2f, 1f, 0.2f)));
                }

                var tableTopShape = new Box(3.2f, 0.2f, 3.2f);
                compoundBuilder.Add(tableTopShape, new RigidPose(new Vector3(0f, 0.6f, 0f), Quaternion.Identity), 3);
                tableTemplates.Add(new ChildTemplate(ShapeKindBox, new Vector3(3.2f, 0.2f, 3.2f)));

                compoundBuilder.BuildDynamicCompound(out var tableChildren, out var tableInertia, out _);
                compoundBuilder.Reset();
                var tableIndex = _simulation.Shapes.Add(new Compound(tableChildren));

                for (var i = 0; i < 10; ++i)
                {
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(new Vector3(10f, 3f + i * 1.4f, 10f)), tableInertia, tableIndex, 0.01f));
                    RegisterChildrenLocked(handle, tableChildren, tableTemplates);
                }

                for (var k = 0; k < 5; ++k)
                {
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(new Vector3(64f + k * 3f, 6f + k * 1.4f, 32f)), tableInertia, tableIndex, 0.01f));
                    RegisterChildrenLocked(handle, tableChildren, tableTemplates);
                }

                // Table on a sphere: divergent normals stress the nonconvex reduction.
                {
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(new Vector3(10f, 6f, 0f)), tableInertia, tableIndex, 0.01f));
                    RegisterChildrenLocked(handle, tableChildren, tableTemplates);

                    var sphereShape = new Sphere(3f);
                    _simulation.Statics.Add(new StaticDescription(
                        new Vector3(10f, 2f, 0f), _simulation.Shapes.Add(sphereShape)));
                    _poses.AddStatic(StaticSphereRenderId, new Vector3(10f, 2f, 0f), Quaternion.Identity, new Vector3(6f));
                }

                // Table on the ground with a clamp-shaped compound generating opposing normals.
                {
                    var tablePosition = new Vector3(10f, 3f, -10f);
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(tablePosition), tableInertia, tableIndex, 0.01f));
                    RegisterChildrenLocked(handle, tableChildren, tableTemplates);

                    var clampPieceShape = new Box(2f, 0.1f, 0.3f);
                    var clampPieceInverseInertia = clampPieceShape.ComputeInertia(1f);
                    var clampPieceShapeIndex = _simulation.Shapes.Add(clampPieceShape);
                    var clampTemplates = new List<ChildTemplate>();
                    var clampPoses = new[]
                    {
                        new RigidPose(new Vector3(0f, -0.2f, -1.1f), Quaternion.Identity),
                        new RigidPose(new Vector3(0f, 0.2f, -1.1f), Quaternion.Identity),
                        new RigidPose(new Vector3(0f, -0.2f, 0f), Quaternion.Identity),
                        new RigidPose(new Vector3(0f, 0.2f, 0f), Quaternion.Identity),
                        new RigidPose(new Vector3(0f, -0.2f, 1.1f), Quaternion.Identity),
                        new RigidPose(new Vector3(0f, 0.2f, 1.1f), Quaternion.Identity),
                    };
                    foreach (var clampPose in clampPoses)
                    {
                        compoundBuilder.Add(clampPieceShapeIndex, clampPose, clampPieceInverseInertia.InverseInertiaTensor, 1);
                        clampTemplates.Add(new ChildTemplate(ShapeKindBox, new Vector3(2f, 0.1f, 0.3f)));
                    }

                    compoundBuilder.BuildDynamicCompound(out var clampChildren, out var clampInertia, out _);
                    compoundBuilder.Reset();
                    var clampHandle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(tablePosition + new Vector3(2f, 0.3f, 0f)),
                        clampInertia, _simulation.Shapes.Add(new Compound(clampChildren)), 0.01f));
                    RegisterChildrenLocked(clampHandle, clampChildren, clampTemplates);
                }
            }

            // Tree-accelerated big compound: 128 randomly posed children, seeded (deterministic).
            {
                var random = new Random(5);
                var bigTemplates = new List<ChildTemplate>();
                var treeCompoundBoxShape = new Box(0.5f, 1.5f, 1f);
                var treeCompoundBoxShapeIndex = _simulation.Shapes.Add(treeCompoundBoxShape);
                var childInertia = treeCompoundBoxShape.ComputeInertia(1f);
                for (var i = 0; i < 128; ++i)
                {
                    RigidPose localPose;
                    localPose.Position = new Vector3(12f, 6f, 12f) * (0.5f * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) - Vector3.One);
                    float orientationLengthSquared;
                    do
                    {
                        localPose.Orientation = new Quaternion(random.NextSingle(), random.NextSingle(), random.NextSingle(), random.NextSingle());
                        orientationLengthSquared = QuaternionEx.LengthSquared(ref localPose.Orientation);
                    }
                    while (orientationLengthSquared < 1e-9f);

                    QuaternionEx.Scale(localPose.Orientation, 1f / MathF.Sqrt(orientationLengthSquared), out localPose.Orientation);
                    compoundBuilder.Add(treeCompoundBoxShapeIndex, localPose, childInertia.InverseInertiaTensor, 1);
                    bigTemplates.Add(new ChildTemplate(ShapeKindBox, new Vector3(0.5f, 1.5f, 1f)));
                }

                compoundBuilder.BuildDynamicCompound(out var children, out var inertia, out _);
                compoundBuilder.Reset();
                var compoundIndex = _simulation.Shapes.Add(new BigCompound(children, _simulation.Shapes, _bufferPool));
                for (var i = 0; i < 8; ++i)
                {
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(new Vector3(0f, 4f + 5f * i, 32f)), inertia, compoundIndex, 0.01f));
                    RegisterChildrenLocked(handle, children, bigTemplates);
                }
            }
        }

        // Ground.
        {
            var boxShape = new Box(256f, 1f, 256f);
            _simulation.Statics.Add(new StaticDescription(RigidPose.Identity, _simulation.Shapes.Add(boxShape)));
            _poses.AddStatic(GroundRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(256f, 1f, 256f));
        }

        // Deformed plane static mesh (client rebuilds the same surface for presentation).
        {
            var planeMesh = DemoMeshHelper.CreateDeformedPlane(PlaneWidth, PlaneHeight,
                static (x, y) =>
                {
                    var offsetFromCenter = new Vector2(x - PlaneWidth / 2, y - PlaneHeight / 2);
                    return new Vector3(
                        offsetFromCenter.X,
                        MathF.Cos(x / 4f) * MathF.Sin(y / 4f) - 0.01f * offsetFromCenter.LengthSquared(),
                        offsetFromCenter.Y);
                },
                new Vector3(PlaneScaleX, PlaneScaleY, PlaneScaleZ), _bufferPool);
            _simulation.Statics.Add(new StaticDescription(
                new Vector3(64f, 4f, 32f),
                QuaternionEx.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 2f),
                _simulation.Shapes.Add(planeMesh)));
            _poses.AddStatic(
                PlaneRenderId, new Vector3(64f, 4f, 32f),
                QuaternionEx.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathF.PI / 2f),
                new Vector3(PlaneScaleX, PlaneScaleY, PlaneScaleZ));
        }
    }

    private void RegisterChildrenLocked(BodyHandle parent, Buffer<CompoundChild> children, List<ChildTemplate> templates)
    {
        _compoundBodyCount++;
        for (var i = 0; i < children.Length; i++)
        {
            var template = templates[i];
            var renderId = template.Kind switch
            {
                ShapeKindSphere => SphereChildRenderIdBase + _nextSphereChildId++,
                ShapeKindCapsule => CapsuleChildRenderIdBase + _nextCapsuleChildId++,
                _ => BoxChildRenderIdBase + _nextBoxChildId++,
            };

            var child = children[i];
            var localPose = new RigidPose(child.LocalPosition, child.LocalOrientation);
            _poses.AddDynamic(renderId, parent, template.Scale, localPose, hasLocalPose: true);
        }
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
