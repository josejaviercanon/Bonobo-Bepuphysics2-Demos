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
///     Port of the upstream <c>ClothDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby): four
///     10×30 curtain lattices (stiff/soft distance constraints, with/without area constraints)
///     hanging from kinematic top corners, plus a 48×48 fully dynamic sheet, draped over two
///     static capsule bars and a floor box.
///
///     Design note (docs/compat-review.md): cloth panels emit vertex-level render records using
///     the existing <see cref="Transform3DState"/> stride-12 layout — id = panel base +
///     row·width + column, quaternion unused, scale carries the node diameter. A demo-specific
///     record struct was rejected: the transform decoder already fits, no new TS decoder or
///     AbiPinTests surface is needed, and the extra fields cost ~340 KB of pinned buffer.
///     The client rebuilds one grid mesh per panel from the records every frame.
///
///     Test-bed deviations: the sheet is reduced from 96×96 to <see cref="SheetWidth"/>² (the
///     full lattice is 4× larger than every other ported constraint demo combined); the
///     upstream rollover text is dropped (no text-overlay channel).
///
///     Render-id ranges: 0 = floor static, 1..2 = static wrap bars,
///     10000 + panelIndex·4096 + row·width + column = cloth nodes (panels 0..3 curtains,
///     panel 4 sheet).
/// </summary>
public sealed class ClothDemo : IDemoSimulation
{
    public const int FloorRenderId = 0;
    public const int BarRenderIdBase = 1;

    public const int ClothNodeRenderIdBase = 10_000;
    public const int ClothNodeRenderIdStride = 4_096;

    public const int CurtainWidth = 10;
    public const int CurtainHeight = 30;
    public const int CurtainCount = 4;
    public const int SheetWidth = 48;
    public const int SheetHeight = 48;

    public const int PanelCount = CurtainCount + 1;
    public const int NodeCount = CurtainCount * CurtainWidth * CurtainHeight + SheetWidth * SheetHeight;
    public const int MaxTransformCount = 3 + NodeCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "cloth",
        "cloth",
        BufferCapacity,
        static (config, transport) => new ClothDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private delegate bool KinematicDecider(int rowIndex, int columnIndex, int width, int height);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly CollidableProperty<ClothCollisionFilter> _filters = new();

    private long _seq;

    /// <summary>Panel geometry (width, height, spacing, radius) indexed by panel id, for tests/client.</summary>
    public static readonly int[] PanelWidths = [CurtainWidth, CurtainWidth, CurtainWidth, CurtainWidth, SheetWidth];

    public static readonly int[] PanelHeights = [CurtainHeight, CurtainHeight, CurtainHeight, CurtainHeight, SheetHeight];

    public ClothDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new ClothCallbacks(_filters),
            new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        var initialRotation = QuaternionEx.CreateFromAxisAngle(new Vector3(1, 0, 0), MathF.PI * -0.5f);

        // Four curtains: stiff/soft distance constraints, with/without area constraints.
        CreatePanel(new Vector3(-90, 40, 0), initialRotation, CurtainWidth, CurtainHeight, 1f, 0.65f, 0, true,
            new SpringSettings(20, 1), null);
        CreatePanel(new Vector3(-70, 40, 0), initialRotation, CurtainWidth, CurtainHeight, 1f, 0.65f, 1, true,
            new SpringSettings(20, 1), new SpringSettings(30, 1));
        CreatePanel(new Vector3(-50, 40, 0), initialRotation, CurtainWidth, CurtainHeight, 1f, 0.65f, 2, true,
            new SpringSettings(5, 1), null);
        CreatePanel(new Vector3(-30, 40, 0), initialRotation, CurtainWidth, CurtainHeight, 1f, 0.65f, 3, true,
            new SpringSettings(5, 1), new SpringSettings(30, 1));

        // Static wrap bars (capsule contract: records emit scale 1, the client bakes one mesh per
        // (radius, length) pair).
        var barScale = Vector3.One;
        var bar0Position = new Vector3(60, 20, 0);
        var bar0Orientation = QuaternionEx.CreateFromAxisAngle(new Vector3(0, 0, 1), MathF.PI * 0.5f);
        _simulation.Statics.Add(new StaticDescription(bar0Position, bar0Orientation, _simulation.Shapes.Add(new Capsule(8, 120))));
        _poses.AddStatic(BarRenderIdBase, bar0Position, bar0Orientation, barScale);

        var bar1Position = new Vector3(30, 5, 0);
        var bar1Orientation = QuaternionEx.CreateFromAxisAngle(new Vector3(1, 0, 0), MathF.PI * 0.5f);
        _simulation.Statics.Add(new StaticDescription(bar1Position, bar1Orientation, _simulation.Shapes.Add(new Capsule(8, 60))));
        _poses.AddStatic(BarRenderIdBase + 1, bar1Position, bar1Orientation, barScale);

        // Fully dynamic sheet.
        CreatePanel(new Vector3(10, 40, -32), initialRotation, SheetWidth, SheetHeight, 0.666f, 0.5f, 4, false,
            new SpringSettings(10, 1), new SpringSettings(30, 1));

