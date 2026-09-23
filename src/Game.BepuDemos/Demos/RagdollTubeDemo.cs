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
///     Port of the upstream <c>RagdollTubeDemo</c> (BepuPhysics2, Apache-2.0): 176 subgroup-filtered
///     capsule ragdolls tumble inside a spinning kinematic tube (a <c>BigCompound</c> of 12 panels
///     plus a spine), rendered as one parent ∘ local box record per panel.
///
///     Test-bed deviations: 4×4×11 ragdolls (upstream 4×4×44) and 12 tube panels (upstream 20) —
///     documented in docs/compat-review.md.
///
///     Render-id ranges: 0 = ground, 10 + = tube children (12 panels + spine), 100 + ragdoll·16 =
///     ragdoll bodies (hips, abdomen, chest, head, right arm ×3, left arm ×3, right leg ×3,
///     left leg ×3).
/// </summary>
public sealed class RagdollTubeDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int TubeRenderIdBase = 10;
    public const int TubePanelCount = 12;
    public const int RagdollRenderIdBase = 100;
    public const int RagdollRenderIdStride = RagdollBuilder.BodiesPerRagdoll;

    public const int GridWidth = 4;
    public const int GridHeight = 4;

    /// <summary>Upstream uses 44; reduced for desktop host interactivity.</summary>
    public const int GridLength = 11;

    public const int RagdollCount = GridWidth * GridHeight * GridLength;
    public const int MaxRecords = 1 + (TubePanelCount + 1) + RagdollCount * RagdollRenderIdStride;
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float TubeRadius = 6f;
    public const float TubeLength = 80f;
    public const float TubeSpinSpeed = 0.25f;

    /// <summary>Body-part render-id slots inside one ragdoll block (matches <c>RagdollBuilder.CopyTo</c>).</summary>
    public const int SlotHips = 0;
    public const int SlotAbdomen = 1;
    public const int SlotChest = 2;
    public const int SlotHead = 3;
    public const int SlotRightUpperArm = 4;
    public const int SlotRightLowerArm = 5;
    public const int SlotRightHand = 6;
    public const int SlotLeftUpperArm = 7;
    public const int SlotLeftLowerArm = 8;
    public const int SlotLeftHand = 9;
    public const int SlotRightUpperLeg = 10;
    public const int SlotRightLowerLeg = 11;
    public const int SlotRightFoot = 12;
    public const int SlotLeftUpperLeg = 13;
    public const int SlotLeftLowerLeg = 14;
    public const int SlotLeftFoot = 15;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "ragdoll-tube",
        "ragdoll-tube",
        BufferCapacity,
        static (config, transport) => new RagdollTubeDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<SubgroupCollisionFilter> _filters = new();
    private readonly DemoPoseSet _poses;
    private long _seq;

    public RagdollTubeDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new SubgroupFilteredCallbacks(_filters, new PairMaterialProperties(2f, float.MaxValue, new SpringSettings(10f, 1f))),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(4, 1));

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int RecordCount => _poses.Count;
    internal int ConstraintCount => _simulation.Solver.CountConstraints();
    internal int BodyCount => _simulation.Bodies.ActiveSet.Count;

    /// <summary>Test probe: the Bepu body handle owned by a render id.</summary>
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

    /// <summary>Rebuilds the fixture deterministically (bodies, constraints and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
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
        var ragdollIndex = 0;
        var spacing = new Vector3(1.7f, 1.8f, 0.5f);
        var origin = -0.5f * spacing * new Vector3(GridWidth - 1, 0, GridLength - 1) + new Vector3(0f, 5f, 0f);
        for (var i = 0; i < GridWidth; ++i)
        {
            for (var j = 0; j < GridHeight; ++j)
            {
                for (var k = 0; k < GridLength; ++k)
                {
                    var handles = RagdollBuilder.AddRagdoll(
                        origin + spacing * new Vector3(i, j, k),
                        QuaternionEx.CreateFromAxisAngle(new Vector3(0f, 1f, 0f), MathHelper.Pi * 0.05f),
                        ragdollIndex++, _filters, _simulation);

                    var bodyHandles = new BodyHandle[RagdollBuilder.BodiesPerRagdoll];
                    handles.CopyTo(bodyHandles);
                    var blockBase = RagdollRenderIdBase + (ragdollIndex - 1) * RagdollRenderIdStride;
                    for (var slot = 0; slot < bodyHandles.Length; slot++)
                    {
                        _poses.AddDynamic(blockBase + slot, bodyHandles[slot], RagdollSlotScale(slot));
                    }
                }
            }
        }

        // The tube: 12 kinematic panels around Y plus a spine, built as one BigCompound.
        var tubeCenter = new Vector3(0f, 8f, 0f);
        var panelShape = new Box(MathF.PI * 2 * TubeRadius / TubePanelCount, 1f, TubeLength);
        var panelShapeIndex = _simulation.Shapes.Add(panelShape);
        var builder = new CompoundBuilder(_bufferPool, _simulation.Shapes, TubePanelCount + 1);
        for (var i = 0; i < TubePanelCount; ++i)
        {
            var rotation = QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, i * MathHelper.TwoPi / TubePanelCount);
            QuaternionEx.TransformUnitY(rotation, out var localUp);
            var position = localUp * TubeRadius;
            builder.AddForKinematic(panelShapeIndex, (position, rotation), 1);
        }

        var spineShape = new Box(1f, 2f, TubeLength);
        var spineShapeIndex = _simulation.Shapes.Add(spineShape);
        builder.AddForKinematic(spineShapeIndex, new Vector3(0f, TubeRadius - 1f, 0f), 0);
        builder.BuildKinematicCompound(out var children);
        var compound = new BigCompound(children, _simulation.Shapes, _bufferPool, null);
        var tubeHandle = _simulation.Bodies.Add(BodyDescription.CreateKinematic(
            new RigidPose(tubeCenter, Quaternion.Identity),
            new BodyVelocity(default, new Vector3(0f, 0f, TubeSpinSpeed)),
            _simulation.Shapes.Add(compound), 0f));
        _filters[tubeHandle] = new SubgroupCollisionFilter(int.MaxValue);
        builder.Dispose();

        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            var isSpine = child.ShapeIndex.Packed == spineShapeIndex.Packed;
            var scale = isSpine ? new Vector3(1f, 2f, TubeLength) : new Vector3(panelShape.Width, 1f, TubeLength);
            _poses.AddDynamic(
                TubeRenderIdBase + i, tubeHandle, scale,
                new RigidPose(child.LocalPosition, child.LocalOrientation), hasLocalPose: true);
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f), _simulation.Shapes.Add(new Box(300f, 1f, 300f))));
        _poses.AddStatic(GroundRenderId, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, new Vector3(300f, 1f, 300f));
    }

    /// <summary>Capsule dimensions per ragdoll render-id slot (radius, inner length); (0, 0) for the box/sphere slots.</summary>
    public static readonly (float Radius, float Length)[] SlotCapsules =
    {
        (0.17f, 0.25f), // hips
        (0.17f, 0.22f), // abdomen
        (0.21f, 0.3f), // chest
        (0f, 0f), // head (sphere)
        (0.1f, 0.45f), // right upper arm
        (0.09f, 0.45f), // right lower arm
        (0f, 0f), // right hand (box)
        (0.1f, 0.45f), // left upper arm
        (0.09f, 0.45f), // left lower arm
        (0f, 0f), // left hand (box)
        (0.12f, 0.5f), // right upper leg
        (0.11f, 0.5f), // right lower leg
        (0f, 0f), // right foot (box)
        (0.12f, 0.5f), // left upper leg
        (0.11f, 0.5f), // left lower leg
        (0f, 0f), // left foot (box)
    };

    /// <summary>
    ///     Render scale for one ragdoll body slot. Capsules bake their aspect into the client mesh
    ///     (scale 1); unit box/sphere meshes carry the full dimensions in the record scale.
    /// </summary>
    public static Vector3 RagdollSlotScale(int slot) => SlotCapsules[slot].Radius > 0f
        ? Vector3.One
        : slot switch
        {
            SlotHead => new Vector3(0.4f),
            SlotRightHand or SlotLeftHand => new Vector3(0.2f, 0.1f, 0.2f),
            _ => new Vector3(0.2f, 0.15f, 0.3f),
        };

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
