using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>ContactEventsDemo</c> (BepuPhysics2, Apache-2.0): the full
///     <see cref="IContactEventHandler"/> event layer (<see cref="ContactEvents"/>) turns narrow
///     phase manifold callbacks into add/remove/touching/pair events. A box and a capsule drop
///     onto a floor and a wall; every newly added contact spawns a particle that ages out.
///
///     Test-bed deviation (documented in docs/compat-review.md): particles are a fixed
///     preallocated array (upstream used a growable <c>QuickList</c> with cross-thread
///     increments) and render as sphere <c>Transform3DState</c> records.
///
///     Render-id ranges: 0 = floor, 1 = wall, 100 = box, 101 = capsule, 10_000+ = particles.
/// </summary>
public sealed class ContactEventsDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int FloorRenderId = 0;
    public const int WallRenderId = 1;
    public const int BoxRenderId = 100;
    public const int CapsuleRenderId = 101;
    public const int ParticleRenderIdBase = 10_000;
    public const int MaxParticles = 256;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + (2 + 2 + MaxParticles) * SignalBufferLayout.Transform3DStride;

    private const float ParticleLifetime = 0.7325f;

    private struct Particle
    {
        public Vector3 Position;
        public float Age;
        public Vector3 Normal;
    }

    /// <summary>
    ///     Spawns one particle per added contact (upstream <c>EventHandler.OnContactAdded</c>).
    ///     Single-threaded test bed: plain array mutation, no interlocked counter.
    /// </summary>
    private sealed class ParticleEventHandler : IContactEventHandler
    {
        public readonly Particle[] Particles = new Particle[MaxParticles];
        public int Count;
        /// <summary>Total particles spawned since the demo started (never reset by aging).</summary>
        public int SpawnedCount;

        private readonly Simulation _simulation;

        public ParticleEventHandler(Simulation simulation) => _simulation = simulation;

        public void OnContactAdded<TManifold>(CollidableReference eventSource, CollidablePair pair, ref TManifold contactManifold,
            Vector3 contactOffset, Vector3 contactNormal, float depth, int featureId, int contactIndex, int workerIndex)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            if (Count >= MaxParticles) return;

            SpawnedCount++;
            ref var particle = ref Particles[Count++];
            // Contact data is calibrated according to the order of the pair, so using A's position is important.
            particle.Position = contactOffset + (pair.A.Mobility == CollidableMobility.Static
                ? new StaticReference(pair.A.StaticHandle, _simulation.Statics).Pose.Position
                : new BodyReference(pair.A.BodyHandle, _simulation.Bodies).Pose.Position);
            particle.Age = 0f;
            particle.Normal = contactNormal;
        }
    }

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "contact-events",
        "contact-events",
        BufferCapacity,
        static (config, transport) => new ContactEventsDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly ContactEvents _events;
    private readonly ParticleEventHandler _eventHandler;
    private readonly DemoPoseSet _poses;
    private BodyHandle _listenedBox;
    private BodyHandle _listenedCapsule;

    private long _seq;

    public ContactEventsDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _events = new ContactEvents();
        _simulation = Simulation.Create(
            _bufferPool,
            new ContactEventCallbacks(_events),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));
        _eventHandler = new ParticleEventHandler(_simulation);
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
    }

    internal int ParticleCount => _eventHandler.Count;
    internal int SpawnedParticleCount => _eventHandler.SpawnedCount;
    internal int ListenedBodyCount => 2;

    /// <summary>Test probe: the Bepu body handle owned by the box render id.</summary>
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

    /// <summary>Payload-free verbs: `drop` teleports the listened bodies back up for a fresh burst of contacts.</summary>
    public bool TryCommand(string verb)
    {
        switch (verb)
        {
            case "drop":
                lock (_sync)
                {
                    DropBodyLocked(_listenedBox, new Vector3(0f, 5f, 0f));
                    DropBodyLocked(_listenedCapsule, new Vector3(0.5f, 10f, 0f));
                }

                return true;
            default:
                return false;
        }
    }

    private void DropBodyLocked(BodyHandle handle, Vector3 position)
    {
        var body = new BodyReference(handle, _simulation.Bodies);
        body.Pose.Position = position;
        body.Velocity.Linear = Vector3.Zero;
        body.Velocity.Angular = Vector3.Zero;
        _simulation.Awakener.AwakenBody(handle);
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            _simulation.Timestep((float)deltaSeconds);
            // Newly observed collisions are pushed into main storage; dead ones are cleaned out.
            _events.Flush();
            stopwatch.Stop();

            AgeParticles((float)deltaSeconds);
            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void BuildSceneLocked()
    {
        _listenedBox = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(
            new Vector3(0f, 5f, 0f), 1f, _simulation.Shapes, new Box(1f, 2f, 3f)));
        _events.Register(_simulation.Bodies[_listenedBox].CollidableReference, _eventHandler);
        _poses.AddDynamic(BoxRenderId, _listenedBox, new Vector3(1f, 2f, 3f));

        _listenedCapsule = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(
            new Vector3(0.5f, 10f, 0f), 1f, _simulation.Shapes, new Capsule(0.25f, 0.7f)));
        _events.Register(_simulation.Bodies[_listenedCapsule].CollidableReference, _eventHandler);
        _poses.AddDynamic(CapsuleRenderId, _listenedCapsule, Vector3.One);

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f), _simulation.Shapes.Add(new Box(30f, 1f, 30f))));
        _poses.AddStatic(FloorRenderId, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, new Vector3(60f, 2f, 60f));

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, 3f, 15f), _simulation.Shapes.Add(new Box(30f, 5f, 1f))));
        _poses.AddStatic(WallRenderId, new Vector3(0f, 3f, 15f), Quaternion.Identity, new Vector3(30f, 10f, 2f));
    }

    private void AgeParticles(float dt)
    {
        for (var i = _eventHandler.Count - 1; i >= 0; --i)
        {
            ref var particle = ref _eventHandler.Particles[i];
            particle.Age += dt;
            if (particle.Age > ParticleLifetime)
            {
                _eventHandler.Particles[i] = _eventHandler.Particles[--_eventHandler.Count];
            }
            else
            {
                particle.Position += particle.Normal * (2f * dt);
            }
        }
    }

    private Transform3DRenderSignal BuildSignalLocked(double tickMs)
    {
        _seq++;
        var states = new List<Transform3DState>(_poses.Count + _eventHandler.Count);
        _poses.Emit(states);

        for (var i = 0; i < _eventHandler.Count; i++)
        {
            var particle = _eventHandler.Particles[i];
            var radius = particle.Age * (particle.Age * (0.135f - 2.7f * particle.Age) + 1.35f);
            var diameter = radius * 2f;
            states.Add(new Transform3DState(
                ParticleRenderIdBase + i,
                particle.Position.X, particle.Position.Y, particle.Position.Z,
                0d, 0d, 0d, 1d,
                diameter, diameter, diameter,
                EntityLifecycle3.Active));
        }

        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _events.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
