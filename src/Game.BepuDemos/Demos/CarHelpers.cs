using System.Numerics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Car building blocks ported from the upstream <c>Cars</c> demo folder (BepuPhysics2,
///     Apache-2.0, Ross Nordby): <c>WheelHandles</c>, <c>SimpleCar</c>,
///     <c>SimpleCarController</c>, <c>CarCallbacks</c>, <c>RaceTrack</c>. Namespaces renamed to
///     the Bonobo packages; behavior is unchanged (same constraints, same Ackerman steering,
///     same AI-track math).
/// </summary>
internal struct WheelHandles
{
    public BodyHandle Wheel;
    public ConstraintHandle SuspensionSpring;
    public ConstraintHandle SuspensionTrack;
    public ConstraintHandle Hinge;
    public ConstraintHandle Motor;
}

internal struct CarBodyProperties
{
    public SubgroupCollisionFilter Filter;
    public float Friction;
}

/// <summary>
///     For the car demo, we want both wheel-body collision filtering and different friction
///     for wheels versus the car body.
/// </summary>
internal struct CarCallbacks : INarrowPhaseCallbacks
{
    public CollidableProperty<CarBodyProperties> Properties;

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
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = Properties[pair.A.BodyHandle].Friction;
        if (pair.B.Mobility != CollidableMobility.Static)
        {
            // If two bodies collide, just average the friction.
            pairMaterial.FrictionCoefficient = (pairMaterial.FrictionCoefficient + Properties[pair.B.BodyHandle].Friction) * 0.5f;
        }

        pairMaterial.MaximumRecoveryVelocity = 2f;
        pairMaterial.SpringSettings = new SpringSettings(30, 1);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Dispose()
    {
        Properties.Dispose();
    }
}

/// <summary>Quadrant-circle race track used by the AI drivers (upstream <c>RaceTrack</c>).</summary>
internal struct RaceTrack
{
    public float QuadrantRadius;
    public Vector2 Center;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void GetClosestPoint(in Vector2 point, float laneOffset, out Vector2 closestPoint, out Vector2 flowDirection)
    {
        var localPoint = point - Center;
        var quadrantCenter = new Vector2(localPoint.X < 0 ? -QuadrantRadius : QuadrantRadius, localPoint.Y < 0 ? -QuadrantRadius : QuadrantRadius);
        var quadrantCenterToPoint = new Vector2(localPoint.X, localPoint.Y) - quadrantCenter;
        var distanceToQuadrantCenter = quadrantCenterToPoint.Length();
        var on01Or10 = localPoint.X * localPoint.Y < 0;
        var signedLaneOffset = on01Or10 ? -laneOffset : laneOffset;
        var toCircleEdgeDirection = distanceToQuadrantCenter > 0 ? quadrantCenterToPoint * (1f / distanceToQuadrantCenter) : new Vector2(QuadrantRadius + signedLaneOffset, 0);
        var offsetFromQuadrantCircle = (QuadrantRadius + signedLaneOffset) * toCircleEdgeDirection;
        closestPoint = quadrantCenter + offsetFromQuadrantCircle;
        var perpendicular = new Vector2(toCircleEdgeDirection.Y, -toCircleEdgeDirection.X);
        flowDirection = on01Or10 ? perpendicular : -perpendicular;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float GetDistance(in Vector2 point)
    {
        GetClosestPoint(point, 0, out var closest, out _);
        return Vector2.Distance(closest, point);
    }
}

internal struct SimpleCar
{
    public BodyHandle Body;
    public WheelHandles FrontLeftWheel;
    public WheelHandles FrontRightWheel;
    public WheelHandles BackLeftWheel;
    public WheelHandles BackRightWheel;

    private Vector3 suspensionDirection;
    private AngularHinge hingeDescription;

    public void Steer(Simulation simulation, in WheelHandles wheel, float angle)
    {
        var steeredHinge = hingeDescription;
        Matrix3x3.CreateFromAxisAngle(suspensionDirection, -angle, out var rotation);
        Matrix3x3.Transform(hingeDescription.LocalHingeAxisA, rotation, out steeredHinge.LocalHingeAxisA);
        simulation.Solver.ApplyDescription(wheel.Hinge, steeredHinge);
    }

    public void SetSpeed(Simulation simulation, in WheelHandles wheel, float speed, float maximumForce)
    {
        simulation.Solver.ApplyDescription(wheel.Motor, new AngularAxisMotor
        {
            LocalAxisA = new Vector3(0, -1, 0),
            Settings = new MotorSettings(maximumForce, 1e-6f),
            TargetVelocity = speed,
        });
    }

    public static WheelHandles CreateWheel(
        Simulation simulation, CollidableProperty<CarBodyProperties> properties, in RigidPose bodyPose,
        TypedIndex wheelShape, BodyInertia wheelInertia, float wheelFriction, BodyHandle bodyHandle,
        ref SubgroupCollisionFilter bodyFilter, Vector3 bodyToWheelSuspension, Vector3 suspensionDirection, float suspensionLength,
        in AngularHinge hingeDescription, in SpringSettings suspensionSettings, Quaternion localWheelOrientation)
    {
        RigidPose wheelPose;
        RigidPose.Transform(bodyToWheelSuspension + suspensionDirection * suspensionLength, bodyPose, out wheelPose.Position);
        QuaternionEx.ConcatenateWithoutOverlap(localWheelOrientation, bodyPose.Orientation, out wheelPose.Orientation);
        WheelHandles handles;
        handles.Wheel = simulation.Bodies.Add(BodyDescription.CreateDynamic(wheelPose, wheelInertia, new CollidableDescription(wheelShape, 0.5f), 0.01f));

        handles.SuspensionSpring = simulation.Solver.Add(bodyHandle, handles.Wheel, new LinearAxisServo
        {
            LocalPlaneNormal = suspensionDirection,
            TargetOffset = suspensionLength,
            LocalOffsetA = bodyToWheelSuspension,
            LocalOffsetB = default,
            ServoSettings = ServoSettings.Default,
            SpringSettings = suspensionSettings,
        });
        handles.SuspensionTrack = simulation.Solver.Add(bodyHandle, handles.Wheel, new PointOnLineServo
        {
            LocalDirection = suspensionDirection,
            LocalOffsetA = bodyToWheelSuspension,
            LocalOffsetB = default,
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(30, 1),
        });
        // Braking and acceleration share one motor: it is, after all, a *simple* car.
        handles.Motor = simulation.Solver.Add(handles.Wheel, bodyHandle, new AngularAxisMotor
        {
            LocalAxisA = new Vector3(0, 1, 0),
            Settings = default,
            TargetVelocity = default,
        });
        handles.Hinge = simulation.Solver.Add(bodyHandle, handles.Wheel, hingeDescription);

        // The upstream demo filter only tests one direction, so we make the non-colliding relationship symmetric.
        ref var wheelProperties = ref properties.Allocate(handles.Wheel);
        wheelProperties = new CarBodyProperties { Filter = new SubgroupCollisionFilter(bodyHandle.Value, 1), Friction = wheelFriction };
        SubgroupCollisionFilter.DisableCollision(ref wheelProperties.Filter, ref bodyFilter);

        return handles;
    }

    public static SimpleCar Create(
        Simulation simulation, CollidableProperty<CarBodyProperties> properties, in RigidPose pose,
        TypedIndex bodyShape, BodyInertia bodyInertia, float bodyFriction, TypedIndex wheelShape, BodyInertia wheelInertia, float wheelFriction,
        Vector3 bodyToFrontLeftSuspension, Vector3 bodyToFrontRightSuspension, Vector3 bodyToBackLeftSuspension, Vector3 bodyToBackRightSuspension,
        Vector3 suspensionDirection, float suspensionLength, in SpringSettings suspensionSettings, Quaternion localWheelOrientation)
    {
        SimpleCar car;
        car.Body = simulation.Bodies.Add(BodyDescription.CreateDynamic(pose, bodyInertia, new CollidableDescription(bodyShape, 0.5f), 0.01f));
        ref var bodyProperties = ref properties.Allocate(car.Body);
        bodyProperties = new CarBodyProperties { Friction = bodyFriction, Filter = new SubgroupCollisionFilter(car.Body.Value, 0) };
        QuaternionEx.TransformUnitY(localWheelOrientation, out var wheelAxis);
        car.hingeDescription = new AngularHinge
        {
            LocalHingeAxisA = wheelAxis,
            LocalHingeAxisB = new Vector3(0, 1, 0),
            SpringSettings = new SpringSettings(30, 1),
        };
        car.suspensionDirection = suspensionDirection;
        car.BackLeftWheel = CreateWheel(simulation, properties, pose, wheelShape, wheelInertia, wheelFriction, car.Body, ref bodyProperties.Filter, bodyToBackLeftSuspension, suspensionDirection, suspensionLength, car.hingeDescription, suspensionSettings, localWheelOrientation);
        car.BackRightWheel = CreateWheel(simulation, properties, pose, wheelShape, wheelInertia, wheelFriction, car.Body, ref bodyProperties.Filter, bodyToBackRightSuspension, suspensionDirection, suspensionLength, car.hingeDescription, suspensionSettings, localWheelOrientation);
        car.FrontLeftWheel = CreateWheel(simulation, properties, pose, wheelShape, wheelInertia, wheelFriction, car.Body, ref bodyProperties.Filter, bodyToFrontLeftSuspension, suspensionDirection, suspensionLength, car.hingeDescription, suspensionSettings, localWheelOrientation);
        car.FrontRightWheel = CreateWheel(simulation, properties, pose, wheelShape, wheelInertia, wheelFriction, car.Body, ref bodyProperties.Filter, bodyToFrontRightSuspension, suspensionDirection, suspensionLength, car.hingeDescription, suspensionSettings, localWheelOrientation);
        return car;
    }
}

internal struct SimpleCarController
{
    public SimpleCar Car;

    private float steeringAngle;

    public readonly float SteeringAngle => steeringAngle;

    public float SteeringSpeed;
    public float MaximumSteeringAngle;

    public float ForwardSpeed;
    public float ForwardForce;
    public float ZoomMultiplier;
    public float BackwardSpeed;
    public float BackwardForce;
    public float IdleForce;
    public float BrakeForce;
    public float WheelBaseLength;
    public float WheelBaseWidth;

    /// <summary>
    ///     Fraction of Ackerman steering angle to apply to wheels. 0 leaves the wheels pointed
    ///     exactly along the steering angle; 1 uses the full Ackerman angle.
    /// </summary>
    public float AckermanSteering;

    private float previousTargetSpeed;
    private float previousTargetForce;

    public SimpleCarController(
        SimpleCar car,
        float forwardSpeed, float forwardForce, float zoomMultiplier, float backwardSpeed, float backwardForce, float idleForce, float brakeForce,
        float steeringSpeed, float maximumSteeringAngle, float wheelBaseLength, float wheelBaseWidth, float ackermanSteering)
    {
        Car = car;
        ForwardSpeed = forwardSpeed;
        ForwardForce = forwardForce;
        ZoomMultiplier = zoomMultiplier;
        BackwardSpeed = backwardSpeed;
        BackwardForce = backwardForce;
        IdleForce = idleForce;
        BrakeForce = brakeForce;
        SteeringSpeed = steeringSpeed;
        MaximumSteeringAngle = maximumSteeringAngle;
        WheelBaseLength = wheelBaseLength;
        WheelBaseWidth = wheelBaseWidth;
        AckermanSteering = ackermanSteering;

        steeringAngle = 0;
        previousTargetForce = 0;
        previousTargetSpeed = 0;
    }

    public void Update(Simulation simulation, float dt, float targetSteeringAngle, float targetSpeedFraction, bool zoom, bool brake)
    {
        var steeringAngleDifference = targetSteeringAngle - steeringAngle;
        var maximumChange = SteeringSpeed * dt;
        var steeringAngleChange = MathF.Min(maximumChange, MathF.Max(-maximumChange, steeringAngleDifference));
        var previousSteeringAngle = steeringAngle;

        steeringAngle = MathF.Min(MaximumSteeringAngle, MathF.Max(-MaximumSteeringAngle, steeringAngle + steeringAngleChange));
        if (steeringAngle != previousSteeringAngle)
        {
            float leftSteeringAngle;
            float rightSteeringAngle;

            var steeringAngleAbs = MathF.Abs(steeringAngle);

            if (AckermanSteering > 0 && steeringAngleAbs > 1e-6)
            {
                var turnRadius = MathF.Abs(WheelBaseLength * MathF.Tan(MathF.PI * 0.5f - steeringAngleAbs));
                var wheelBaseHalfWidth = WheelBaseWidth * 0.5f;
                if (steeringAngle > 0)
                {
                    rightSteeringAngle = MathF.Atan(WheelBaseLength / (turnRadius - wheelBaseHalfWidth));
                    rightSteeringAngle = steeringAngle + (rightSteeringAngle - steeringAngleAbs) * AckermanSteering;

                    leftSteeringAngle = MathF.Atan(WheelBaseLength / (turnRadius + wheelBaseHalfWidth));
                    leftSteeringAngle = steeringAngle + (leftSteeringAngle - steeringAngleAbs) * AckermanSteering;
                }
                else
                {
                    rightSteeringAngle = MathF.Atan(WheelBaseLength / (turnRadius + wheelBaseHalfWidth));
                    rightSteeringAngle = steeringAngle - (rightSteeringAngle - steeringAngleAbs) * AckermanSteering;

                    leftSteeringAngle = MathF.Atan(WheelBaseLength / (turnRadius - wheelBaseHalfWidth));
                    leftSteeringAngle = steeringAngle - (leftSteeringAngle - steeringAngleAbs) * AckermanSteering;
                }
            }
            else
            {
                leftSteeringAngle = steeringAngle;
                rightSteeringAngle = steeringAngle;
            }

            // Guarding the constraint modifications behind a state test avoids waking the car every frame.
            Car.Steer(simulation, Car.FrontLeftWheel, leftSteeringAngle);
            Car.Steer(simulation, Car.FrontRightWheel, rightSteeringAngle);
        }

        float newTargetSpeed, newTargetForce;
        bool allWheels;
        if (brake)
        {
            newTargetSpeed = 0;
            newTargetForce = BrakeForce;
            allWheels = true;
        }
        else if (targetSpeedFraction > 0)
        {
            newTargetForce = zoom ? ForwardForce * ZoomMultiplier : ForwardForce;
            newTargetSpeed = targetSpeedFraction * (zoom ? ForwardSpeed * ZoomMultiplier : ForwardSpeed);
            allWheels = false;
        }
        else if (targetSpeedFraction < 0)
        {
            newTargetForce = zoom ? BackwardForce * ZoomMultiplier : BackwardForce;
            newTargetSpeed = targetSpeedFraction * (zoom ? BackwardSpeed * ZoomMultiplier : BackwardSpeed);
            allWheels = false;
        }
        else
        {
            newTargetForce = IdleForce;
            newTargetSpeed = 0;
            allWheels = true;
        }

        if (previousTargetSpeed != newTargetSpeed || previousTargetForce != newTargetForce)
        {
            previousTargetSpeed = newTargetSpeed;
            previousTargetForce = newTargetForce;
            Car.SetSpeed(simulation, Car.FrontLeftWheel, newTargetSpeed, newTargetForce);
            Car.SetSpeed(simulation, Car.FrontRightWheel, newTargetSpeed, newTargetForce);
            if (allWheels)
            {
                Car.SetSpeed(simulation, Car.BackLeftWheel, newTargetSpeed, newTargetForce);
                Car.SetSpeed(simulation, Car.BackRightWheel, newTargetSpeed, newTargetForce);
            }
            else
            {
                Car.SetSpeed(simulation, Car.BackLeftWheel, 0, 0);
                Car.SetSpeed(simulation, Car.BackRightWheel, 0, 0);
            }
        }
    }
}
