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
///     Port of the upstream <c>ContinuousCollisionDetectionDemo</c> (BepuPhysics2, Apache-2.0):
///     three 10×10 grids of boxes falling at 150 m/s show discrete mode (tunnels), unlimited
///     speculative margins (passive) and explicit swept continuous detection. Two spinner pairs
///     (hinge + angular motor on a one-body linear servo) showcase angular CCD.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream Z/X keys and the
///     mouse-locked camera aim are replaced by the deterministic servo oscillation driven from
///     the fixed step time.
///
///     Render-id ranges: 0 = static ground, 100..399 = box grids (discrete/passive/continuous),
///     1000+ = spinner bases and blades.
/// </summary>
public sealed class ContinuousCollisionDetectionDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int BoxRenderIdBase = 100;
    public const int BoxesPerGrid = 100;
    public const int BoxGridCount = 3;
    public const int SpinnerRenderIdBase = 1000;
    public const int SpinnerCount = 4;
    public const int BoxCount = BoxGridCount * BoxesPerGrid;
    public const int SpinnerBodyCount = SpinnerCount * 2;
    public const int MaxRecords = 1 + BoxCount + SpinnerBodyCount;

    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float GroundSize = 300f;
    public const float GroundThickness = 10f;
    public const float FallSpeed = 150f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "continuous-collision-detection",
        "continuous-collision-detection",
        BufferCapacity,
        static (config, transport) => new ContinuousCollisionDetectionDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly record struct Spinner(
        BodyHandle Base, BodyHandle Blade, ConstraintHandle Servo, Vector3 BasePosition);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly TypedIndex _boxShape;
    private readonly BodyInertia _boxInertia;
    private readonly List<Spinner> _spinners = new();

    private double _time;
    private long _seq;

    public ContinuousCollisionDetectionDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(120f, 1f), maximumRecoveryVelocity: 1f),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));

        var box = new Box(1f, 1f, 1f);
        _boxShape = _simulation.Shapes.Add(box);
        _boxInertia = box.ComputeInertia(1f);

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int SpinnerCountValue => _spinners.Count;
    internal double CurrentTime => _time;

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

    /// <summary>Rebuilds the scene deterministically (bodies, constraints and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _spinners.Clear();
            _time = 0d;
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
            UpdateServosLocked();
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _time += deltaSeconds;
            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    /// <summary>Scoots the spinners around (upstream Update: ±3.5 m sine targets on the servos).</summary>
    private void UpdateServosLocked()
    {
        if (_spinners.Count == 0) return;

        var leftTarget = new Vector3(-3.5f * (float)Math.Sin(_time), 10f, -5f);
        var rightTarget = new Vector3(3.5f * (float)Math.Sin(_time), 10f, -5f);
        for (var i = 0; i < _spinners.Count; i++)
        {
            var spinner = _spinners[i];
            var servo = new OneBodyLinearServo
            {
                ServoSettings = ServoSettings.Default,
                SpringSettings = new SpringSettings(30f, 1f),
                Target = spinner.BasePosition + (i % 2 == 0 ? leftTarget : rightTarget),
            };
            _simulation.Solver.ApplyDescription(spinner.Servo, servo);
        }
    }

    private void BuildSceneLocked()
    {
        for (var i = 0; i < 10; ++i)
        {
            for (var j = 0; j < 10; ++j)
            {
                // Grid 0: tiny speculative margin, discrete -> tunnels through the ground.
                AddFallingBoxLocked(new Vector3(-37f + 2f * j, 100f + (i + j) * 2f, -30f + i * 2f), 0.01f, ContinuousDetection.Discrete);
                // Grid 1: unlimited speculative margin, passive.
                AddFallingBoxLocked(new Vector3(-9f + 2f * j, 100f + (i + j) * 2f, -30f + i * 2f), ContinuousDetection.Passive);
                // Grid 2: small margin with an explicit continuous sweep.
                AddFallingBoxLocked(new Vector3(17f + 2f * j, 100f + (i + j) * 2f, -30f + i * 2f), 0.01f, ContinuousDetection.Continuous(1e-3f, 1e-2f));
            }
        }

        BuildSpinnerLocked(new Vector3(-20f, 14f, 0f), 53f, ContinuousDetection.Passive);
        BuildSpinnerLocked(new Vector3(-10f, 14f, 0f), 59f, ContinuousDetection.Passive);
        BuildSpinnerLocked(new Vector3(10f, 14f, 0f), 53f, ContinuousDetection.Continuous(1e-4f, 1e-4f));
        BuildSpinnerLocked(new Vector3(20f, 14f, 0f), 59f, ContinuousDetection.Continuous(1e-4f, 1e-4f));

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -5f, 0f),
            _simulation.Shapes.Add(new Box(GroundSize, GroundThickness, GroundSize))));

        _poses.AddStatic(
            GroundRenderId, new Vector3(0f, -5f, 0f), Quaternion.Identity,
            new Vector3(GroundSize, GroundThickness, GroundSize));
    }

    private void AddFallingBoxLocked(Vector3 position, float maximumSpeculativeMargin, ContinuousDetection continuousDetection)
    {
        var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position), new Vector3(0f, -FallSpeed, 0f), _boxInertia,
            new CollidableDescription(_boxShape, maximumSpeculativeMargin, continuousDetection), 0.01f));
        _poses.AddDynamic(BoxRenderIdBase + _poses.Count, handle, Vector3.One);
    }

    private void AddFallingBoxLocked(Vector3 position, ContinuousDetection continuousDetection) =>
        AddFallingBoxLocked(position, float.MaxValue, continuousDetection);

    private void BuildSpinnerLocked(Vector3 initialPosition, float rotationSpeed, ContinuousDetection continuousDetection)
    {
        var spinnerBase = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(initialPosition), new BodyInertia { InverseMass = 1e-2f },
            _simulation.Shapes.Add(new Box(2f, 2f, 2f)), 0.01f));

        var bladeShape = new Box(5f, 0.01f, 1f);
        var bladeInertia = bladeShape.ComputeInertia(1f);
        var bladeShapeIndex = _simulation.Shapes.Add(bladeShape);
        var spinnerBlade = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(initialPosition), bladeInertia,
            new CollidableDescription(bladeShapeIndex, 0.2f, continuousDetection), 0.01f));

        _simulation.Solver.Add(spinnerBase, spinnerBlade, new Hinge
        {
            LocalHingeAxisA = new Vector3(0f, 0f, 1f),
            LocalHingeAxisB = new Vector3(0f, 0f, 1f),
            LocalOffsetB = new Vector3(0f, 0f, -3f),
            SpringSettings = new SpringSettings(30f, 1f),
        });
        _simulation.Solver.Add(spinnerBase, spinnerBlade, new AngularAxisMotor
        {
            LocalAxisA = new Vector3(0f, 0f, 1f),
            Settings = new MotorSettings(10f, 1e-4f),
            TargetVelocity = rotationSpeed,
        });
        var servo = _simulation.Solver.Add(spinnerBase, new OneBodyLinearServo
        {
            ServoSettings = ServoSettings.Default,
            SpringSettings = new SpringSettings(30f, 1f),
        });

        var spinnerIndex = _spinners.Count;
        _spinners.Add(new Spinner(spinnerBase, spinnerBlade, servo, initialPosition));
        _poses.AddDynamic(SpinnerRenderIdBase + spinnerIndex * 2, spinnerBase, new Vector3(2f));
        _poses.AddDynamic(SpinnerRenderIdBase + spinnerIndex * 2 + 1, spinnerBlade, new Vector3(5f, 0.01f, 1f));
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
