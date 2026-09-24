using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Tank building blocks ported from the upstream <c>Tanks</c> demo folder (BepuPhysics2,
///     Apache-2.0, Ross Nordby): <c>TankPartDescription</c>, <c>TankDescription</c>,
///     <c>Tank</c>, <c>TankController</c>, <c>AITank</c>, <c>TankCallbacks</c>. Documented
///     deviations (docs/compat-review.md): managed arrays replace the pooled
///     <c>QuickList</c>/<c>Buffer</c> handles (demo-lifetime allocations, no pool rules broken)
///     and the upstream <c>SpinLock</c> around projectile impacts is dropped because every solve
///     is a null-dispatcher single-threaded step.
/// </summary>
public struct TankPartDescription
{
    public TypedIndex Shape;
    public BodyInertia Inertia;
    public RigidPose Pose;
    public float Friction;

    public static TankPartDescription Create<TShape>(float mass, in TShape shape, in RigidPose pose, float friction, Shapes shapes)
        where TShape : unmanaged, IConvexShape
    {
        TankPartDescription description;
        description.Shape = shapes.Add(shape);
        description.Inertia = shape.ComputeInertia(mass);
        description.Pose = pose;
        description.Friction = friction;
        return description;
    }
}

/// <summary>Describes a tank's construction (upstream <c>TankDescription</c>).</summary>
public struct TankDescription
{
    public TankPartDescription Turret;
    public TankPartDescription Barrel;
    public TankPartDescription Body;
    public Vector3 BarrelAnchor;
    public Vector3 TurretAnchor;
    public Quaternion TurretBasis;
    public ServoSettings TurretServo;
    public SpringSettings TurretSpring;
    public ServoSettings BarrelServo;
    public SpringSettings BarrelSpring;

    public Vector3 BarrelLocalProjectileSpawn;
    public BodyInertia ProjectileInertia;
    public TypedIndex ProjectileShape;
    public float ProjectileSpeed;

    public TypedIndex WheelShape;
    public BodyInertia WheelInertia;
    public Quaternion WheelOrientation;
    public Vector3 LeftTreadOffset;
    public Vector3 RightTreadOffset;
    public int WheelCountPerTread;
    public float TreadSpacing;
    public float SuspensionLength;
    public SpringSettings SuspensionSettings;
    public float WheelFriction;
}

/// <summary>Set of handles and references to a tank instance (upstream <c>Tank</c>).</summary>
public struct Tank
{
    public BodyHandle Body;
    public BodyHandle Turret;
    public BodyHandle Barrel;
    public ConstraintHandle TurretServo;
    public ConstraintHandle BarrelServo;
    public BodyHandle[] WheelHandles;
    public ConstraintHandle[] Constraints;
    public ConstraintHandle[] LeftMotors;
    public ConstraintHandle[] RightMotors;

    private Quaternion FromBodyLocalToTurretBasisLocal;
    public Quaternion BodyLocalOrientation;
    private Vector3 BarrelLocalProjectileSpawn;
    private Vector3 BarrelLocalDirection;
    private float ProjectileSpeed;
    private BodyInertia ProjectileInertia;
    private TypedIndex ProjectileShape;

    private TwistServo BarrelServoDescription;
    private TwistServo TurretServoDescription;

    public void SetSpeed(Simulation simulation, ConstraintHandle[] motors, float speed, float maximumForce)
    {
        var motorDescription = new AngularAxisMotor
        {
            // Assuming the wheels are cylinders oriented in the obvious way.
            LocalAxisA = new Vector3(0, -1, 0),
            Settings = new MotorSettings(maximumForce, 1e-6f),
            TargetVelocity = speed,
        };
        for (var i = 0; i < motors.Length; ++i)
        {
            simulation.Solver.ApplyDescription(motors[i], motorDescription);
        }
    }

    /// <summary>Computes the swivel and pitch angles required to aim in a given direction.</summary>
    public readonly (float targetSwivelAngle, float targetPitchAngle) ComputeTurretAngles(Simulation simulation, Vector3 aimDirection)
    {
        QuaternionEx.ConcatenateWithoutOverlap(QuaternionEx.Conjugate(simulation.Bodies[Body].Pose.Orientation), FromBodyLocalToTurretBasisLocal, out var toTurretBasis);
        QuaternionEx.TransformWithoutOverlap(aimDirection, toTurretBasis, out var aimDirectionInTurretBasis);
        var targetSwivelAngle = MathF.Atan2(aimDirectionInTurretBasis.X, -aimDirectionInTurretBasis.Z);
        var targetPitchAngle = MathF.Asin(MathF.Max(-1f, MathF.Min(1f, -aimDirectionInTurretBasis.Y)));
        return (targetSwivelAngle, targetPitchAngle);
    }

    /// <summary>Applies a target swivel and pitch angle to the turret's servos.</summary>
    public void SetAim(Simulation simulation, float targetSwivelAngle, float targetPitchAngle)
    {
        var turretDescription = TurretServoDescription;
        turretDescription.TargetAngle = targetSwivelAngle;
        simulation.Solver.ApplyDescription(TurretServo, turretDescription);
        var barrelDescription = BarrelServoDescription;
        barrelDescription.TargetAngle = targetPitchAngle;
        simulation.Solver.ApplyDescription(BarrelServo, barrelDescription);
    }

    /// <summary>Computes the direction along which the barrel points.</summary>
    public readonly void ComputeBarrelDirection(Simulation simulation, out Vector3 barrelDirection)
    {
        QuaternionEx.Transform(BarrelLocalDirection, simulation.Bodies[Barrel].Pose.Orientation, out barrelDirection);
    }

    /// <summary>Fires a projectile; returns the created body handle.</summary>
    public BodyHandle Fire(Simulation simulation, CollidableProperty<TankDemoBodyProperties> bodyProperties)
    {
        var barrel = simulation.Bodies[Barrel];
        ref var barrelPose = ref barrel.Pose;
        RigidPose.Transform(BarrelLocalProjectileSpawn, barrelPose, out var projectileSpawn);
        QuaternionEx.Transform(BarrelLocalDirection, barrelPose.Orientation, out var barrelDirection);
        var projectileHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(
            projectileSpawn, barrelDirection * ProjectileSpeed + barrel.Velocity.Linear, ProjectileInertia,
            // The projectile moves pretty fast, so we'll use continuous collision detection.
            new CollidableDescription(ProjectileShape, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)), 0.01f));
        ref var projectileProperties = ref bodyProperties.Allocate(projectileHandle);
        projectileProperties.Friction = 1f;
        // Prevent the projectile from colliding with the firing tank.
        projectileProperties.Filter = new SubgroupCollisionFilter(Body.Value);
        projectileProperties.Filter.CollidableSubgroups = 0;
        projectileProperties.Filter.SubgroupMembership = 0;
        projectileProperties.Projectile = true;

        barrel.Awake = true;
        barrel.ApplyLinearImpulse(barrelDirection * -ProjectileSpeed / ProjectileInertia.InverseMass);
        return projectileHandle;
    }

    private static BodyHandle CreateWheel(
        Simulation simulation, CollidableProperty<TankDemoBodyProperties> properties, in RigidPose tankPose, in RigidPose bodyLocalPose,
        TypedIndex wheelShape, BodyInertia wheelInertia, float wheelFriction, BodyHandle bodyHandle, ref SubgroupCollisionFilter bodyFilter,
        Vector3 bodyToWheelSuspension, float suspensionLength, in SpringSettings suspensionSettings, Quaternion localWheelOrientation,
        List<BodyHandle> wheelHandles, List<ConstraintHandle> constraints, List<ConstraintHandle> motors)
    {
        RigidPose wheelPose;
        QuaternionEx.TransformUnitX(localWheelOrientation, out var suspensionDirection);
        RigidPose.Transform(bodyToWheelSuspension + suspensionDirection * suspensionLength, tankPose, out wheelPose.Position);
        QuaternionEx.ConcatenateWithoutOverlap(localWheelOrientation, tankPose.Orientation, out wheelPose.Orientation);

        var wheelHandle = simulation.Bodies.Add(BodyDescription.CreateDynamic(wheelPose, wheelInertia, wheelShape, 0.01f));
        wheelHandles.Add(wheelHandle);

        // A LinearAxisServo acts as the suspension spring, pushing the wheel down.
        constraints.Add(simulation.Solver.Add(bodyHandle, wheelHandle, new LinearAxisServo
        {
            LocalPlaneNormal = suspensionDirection,
            TargetOffset = suspensionLength,
            LocalOffsetA = bodyToWheelSuspension,
            LocalOffsetB = default,
            ServoSettings = ServoSettings.Default,
            SpringSettings = suspensionSettings,
        }));
        // A PointOnLineServo keeps the wheel on a fixed track (no angular constraint).
        constraints.Add(simulation.Solver.Add(bodyHandle, wheelHandle, new PointOnLineServo
        {
            LocalDirection = suspensionDirection,
            LocalOffsetA = bodyToWheelSuspension,
            LocalOffsetB = default,
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(30, 1),
        }));
        // The angular component is handled by a hinge; the motor acts along the cylinder's local Y.
        QuaternionEx.TransformUnitY(localWheelOrientation, out var wheelRotationAxis);
        constraints.Add(simulation.Solver.Add(bodyHandle, wheelHandle, new AngularHinge
        {
            LocalHingeAxisA = QuaternionEx.Transform(wheelRotationAxis, QuaternionEx.Conjugate(bodyLocalPose.Orientation)),
            LocalHingeAxisB = new Vector3(0, 1, 0),
            SpringSettings = new SpringSettings(30, 1),
        }));
        var motorHandle = simulation.Solver.Add(wheelHandle, bodyHandle, new AngularAxisMotor
        {
            LocalAxisA = new Vector3(0, 1, 0),
            Settings = default,
            TargetVelocity = default,
        });
        motors.Add(motorHandle);
        constraints.Add(motorHandle);
        ref var wheelProperties = ref properties.Allocate(wheelHandle);
        wheelProperties = new TankDemoBodyProperties { Filter = new SubgroupCollisionFilter(bodyHandle.Value, 3), Friction = wheelFriction, TankPart = true };
        // The wheels don't need to be tested against the body or each other.
        SubgroupCollisionFilter.DisableCollision(ref wheelProperties.Filter, ref bodyFilter);
        SubgroupCollisionFilter.DisableCollision(ref wheelProperties.Filter, ref wheelProperties.Filter);
        return wheelHandle;
    }

    private static ref SubgroupCollisionFilter CreatePart(
        Simulation simulation, in TankPartDescription part, RigidPose pose, CollidableProperty<TankDemoBodyProperties> properties, out BodyHandle handle)
    {
        RigidPose.MultiplyWithoutOverlap(part.Pose, pose, out var bodyPose);
        handle = simulation.Bodies.Add(BodyDescription.CreateDynamic(bodyPose, part.Inertia, part.Shape, 0.01f));
        ref var partProperties = ref properties.Allocate(handle);
        partProperties = new TankDemoBodyProperties { Friction = part.Friction, TankPart = true };
        return ref partProperties.Filter;
    }

    /// <summary>Creates a tank from a provided tank description.</summary>
    public static Tank Create(Simulation simulation, CollidableProperty<TankDemoBodyProperties> properties, in RigidPose pose, in TankDescription description)
    {
        var wheelHandles = new List<BodyHandle>(description.WheelCountPerTread * 2);
        var constraints = new List<ConstraintHandle>(description.WheelCountPerTread * 2 * 6 + 4);
        var leftMotors = new List<ConstraintHandle>(description.WheelCountPerTread);
        var rightMotors = new List<ConstraintHandle>(description.WheelCountPerTread);
        Tank tank;
        ref var bodyFilter = ref CreatePart(simulation, description.Body, pose, properties, out tank.Body);
        ref var turretFilter = ref CreatePart(simulation, description.Turret, pose, properties, out tank.Turret);
        ref var barrelFilter = ref CreatePart(simulation, description.Barrel, pose, properties, out tank.Barrel);
        // Use the tank's body handle as the group id for collision filters.
        bodyFilter = new SubgroupCollisionFilter(tank.Body.Value, 0);
        turretFilter = new SubgroupCollisionFilter(tank.Body.Value, 1);
        barrelFilter = new SubgroupCollisionFilter(tank.Body.Value, 2);
        SubgroupCollisionFilter.DisableCollision(ref bodyFilter, ref turretFilter);
        SubgroupCollisionFilter.DisableCollision(ref turretFilter, ref barrelFilter);

        Matrix3x3.CreateFromQuaternion(description.TurretBasis, out var turretBasis);

        // Attach the turret to the body.
        QuaternionEx.Transform(turretBasis.Y, QuaternionEx.Conjugate(description.Body.Pose.Orientation), out var bodyLocalSwivelAxis);
        QuaternionEx.Transform(turretBasis.Y, QuaternionEx.Conjugate(description.Turret.Pose.Orientation), out var turretLocalSwivelAxis);
        RigidPose.TransformByInverse(description.TurretAnchor, description.Body.Pose, out var bodyLocalTurretAnchor);
        RigidPose.TransformByInverse(description.TurretAnchor, description.Turret.Pose, out var turretLocalTurretAnchor);
        constraints.Add(simulation.Solver.Add(tank.Body, tank.Turret, new Hinge
        {
            LocalHingeAxisA = bodyLocalSwivelAxis,
            LocalHingeAxisB = turretLocalSwivelAxis,
            LocalOffsetA = bodyLocalTurretAnchor,
            LocalOffsetB = turretLocalTurretAnchor,
            SpringSettings = new SpringSettings(30, 1),
        }));
        Matrix3x3 turretSwivelBasis;
        turretSwivelBasis.Z = -turretBasis.Y;
        turretSwivelBasis.X = -turretBasis.Z;
        turretSwivelBasis.Y = turretBasis.X;
        Debug.Assert(turretSwivelBasis.Determinant() > 0.999f && turretSwivelBasis.Determinant() < 1.0001f, "The turret swivel axis and forward axis should be perpendicular and unit length.");
        QuaternionEx.CreateFromRotationMatrix(turretSwivelBasis, out var turretSwivelBasisQuaternion);
        QuaternionEx.ConcatenateWithoutOverlap(turretSwivelBasisQuaternion, QuaternionEx.Conjugate(description.Body.Pose.Orientation), out var bodyLocalTurretBasis);
        QuaternionEx.ConcatenateWithoutOverlap(turretSwivelBasisQuaternion, QuaternionEx.Conjugate(description.Turret.Pose.Orientation), out var turretLocalTurretBasis);
        tank.TurretServoDescription = new TwistServo
        {
            LocalBasisA = bodyLocalTurretBasis,
            LocalBasisB = turretLocalTurretBasis,
            SpringSettings = description.TurretSpring,
            ServoSettings = description.TurretServo,
        };
        tank.TurretServo = simulation.Solver.Add(tank.Body, tank.Turret, tank.TurretServoDescription);
        constraints.Add(tank.TurretServo);

        // Attach the barrel to the turret.
        QuaternionEx.Transform(turretBasis.X, QuaternionEx.Conjugate(description.Turret.Pose.Orientation), out var turretLocalPitchAxis);
        QuaternionEx.Transform(turretBasis.X, QuaternionEx.Conjugate(description.Barrel.Pose.Orientation), out var barrelLocalPitchAxis);
        RigidPose.TransformByInverse(description.BarrelAnchor, description.Turret.Pose, out var turretLocalBarrelAnchor);
        RigidPose.TransformByInverse(description.BarrelAnchor, description.Barrel.Pose, out var barrelLocalBarrelAnchor);
        constraints.Add(simulation.Solver.Add(tank.Turret, tank.Barrel, new Hinge
        {
            LocalHingeAxisA = turretLocalPitchAxis,
            LocalHingeAxisB = barrelLocalPitchAxis,
            LocalOffsetA = turretLocalBarrelAnchor,
            LocalOffsetB = barrelLocalBarrelAnchor,
            SpringSettings = new SpringSettings(30, 1),
        }));
        Matrix3x3 barrelPitchBasis;
        barrelPitchBasis.Z = -turretBasis.X;
        barrelPitchBasis.X = -turretBasis.Z;
        barrelPitchBasis.Y = -turretBasis.Y;
        Debug.Assert(barrelPitchBasis.Determinant() > 0.999f && barrelPitchBasis.Determinant() < 1.0001f, "The barrel axis and forward axis should be perpendicular and unit length.");
        QuaternionEx.CreateFromRotationMatrix(barrelPitchBasis, out var barrelPitchBasisQuaternion);
        QuaternionEx.ConcatenateWithoutOverlap(barrelPitchBasisQuaternion, QuaternionEx.Conjugate(description.Turret.Pose.Orientation), out var turretLocalBarrelBasis);
        QuaternionEx.ConcatenateWithoutOverlap(barrelPitchBasisQuaternion, QuaternionEx.Conjugate(description.Barrel.Pose.Orientation), out var barrelLocalBarrelBasis);
        tank.BarrelServoDescription = new TwistServo
        {
            LocalBasisA = turretLocalBarrelBasis,
            LocalBasisB = barrelLocalBarrelBasis,
            SpringSettings = description.BarrelSpring,
            ServoSettings = description.BarrelServo,
        };
        tank.BarrelServo = simulation.Solver.Add(tank.Turret, tank.Barrel, tank.BarrelServoDescription);
        constraints.Add(tank.BarrelServo);

        QuaternionEx.TransformUnitY(description.WheelOrientation, out _);
        QuaternionEx.TransformUnitZ(description.WheelOrientation, out var treadDirection);
        var treadStart = description.TreadSpacing * (description.WheelCountPerTread - 1) * -0.5f;
        BodyHandle previousLeftWheelHandle = default, previousRightWheelHandle = default;
        for (var i = 0; i < description.WheelCountPerTread; ++i)
        {
            var wheelOffsetFromTread = treadDirection * (treadStart + i * description.TreadSpacing);
            var rightWheelHandle = CreateWheel(simulation, properties, pose, description.Body.Pose,
                description.WheelShape, description.WheelInertia, description.WheelFriction, tank.Body, ref properties[tank.Body].Filter,
                description.RightTreadOffset + wheelOffsetFromTread - description.Body.Pose.Position,
                description.SuspensionLength, description.SuspensionSettings, description.WheelOrientation,
                wheelHandles, constraints, rightMotors);
            var leftWheelHandle = CreateWheel(simulation, properties, pose, description.Body.Pose,
                description.WheelShape, description.WheelInertia, description.WheelFriction, tank.Body, ref properties[tank.Body].Filter,
                description.LeftTreadOffset + wheelOffsetFromTread - description.Body.Pose.Position,
                description.SuspensionLength, description.SuspensionSettings, description.WheelOrientation,
                wheelHandles, constraints, leftMotors);

            if (i >= 1)
            {
                // Connect wheels in a tread to each other to distribute the drive forces.
                var motorDescription = new AngularAxisMotor { LocalAxisA = new Vector3(0, 1, 0), Settings = new MotorSettings(float.MaxValue, 1e-4f) };
                constraints.Add(simulation.Solver.Add(previousLeftWheelHandle, leftWheelHandle, motorDescription));
                constraints.Add(simulation.Solver.Add(previousRightWheelHandle, rightWheelHandle, motorDescription));
            }

            previousLeftWheelHandle = leftWheelHandle;
            previousRightWheelHandle = rightWheelHandle;
        }

        tank.WheelHandles = wheelHandles.ToArray();
        tank.Constraints = constraints.ToArray();
        tank.LeftMotors = leftMotors.ToArray();
        tank.RightMotors = rightMotors.ToArray();

        // aimDirectionInTurretBasis = worldAimDirection * inverse(body.Pose.Orientation) * description.Body.Pose.Orientation * inverse(description.TurretBasis).
        QuaternionEx.ConcatenateWithoutOverlap(description.Body.Pose.Orientation, QuaternionEx.Conjugate(description.TurretBasis), out tank.FromBodyLocalToTurretBasisLocal);
        tank.BodyLocalOrientation = description.Body.Pose.Orientation;
        tank.BarrelLocalProjectileSpawn = description.BarrelLocalProjectileSpawn;
        QuaternionEx.Transform(-turretBasis.Z, QuaternionEx.Conjugate(description.Barrel.Pose.Orientation), out tank.BarrelLocalDirection);
        tank.ProjectileInertia = description.ProjectileInertia;
        tank.ProjectileShape = description.ProjectileShape;
        tank.ProjectileSpeed = description.ProjectileSpeed;
        return tank;
    }

    private static void ClearBodyProperties(ref TankDemoBodyProperties properties)
    {
        // After blowing up, all tank parts collide with each other and are no longer a living tank.
        properties.Filter = new SubgroupCollisionFilter(properties.Filter.GroupId);
        properties.TankPart = false;
    }

    public readonly void Explode(Simulation simulation, CollidableProperty<TankDemoBodyProperties> properties)
    {
        // When the tank explodes, remove all binding constraints and let it fall apart.
        for (var i = 0; i < WheelHandles.Length; ++i)
        {
            ClearBodyProperties(ref properties[WheelHandles[i]]);
        }

        ClearBodyProperties(ref properties[Body]);
        ClearBodyProperties(ref properties[Turret]);
        ClearBodyProperties(ref properties[Barrel]);
        var turret = simulation.Bodies[Turret];
        turret.Awake = true;
        turret.Velocity.Linear += new Vector3(0, 10, 0);
        for (var i = 0; i < Constraints.Length; ++i)
        {
            simulation.Solver.Remove(Constraints[i]);
        }
    }
}

