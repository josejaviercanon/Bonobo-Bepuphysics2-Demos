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
///     Port of the upstream <c>PlanetDemo</c> (BepuPhysics2, Apache-2.0): custom velocity
///     integration pulls every body toward a planet center (inverse-square gravity), and a
///     sheet of orbiting spheres is launched at 30 u/s.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream 40×20×40 sheet
///     (32,000 spheres) is reduced to 24×8×24 = 4608 spheres so the desktop test host stays
///     interactive; gravity, launch velocity and geometry are unchanged.
///
///     Render-id ranges: 0 = static planet sphere, 100+ = orbiting spheres.
/// </summary>
public sealed class PlanetDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int PlanetRenderId = 0;
    public const int BallRenderIdBase = 100;

    public const int SheetLength = 24;
    public const int SheetHeight = 8;
    public const int SheetWidth = 24;
    public const int BallCount = SheetLength * SheetHeight * SheetWidth;
    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (1 + BallCount) * SignalBufferLayout.Transform3DStride;

    public const float PlanetRadius = 50f;
    public const float BallRadius = 1f;
    public const float GravityStrength = 100_000f;
    public const float LaunchSpeed = 30f;
    public const float SheetSpacing = 5f;

    /// <summary>Inverse-square gravity toward <see cref="PlanetCenter"/> (upstream logic).</summary>
    public struct PlanetaryGravityCallbacks : IPoseIntegratorCallbacks
    {
        public Vector3 PlanetCenter;
        public float Gravity;

        public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;

        public readonly bool AllowSubstepsForUnconstrainedBodies => false;

        public readonly bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation)
        {
        }

        private float _gravityDt;

        public void PrepareForIntegration(float dt)
        {
            _gravityDt = dt * Gravity;
        }

        public void IntegrateVelocity(
            Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            var offset = position - Vector3Wide.Broadcast(PlanetCenter);
            var distance = offset.Length();
            velocity.Linear -= new Vector<float>(_gravityDt) * offset / Vector.Max(Vector<float>.One, distance * distance * distance);
        }
    }

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "planet",
        "planet",
        BufferCapacity,
        static (config, transport) => new PlanetDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly TypedIndex _sphereShape;
    private readonly BodyInertia _sphereInertia;
    private readonly Dictionary<int, Entity> _entityByRenderId = new();
    private readonly List<int> _ballRenderIds = new();

    private long _seq;

    public PlanetDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30, 1)),
            new PlanetaryGravityCallbacks { PlanetCenter = Vector3.Zero, Gravity = GravityStrength },
            new SolveDescription(4, 1));

        _simulation.Statics.Add(new StaticDescription(
            Vector3.Zero, _simulation.Shapes.Add(new Sphere(PlanetRadius))));

        var orbiter = new Sphere(BallRadius);
        _sphereShape = _simulation.Shapes.Add(orbiter);
        _sphereInertia = orbiter.ComputeInertia(1f);

        BuildSheetLocked();
    }

    internal int BallEntityCount => _ballRenderIds.Count;

    /// <summary>Test probe: the Bepu body handle owned by an orbiter render id.</summary>
    internal bool TryGetPhysicsBody(int renderId, out PhysicsBody body)
    {
        lock (_sync)
        {
            body = default;
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) return false;
            if (!_world.IsAlive(entity) || !_world.Has<PhysicsBody>(entity)) return false;
            body = _world.Get<PhysicsBody>(entity);
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

    public void Reset()
    {
        lock (_sync)
        {
            foreach (var (_, entity) in _entityByRenderId)
            {
                if (!_world.IsAlive(entity)) continue;
                if (_world.Has<PhysicsBody>(entity))
                    _simulation.Bodies.Remove(_world.Get<PhysicsBody>(entity).Handle);
                _world.Destroy(entity);
            }

            _entityByRenderId.Clear();
            _ballRenderIds.Clear();
            _seq = 0;
            BuildSheetLocked();
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

            SyncPhysicsPosesLocked();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildSheetLocked()
    {
        var origin = new Vector3(-50f, 95f, 0f)
                     + new Vector3(SheetSpacing) * new Vector3(SheetLength * -0.5f, 0f, SheetWidth * -0.5f);

        for (var i = 0; i < SheetLength; i++)
        {
            for (var j = 0; j < SheetHeight; j++)
            {
                for (var k = 0; k < SheetWidth; k++)
                {
                    var position = origin + new Vector3(i, j, k) * SheetSpacing;
                    var handle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                        new RigidPose(position),
                        new BodyVelocity(new Vector3(LaunchSpeed, 0f, 0f)),
                        _sphereInertia, _sphereShape, 0.01f));

                    var renderId = BallRenderIdBase + _ballRenderIds.Count;
                    _ballRenderIds.Add(renderId);
                    _entityByRenderId[renderId] = _world.Create(
                        new Position3(position.X, position.Y, position.Z),
                        Rotation3.Identity,
                        Scale3.One,
                        new RenderId(renderId),
                        new RenderLifecycle3 { State = EntityLifecycle3.Spawned },
                        new PhysicsBody(handle));
                }
            }
        }
    }

    private void SyncPhysicsPosesLocked()
    {
        foreach (var (_, entity) in _entityByRenderId)
        {
            if (!_world.IsAlive(entity) || !_world.Has<PhysicsBody>(entity)) continue;

            var body = _simulation.Bodies[_world.Get<PhysicsBody>(entity).Handle];
            var position = body.Pose.Position;
            var orientation = body.Pose.Orientation;

            ref var pos = ref _world.Get<Position3>(entity);
            pos.X = position.X;
            pos.Y = position.Y;
            pos.Z = position.Z;

            ref var rot = ref _world.Get<Rotation3>(entity);
            rot.Qx = orientation.X;
            rot.Qy = orientation.Y;
            rot.Qz = orientation.Z;
            rot.Qw = orientation.W;
        }
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        _seq++;
        var states = new List<Transform3DState>(1 + _ballRenderIds.Count);

        states.Add(new Transform3DState(
            PlanetRenderId, 0d, 0d, 0d, 0d, 0d, 0d, 1d,
            PlanetRadius * 2f, PlanetRadius * 2f, PlanetRadius * 2f, EntityLifecycle3.Active));

        var diameter = BallRadius * 2f;
        foreach (var renderId in _ballRenderIds)
        {
            if (!_entityByRenderId.TryGetValue(renderId, out var entity)) continue;
            if (!_world.IsAlive(entity)) continue;

            var position = _world.Get<Position3>(entity);
            var rotation = _world.Get<Rotation3>(entity);
            states.Add(new Transform3DState(
                renderId,
                position.X, position.Y, position.Z,
                rotation.Qx, rotation.Qy, rotation.Qz, rotation.Qw,
                diameter, diameter, diameter,
                EntityLifecycle3.Active));
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