        var floorPosition = new Vector3(-40, 0, 0);
        _simulation.Statics.Add(new StaticDescription(floorPosition, _simulation.Shapes.Add(new Box(200, 1, 200))));
        _poses.AddStatic(FloorRenderId, floorPosition, Quaternion.Identity, new Vector3(400f, 2f, 400f));
    }

    /// <summary>Test probe: total registered cloth node records.</summary>
    internal int NodeRecordCount { get; private set; }

    /// <summary>Test probe: total constraint count (distance + area).</summary>
    internal int ConstraintCount { get; private set; }

    private void CreatePanel(
        Vector3 position, Quaternion orientation, int width, int height, float spacing, float bodyRadius,
        int instanceId, bool kinematicTopCorners, SpringSettings distanceSpring, SpringSettings? areaSpring)
    {
        var handles = CreateBodyGrid(position, orientation, width, height, spacing, bodyRadius, instanceId,
            kinematicTopCorners
                ? static (rowIndex, columnIndex, w, _) => rowIndex == 0 && (columnIndex == w - 1 || columnIndex == 0)
                : static (_, _, _, _) => false);

        CreateDistanceConstraints(handles, width, height, distanceSpring);
        if (areaSpring.HasValue) CreateAreaConstraints(handles, width, height, areaSpring.Value);

        if (instanceId == PanelCount - 1)
        {
            NodeRecordCount = (CurtainCount * CurtainWidth * CurtainHeight) + width * height;
            ConstraintCount = _simulation.Solver.CountConstraints();
        }
    }

    private BodyHandle[,] CreateBodyGrid(
        Vector3 position, Quaternion orientation, int width, int height, float spacing, float bodyRadius,
        int instanceId, KinematicDecider isKinematic)
    {
        var description = BodyDescription.CreateDynamic(orientation, default, _simulation.Shapes.Add(new Sphere(bodyRadius)), 0.01f);
        var inverseMass = 1f;
        var handles = new BodyHandle[height, width];
        for (var rowIndex = 0; rowIndex < height; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < width; ++columnIndex)
            {
                description.LocalInertia.InverseMass = isKinematic(rowIndex, columnIndex, width, height) ? 0 : inverseMass;
                var localPosition = new Vector3(columnIndex * spacing, rowIndex * -spacing, 0);
                QuaternionEx.TransformWithoutOverlap(localPosition, orientation, out var rotatedPosition);
                description.Pose.Position = rotatedPosition + position;
                var handle = _simulation.Bodies.Add(description);
                handles[rowIndex, columnIndex] = handle;
                _filters.Allocate(handle) = new ClothCollisionFilter(rowIndex, columnIndex, instanceId);

                // Vertex-level render record: row-major id inside the panel's id range.
                _poses.AddDynamic(
                    ClothNodeRenderIdBase + instanceId * ClothNodeRenderIdStride + rowIndex * width + columnIndex,
                    handle,
                    new Vector3(bodyRadius * 2f, bodyRadius * 2f, bodyRadius * 2f));
            }
        }

        return handles;
    }

    private void CreateDistanceConstraints(BodyHandle[,] bodyHandles, int width, int height, SpringSettings springSettings)
    {
        void CreateConstraintBetweenBodies(BodyHandle aHandle, BodyHandle bHandle)
        {
            var a = _simulation.Bodies[aHandle];
            var b = _simulation.Bodies[bHandle];
            // Don't create constraints between two kinematic bodies.
            if (a.LocalInertia.InverseMass > 0 || b.LocalInertia.InverseMass > 0)
            {
                // A limit lets the distance go smaller, which stops the cloth from feeling rigid.
                var distance = Vector3.Distance(a.Pose.Position, b.Pose.Position);
                _simulation.Solver.Add(aHandle, bHandle, new CenterDistanceLimit(distance * 0.15f, distance, springSettings));
            }
        }

        for (var rowIndex = 0; rowIndex < height; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < width - 1; ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex, columnIndex + 1]);
            }
        }

        for (var rowIndex = 0; rowIndex < height - 1; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < width; ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex + 1, columnIndex]);
            }
        }

        for (var rowIndex = 0; rowIndex < height - 1; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < width - 1; ++columnIndex)
            {
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex], bodyHandles[rowIndex + 1, columnIndex + 1]);
                CreateConstraintBetweenBodies(bodyHandles[rowIndex, columnIndex + 1], bodyHandles[rowIndex + 1, columnIndex]);
            }
        }
    }

    private void CreateAreaConstraints(BodyHandle[,] bodyHandles, int width, int height, SpringSettings springSettings)
    {
        for (var rowIndex = 0; rowIndex < height - 1; ++rowIndex)
        {
            for (var columnIndex = 0; columnIndex < width - 1; ++columnIndex)
            {
                var aHandle = bodyHandles[rowIndex, columnIndex];
                var bHandle = bodyHandles[rowIndex + 1, columnIndex];
                var cHandle = bodyHandles[rowIndex, columnIndex + 1];
                var dHandle = bodyHandles[rowIndex + 1, columnIndex + 1];
                var a = _simulation.Bodies[aHandle];
                var b = _simulation.Bodies[bHandle];
                var c = _simulation.Bodies[cHandle];
                var d = _simulation.Bodies[dHandle];
                // At most one row of kinematics exists per panel, so a quad is never all kinematic.
                _simulation.Solver.Add(aHandle, bHandle, cHandle, new AreaConstraint(a.Pose.Position, b.Pose.Position, c.Pose.Position, springSettings));
                _simulation.Solver.Add(bHandle, cHandle, dHandle, new AreaConstraint(b.Pose.Position, c.Pose.Position, d.Pose.Position, springSettings));
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

            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        ++_seq;
        var states = new List<Transform3DState>(MaxTransformCount);
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
