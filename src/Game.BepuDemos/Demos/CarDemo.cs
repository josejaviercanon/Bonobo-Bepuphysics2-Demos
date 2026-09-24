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
using DemoEngine.Inputs;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>Cars/CarDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby): a
///     player car plus AI cars racing on a quarter-circle track, built from compound body +
///     four suspension/hinge/motor wheels with Ackerman steering, driven by
///     <see cref="SimpleCarController"/>.
///
///     Test-bed deviations (documented in docs/compat-review.md): AI car count reduced from
///     384 to <see cref="AiCarCount"/> and the terrain from 257×257×3 to 129×129×6 (same world
///     extent, same deformer formula duplicated client side); the player car is driven by the
///     pinned input ring (<see cref="IVehicleControlSink"/>) instead of OpenTK keyboard state;
///     the behind-car camera and the on-screen control overlay are replaced by the shared orbit
///     camera; the C-key player/AI toggle is dropped (the player car is always active).
///
///     Render-id ranges: 100 + carIndex·8 + child = car records (carIndex 0 = player, 1..=
///     AI; child 0 = body box, 1 = cabin box, 2..5 = wheels); 100000+ = static landmark
///     buildings. The terrain is a presentation-only deformed plane (the client rebuilds the
///     same heightfield from the shared formula; no record is emitted).
/// </summary>
public sealed class CarDemo : IDemoSimulation, IVehicleControlSink
{
    public const int CarRenderIdBase = 100;
    public const int CarRenderIdStride = 8;
    public const int BodyChildOffset = 0;
    public const int CabinChildOffset = 1;
    public const int FrontLeftWheelChildOffset = 2;
    public const int FrontRightWheelChildOffset = 3;
    public const int BackLeftWheelChildOffset = 4;
    public const int BackRightWheelChildOffset = 5;
    public const int BuildingRenderIdBase = 100_000;

    public const int PlayerCarIndex = 0;
    public const int AiCarCount = 64;
    public const int BuildingLandmarkCount = 4;
    public const int BuildingsPerLandmark = 25;
    public const int BuildingCount = BuildingLandmarkCount * BuildingsPerLandmark;

    public const int PlaneWidth = 129;
    public const float TerrainScale = 6f;

    public const int MaxTransformCount = (AiCarCount + 1) * 6 + BuildingCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "car",
        "car",
        BufferCapacity,
        static (config, transport) => new CarDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private struct AIController
    {
        public SimpleCarController Controller;
        public float LaneOffset;
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly SimpleCarController _playerController;
    private readonly AIController[] _aiControllers = new AIController[AiCarCount];
    private readonly RaceTrack _raceTrack;

    private readonly Vector3 _cabinLocalPosition = new(0f, 0.65f, -0.35f);

    private double _targetSteering;
    private double _targetThrottle;
    private bool _zoom;
    private bool _brake;
    private long _seq;

    public CarDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        var properties = new CollidableProperty<CarBodyProperties>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new CarCallbacks { Properties = properties },
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(6, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        var builder = new CompoundBuilder(_bufferPool, _simulation.Shapes, 2);
        builder.Add(new Box(1.85f, 0.7f, 4.73f), RigidPose.Identity, 10);
        builder.Add(new Box(1.85f, 0.6f, 2.5f), new RigidPose(_cabinLocalPosition), 0.5f);
        builder.BuildDynamicCompound(out var children, out var bodyInertia, out _);
        builder.Dispose();
        var bodyShapeIndex = _simulation.Shapes.Add(new Compound(children));
        var wheelShape = new Cylinder(0.4f, 0.18f);
        var wheelInertia = wheelShape.ComputeInertia(0.25f);
        var wheelShapeIndex = _simulation.Shapes.Add(wheelShape);

        const float x = 0.9f;
        const float y = -0.1f;
        const float frontZ = 1.7f;
        const float backZ = -1.7f;
        const float wheelBaseWidth = x * 2;
        const float wheelBaseLength = frontZ - backZ;

        _playerController = new SimpleCarController(
            SimpleCar.Create(_simulation, properties, new RigidPose(new Vector3(0, 10, 0)), bodyShapeIndex, bodyInertia, 0.35f, wheelShapeIndex, wheelInertia, 1f,
                new Vector3(-x, y, frontZ), new Vector3(x, y, frontZ), new Vector3(-x, y, backZ), new Vector3(x, y, backZ), new Vector3(0, -1, 0), 0.25f,
                new SpringSettings(5f, 0.7f), QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f)),
            forwardSpeed: 75, forwardForce: 6, zoomMultiplier: 2, backwardSpeed: 30, backwardForce: 4, idleForce: 0.25f, brakeForce: 7,
            steeringSpeed: 1.5f, maximumSteeringAngle: MathF.PI * 0.23f,
            wheelBaseLength: wheelBaseLength, wheelBaseWidth: wheelBaseWidth, ackermanSteering: 1);

        const float scale = TerrainScale;
        var terrainPosition = new Vector2(1 - PlaneWidth, 1 - PlaneWidth) * scale * 0.5f;
        _raceTrack = new RaceTrack { QuadrantRadius = (PlaneWidth - 32) * scale * 0.25f, Center = default };
        var random = new Random(5);

        // Landmark buildings in the middle of each race-track quadrant.
        var buildingCount = 0;
        for (var i = 0; i < BuildingLandmarkCount; ++i)
        {
            var landmarkCenter = new Vector3((i & 1) * _raceTrack.QuadrantRadius * 2 - _raceTrack.QuadrantRadius, -20, (i & 2) * _raceTrack.QuadrantRadius - _raceTrack.QuadrantRadius);
            var landmarkMin = landmarkCenter -new Vector3(_raceTrack.QuadrantRadius * 0.5f, 0, _raceTrack.QuadrantRadius * 0.5f);
            var landmarkSpan = new Vector3(_raceTrack.QuadrantRadius, 0, _raceTrack.QuadrantRadius);
            for (var j = 0; j < BuildingsPerLandmark; ++j)
            {
                var buildingShape = new Box(10 + random.NextSingle() * 10, 20 + random.NextSingle() * 20, 10 + random.NextSingle() * 10);
                var position = new Vector3(0, buildingShape.HalfHeight, 0) + landmarkMin + landmarkSpan * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
                var orientation = QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, random.NextSingle() * MathF.PI);
                _simulation.Statics.Add(new StaticDescription(position, orientation, _simulation.Shapes.Add(buildingShape)));
                _poses.AddStatic(
                    BuildingRenderIdBase + buildingCount++,
                    position,
                    orientation,
                    new Vector3(buildingShape.HalfWidth, buildingShape.HalfHeight, buildingShape.HalfLength) * 2f);
            }
        }