/// <summary>Applies control inputs to a tank instance (upstream <c>TankController</c>).</summary>
public struct TankController
{
    public Tank Tank;

    public float Speed;
    public float Force;
    public float ZoomMultiplier;
    public float IdleForce;
    public float BrakeForce;

    private float previousLeftTargetSpeed;
    private float previousLeftForce;
    private float previousRightTargetSpeed;
    private float previousRightForce;
    private float previousTurretSwivel;
    private float previousBarrelPitch;

    public TankController(Tank tank, float speed, float force, float zoomMultiplier, float idleForce, float brakeForce)
    {
        Tank = tank;
        Speed = speed;
        Force = force;
        ZoomMultiplier = zoomMultiplier;
        IdleForce = idleForce;
        BrakeForce = brakeForce;
        previousLeftTargetSpeed = 0;
        previousLeftForce = 0;
        previousRightTargetSpeed = 0;
        previousRightForce = 0;
        previousTurretSwivel = 0;
        previousBarrelPitch = 0;
    }

    /// <summary>Updates constraint targets for an input state.</summary>
    public void UpdateMovementAndAim(
        Simulation simulation, float leftTargetSpeedFraction, float rightTargetSpeedFraction,
        bool zoom, bool brakeLeft, bool brakeRight, Vector3 aimDirection)
    {
        var leftTargetSpeed = brakeLeft ? 0 : leftTargetSpeedFraction * Speed;
        var rightTargetSpeed = brakeRight ? 0 : rightTargetSpeedFraction * Speed;
        if (zoom)
        {
            leftTargetSpeed *= ZoomMultiplier;
            rightTargetSpeed *= ZoomMultiplier;
        }

        var leftForce = brakeLeft ? BrakeForce : leftTargetSpeedFraction == 0 ? IdleForce : Force;
        var rightForce = brakeRight ? BrakeForce : rightTargetSpeedFraction == 0 ? IdleForce : Force;

        var (targetSwivelAngle, targetPitchAngle) = Tank.ComputeTurretAngles(simulation, aimDirection);

        if (leftTargetSpeed != previousLeftTargetSpeed || rightTargetSpeed != previousRightTargetSpeed ||
            leftForce != previousLeftForce || rightForce != previousRightForce ||
            targetSwivelAngle != previousTurretSwivel || targetPitchAngle != previousBarrelPitch)
        {
            // Guarding the constraint modifications behind a state test avoids waking the tank every frame.
            Tank.SetSpeed(simulation, Tank.LeftMotors, leftTargetSpeed, leftForce);
            Tank.SetSpeed(simulation, Tank.RightMotors, rightTargetSpeed, rightForce);
            previousLeftTargetSpeed = leftTargetSpeed;
            previousRightTargetSpeed = rightTargetSpeed;
            previousLeftForce = leftForce;
            previousRightForce = rightForce;
            Tank.SetAim(simulation, targetSwivelAngle, targetPitchAngle);
            previousTurretSwivel = targetSwivelAngle;
            previousBarrelPitch = targetPitchAngle;
        }
    }
}

