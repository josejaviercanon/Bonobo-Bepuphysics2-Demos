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
///     Port of the upstream <c>CollisionTrackingDemo</c> (BepuPhysics2, Apache-2.0): narrow phase
///     manifold data is collected by <see cref="CollisionTracker"/> and analyzed after the
///     timestep — new touching feature ids spawn particles, matching the
///     <see cref="ContactEventsDemo"/> add behavior without the event control flow.
///
///     Test-bed deviation (documented in docs/compat-review.md): particles are a fixed
///     preallocated array rendered as sphere <c>Transform3DState</c> records (upstream rendered
///     them directly through the demo renderer).
///
///     Render-id ranges: 0 = floor, 1 = wall, 100 = box, 101 = capsule, 10_000+ = particles.
/// </summary>
public sealed class CollisionTrackingDemo : IDemoSimulation, IDemoCommands
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

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "collision-tracking",
        "collision-tracking",
        BufferCapacity,
        static (config, transport) => new CollisionTrackingDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly CollisionTracker _tracker;
    private readonly DemoPoseSet _poses;
    private readonly Particle[] _particles = new Particle[MaxParticles];
    private int _particleCount;
    private int _spawnedParticleCount;
    private BodyHandle _listenedBox;
    private BodyHandle _listenedCapsule;

    private long _seq;

    public CollisionTrackingDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _tracker = new CollisionTracker(_bufferPool);
        _simulation = Simulation.Create(
            _bufferPool,
            new CollisionTrackingCallbacks(_tracker),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
    }

    internal int ParticleCount => _particleCount;
    internal int SpawnedParticleCount => _spawnedParticleCount;
    internal int TrackedCount => _tracker.Tracked.Count;

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

    /// <summary>Payload-free verbs: `drop` teleports the tracked bodies back up for a fresh burst of contacts.</summary>
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
            stopwatch.Stop();

            AnalyzeContactsLocked();
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
        _tracker.Track(_simulation.Bodies[_listenedBox].CollidableReference);
        _poses.AddDynamic(BoxRenderId, _listenedBox, new Vector3(1f, 2f, 3f));

        _listenedCapsule = _simulation.Bodies.Add(BodyDescription.CreateConvexDynamic(
            new Vector3(0.5f, 10f, 0f), 1f, _simulation.Shapes, new Capsule(0.25f, 0.7f)));
        _tracker.Track(_simulation.Bodies[_listenedCapsule].CollidableReference);
        _poses.AddDynamic(CapsuleRenderId, _listenedCapsule, Vector3.One);

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, -0.5f, 0f), _simulation.Shapes.Add(new Box(30f, 1f, 30f))));
        _poses.AddStatic(FloorRenderId, new Vector3(0f, -0.5f, 0f), Quaternion.Identity, new Vector3(60f, 2f, 60f));

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, 3f, 15f), _simulation.Shapes.Add(new Box(30f, 5f, 1f))));
        _poses.AddStatic(WallRenderId, new Vector3(0f, 3f, 15f), Quaternion.Identity, new Vector3(30f, 10f, 2f));
    }

    /// <summary>
    ///     Post-step contact analysis (upstream Update): for every tracked pair, spawn a particle
    ///     for each touching contact whose feature id was not touching in the previous state.
    /// </summary>
    private void AnalyzeContactsLocked()
    {
        for (var trackedIndex = 0; trackedIndex < _tracker.Tracked.Count; ++trackedIndex)
        {
            ref var collisions = ref _tracker.Tracked.Values[trackedIndex];
            var self = _tracker.Tracked.Keys[trackedIndex];
            for (var pairIndex = 0; pairIndex < collisions.Pairs.Count; ++pairIndex)
            {
                ref var pair = ref collisions.Pairs.Values[pairIndex];
                var other = collisions.Pairs.Keys[pairIndex];
                if (collisions.PreviousPairs.GetTableIndices(ref other, out _, out var otherIndexInPrevious))
                {
                    ref var previous = ref collisions.PreviousPairs.Values[otherIndexInPrevious];
                    for (var i = 0; i < pair.Contacts.Count; ++i)
                    {
                        if (pair.Contacts.GetDepth(i) < 0) continue;
                        if (!PreviousContainsTouchingFeatureId(ref previous, pair.Contacts.GetFeatureId(i)))
                            AddParticle(pair.Contacts.GetOffset(i), pair.Contacts.GetNormal(i), pair.OtherIsAInPair ? other.Collidable : self.Collidable);
                    }
                }
                else
                {
                    for (var i = 0; i < pair.Contacts.Count; ++i)
                        AddParticle(pair.Contacts.GetOffset(i), pair.Contacts.GetNormal(i), pair.OtherIsAInPair ? other.Collidable : self.Collidable);
                }
            }
        }
    }

    private static bool PreviousContainsTouchingFeatureId(ref PairCollision pair, int featureId)
    {
        for (var i = 0; i < pair.Contacts.Count; ++i)
        {
            if (pair.Contacts.GetFeatureId(i) == featureId && pair.Contacts.GetDepth(i) >= 0) return true;
        }

        return false;
    }

    private void AddParticle(Vector3 contactOffset, Vector3 contactNormal, CollidableReference firstCollidableInPair)
    {
        if (_particleCount >= MaxParticles) return;

        _spawnedParticleCount++;
        ref var particle = ref _particles[_particleCount++];
        particle.Position = contactOffset + (firstCollidableInPair.Mobility == CollidableMobility.Static
            ? new StaticReference(firstCollidableInPair.StaticHandle, _simulation.Statics).Pose.Position
            : new BodyReference(firstCollidableInPair.BodyHandle, _simulation.Bodies).Pose.Position);
        particle.Age = 0f;
        particle.Normal = contactNormal;
    }

    private void AgeParticles(float dt)
    {
        for (var i = _particleCount - 1; i >= 0; --i)
        {
            ref var particle = ref _particles[i];
            particle.Age += dt;
            if (particle.Age > ParticleLifetime)
            {
                _particles[i] = _particles[--_particleCount];
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
        var states = new List<Transform3DState>(_poses.Count + _particleCount);
        _poses.Emit(states);

        for (var i = 0; i < _particleCount; i++)
        {
            var particle = _particles[i];
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
        _tracker.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
