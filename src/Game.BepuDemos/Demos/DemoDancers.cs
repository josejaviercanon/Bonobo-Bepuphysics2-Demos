using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Handles of the 12 source-dancer bodies (no hands or feet: they aren't important for
///     scooting a dress around). Ported from the upstream BepuPhysics2 <c>DemoDancers</c>
///     (Apache-2.0, Ross Nordby).
/// </summary>
public struct DancerBodyHandles
{
    /// <summary>Number of dancer bodies (render-id slot stride per dancer).</summary>
    public const int Count = 12;

    public BodyHandle UpperLeftLeg;
    public BodyHandle LowerLeftLeg;
    public BodyHandle UpperRightLeg;
    public BodyHandle LowerRightLeg;
    public BodyHandle UpperLeftArm;
    public BodyHandle LowerLeftArm;
    public BodyHandle UpperRightArm;
    public BodyHandle LowerRightArm;
    public BodyHandle Hips;
    public BodyHandle Abdomen;
    public BodyHandle Chest;
    public BodyHandle Head;

    /// <summary>Slot order used by every per-dancer render-id range: torso, arms, legs (upstream layout).</summary>
    public BodyHandle this[int index]
    {
        get => index switch
        {
            0 => UpperLeftLeg,
            1 => LowerLeftLeg,
            2 => UpperRightLeg,
            3 => LowerRightLeg,
            4 => UpperLeftArm,
            5 => LowerLeftArm,
            6 => UpperRightArm,
            7 => LowerRightArm,
            8 => Hips,
            9 => Abdomen,
            10 => Chest,
            _ => Head,
        };
        set
        {
            switch (index)
            {
                case 0: UpperLeftLeg = value; break;
                case 1: LowerLeftLeg = value; break;
                case 2: UpperRightLeg = value; break;
                case 3: LowerRightLeg = value; break;
                case 4: UpperLeftArm = value; break;
                case 5: LowerLeftArm = value; break;
                case 6: UpperRightArm = value; break;
                case 7: LowerRightArm = value; break;
                case 8: Hips = value; break;
                case 9: Abdomen = value; break;
                case 10: Chest = value; break;
                default: Head = value; break;
            }
        }
    }

    public void CopyTo(Span<BodyHandle> target)
    {
        for (var i = 0; i < Count; i++) target[i] = this[i];
    }
}

/// <summary>Controls one of the main dancer's limbs by yanking it around (upstream port).</summary>
public struct DancerControl
{
    public ConstraintHandle Servo;
    public Vector3 LocalOffset;
    public ServoSettings ServoSettings;
    public SpringSettings SpringSettings;

    public DancerControl(Simulation simulation, BodyHandle body, Vector3 worldControlPoint, ServoSettings servoSettings, SpringSettings springSettings)
    {
        ServoSettings = servoSettings;
        SpringSettings = springSettings;
        var pose = simulation.Bodies[body].Pose;
        LocalOffset = QuaternionEx.Transform(worldControlPoint - pose.Position, Quaternion.Conjugate(pose.Orientation));
        Servo = simulation.Solver.Add(body, new OneBodyLinearServo
        {
            ServoSettings = servoSettings,
            SpringSettings = springSettings,
            LocalOffset = LocalOffset,
            Target = worldControlPoint,
        });
    }

    public void UpdateTarget(Simulation simulation, Vector3 target)
    {
        simulation.Solver.ApplyDescription(Servo, new OneBodyLinearServo
        {
            ServoSettings = ServoSettings,
            SpringSettings = SpringSettings,
            LocalOffset = LocalOffset,
            Target = target,
        });
    }
}

/// <summary>Marks a narrow phase callbacks struct as usable with the <see cref="DemoDancers"/> infrastructure.</summary>
public interface IDancerNarrowPhaseCallbacks<TCallbacks, TFilter>
    where TCallbacks : struct, INarrowPhaseCallbacks, IDancerNarrowPhaseCallbacks<TCallbacks, TFilter>
    where TFilter : unmanaged
{
    static abstract TCallbacks Create(
        CollidableProperty<TFilter> filters, PairMaterialProperties pairMaterialProperties, int minimumDistanceForSelfCollisions);
}

/// <summary>
///     Coordinates the main dancer and its background copies, ported from the upstream
///     BepuPhysics2 <c>DemoDancers</c> (Apache-2.0, Ross Nordby). Each background dancer owns a
///     separate cosmetic simulation; the 12 core bodies are kinematic shells teleported from a
///     motion-history ring, and the dress/suit attachments are dynamic.
///
///     Deviation (documented in docs/compat-review.md): the upstream demo runs the per-dancer
///     timesteps in parallel through a <c>ParallelLooper</c>; this port steps them sequentially
///     to stay bit-deterministic under the null-<c>ThreadDispatcher</c> rule.
/// </summary>
public sealed class DemoDancers
{
    public const float LegOffsetX = 0.135f;
    public const float ArmOffsetX = 0.25f;
    public const int HistoryLength = 256;

    /// <summary>Fixed timestep used for the per-dancer cosmetic solves.</summary>
    public const float TickDurationSeconds = 1f / 60f;

    public DancerBodyHandles SourceBodyHandles;
    public Simulation[] Simulations = Array.Empty<Simulation>();
    public DancerBodyHandles[] Handles = Array.Empty<DancerBodyHandles>();
    public int BodyCount;
    public int ConstraintCount;
    public int DancerGridWidth = 16;
    public int DancerGridLength = 16;
    public double ExecutionTime;

    private readonly MotionState[] _history = new MotionState[HistoryLength * DancerBodyHandles.Count];
    private int _historyStart;
    private int _historyCount;
    private DancerControl _hipsControl;
    private DancerControl _leftFootControl;
    private DancerControl _rightFootControl;
    private DancerControl _leftHandControl;
    private DancerControl _rightHandControl;
    private double _time;

    public delegate void DressUpDancer<TCollisionFilter>(
        Simulation simulation, CollidableProperty<TCollisionFilter> filters, DancerBodyHandles bodyHandles,
        int dancerIndex, int dancerGridWidth, float levelOfDetail)
        where TCollisionFilter : unmanaged;

    /// <summary>Number of dancers to run (grid area).</summary>
    public int DancerCount => Handles.Length;

    public DemoDancers Initialize<TNarrowPhaseCallbacks, TCollisionFilter>(
        int dancerGridWidth, int dancerGridLength, Simulation mainSimulation,
        CollidableProperty<SubgroupCollisionFilter> mainCollisionFilters, BufferPool pool,
        SolveDescription dancerSolveDescription, DressUpDancer<TCollisionFilter> dressUpDancer, TCollisionFilter filterForDancerBodies)
        where TNarrowPhaseCallbacks : struct, INarrowPhaseCallbacks, IDancerNarrowPhaseCallbacks<TNarrowPhaseCallbacks, TCollisionFilter>
        where TCollisionFilter : unmanaged
    {
        DancerGridWidth = dancerGridWidth;
        DancerGridLength = dancerGridLength;

        var hipsPosition = new Vector3(0, 0, 0);
        var abdomenPosition = hipsPosition + new Vector3(0, 0.25f, 0);
        var chestPosition = abdomenPosition + new Vector3(0, 0.25f, 0);
        var headPosition = chestPosition + new Vector3(0, 0.4f, 0);
        var kneePosition = hipsPosition - new Vector3(0, 0.5f, 0);
        var anklePosition = kneePosition - new Vector3(0, 0.5f, 0);
        var elbowPosition = chestPosition + new Vector3(0, 0.39f, 0);
        var wristPosition = elbowPosition + new Vector3(0, 0.39f, 0);
        var armOffset = new Vector3(ArmOffsetX, 0, 0);
        var legOffset = new Vector3(LegOffsetX, 0, 0);
        const int groupIndex = 0;

        // Build the torso and head bodies.
        RagdollBuilder.GetCapsuleForLineSegment(hipsPosition - legOffset, hipsPosition + legOffset, 0.14f, out var hipShape, out _, out var hipOrientation);
        SourceBodyHandles.Hips = mainSimulation.Bodies.Add(BodyDescription.CreateDynamic(
            (hipsPosition, hipOrientation), hipShape.ComputeInertia(1), mainSimulation.Shapes.Add(hipShape), 0.01f));
        ref var hipsFilter = ref mainCollisionFilters.Allocate(SourceBodyHandles.Hips);
        hipsFilter = new SubgroupCollisionFilter(groupIndex, 0);

        RagdollBuilder.GetCapsuleForLineSegment(abdomenPosition - legOffset * 0.8f, abdomenPosition + legOffset * 0.8f, 0.13f, out var abdomenShape, out _, out var abdomenOrientation);
        SourceBodyHandles.Abdomen = mainSimulation.Bodies.Add(BodyDescription.CreateDynamic(
            (abdomenPosition, abdomenOrientation), abdomenShape.ComputeInertia(1), mainSimulation.Shapes.Add(abdomenShape), 0.01f));
        ref var abdomenFilter = ref mainCollisionFilters.Allocate(SourceBodyHandles.Abdomen);
        abdomenFilter = new SubgroupCollisionFilter(groupIndex, 1);

        RagdollBuilder.GetCapsuleForLineSegment(abdomenPosition - legOffset * 0.8f, abdomenPosition + legOffset * 0.8f, 0.165f, out var chestShape, out _, out var chestOrientation);
        SourceBodyHandles.Chest = mainSimulation.Bodies.Add(BodyDescription.CreateDynamic(
            (chestPosition, chestOrientation), chestShape.ComputeInertia(1), mainSimulation.Shapes.Add(chestShape), 0.01f));
        ref var chestFilter = ref mainCollisionFilters.Allocate(SourceBodyHandles.Chest);
        chestFilter = new SubgroupCollisionFilter(groupIndex, 2);

        var headShape = new Sphere(0.17f);
        SourceBodyHandles.Head = mainSimulation.Bodies.Add(BodyDescription.CreateDynamic(
            headPosition, headShape.ComputeInertia(1), mainSimulation.Shapes.Add(headShape), 1e-2f));
        ref var headFilter = ref mainCollisionFilters.Allocate(SourceBodyHandles.Head);
        headFilter = new SubgroupCollisionFilter(groupIndex, 3);

        var springSettings = new SpringSettings(30, 1);
        Connect(mainSimulation, SourceBodyHandles.Hips, SourceBodyHandles.Abdomen, 0.5f * (hipsPosition + abdomenPosition), springSettings, ref hipsFilter, ref abdomenFilter);
        ConstrainOrientation(mainSimulation, SourceBodyHandles.Hips, SourceBodyHandles.Abdomen);
        Connect(mainSimulation, SourceBodyHandles.Abdomen, SourceBodyHandles.Chest, 0.5f * (abdomenPosition + chestPosition), springSettings, ref abdomenFilter, ref chestFilter);
        ConstrainOrientation(mainSimulation, SourceBodyHandles.Abdomen, SourceBodyHandles.Chest);
        Connect(mainSimulation, SourceBodyHandles.Chest, SourceBodyHandles.Head, 0.5f * (chestPosition + headPosition), springSettings, ref chestFilter, ref headFilter);
        ConstrainOrientation(mainSimulation, SourceBodyHandles.Chest, SourceBodyHandles.Head);

        // Create the legs.
        RagdollBuilder.GetCapsuleForLineSegment(hipsPosition, kneePosition, 0.11f, out var upperLegShape, out var upperLegPosition, out var upperLegOrientation);
        RagdollBuilder.GetCapsuleForLineSegment(kneePosition, anklePosition, 0.1f, out var lowerLegShape, out var lowerLegPosition, out var lowerLegOrientation);
        var upperLegDescription = BodyDescription.CreateDynamic((upperLegPosition, upperLegOrientation), upperLegShape.ComputeInertia(1), mainSimulation.Shapes.Add(upperLegShape), 0.01f);
        var lowerLegDescription = BodyDescription.CreateDynamic((lowerLegPosition, lowerLegOrientation), lowerLegShape.ComputeInertia(1), mainSimulation.Shapes.Add(lowerLegShape), 0.01f);

        upperLegDescription.Pose.Position -= legOffset;
        lowerLegDescription.Pose.Position -= legOffset;
        SourceBodyHandles.UpperLeftLeg = mainSimulation.Bodies.Add(upperLegDescription);
        SourceBodyHandles.LowerLeftLeg = mainSimulation.Bodies.Add(lowerLegDescription);
        upperLegDescription.Pose.Position += 2 * legOffset;
        lowerLegDescription.Pose.Position += 2 * legOffset;
        SourceBodyHandles.UpperRightLeg = mainSimulation.Bodies.Add(upperLegDescription);
        SourceBodyHandles.LowerRightLeg = mainSimulation.Bodies.Add(lowerLegDescription);

        CreateLimb(mainSimulation, mainCollisionFilters, SourceBodyHandles.Hips, SourceBodyHandles.UpperLeftLeg, SourceBodyHandles.LowerLeftLeg, hipsPosition - legOffset, kneePosition - legOffset, springSettings, groupIndex, 4);
        CreateLimb(mainSimulation, mainCollisionFilters, SourceBodyHandles.Hips, SourceBodyHandles.UpperRightLeg, SourceBodyHandles.LowerRightLeg, hipsPosition + legOffset, kneePosition + legOffset, springSettings, groupIndex, 6);

        // Create the arms.
        RagdollBuilder.GetCapsuleForLineSegment(chestPosition, elbowPosition, 0.08f, out var upperArmShape, out var upperArmPosition, out var upperArmOrientation);
        RagdollBuilder.GetCapsuleForLineSegment(elbowPosition, wristPosition, 0.075f, out var lowerArmShape, out var lowerArmPosition, out var lowerArmOrientation);
        var upperArmDescription = BodyDescription.CreateDynamic((upperArmPosition, upperArmOrientation), upperArmShape.ComputeInertia(1), mainSimulation.Shapes.Add(upperArmShape), 0.01f);
        var lowerArmDescription = BodyDescription.CreateDynamic((lowerArmPosition, lowerArmOrientation), lowerArmShape.ComputeInertia(1), mainSimulation.Shapes.Add(lowerArmShape), 0.01f);

        upperArmDescription.Pose.Position -= armOffset;
        lowerArmDescription.Pose.Position -= armOffset;
        SourceBodyHandles.UpperLeftArm = mainSimulation.Bodies.Add(upperArmDescription);
        SourceBodyHandles.LowerLeftArm = mainSimulation.Bodies.Add(lowerArmDescription);
        upperArmDescription.Pose.Position += 2 * armOffset;
        lowerArmDescription.Pose.Position += 2 * armOffset;
        SourceBodyHandles.UpperRightArm = mainSimulation.Bodies.Add(upperArmDescription);
        SourceBodyHandles.LowerRightArm = mainSimulation.Bodies.Add(lowerArmDescription);

        CreateLimb(mainSimulation, mainCollisionFilters, SourceBodyHandles.Chest, SourceBodyHandles.UpperLeftArm, SourceBodyHandles.LowerLeftArm, chestPosition - armOffset, elbowPosition - armOffset, springSettings, groupIndex, 8);
        CreateLimb(mainSimulation, mainCollisionFilters, SourceBodyHandles.Chest, SourceBodyHandles.UpperRightArm, SourceBodyHandles.LowerRightArm, chestPosition + armOffset, elbowPosition + armOffset, springSettings, groupIndex, 10);

        // Create the dance controls.
        _hipsControl = new DancerControl(mainSimulation, SourceBodyHandles.Hips, hipsPosition, ServoSettings.Default, new SpringSettings(5, 1));
        mainSimulation.Solver.Add(SourceBodyHandles.Hips, new OneBodyAngularServo
        {
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(30, 1),
            TargetOrientation = mainSimulation.Bodies[SourceBodyHandles.Hips].Pose.Orientation,
        });

        var limbServoSettings = ServoSettings.Default;
        var limbSpringSettings = new SpringSettings(4, 1);
        _leftFootControl = new DancerControl(mainSimulation, SourceBodyHandles.LowerLeftLeg, anklePosition - legOffset, limbServoSettings, limbSpringSettings);
        _rightFootControl = new DancerControl(mainSimulation, SourceBodyHandles.LowerRightLeg, anklePosition + legOffset, limbServoSettings, limbSpringSettings);
        _leftHandControl = new DancerControl(mainSimulation, SourceBodyHandles.LowerLeftArm, wristPosition - armOffset, limbServoSettings, limbSpringSettings);
        _rightHandControl = new DancerControl(mainSimulation, SourceBodyHandles.LowerRightArm, wristPosition + armOffset, limbServoSettings, limbSpringSettings);

        mainSimulation.Statics.Add(new StaticDescription(
            new Vector3(0, -1.24f, 0), mainSimulation.Shapes.Add(new Box(1000, 1, 1000))));

        // Build the background dancers. Every dancer has its own cosmetic simulation; the 12 core
        // bodies are kinematic shells driven by the source dancer's motion history.
        Handles = new DancerBodyHandles[dancerGridWidth * dancerGridLength];
        Simulations = new Simulation[Handles.Length];
        for (var i = 0; i < Handles.Length; ++i)
        {
            ref var dancer = ref Handles[i];
            var dancerFilters = new CollidableProperty<TCollisionFilter>();
            var distanceFromMainDancer = GetDistanceFromMainDancer(i, dancerGridWidth);
            var levelOfDetail = MathF.Log2(MathF.Max(1, distanceFromMainDancer) - 0.8f);
            var narrowPhaseCallbacks = TNarrowPhaseCallbacks.Create(
                dancerFilters, new PairMaterialProperties(0.4f, 20, new SpringSettings(120, 1)),
                levelOfDetail <= 0.5f ? 3 : int.MaxValue);
            var dancerSimulation = Simulation.Create(
                new BufferPool(16384), narrowPhaseCallbacks,
                new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0)), dancerSolveDescription,
                null, initialAllocationSizes: new SimulationAllocationSizes(128, 1, 1, 8, 512, 64, 8));

            dancer.Hips = CreateCopyForDancer(mainSimulation, SourceBodyHandles.Hips, dancerSimulation.Shapes.Add(hipShape), dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.Abdomen = CreateCopyForDancer(mainSimulation, SourceBodyHandles.Abdomen, dancerSimulation.Shapes.Add(abdomenShape), dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.Chest = CreateCopyForDancer(mainSimulation, SourceBodyHandles.Chest, dancerSimulation.Shapes.Add(chestShape), dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.Head = CreateCopyForDancer(mainSimulation, SourceBodyHandles.Head, dancerSimulation.Shapes.Add(headShape), dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);

            var upperLegShapeInTarget = dancerSimulation.Shapes.Add(upperLegShape);
            var lowerLegShapeInTarget = dancerSimulation.Shapes.Add(lowerLegShape);
            dancer.UpperLeftLeg = CreateCopyForDancer(mainSimulation, SourceBodyHandles.UpperLeftLeg, upperLegShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.LowerLeftLeg = CreateCopyForDancer(mainSimulation, SourceBodyHandles.LowerLeftLeg, lowerLegShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.UpperRightLeg = CreateCopyForDancer(mainSimulation, SourceBodyHandles.UpperRightLeg, upperLegShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.LowerRightLeg = CreateCopyForDancer(mainSimulation, SourceBodyHandles.LowerRightLeg, lowerLegShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);

            var upperArmShapeInTarget = dancerSimulation.Shapes.Add(upperArmShape);
            var lowerArmShapeInTarget = dancerSimulation.Shapes.Add(lowerArmShape);
            dancer.UpperLeftArm = CreateCopyForDancer(mainSimulation, SourceBodyHandles.UpperLeftArm, upperArmShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.LowerLeftArm = CreateCopyForDancer(mainSimulation, SourceBodyHandles.LowerLeftArm, lowerArmShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.UpperRightArm = CreateCopyForDancer(mainSimulation, SourceBodyHandles.UpperRightArm, upperArmShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);
            dancer.LowerRightArm = CreateCopyForDancer(mainSimulation, SourceBodyHandles.LowerRightArm, lowerArmShapeInTarget, dancerSimulation, i, dancerGridWidth, dancerFilters, filterForDancerBodies);

            dressUpDancer(dancerSimulation, dancerFilters, dancer, i, dancerGridWidth, levelOfDetail);
            BodyCount += dancerSimulation.Bodies.ActiveSet.Count;
            ConstraintCount += dancerSimulation.Solver.CountConstraints();
            Simulations[i] = dancerSimulation;
        }

        _ = pool;
        return this;
    }

    private static BodyHandle CreateCopyForDancer<TCollisionFilter>(
        Simulation sourceSimulation, BodyHandle sourceHandle, TypedIndex shapeIndexInTargetSimulation,
        Simulation targetSimulation, int dancerIndex, int dancerGridWidth,
        CollidableProperty<TCollisionFilter> filters, TCollisionFilter bodyFilter)
        where TCollisionFilter : unmanaged
    {
        var description = sourceSimulation.Bodies.GetDescription(sourceHandle);
        description.Pose.Position += GetOffsetForDancer(dancerIndex, dancerGridWidth);
        description.Collidable.Shape = shapeIndexInTargetSimulation;
        description.LocalInertia = default;
        description.Activity.SleepThreshold = -1;
        var handle = targetSimulation.Bodies.Add(description);
        filters.Allocate(handle) = bodyFilter;
        return handle;
    }

    private static void Connect(
        Simulation simulation, BodyHandle a, BodyHandle b, Vector3 jointLocation, SpringSettings springSettings,
        ref SubgroupCollisionFilter filterA, ref SubgroupCollisionFilter filterB)
    {
        var poseA = simulation.Bodies[a].Pose;
        var poseB = simulation.Bodies[b].Pose;
        simulation.Solver.Add(a, b, new BallSocket
        {
            LocalOffsetA = QuaternionEx.Transform(jointLocation - poseA.Position, Quaternion.Conjugate(poseA.Orientation)),
            LocalOffsetB = QuaternionEx.Transform(jointLocation - poseB.Position, Quaternion.Conjugate(poseB.Orientation)),
            SpringSettings = springSettings,
        });
        SubgroupCollisionFilter.DisableCollision(ref filterA, ref filterB);
    }

    private static void CreateLimb(
        Simulation simulation, CollidableProperty<SubgroupCollisionFilter> collisionFilters,
        BodyHandle body, BodyHandle upperLimb, BodyHandle lowerLimb,
        Vector3 bodyToUpperJointLocation, Vector3 upperToLowerJointLocation, SpringSettings springSettings,
        int groupIndex, int limbSubgroupIndexStart)
    {
        ref var bodyFilter = ref collisionFilters[body];
        ref var upperFilter = ref collisionFilters.Allocate(upperLimb);
        ref var lowerFilter = ref collisionFilters.Allocate(lowerLimb);
        upperFilter = new SubgroupCollisionFilter(groupIndex, limbSubgroupIndexStart);
        lowerFilter = new SubgroupCollisionFilter(groupIndex, limbSubgroupIndexStart + 1);
        Connect(simulation, body, upperLimb, bodyToUpperJointLocation, springSettings, ref bodyFilter, ref upperFilter);
        Connect(simulation, upperLimb, lowerLimb, upperToLowerJointLocation, springSettings, ref upperFilter, ref lowerFilter);

        var bodyPose = simulation.Bodies[body].Pose;
        var upperPose = simulation.Bodies[upperLimb].Pose;
        var lowerPose = simulation.Bodies[lowerLimb].Pose;
        // Prevent the hip from spinning 360 degrees.
        simulation.Solver.Add(body, upperLimb, new TwistServo
        {
            LocalBasisA = QuaternionEx.Concatenate(RagdollBuilder.CreateBasis(new Vector3(0, -1, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(bodyPose.Orientation)),
            LocalBasisB = QuaternionEx.Concatenate(RagdollBuilder.CreateBasis(new Vector3(0, -1, 0), new Vector3(0, 0, 1)), Quaternion.Conjugate(upperPose.Orientation)),
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(30, 1),
        });
        // Stop the knee from flopping every which way.
        simulation.Solver.Add(upperLimb, lowerLimb, new AngularHinge
        {
            LocalHingeAxisA = QuaternionEx.Transform(Vector3.UnitX, Quaternion.Conjugate(upperPose.Orientation)),
            LocalHingeAxisB = QuaternionEx.Transform(Vector3.UnitX, Quaternion.Conjugate(lowerPose.Orientation)),
            SpringSettings = new SpringSettings(30, 1),
        });
        // Prevent knee hyperextension.
        simulation.Solver.Add(upperLimb, lowerLimb, new SwingLimit
        {
            AxisLocalA = QuaternionEx.Transform(new Vector3(0, 0, 1), Quaternion.Conjugate(upperPose.Orientation)),
            AxisLocalB = QuaternionEx.Transform(new Vector3(0, 1, 0), Quaternion.Conjugate(lowerPose.Orientation)),
            MaximumSwingAngle = MathF.PI * 0.4f,
            SpringSettings = new SpringSettings(15, 1),
        });
    }

    private static void ConstrainOrientation(Simulation simulation, BodyHandle a, BodyHandle b)
    {
        simulation.Solver.Add(a, b, new AngularServo
        {
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(6, 1),
            TargetRelativeRotationLocalA = QuaternionEx.Concatenate(
                simulation.Bodies[b].Pose.Orientation, QuaternionEx.Conjugate(simulation.Bodies[a].Pose.Orientation)),
        });
    }

    private static (int columnIndex, int rowIndex) GetRowAndColumnForDancer(int dancerIndex, int dancerGridWidth)
    {
        var rowIndex = dancerIndex / dancerGridWidth;
        return (dancerIndex - rowIndex * dancerGridWidth, rowIndex);
    }

    public static Vector3 GetOffsetForDancer(int i, int dancerGridWidth)
    {
        const float spacing = 2;
        var (columnIndex, rowIndex) = GetRowAndColumnForDancer(i, dancerGridWidth);
        return new Vector3(dancerGridWidth * spacing / -2 + (columnIndex + 0.5f) * spacing, 0, -2 + rowIndex * -spacing);
    }

    public static float GetDistanceFromMainDancer(int dancerIndex, int dancerGridWidth)
    {
        var (columnIndex, rowIndex) = GetRowAndColumnForDancer(dancerIndex, dancerGridWidth);
        var offsetX = columnIndex - (dancerGridWidth / 2 - 0.5f);
        return MathF.Sqrt(offsetX * offsetX + rowIndex * rowIndex);
    }

    private static float Smoothstep(float v)
    {
        var v2 = v * v;
        return 3 * v2 - 2 * v2 * v;
    }

    private static Vector3 CreateLegTarget(float t)
    {
        var z = MathF.Cos(t * MathF.Tau);
        var zOffset = (Smoothstep(z * 0.5f + 0.5f) * 2 - 1) * 0.7f;
        var offset = 0.5f + 0.5f * MathF.Cos(MathF.PI + t * 4 * MathF.PI);
        var xOffset = -0.2f + 0.4f * offset;
        var yOffset = -0.7f + 0.2f * offset;
        return new Vector3(-xOffset - LegOffsetX, yOffset, zOffset);
    }

    private static Vector3 CreateArmTarget(float t)
    {
        var z = MathF.Cos(t * MathF.Tau);
        var zOffset = (Smoothstep(z * 0.5f + 0.5f) * 2 - 1);
        var offset = 0.5f + 0.5f * MathF.Cos(MathF.PI + t * 4 * MathF.PI);
        var xOffset = -0.2f + 0.6f * offset;
        var yOffset = 0.9f - 0.2f * offset;
        return new Vector3(-xOffset - ArmOffsetX, yOffset, zOffset);
    }

    private void ExecuteDancer(int dancerIndex)
    {
        var dancerSimulation = Simulations[dancerIndex];
        var delayFrames =
            (int)GetDistanceFromMainDancer(dancerIndex, DancerGridWidth) * 8 + 1 + (HashHelper.Rehash(dancerIndex) & 0xF);
        var availableFrames = _historyCount / DancerBodyHandles.Count;
        var startFrame = availableFrames - delayFrames;
        if (startFrame < 0) startFrame = 0;

        var offset = GetOffsetForDancer(dancerIndex, DancerGridWidth);
        for (var j = 0; j < DancerBodyHandles.Count; ++j)
        {
            var historyIndex = (_historyStart + startFrame * DancerBodyHandles.Count + j) % _history.Length;
            var state = _history[historyIndex];
            state.Pose.Position += offset;
            dancerSimulation.Bodies[Handles[dancerIndex][j]].MotionState = state;
        }

        dancerSimulation.Timestep(TickDurationSeconds);
    }

    /// <summary>
    ///     Advances the source dancer's servo targets, records its motion state and steps every
    ///     background dancer (sequentially — see the class deviation note).
    /// </summary>
    public void UpdateTargets(Simulation mainSimulation)
    {
        _time += TickDurationSeconds;
        var hipsTarget = new Vector3(0, 0, 3 * (float)Math.Sin(_time / 4));
        _hipsControl.UpdateTarget(mainSimulation, hipsTarget);
        const float stepDuration = 3.5f;
        var scaledTime = _time / stepDuration;
        var t = (float)(scaledTime - Math.Floor(scaledTime));
        var tOffset = (t + 0.5f) % 1;
        var leftFootLocalTarget = CreateLegTarget(t);
        var rightFootLocalTarget = CreateLegTarget(tOffset);
        rightFootLocalTarget.X *= -1;
        _leftFootControl.UpdateTarget(mainSimulation, hipsTarget + leftFootLocalTarget);
        _rightFootControl.UpdateTarget(mainSimulation, hipsTarget + rightFootLocalTarget);

        var leftArmLocalTarget = CreateArmTarget(tOffset);
        var rightArmLocalTarget = CreateArmTarget(t);
        rightArmLocalTarget.X *= -1;
        _leftHandControl.UpdateTarget(mainSimulation, hipsTarget + leftArmLocalTarget);
        _rightHandControl.UpdateTarget(mainSimulation, hipsTarget + rightArmLocalTarget);

        // Record the latest motion state from the source dancer (one frame = 12 states).
        if (_historyCount == _history.Length)
        {
            _historyStart = (_historyStart + DancerBodyHandles.Count) % _history.Length;
            _historyCount -= DancerBodyHandles.Count;
        }

        for (var i = 0; i < DancerBodyHandles.Count; ++i)
        {
            var writeIndex = (_historyStart + _historyCount) % _history.Length;
            _history[writeIndex] = mainSimulation.Bodies[SourceBodyHandles[i]].MotionState;
            _historyCount++;
        }

        var startTime = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < Handles.Length; ++i) ExecuteDancer(i);
        var endTime = System.Diagnostics.Stopwatch.GetTimestamp();
        ExecutionTime = (endTime - startTime) / (double)System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>Returns every dancer simulation's buffers to its own pool (simulations are GC'd).</summary>
    public void Dispose()
    {
        for (var i = 0; i < Simulations.Length; ++i)
        {
            Simulations[i].BufferPool.Clear();
        }
    }
}
