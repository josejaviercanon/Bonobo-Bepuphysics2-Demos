using System.Diagnostics;
using System.Numerics;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;
using Bonobo.BepuUtilities.Collections;
using Bonobo.BepuUtilities.Memory;
using Bonobo.ECS.Core;
using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Simulations;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Port of the upstream <c>SubsteppingDemo</c> (BepuPhysics2, Apache-2.0): substepping lets a
///     solver run multiple mini timesteps per <c>Simulation.Timestep</c>, stabilizing extreme mass
///     ratios and long constraint sequences at low cost. Fixture: a 10000:1 rope + wrecking ball,
///     a 20-box stack with a 10000-mass capstone, and four motorized hinge chains.
///
///     Test-bed deviation (documented in docs/compat-review.md): the upstream Z/X/C/V key solver
///     mutations are the payload-free verbs <c>substeps-more</c>/<c>substeps-less</c>/
///     <c>iters-more</c>/<c>iters-less</c> (GUI buttons in the scene).
///
///     Render-id ranges: 0 = static ground, 100+ = rope links, 200 = wrecking ball,
///     300+ = stack boxes, 400 = stack capstone, 500+ = chain links (9 per chain).
/// </summary>
public sealed class SubsteppingDemo : IDemoSimulation, IDemoCommands
{
    public const double TickIntervalSeconds = 1.0 / 60.0;

    public const int GroundRenderId = 0;
    public const int RopeRenderIdBase = 100;
    public const int RopeLinkCount = 13;
    public const int WreckingBallRenderId = 200;
    public const int StackRenderIdBase = 300;
    public const int StackBoxCount = 20;
    public const int CapstoneRenderId = 400;
    public const int ChainRenderIdBase = 500;
    public const int ChainCount = 4;
    public const int LinksPerChain = 9;
    public const int MaxRecords =
        1 + RopeLinkCount + 1 + StackBoxCount + 1 + ChainCount * LinksPerChain;

    public const int BufferCapacity = SignalBuffer.HeaderLength + MaxRecords * SignalBufferLayout.Transform3DStride;

    public const float RopeBodyRadius = 0.5f;
    public const float WreckingBallRadius = 5f;
    public const float StackBoxHeight = 0.5f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "substepping",
        "substepping",
        BufferCapacity,
        static (config, transport) => new SubsteppingDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private long _seq;

