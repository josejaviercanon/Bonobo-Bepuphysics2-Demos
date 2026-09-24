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
using Game.BepuDemos.Demos.Characters;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>Characters/CharacterDemo</c> (BepuPhysics2, Apache-2.0, Ross
///     Nordby): a dynamic capsule character (custom motion constraints via
///     <see cref="CharacterControllers"/>) walking over legos, spinning fans, a tongue, a seesaw,
///     moving platforms and a giant static newt.
///
///     Test-bed deviations (documented in docs/compat-review.md): character goals arrive through
///     the pinned input ring (<see cref="ICharacterMoveSink"/>, camera-relative direction computed
///     client side) instead of OpenTK keyboard/camera polling; the camera is the shared orbit
///     camera following the capsule; the C-key character add/remove toggle is dropped; the newt
///     mesh comes from the embedded OBJ parsed by <see cref="ObjMeshParser"/>; the character
///     spawns beside the lego field (the upstream spawn sits inside the 15× newt mesh).
///
///     Render-id ranges: 0 = floor, 1 = static newt (client loads newt.obj at scale 15),
///     2 = character capsule, 1000+ = lego boxes, 10000+ = fan base/blade, 20000 = tongue,
///     20100+ = seesaw parts, 30000+ = moving platforms, 40000+ = static box field.
/// </summary>
public sealed class CharacterDemo : IDemoSimulation, ICharacterMoveSink
{
    public const int FloorRenderId = 0;
    public const int NewtRenderId = 1;
    public const int CharacterRenderId = 2;
    public const int LegoRenderIdBase = 1_000;
    public const int FanRenderIdBase = 10_000;
    public const int TongueRenderId = 20_000;
    public const int SeesawRenderIdBase = 20_100;
    public const int PlatformRenderIdBase = 30_000;
    public const int BoxFieldRenderIdBase = 40_000;

    public const int LegoGridSize = 12;
    public const int LegoCount = LegoGridSize * LegoGridSize;
    public const int FanCount = 3;
    public const int PlatformCount = 16;
    public const int BoxFieldWidth = 8;
    public const int BoxFieldCount = BoxFieldWidth * BoxFieldWidth;

    /// <summary>
    ///     Every emitted record: floor/newt/character statics, legos, fan bases+blades, tongue,
    ///     seesaw blade+payload (the kinematic seesaw base has no collidable and no record),
    ///     platforms and the static box field.
    /// </summary>
    public const int MaxTransformCount =
        3 + LegoCount + FanCount * 2 + 1 + 2 + PlatformCount + BoxFieldCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "character",
        "character",
        BufferCapacity,
        static (config, transport) => new CharacterDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private struct MovingPlatform
    {
        public BodyHandle BodyHandle;
        public float InverseGoalSatisfactionTime;
        public double TimeOffset;
        public Func<double, RigidPose> PoseCreator;

        public MovingPlatform(CollidableDescription collidable, double timeOffset, float goalSatisfactionTime, Simulation simulation, Func<double, RigidPose> poseCreator)
        {
            PoseCreator = poseCreator;
            BodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(poseCreator(timeOffset), collidable, -1));
            InverseGoalSatisfactionTime = 1f / goalSatisfactionTime;
            TimeOffset = timeOffset;
        }

        public readonly void Update(Simulation simulation, double time)
        {
            var body = simulation.Bodies[BodyHandle];
            ref var pose = ref body.Pose;
            ref var velocity = ref body.Velocity;
            var targetPose = PoseCreator(time + TimeOffset);
            velocity.Linear = (targetPose.Position - pose.Position) * InverseGoalSatisfactionTime;
            QuaternionEx.GetRelativeRotationWithoutOverlap(pose.Orientation, targetPose.Orientation, out var rotation);
            QuaternionEx.GetAxisAngleFromQuaternion(rotation, out var axis, out var angle);
            velocity.Angular = axis * (angle * InverseGoalSatisfactionTime);
        }
    }

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly CharacterControllers _characters;
    private readonly CharacterInput _character;
    private readonly MovingPlatform[] _movingPlatforms = new MovingPlatform[PlatformCount];

    private double _inputMoveX;
    private double _inputMoveZ;
    private bool _inputJump;
    private bool _inputSprint;
    private double _time;
    private long _seq;

    public CharacterDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _characters = new CharacterControllers();
        _simulation = Simulation.Create(
            _bufferPool,
            new CharacterNarrowphaseCallbacks(_characters),
            new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        // Deviation: the upstream spawn (0, 2, -4) sits inside the 15× newt mesh, hiding the
        // capsule from the default camera; spawn just east of the lego field instead.
        _character = new CharacterInput(_characters, new Vector3(12, 2, -8), new Capsule(0.5f, 1), 0.1f, 1, 20, 100, 6, 4, MathF.PI * 0.4f);
        _poses.AddDynamic(CharacterRenderId, _character.BodyHandle, Vector3.One);

        // Create a bunch of legos to hurt your feet on.
        var random = new Random(5);
        var origin = new Vector3(-3f, 0.5f, 0);
        var spacing = new Vector3(0.5f, 0, -0.5f);
        for (var i = 0; i < LegoGridSize; ++i)
        {
            for (var j = 0; j < LegoGridSize; ++j)
            {
                var position = origin + new Vector3(i, 0, j) * spacing;
                var orientation = QuaternionEx.CreateFromAxisAngle(Vector3.Normalize(new Vector3(0.0001f) + new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle())), 10 * random.NextSingle());
                var shape = new Box(0.1f + 0.3f * random.NextSingle(), 0.1f + 0.3f * random.NextSingle(), 0.1f + 0.3f * random.NextSingle());
                var shapeIndex = _simulation.Shapes.Add(shape);
                var renderId = LegoRenderIdBase + i * LegoGridSize + j;
                var scale = new Vector3(shape.HalfWidth, shape.HalfHeight, shape.HalfLength) * 2f;
                var choice = (i + j) % 3;
                switch (choice)
                {
                    case 0:
                        _poses.AddDynamic(renderId, _simulation.Bodies.Add(BodyDescription.CreateDynamic(new RigidPose(position, orientation), shape.ComputeInertia(1), shapeIndex, 0.01f)), scale);
                        break;
                    case 1:
                        _poses.AddDynamic(renderId, _simulation.Bodies.Add(BodyDescription.CreateKinematic(new RigidPose(position, orientation), shapeIndex, 0.01f)), scale);
                        break;
                    case 2:
                        _simulation.Statics.Add(new StaticDescription(position, orientation, shapeIndex));
                        _poses.AddStatic(renderId, position, orientation, scale);
                        break;
                }
            }
        }

        // Add some spinning fans to get slapped by.
        var bladeDescription = BodyDescription.CreateConvexDynamic(new Vector3(), 3, _simulation.Shapes, new Box(10, 0.2f, 2));
        var bladeBaseDescription = BodyDescription.CreateConvexKinematic(new Vector3(), _simulation.Shapes, new Box(0.2f, 1, 0.2f));
        for (var i = 0; i < FanCount; ++i)
        {
            bladeBaseDescription.Pose.Position = new Vector3(-22, 1, i * 11);
            bladeDescription.Pose.Position = new Vector3(-22, 1.7f, i * 11);
            var baseHandle = _simulation.Bodies.Add(bladeBaseDescription);
            var bladeHandle = _simulation.Bodies.Add(bladeDescription);
            _simulation.Solver.Add(baseHandle, bladeHandle, new Hinge
            {
                LocalHingeAxisA = Vector3.UnitY,
                LocalHingeAxisB = Vector3.UnitY,
                LocalOffsetA = new Vector3(0, 0.7f, 0),
                LocalOffsetB = new Vector3(0, 0, 0),
                SpringSettings = new SpringSettings(30, 1),
            });
            _simulation.Solver.Add(baseHandle, bladeHandle, new AngularAxisMotor
            {
                LocalAxisA = Vector3.UnitY,
                TargetVelocity = (i + 1) * (i + 1) * (i + 1) * (i + 1) * 0.2f,
                Settings = new MotorSettings(5 * (i + 1), 0.0001f),
            });
            _poses.AddDynamic(FanRenderIdBase + i * 2, baseHandle, new Vector3(0.4f, 2f, 0.4f));
            _poses.AddDynamic(FanRenderIdBase + i * 2 + 1, bladeHandle, new Vector3(20f, 0.4f, 4f));
        }

        // Include a giant newt to test character-newt behavior and to ensure thematic consistency.
        var newtMesh = ObjMeshParser.CreateMesh(ObjMeshParser.Parse(DemoContent.NewtObjText), new Vector3(15, 15, 15), _bufferPool);
        var newtPosition = new Vector3(0, 0.5f, 0);
        _simulation.Statics.Add(new StaticDescription(newtPosition, _simulation.Shapes.Add(newtMesh)));
        _poses.AddStatic(NewtRenderId, newtPosition, Quaternion.Identity, new Vector3(15, 15, 15));

        // Give the newt a tongue, I guess.
        var tongueBase = _simulation.Bodies.Add(BodyDescription.CreateKinematic(new Vector3(0, 8.4f, 24), default, default));
        var tongue = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(new Vector3(0, 8.4f, 27.5f), 1, _simulation.Shapes, new Box(1, 0.1f, 6f)));
        _simulation.Solver.Add(tongueBase, tongue, new Hinge
        {
            LocalHingeAxisA = Vector3.UnitX,
            LocalHingeAxisB = Vector3.UnitX,
            LocalOffsetB = new Vector3(0, 0, -3f),
            SpringSettings = new SpringSettings(30, 1),
        });
        _simulation.Solver.Add(tongueBase, tongue, new AngularServo
        {
            TargetRelativeRotationLocalA = Quaternion.Identity,
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(2, 0),
        });
        _poses.AddDynamic(TongueRenderId, tongue, new Vector3(2f, 0.2f, 12f));

        // And a seesaw thing?
        var seesawBase = _simulation.Bodies.Add(BodyDescription.CreateKinematic(new Vector3(0, 1f, 34f), _simulation.Shapes.Add(new Box(0.2f, 1, 0.2f)), 0.01f));
        var seesaw = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(new Vector3(0, 1.7f, 34f), 1, _simulation.Shapes, new Box(1, 0.1f, 6f)));
        _simulation.Solver.Add(seesawBase, seesaw, new Hinge
        {
            LocalHingeAxisA = Vector3.UnitX,
            LocalHingeAxisB = Vector3.UnitX,
            LocalOffsetA = new Vector3(0, 0.7f, 0),
            LocalOffsetB = new Vector3(0, 0, 0),
            SpringSettings = new SpringSettings(30, 1),
        });
        var seesawThing = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(new Vector3(0, 2.25f, 35.5f), 0.5f, _simulation.Shapes, new Box(1f, 1f, 1f)));
        _poses.AddDynamic(SeesawRenderIdBase, seesaw, new Vector3(2f, 0.2f, 12f));
        _poses.AddDynamic(SeesawRenderIdBase + 2, seesawThing, new Vector3(2f, 2f, 2f));

        // Create some moving platforms to jump on.
        Func<double, RigidPose> poseCreator = time =>
        {
            RigidPose pose;
            var horizontalScale = (float)(45 + 10 * Math.Sin(time * 0.015));
            // Float in a noisy ellipse around the newt.
            pose.Position = new Vector3(0.7f * horizontalScale * (float)Math.Sin(time * 0.1), 10 + 4 * (float)Math.Sin((time + Math.PI * 0.5f) * 0.25), horizontalScale * (float)Math.Cos(time * 0.1));
            // As the platform goes behind the newt, dip toward the ground. Use smoothstep for a less jerky ride.
            var x = MathF.Max(0f, MathF.Min(1f, 1f - (pose.Position.Z + 20f) / -20f));
            var smoothStepped = 3 * x * x - 2 * x * x * x;
            pose.Position.Y = smoothStepped * (pose.Position.Y - 0.025f) + 0.025f;
            pose.Orientation = Quaternion.Identity;
            return pose;
        };
        var platformShapeIndex = _simulation.Shapes.Add(new Box(5, 1, 5));
        for (var i = 0; i < PlatformCount; ++i)
        {
            _movingPlatforms[i] = new MovingPlatform(platformShapeIndex, i * 3559, 1f / 60f, _simulation, poseCreator);
            _poses.AddDynamic(PlatformRenderIdBase + i, _movingPlatforms[i].BodyHandle, new Vector3(10f, 2f, 10f));
        }

        var box = new Box(4, 1, 4);
        var boxShapeIndex = _simulation.Shapes.Add(box);
        for (var i = 0; i < BoxFieldWidth; ++i)
        {
            for (var j = 0; j < BoxFieldWidth; ++j)
            {
                var position = new Vector3(box.Width, 0, box.Length) * new Vector3(i, 0, j) + new Vector3(32f, 1, 0);
                _simulation.Statics.Add(new StaticDescription(position, boxShapeIndex));
                _poses.AddStatic(BoxFieldRenderIdBase + i * BoxFieldWidth + j, position, Quaternion.Identity, new Vector3(8f, 2f, 8f));
            }
        }

        // Prevent the character from falling into the void.
        var floorPosition = new Vector3(0, 0, 0);
        _simulation.Statics.Add(new StaticDescription(floorPosition, _simulation.Shapes.Add(new Box(200, 1, 200))));
        _poses.AddStatic(FloorRenderId, floorPosition, Quaternion.Identity, new Vector3(400f, 2f, 400f));
    }

    /// <summary>Routes one decoded character-move packet (generated dispatcher → active simulation).</summary>
    public void OnCharacterMove(in CharacterMoveInput input)
    {
        lock (_sync)
        {
            _inputMoveX = input.MoveX;
            _inputMoveZ = input.MoveZ;
            _inputJump = input.Jump > 0.5d;
            _inputSprint = input.Sprint > 0.5d;
        }
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            _character.UpdateCharacterGoals(
                new Vector2((float)_inputMoveX, (float)_inputMoveZ), _inputJump, _inputSprint, (float)deltaSeconds);

            // Using a fixed time per update to match the simulation update rate.
            _time += deltaSeconds;
            for (var i = 0; i < _movingPlatforms.Length; ++i)
            {
                _movingPlatforms[i].Update(_simulation, _time);
            }

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
