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
///     Port of the upstream <c>PerBodyGravityDemo</c> (BepuPhysics2, Apache-2.0): custom
///     <c>IPoseIntegratorCallbacks.IntegrateVelocity</c> gathers a per-body gravity value from a
///     <c>CollidableProperty&lt;float&gt;</c> keyed by body handle. Spheres fall slowly (-0.1),
///     capsules in between (-3), boxes quickly (-10), cycling by <c>(i + k) % 3</c>.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream 20×20×20 grid
///     (8000 bodies) is reduced to 20×20×4 = 1600 so the desktop test host stays interactive;
///     the per-shape gravity sweep is preserved.
///
///     Render-id ranges: 0 = static floor, 100+ = bodies.
/// </summary>
public sealed class PerBodyGravityDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int BodyRenderIdBase = 100;

    public const int Length = 20;
    public const int Width = 20;
    public const int Height = 4;
    public const int BodyCount = Length * Width * Height;
    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + BodyCount) * SignalBufferLayout.Transform3DStride;

    public const float Spacing = 4f;
    public const float SphereGravity = -0.1f;
    public const float CapsuleGravity = -3f;
    public const float BoxGravity = -10f;

    public const float FloorSize = 1000f;
    public const float FloorThickness = 10f;

    public const int ShapeKindSphere = 0;
    public const int ShapeKindCapsule = 1;
    public const int ShapeKindBox = 2;

    /// <summary>
    ///     Per-body gravity gather (upstream): one handle lookup per SIMD lane, written into
    ///     <c>velocity.Linear.Y</c>. Public for the unit tests' gravity probes.
    /// </summary>
    public struct PerBodyGravityCallbacks : IPoseIntegratorCallbacks
    {
        public CollidableProperty<float> BodyGravities;
        private Bodies _bodies = default!;

        public PerBodyGravityCallbacks(CollidableProperty<float> bodyGravities)
            : this()
        {
            BodyGravities = bodyGravities;
        }

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        public readonly bool AllowSubstepsForUnconstrainedBodies => false;

        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
            BodyGravities.Initialize(simulation);
            _bodies = simulation.Bodies;
        }

        public void PrepareForIntegration(float dt)
        {
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            Span<float> gravityValues = stackalloc float[Vector<float>.Count];
            for (var bundleSlotIndex = 0; bundleSlotIndex < Vector<int>.Count; ++bundleSlotIndex)
            {
                var bodyIndex = bodyIndices[bundleSlotIndex];
                if (bodyIndex >= 0)
                {
                    var bodyHandle = _bodies.ActiveSet.IndexToHandle[bodyIndex];
                    gravityValues[bundleSlotIndex] = BodyGravities[bodyHandle];
                }
            }

            velocity.Linear.Y += new Vector<float>(gravityValues) * dt;
        }
    }

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "per-body-gravity",
        "per-body-gravity",
        BufferCapacity,
        static (config, transport) => new PerBodyGravityDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollidableProperty<float> _bodyGravities = new();
    private readonly DemoPoseSet _poses;
    private readonly TypedIndex _sphereShape;
    private readonly TypedIndex _capsuleShape;
    private readonly TypedIndex _boxShape;
    private readonly BodyInertia _sphereInertia;
    private readonly BodyInertia _capsuleInertia;
    private readonly BodyInertia _boxInertia;

    private long _seq;

    public PerBodyGravityDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30f, 1f)),
            new PerBodyGravityCallbacks(_bodyGravities),
            new SolveDescription(4, 1));

        var sphere = new Sphere(1f);
        _sphereShape = _simulation.Shapes.Add(sphere);
        _sphereInertia = sphere.ComputeInertia(1f);

        var capsule = new Capsule(1f, 1f);
        _capsuleShape = _simulation.Shapes.Add(capsule);
        _capsuleInertia = capsule.ComputeInertia(1f);

        var box = new Box(1f, 1f, 1f);
        _boxShape = _simulation.Shapes.Add(box);
        _boxInertia = box.ComputeInertia(1f);

        _poses = new DemoPoseSet(_world, _simulation);
        BuildGridLocked();
    }

    internal int BodyEntityCount => BodyCount;

    /// <summary>Test probe: the Bepu body handle owned by a body render id.</summary>
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

    /// <summary>Test probe: the shape kind (0 sphere / 1 capsule / 2 box) of a body render id.</summary>
    internal static int ShapeKindOf(int renderId)
    {
        var index = renderId - BodyRenderIdBase;
        var i = index / (Height * Width);
        var k = index % Width;
        return ((i + k) % 3) switch
        {
            0 => ShapeKindSphere,
            1 => ShapeKindCapsule,
            _ => ShapeKindBox,
        };
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

    /// <summary>Rebuilds the grid deterministically (bodies and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _seq = 0;
            BuildGridLocked();
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

    private void BuildGridLocked()
    {
        var origin = new Vector3(0f, 40f, 0f) + new Vector3(Spacing) * new Vector3(Length * -0.5f, 0f, Width * -0.5f);

        for (var i = 0; i < Length; i++)
        {
            for (var j = 0; j < Height; j++)
            {
                for (var k = 0; k < Width; k++)
                {
                    BodyInertia inertia;
                    TypedIndex shapeIndex;
                    float gravity;
                    Vector3 scale;
                    switch ((i + k) % 3)
                    {
                        case 0:
                            inertia = _sphereInertia;
                            shapeIndex = _sphereShape;
                            gravity = SphereGravity;
                            scale = new Vector3(2f);
                            break;
                        case 1:
                            inertia = _capsuleInertia;
                            shapeIndex = _capsuleShape;
                            gravity = CapsuleGravity;
                            scale = Vector3.One;
                            break;
                        default:
                            inertia = _boxInertia;
                            shapeIndex = _boxShape;
                            gravity = BoxGravity;
                            scale = Vector3.One;
                            break;
                    }

                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        origin + new Vector3(i, j, k) * Spacing, Vector3.Zero, inertia, shapeIndex, 0.001f));
                    _bodyGravities.Allocate(handle) = gravity;

                    _poses.AddDynamic(BodyRenderIdBase + i * Height * Width + j * Width + k, handle, scale);
                }
            }
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(), _simulation.Shapes.Add(new Box(FloorSize, FloorThickness, FloorSize))));

        _poses.AddStatic(
            FloorRenderId, new Vector3(), Quaternion.Identity,
            new Vector3(FloorSize, FloorThickness, FloorSize));
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