    public SubsteppingDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new DemoNarrowPhaseCallbacks(new SpringSettings(640f, 480f), float.MaxValue, 1f),
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(2, 48));

        _poses = new DemoPoseSet(_world, _simulation);
        BuildSceneLocked();
    }

    internal int SubstepCount => _simulation.Solver.SubstepCount;
    internal int VelocityIterationCount => _simulation.Solver.VelocityIterationCount;
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
            case "substeps-more":
                _simulation.Solver.SubstepCount = Math.Min(512, _simulation.Solver.SubstepCount + SubstepChange());
                AwakenAllBodies();
                return true;
            case "substeps-less":
                _simulation.Solver.SubstepCount = Math.Max(1, _simulation.Solver.SubstepCount - SubstepChange());
                AwakenAllBodies();
                return true;
            case "iters-more":
                _simulation.Solver.VelocityIterationCount = Math.Min(512, _simulation.Solver.VelocityIterationCount + IterationChange());
                AwakenAllBodies();
                return true;
            case "iters-less":
                _simulation.Solver.VelocityIterationCount = Math.Max(1, _simulation.Solver.VelocityIterationCount - IterationChange());
                AwakenAllBodies();
                return true;
            case "reset":
                Reset();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Rebuilds the fixture deterministically (bodies, constraints and ECS entities).</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _poses.RemoveAllBodies();
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

    private int SubstepChange() => (int)MathF.Max(1f, _simulation.Solver.SubstepCount * 0.25f);

    private int IterationChange() => (int)MathF.Max(1f, _simulation.Solver.VelocityIterationCount * 0.25f);

    /// <summary>
    ///     Any simulation configuration change can change behavior; sleeping bodies would hide the
    ///     effect, so wake every allocated set (upstream <c>AwakenAllBodies</c>).
    /// </summary>
    private unsafe void AwakenAllBodies()
    {
        var sleepingSetsMemory = stackalloc int[_simulation.Bodies.Sets.Length - 1];
        var sleepingSets = new QuickList<int>(new Buffer<int>(sleepingSetsMemory, _simulation.Bodies.Sets.Length - 1));
        for (var i = 1; i < _simulation.Bodies.Sets.Length; ++i)
        {
            if (_simulation.Bodies.Sets[i].Allocated) sleepingSets.AllocateUnsafely() = i;
        }

        _simulation.Awakener.AwakenSets(ref sleepingSets);
    }

    private void BuildSceneLocked()
    {
        // 10000:1 mass ratio rope, no constraint offsets (a 0-level arm rope).
        {
            var startLocation = new Vector3(15f, 40f, 0f);
            const float bodySpacing = 0.3f;
            var springSettings = new SpringSettings(480f, 480f);
            var bodyHandles = RopeHelpers.BuildRope(
                _simulation, startLocation, 12, RopeBodyRadius, bodySpacing, 0, 1, 1, springSettings);

            var bigWreckingBall = new Sphere(WreckingBallRadius);
            const float mass = 10000f;
            var bigWreckingBallInertia = bigWreckingBall.ComputeInertia(mass);
            var wreckingBallHandle = RopeHelpers.AttachWreckingBall(
                _simulation, bodyHandles, RopeBodyRadius, bodySpacing, 0,
                bigWreckingBall.Radius, bigWreckingBallInertia,
                _simulation.Shapes.Add(bigWreckingBall), springSettings);

            for (var i = 0; i < bodyHandles.Length; i++)
                _poses.AddDynamic(RopeRenderIdBase + i, bodyHandles[i], new Vector3(RopeBodyRadius * 2f));
            _poses.AddDynamic(WreckingBallRenderId, wreckingBallHandle, new Vector3(WreckingBallRadius * 2f));
        }

        // Stack with a heavy block on top: the 120 Hz contact stiffness needs the substeps.
        {
            var boxShape = new Box(4f, StackBoxHeight, 6f);
            var boxInertia = boxShape.ComputeInertia(1f);
            var boxDescription = BodyDescription.CreateDynamic(RigidPose.Identity, boxInertia, _simulation.Shapes.Add(boxShape), 0.01f);
            for (var i = 0; i < StackBoxCount; ++i)
            {
                boxDescription.Pose = new RigidPose(
                    new Vector3(0f, 0.5f + StackBoxHeight * (i + 0.5f), 0f),
                    QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.05f * i));
                var handle = _simulation.Bodies.Add(boxDescription);
                _poses.AddDynamic(StackRenderIdBase + i, handle, new Vector3(4f, StackBoxHeight, 6f));
            }

            var topBlockShape = new Box(8f, 2f, 8f);
            const float mass = 10000f;
            var topHandle = _simulation.Bodies.Add(BodyDescription.CreateDynamic(
                boxDescription.Pose.Position + new Vector3(0f, boxShape.HalfHeight + 1f, 0f),
                topBlockShape.ComputeInertia(mass),
                _simulation.Shapes.Add(topBlockShape), 0.01f));
            _poses.AddDynamic(CapstoneRenderId, topHandle, new Vector3(8f, 2f, 8f));
        }

        // Four motorized hinge chains: long constraint sequences under high leverage.
        {
            var basePosition = new Vector3(-20f, 20f, 0f);
            var boxShape = new Box(0.5f, 0.5f, 3f);
            var boxShapeIndex = _simulation.Shapes.Add(boxShape);
            var boxInertia = boxShape.ComputeInertia(1f);
            var linkDescription = BodyDescription.CreateDynamic(RigidPose.Identity, boxInertia, boxShapeIndex, 0.01f);

            for (var chainIndex = 0; chainIndex < ChainCount; ++chainIndex)
            {
                linkDescription.Pose.Position = basePosition + new Vector3(0f, 0f, chainIndex * 15f);
                var previousLinkHandle = _simulation.Bodies.Add(
                    BodyDescription.CreateKinematic(linkDescription.Pose.Position, boxShapeIndex, 0.01f));
                _poses.AddDynamic(ChainRenderIdBase + chainIndex * LinksPerChain, previousLinkHandle, new Vector3(0.5f, 0.5f, 3f));

                for (var linkIndex = 0; linkIndex < LinksPerChain - 1; ++linkIndex)
                {
                    var offset = new Vector3(boxShape.Width * 1.05f, 0f, boxShape.Length - boxShape.Width);
                    linkDescription.Pose.Position += offset;
                    var linkHandle = _simulation.Bodies.Add(linkDescription);
                    _simulation.Solver.Add(previousLinkHandle, linkHandle, new Hinge
                    {
                        LocalHingeAxisA = Vector3.UnitX,
                        LocalHingeAxisB = Vector3.UnitX,
                        LocalOffsetA = offset * 0.5f,
                        LocalOffsetB = offset * -0.5f,
                        SpringSettings = new SpringSettings(640f, 1f),
                    });
                    _simulation.Solver.Add(previousLinkHandle, linkHandle, new AngularAxisMotor
                    {
                        LocalAxisA = Vector3.UnitX,
                        TargetVelocity = .25f,
                        Settings = new MotorSettings(float.MaxValue, 0.00001f),
                    });
                    previousLinkHandle = linkHandle;
                    _poses.AddDynamic(ChainRenderIdBase + chainIndex * LinksPerChain + linkIndex + 1, linkHandle, new Vector3(0.5f, 0.5f, 3f));
                }
            }
        }

        _simulation.Statics.Add(new StaticDescription(
            new Vector3(0f, 0f, 0f), _simulation.Shapes.Add(new Box(200f, 1f, 200f))));
        _poses.AddStatic(GroundRenderId, Vector3.Zero, Quaternion.Identity, new Vector3(200f, 1f, 200f));
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
