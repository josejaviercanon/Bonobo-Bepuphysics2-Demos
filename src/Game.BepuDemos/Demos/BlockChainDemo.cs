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
///     Port of the upstream <c>BlockChainDemo</c> (BepuPhysics2, Apache-2.0): 20 forks of 20
///     boxes, each chain held by a kinematic top block and connected with <see cref="BallSocket"/>
///     constraints, plus a "press Z for an ICO" coin fountain. The upstream Z key is the
///     payload-free <c>ico</c> verb (GUI button); each ICO replaces the previous coin batch so the
///     pinned signal buffer stays fixed-capacity.
///
///     Render-id ranges: 0 = static ground, 100+ = blocks (fork-major), 1000+ = coins.
/// </summary>
public sealed class BlockChainDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int BlockRenderIdBase = 100;
    public const int ForkCount = 20;
    public const int BlocksPerChain = 20;
    public const int CoinRenderIdBase = 1000;
    public const int CoinCount = 128;

    public const int MaxRecords = 1 + ForkCount * BlocksPerChain + CoinCount;
    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float BoxSize = 1f;
    public const float CoinRadius = 1.5f;
    public const float CoinHalfLength = 0.1f;
    public const float CoinMass = 1f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "block-chain",
        "block-chain",
        BufferCapacity,
        static (config, transport) => new BlockChainDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly TypedIndex _boxShape;
    private readonly BodyInertia _boxInertia;
    private readonly TypedIndex _coinShape;
    private readonly BodyInertia _coinInertia;
    private readonly int _coinSeed = 5;

    private Random _random = new(5);
    private BodyHandle[] _coinHandles = Array.Empty<BodyHandle>();
    private int _coinCount;
    private long _seq;

    public BlockChainDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(30f, 3f)),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f), angularDamping: 0.2f),
            new SolveDescription(8, 1));

        var box = new Box(BoxSize, BoxSize, BoxSize);
        _boxShape = _simulation.Shapes.Add(box);
        _boxInertia = box.ComputeInertia(1f);
        var coin = new Cylinder(CoinRadius, CoinHalfLength);
        _coinShape = _simulation.Shapes.Add(coin);
        _coinInertia = coin.ComputeInertia(CoinMass);
        _poses = new DemoPoseSet(_world, _simulation);

        BuildSceneLocked();
    }

    internal int BlockCount => ForkCount * BlocksPerChain;
    internal int CoinCountSpawned => _coinCount;
    internal int RecordCount => _poses.Count;

    /// <summary>Test probe: the Bepu body handle owned by a render id.</summary>
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
            case "ico":
                SpawnIco();
                return true;
            case "reset":
                Reset();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the chains deterministically (bodies, constraints and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
            _coinHandles = Array.Empty<BodyHandle>();
            _coinCount = 0;
            _random = new Random(_coinSeed);
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
            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    /// <summary>Spawns one 128-coin batch, replacing the previous batch (fixed-capacity signal).</summary>
    private void SpawnIco()
    {
        lock (_sync)
        {
            for (var i = 0; i < _coinCount; i++)
            {
                _poses.Remove(CoinRenderIdBase + i, removeBody: true);
            }

            _coinCount = 0;
            _coinHandles = new BodyHandle[CoinCount];

            var origin = new Vector3(-30f, 5f, -30f);
            for (var i = 0; i < CoinCount; ++i)
            {
                var direction = new Vector3(-1f) + 2f * new Vector3(_random.NextSingle(), _random.NextSingle(), _random.NextSingle());
                var length = direction.Length();
                direction = length > 1e-7f ? direction / length : new Vector3(0, 1, 0);

                var orientation = QuaternionEx.Normalize(new Quaternion(
                    0.01f + _random.NextSingle(), _random.NextSingle(), _random.NextSingle(), _random.NextSingle()));
                var description = BodyDescription.CreateDynamic(
                    new RigidPose(origin + direction * (10f * _random.NextSingle()), orientation),
                    direction * (5f + 30f * _random.NextSingle()),
                    _coinInertia, _coinShape, 0.03f);
                var handle = _simulation.Bodies.Add(description);
                _coinHandles[_coinCount] = handle;
                _poses.AddDynamic(CoinRenderIdBase + _coinCount, handle, new Vector3(CoinRadius * 2f, CoinHalfLength * 2f, CoinRadius * 2f));
                _coinCount++;
            }
        }
    }

    private void BuildSceneLocked()
    {
        for (var forkIndex = 0; forkIndex < ForkCount; ++forkIndex)
        {
            var blockHandles = new BodyHandle[BlocksPerChain];
            for (var blockIndex = 0; blockIndex < BlocksPerChain; ++blockIndex)
            {
                var description = BodyDescription.CreateDynamic(
                    new RigidPose(
                        new Vector3(0f, 5f + blockIndex * (BoxSize + 1f), (forkIndex - ForkCount * 0.5f) * (BoxSize + 4f)),
                        Quaternion.Identity),
                    // Make the uppermost block kinematic to hold up the rest of the chain.
                    blockIndex == BlocksPerChain - 1 ? new BodyInertia() : _boxInertia,
                    _boxShape, 0.01f);
                blockHandles[blockIndex] = _simulation.Bodies.Add(description);
                _poses.AddDynamic(
                    BlockRenderIdBase + forkIndex * BlocksPerChain + blockIndex,
                    blockHandles[blockIndex],
                    new Vector3(BoxSize, BoxSize, BoxSize));
            }

            for (var i = 1; i < BlocksPerChain; ++i)
            {
                _simulation.Solver.Add(blockHandles[i - 1], blockHandles[i], new BallSocket
                {
                    LocalOffsetA = new Vector3(0, 1f, 0),
                    LocalOffsetB = new Vector3(0, -1f, 0),
                    SpringSettings = new SpringSettings(30f, 5f),
                });
            }
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(1f, -0.5f, 1f), _simulation.Shapes.Add(new Box(200f, 1f, 200f))));
        _poses.AddStatic(GroundRenderId, new Vector3(1f, -0.5f, 1f), Quaternion.Identity, new Vector3(200f, 1f, 200f));
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