/// <summary>Stores properties about a body in the tank demo.</summary>
public struct TankDemoBodyProperties
{
    public SubgroupCollisionFilter Filter;
    public float Friction;
    public bool Projectile;
    public bool TankPart;
}

public struct ProjectileImpact
{
    public BodyHandle ProjectileHandle;
    public BodyHandle ImpactedTankBodyHandle;
}

/// <summary>
///     Reference-type impact sink shared by <see cref="TankCallbacks"/> (a struct copied into the
///     narrow phase) and the demo loop. Fixed capacity so no managed allocations happen inside a
///     step; single-threaded solves make the upstream <c>SpinLock</c> unnecessary.
/// </summary>
public sealed class ProjectileImpactTracker
{
    public const int Capacity = 256;

    public readonly ProjectileImpact[] Impacts = new ProjectileImpact[Capacity];
    public int Count;

    public void Clear() => Count = 0;
}

/// <summary>
///     For the tank demo, we want both wheel-body collision filtering and different friction for
///     wheels versus the tank body, plus projectile impact reporting.
/// </summary>
internal struct TankCallbacks : INarrowPhaseCallbacks
{
    public CollidableProperty<TankDemoBodyProperties> Properties;
    public ProjectileImpactTracker Tracker;

    public void Initialize(Simulation simulation)
    {
        Properties.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // It's impossible for two statics to collide, and pairs are sorted such that bodies always come before statics.
        if (b.Mobility != CollidableMobility.Static)
        {
            return SubgroupCollisionFilter.AllowCollision(Properties[a.BodyHandle].Filter, Properties[b.BodyHandle].Filter);
        }

        return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryAddProjectileImpact(BodyHandle projectileHandle, CollidableReference impactedCollidable)
    {
        // Protect against redundant adds: a projectile might hit multiple things in the same frame.
        for (var i = 0; i < Tracker.Count; ++i)
        {
            ref var impact = ref Tracker.Impacts[i];
            if (impact.ProjectileHandle.Value == projectileHandle.Value) return;
        }

        if (Tracker.Count >= ProjectileImpactTracker.Capacity) return;
        ref var newImpact = ref Tracker.Impacts[Tracker.Count++];
        newImpact.ProjectileHandle = projectileHandle;
        if (impactedCollidable.Mobility != CollidableMobility.Static)
        {
            // The filter's group id is the tank's main body handle.
            ref var properties = ref Properties[impactedCollidable.BodyHandle];
            newImpact.ImpactedTankBodyHandle = new BodyHandle(properties.TankPart ? properties.Filter.GroupId : -1);
        }
        else
        {
            newImpact.ImpactedTankBodyHandle = new BodyHandle(-1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        // Different tank parts have different friction values. Wheels tend to stick more than the body.
        ref var propertiesA = ref Properties[pair.A.BodyHandle];
        pairMaterial.FrictionCoefficient = propertiesA.Friction;
        if (pair.B.Mobility != CollidableMobility.Static)
        {
            ref var propertiesB = ref Properties[pair.B.BodyHandle];
            pairMaterial.FrictionCoefficient = (pairMaterial.FrictionCoefficient + propertiesB.Friction) * 0.5f;
        }

        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);

        if (propertiesA.Projectile || (pair.B.Mobility != CollidableMobility.Static && Properties[pair.B.BodyHandle].Projectile))
        {
            for (var i = 0; i < manifold.Count; ++i)
            {
                // A nonzero negative threshold catches fast projectiles that don't quite reach the surface.
                if (manifold.GetDepth(i) >= -1e-3f)
                {
                    if (propertiesA.Projectile)
                    {
                        TryAddProjectileImpact(pair.A.BodyHandle, pair.B);
                    }

                    if (pair.B.Mobility != CollidableMobility.Static && Properties[pair.B.BodyHandle].Projectile)
                    {
                        TryAddProjectileImpact(pair.B.BodyHandle, pair.A);
                    }

                    break;
                }
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Dispose()
    {
        Properties.Dispose();
    }
}

/// <summary>AI tank driver (upstream <c>AITank</c>).</summary>
public struct AITank
{
    public TankController Controller;
    public int Target;
    public Vector2 MovementTarget;
    public long LastShotFrame;
    public int HitPoints;

    /// <summary>
    ///     Runs one AI update tick; returns true (with the fired body handle) when the tank shot
    ///     this frame. The caller owns the render record for fired projectiles.
    /// </summary>
    public bool Update(
        Simulation simulation, CollidableProperty<TankDemoBodyProperties> bodyProperties, Random random,
        long frameIndex, in Vector2 playAreaMin, in Vector2 playAreaMax, int aiIndex,
        AITank[] aiTanks, int aiTankCount, out BodyHandle firedProjectile)
    {
        firedProjectile = default;
        var fired = false;
        ref var currentPose = ref simulation.Bodies[Controller.Tank.Body].Pose;
        QuaternionEx.TransformUnitY(QuaternionEx.Concatenate(QuaternionEx.Conjugate(Controller.Tank.BodyLocalOrientation), currentPose.Orientation), out var tankUp);
        if (tankUp.Y < -0.5f)
        {
            // The tank is upside down. Don't bother doing anything.
            Controller.UpdateMovementAndAim(simulation, 0, 0, false, false, false, new Vector3(1, 0, 0));
            return false;
        }

        if (Target >= aiTankCount || Target == aiIndex || random.NextDouble() < 1f / 250f)
        {
            // Change target.
            if (aiTankCount > 1)
            {
                do
                {
                    Target = random.Next(0, aiTankCount);
                }
                while (Target == aiIndex);
            }
        }

        ref var targetTank = ref aiTanks[Target];
        var targetTankBody = simulation.Bodies[targetTank.Controller.Tank.Body];
        ref var targetTankPosition = ref targetTankBody.Pose.Position;
        var currentTankPosition2D = new Vector2(currentPose.Position.X, currentPose.Position.Z);
        var targetTankPosition2D = new Vector2(targetTankPosition.X, targetTankPosition.Z);
        if (random.NextDouble() < 1f / 250f)
        {
            // Change movement target. Pick a random point around the target's current location.
            // Avoid being excessively close: that tends to make all the tanks bunch up in the middle.
            Vector2 movementTargetOffset;
            float offsetLengthSquared;
            do
            {
                movementTargetOffset = new Vector2((float)(random.NextDouble() - 0.5) * 150, (float)(random.NextDouble() - 0.5) * 150);
                offsetLengthSquared = movementTargetOffset.LengthSquared();
            }
            while (offsetLengthSquared > 150 * 150 || offsetLengthSquared < 50 * 50);
            MovementTarget = Vector2.Min(playAreaMax, Vector2.Max(playAreaMin, targetTankPosition2D + movementTargetOffset));
        }

        var offset = new Vector3(MovementTarget.X - currentTankPosition2D.X, 0, MovementTarget.Y - currentTankPosition2D.Y);
        QuaternionEx.Transform(offset, QuaternionEx.Concatenate(QuaternionEx.Conjugate(currentPose.Orientation), Controller.Tank.BodyLocalOrientation), out var localMovementOffset);

        var targetHorizontalMovementDirection = new Vector2(localMovementOffset.X, -localMovementOffset.Z);
        var targetDirectionLength = targetHorizontalMovementDirection.Length();
        targetHorizontalMovementDirection = targetDirectionLength > 1e-10f ? targetHorizontalMovementDirection / targetDirectionLength : new Vector2(0, 1);
        var turnWeight = targetHorizontalMovementDirection.Y > 0 ? targetHorizontalMovementDirection.X : targetHorizontalMovementDirection.X > 0 ? 1f : -1f;
        // Set the left track to 1 at turnWeight >= 0. At turnWeight -1, leftTrack should be -1.
        var leftTrack = MathF.Min(1f, 2f * turnWeight + 1f);
        // rightTrack = 1 at turnWeight <= 0, rightTrack = -1 at turnWeight = 1.
        var rightTrack = MathF.Min(1f, -2f * turnWeight + 1f);

        // If we're close to the target, slow down a bit.
        var speedMultiplier = Math.Min(targetDirectionLength * 0.05f, 1f);
        leftTrack *= speedMultiplier;
        rightTrack *= speedMultiplier;
        // If we're far away from the target but are pointing in the right direction, zoom.
        var zoom = targetDirectionLength > 50 && targetHorizontalMovementDirection.Y > 0.8f;
        // Not an optimal firing solution: just aim directly at the middle of the other tank.
        ref var barrelPosition = ref simulation.Bodies[Controller.Tank.Barrel].Pose.Position;
        var barrelToTarget = targetTankPosition - barrelPosition;
        var barrelToTargetLength = barrelToTarget.Length();
        barrelToTarget = barrelToTargetLength > 1e-10f ? barrelToTarget / barrelToTargetLength : new Vector3(0, 1, 0);
        Controller.UpdateMovementAndAim(simulation, leftTrack, rightTrack, zoom, false, false, barrelToTarget);
        if (frameIndex > LastShotFrame + 60)
        {
            if (barrelToTargetLength > 1e-10f && barrelToTargetLength < 100)
            {
                Controller.Tank.ComputeBarrelDirection(simulation, out var barrelDirection);
                var dot = Vector3.Dot(barrelDirection, barrelToTarget);
                if (dot > 0.98f)
                {
                    firedProjectile = Controller.Tank.Fire(simulation, bodyProperties);
                    fired = true;
                }
            }

            LastShotFrame = frameIndex;
            return fired;
        }

        return false;
    }
}