        var min = new Vector3(-PlaneWidth * scale * 0.45f, 10, -PlaneWidth * scale * 0.45f);
        var span = new Vector3(PlaneWidth * scale * 0.9f, 15, PlaneWidth * scale * 0.9f);

        for (var i = 0; i < AiCarCount; ++i)
        {
            var position = min + span * new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle());
            var orientation = QuaternionEx.CreateFromAxisAngle(new Vector3(0, 1, 0), random.NextSingle() * MathF.PI * 2);
            _aiControllers[i].Controller = new SimpleCarController(
                SimpleCar.Create(_simulation, properties, new RigidPose(position, orientation), bodyShapeIndex, bodyInertia, 0.5f, wheelShapeIndex, wheelInertia, 2f,
                    new Vector3(-x, y, frontZ), new Vector3(x, y, frontZ), new Vector3(-x, y, backZ), new Vector3(x, y, backZ), new Vector3(0, -1, 0), 0.25f,
                    new SpringSettings(5, 0.7f), QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f)),
                forwardSpeed: 50, forwardForce: 5, zoomMultiplier: 2, backwardSpeed: 10, backwardForce: 4, idleForce: 0.25f, brakeForce: 7,
                steeringSpeed: 1.5f, maximumSteeringAngle: MathF.PI * 0.23f,
                wheelBaseLength: wheelBaseLength, wheelBaseWidth: wheelBaseWidth, ackermanSteering: 1);
            _aiControllers[i].LaneOffset = random.NextSingle() * 20 - 10;
        }

        var track = _raceTrack;
        var planeMesh = DemoMeshHelper.CreateDeformedPlane(PlaneWidth, PlaneWidth,
            (vX, vY) =>
            {
                var octave0 = (MathF.Sin((vX + 5f) * 0.05f) + MathF.Sin((vY + 11) * 0.05f)) * 1.8f;
                var octave1 = (MathF.Sin((vX + 17) * 0.15f) + MathF.Sin((vY + 19) * 0.15f)) * 0.9f;
                var octave2 = (MathF.Sin((vX + 37) * 0.35f) + MathF.Sin((vY + 93) * 0.35f)) * 0.4f;
                var octave3 = (MathF.Sin((vX + 53) * 0.65f) + MathF.Sin((vY + 47) * 0.65f)) * 0.2f;
                var octave4 = (MathF.Sin((vX + 67) * 1.50f) + MathF.Sin((vY + 13) * 1.5f)) * 0.125f;
                var distanceToEdge = PlaneWidth / 2 - Math.Max(Math.Abs(vX - PlaneWidth / 2), Math.Abs(vY - PlaneWidth / 2));
                var edgeRamp = 25f / (distanceToEdge + 1);
                var terrainHeight = octave0 + octave1 + octave2 + octave3 + octave4;
                var vertexPosition = new Vector2(vX * scale, vY * scale) + terrainPosition;
                var distanceToTrack = track.GetDistance(vertexPosition);
                var trackWeight = MathF.Min(1f, 3f / (distanceToTrack * 0.1f + 1f));
                var height = trackWeight * -10f + terrainHeight * (1 - trackWeight);
                return new Vector3(vertexPosition.X, height + edgeRamp, vertexPosition.Y);
            }, new Vector3(1, 1, 1), _bufferPool);
        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0, -15, 0),
            QuaternionEx.CreateFromAxisAngle(new Vector3(0, 1, 0), MathF.PI / 2),
            _simulation.Shapes.Add(planeMesh)));

        RegisterCarRenderRecords(PlayerCarIndex, _playerController.Car);
        for (var i = 0; i < AiCarCount; ++i) RegisterCarRenderRecords(i + 1, _aiControllers[i].Controller.Car);
    }

    /// <summary>Routes one decoded vehicle-control packet (generated dispatcher → active simulation).</summary>
    public void OnVehicleControl(in VehicleControlInput input)
    {
        lock (_sync)
        {
            _targetThrottle = input.Throttle;
            _targetSteering = input.Steer;
            _zoom = input.Zoom > 0.5d;
            _brake = input.Brake > 0.5d;
        }
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            // Upstream order: controllers write constraint targets, then the step integrates them.
            UpdateControllersLocked((float)deltaSeconds);
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void UpdateControllersLocked(float dt)
    {
        _playerController.Update(_simulation, dt, (float)_targetSteering, (float)_targetThrottle, _zoom, _brake);

        for (var i = 0; i < _aiControllers.Length; ++i)
        {
            ref var ai = ref _aiControllers[i];
            var body = _simulation.Bodies[ai.Controller.Car.Body];
            ref var pose = ref body.Pose;
            Matrix3x3.CreateFromQuaternion(pose.Orientation, out var orientation);
            var forwardVelocity = Vector3.Dot(orientation.Z, body.Velocity.Linear);
            var predictedLocation = new Vector2(pose.Position.X, pose.Position.Z) + new Vector2(orientation.Z.X, orientation.Z.Z) * (5 + forwardVelocity * 2);
            _raceTrack.GetClosestPoint(predictedLocation, ai.LaneOffset, out var closestPoint, out var flowDirection);
            float steeringAngle;
            if (flowDirection.X * orientation.Z.X + flowDirection.Y * orientation.Z.Z < 0)
            {
                // Don't drive against traffic!
                steeringAngle = ai.Controller.MaximumSteeringAngle;
            }
            else
            {
                var toClosestPoint = closestPoint - new Vector2(pose.Position.X, pose.Position.Z);
                var horizontalOffset = orientation.X.X * toClosestPoint.X + orientation.X.Z * toClosestPoint.Y;
                var forwardOffset = orientation.Z.X * toClosestPoint.X + orientation.Z.Z * toClosestPoint.Y;
                steeringAngle = MathF.Atan2(horizontalOffset, forwardOffset);
            }

            var speedFraction = 0.25f + MathF.Min(0.75f, MathF.Max(0, 0.75f * (MathF.Abs(steeringAngle) - 0.2f) / -0.4f));
            if (orientation.Y.Y < 0.4f)
                speedFraction = 0;

            ai.Controller.Update(_simulation, dt, steeringAngle, speedFraction, steeringAngle < 0.05f, steeringAngle > MathF.PI * 0.2f && forwardVelocity > ai.Controller.ForwardSpeed * 0.6f);
        }
    }

    private void RegisterCarRenderRecords(int carIndex, in SimpleCar car)
    {
        var baseId = CarRenderIdBase + carIndex * CarRenderIdStride;
        _poses.AddDynamic(baseId + BodyChildOffset, car.Body, new Vector3(3.7f, 1.4f, 9.46f));
        _poses.AddDynamic(baseId + CabinChildOffset, car.Body, new Vector3(3.7f, 1.2f, 5f), new RigidPose(_cabinLocalPosition), true);
        _poses.AddDynamic(baseId + FrontLeftWheelChildOffset, car.FrontLeftWheel.Wheel, new Vector3(0.8f, 0.36f, 0.8f));
        _poses.AddDynamic(baseId + FrontRightWheelChildOffset, car.FrontRightWheel.Wheel, new Vector3(0.8f, 0.36f, 0.8f));
        _poses.AddDynamic(baseId + BackLeftWheelChildOffset, car.BackLeftWheel.Wheel, new Vector3(0.8f, 0.36f, 0.8f));
        _poses.AddDynamic(baseId + BackRightWheelChildOffset, car.BackRightWheel.Wheel, new Vector3(0.8f, 0.36f, 0.8f));
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
