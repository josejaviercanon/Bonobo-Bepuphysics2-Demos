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
using Game.BepuDemos.Demos.Characters;

namespace Game.BepuDemos.Demos;

/// <summary>Kinematic hopping newt used by <see cref="SponsorDemo"/> (upstream <c>SponsorNewt</c>).</summary>
internal struct SponsorNewt
{
    public BodyHandle BodyHandle;

    private double _nextAllowedJump;
    private Vector2 _jumpStart, _jumpEnd;
    private Vector2 _forwardAtJumpStart, _forwardAtJumpEnd;
    private double _jumpStartTime, _jumpEndTime;

    public SponsorNewt(Simulation simulation, TypedIndex shape, float height, in Vector2 arenaMin, in Vector2 arenaMax, Random random)
    {
        var arenaSpan = arenaMax - arenaMin;
        var position = arenaMin + arenaSpan * new Vector2(random.NextSingle(), random.NextSingle());
        var angle = MathF.PI * 2 * random.NextSingle();
        BodyHandle = simulation.Bodies.Add(BodyDescription.CreateKinematic(
            new RigidPose(new Vector3(position.X, height, position.Y), QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, angle)), shape, -1));
        _nextAllowedJump = 0;
        _jumpStart = default;
        _jumpEnd = default;
        _forwardAtJumpStart = default;
        _forwardAtJumpEnd = default;
        _jumpStartTime = 0;
        _jumpEndTime = 0;
    }

    /// <summary>Hops between predetermined points unstoppably, waiting a moment between jumps.</summary>
    public void Update(Simulation simulation, double time, float height, in Vector2 arenaMin, in Vector2 arenaMax, Random random, float inverseDt)
    {
        const float jumpDuration = 1;
        var body = simulation.Bodies[BodyHandle];
        if (time >= _nextAllowedJump)
        {
            // Choose a jump location within the arena, generally somewhere ahead of the newt.
            QuaternionEx.TransformUnitZ(body.Pose.Orientation, out var backward);
            _jumpStart = new Vector2(body.Pose.Position.X, body.Pose.Position.Z);
            _jumpEnd = _jumpStart + new Vector2(backward.X + random.NextSingle() * 1.4f - 0.7f, backward.Z + random.NextSingle() * 1.4f - 0.7f) * -20;
            _jumpEnd -= _jumpStart * 0.05f;
            _jumpEnd = Vector2.Max(arenaMin, Vector2.Min(arenaMax, _jumpEnd));
            _jumpStartTime = time;
            _jumpEndTime = time + jumpDuration;
            _forwardAtJumpStart = -new Vector2(backward.X, backward.Z);
            _forwardAtJumpEnd = _jumpEnd - _jumpStart;
            var newForwardLengthSquared = _forwardAtJumpEnd.LengthSquared();
            _forwardAtJumpEnd = newForwardLengthSquared < 1e-10f ? _forwardAtJumpStart : _forwardAtJumpEnd / MathF.Sqrt(newForwardLengthSquared);
            _nextAllowedJump = _jumpEndTime + (1 + random.NextDouble() * 2.5f);
        }

        Vector3 targetPosition;
        Vector2 targetForward;
        if (time >= _jumpStartTime && time <= _jumpEndTime)
        {
            // In the middle of a jump: interpolate position and orientation.
            const float maximumJumpHeight = 5;
            var jumpProgress = (float)(time - _jumpStartTime) / jumpDuration;
            var targetPosition2D = jumpProgress * (_jumpEnd - _jumpStart) + _jumpStart;
            var parabolaTerm = 2 * jumpProgress - 1;
            var currentHeight = height + (1 - parabolaTerm * parabolaTerm) * maximumJumpHeight;
            targetPosition = new Vector3(targetPosition2D.X, currentHeight, targetPosition2D.Y);
            targetForward = jumpProgress * (_forwardAtJumpEnd - _forwardAtJumpStart) + _forwardAtJumpStart;
            var targetForwardLengthSquared = targetForward.LengthSquared();
            if (targetForwardLengthSquared < 1e-10f)
            {
                QuaternionEx.TransformUnitZ(body.Pose.Orientation, out var backward);
                targetForward = -new Vector2(backward.X, backward.Z);
            }
            else
            {
                targetForward /= MathF.Sqrt(targetForwardLengthSquared);
            }
        }
        else
        {
            // The target pose is just wherever the previous jump ended.
            targetPosition = new Vector3(_jumpEnd.X, height, _jumpEnd.Y);
            targetForward = _forwardAtJumpEnd;
        }

        // Kinematic body: compute the pose error and the velocity to correct it within one frame.
        body.Velocity.Linear = (targetPosition - body.Pose.Position) * inverseDt;
        Matrix3x3 targetOrientationBasis;
        targetOrientationBasis.X = new Vector3(-targetForward.Y, 0, targetForward.X);
        targetOrientationBasis.Y = Vector3.UnitY;
        targetOrientationBasis.Z = -new Vector3(targetForward.X, 0, targetForward.Y);
        QuaternionEx.CreateFromRotationMatrix(targetOrientationBasis, out var targetOrientation);
        QuaternionEx.GetRelativeRotationWithoutOverlap(body.Pose.Orientation, targetOrientation, out var orientationError);
        QuaternionEx.GetAxisAngleFromQuaternion(orientationError, out var errorAxis, out var errorAngle);
        body.Velocity.Angular = errorAxis * (errorAngle * inverseDt);
    }
}

/// <summary>Fleeing character AI used by <see cref="SponsorDemo"/> (upstream <c>SponsorCharacterAI</c>).</summary>
internal struct SponsorCharacterAI
{
    private BodyHandle _bodyHandle;
    private Vector2 _targetLocation;

    public BodyHandle BodyHandle => _bodyHandle;

    public SponsorCharacterAI(CharacterControllers characters, in CollidableDescription characterCollidable, Vector3 initialPosition, in Vector2 targetLocation)
    {
        _bodyHandle = characters.Simulation.Bodies.Add(BodyDescription.CreateDynamic(initialPosition, new BodyInertia { InverseMass = 1f }, characterCollidable, -1f));

        ref var character = ref characters.AllocateCharacter(_bodyHandle);
        character.LocalUp = new Vector3(0, 1, 0);
        character.CosMaximumSlope = MathF.Cos(MathF.PI * 0.48f);
        character.JumpVelocity = 4;
        character.MaximumVerticalForce = 10f;
        character.MaximumHorizontalForce = 5f;
        character.MinimumSupportDepth = -0.01f;
        character.MinimumSupportContinuationDepth = -0.1f;
        character.ViewDirection = new Vector3(0, 0, -1);
        _targetLocation = targetLocation;
    }

    public void Update(CharacterControllers characters, Simulation simulation, SponsorNewt[] newts, Random random)
    {
        var body = simulation.Bodies[_bodyHandle];
        Vector2 influenceSum = default;
        var spooked = false;
        for (var i = 0; i < newts.Length; ++i)
        {
            ref var newtPosition = ref simulation.Bodies[newts[i].BodyHandle].Pose.Position;
            var offset = newtPosition - body.Pose.Position;
            var distance = offset.Length();
            if (distance > 1e-10f)
            {
                var influenceMagnitude = 1f / (distance * 0.1f + 0.1f);
                influenceSum -= new Vector2(offset.X, offset.Z) * influenceMagnitude / distance;
            }

            if (distance < 20) spooked = true;
        }

        // Target-position influence is kept consistent regardless of newt count.
        if (newts.Length > 0) influenceSum /= newts.Length;
        ref var character = ref characters.GetCharacterByBodyHandle(_bodyHandle);
        influenceSum -= (new Vector2(body.Pose.Position.X, body.Pose.Position.Z) - _targetLocation) * 0.001f;
        var influenceSumLength = influenceSum.Length();
        var targetWorldVelocity = influenceSumLength > 1e-6f ? influenceSum * (6f / influenceSumLength) : new Vector2();
        // Rephrase the target velocity in terms of the character's control basis.
        character.TargetVelocity = new Vector2(targetWorldVelocity.X, -targetWorldVelocity.Y);
        if (spooked && random.NextDouble() < 0.015f)
        {
            // Oh no oh no oh no he gonna get me.
            character.TryJump = true;
        }
    }
}

/// <summary>
///     Port of the upstream <c>Sponsors/SponsorDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby):
///     a walled arena of hopping kinematic sponsor newts chasing 150 AI characters (dynamic
///     character controllers) through box huts, with a giant static overlord newt watching.
///     The 27 sponsor PNGs are drawn client side as billboards (the upstream screen-space text
///     tiers and mouseover reward images need a text-overlay channel this ABI does not have).
///
///     Test-bed deviations (documented in docs/compat-review.md): AI character count reduced
///     from 1000 to <see cref="AiCharacterCount"/> and huts from 30 to <see cref="HutCount"/>;
///     the newt/hut meshes come from the embedded OBJ + local ring helper; sponsor name text is
///     dropped in favor of billboards.
///
///     Render-id ranges: 0 = floor, 1..4 = arena walls, 5 = overlord newt, 10000+ = hopping
///     newts, 20000+ = AI characters (capsules), 30000+ = hut boxes.
/// </summary>
public sealed class SponsorDemo : IDemoSimulation
{
    public const int FloorRenderId = 0;
    public const int WallRenderIdBase = 1;
    public const int OverlordRenderId = 5;
    public const int NewtRenderIdBase = 10_000;
    public const int CharacterRenderIdBase = 20_000;
    public const int HutRenderIdBase = 30_000;

    public const int NewtCount = 8;
    public const int AiCharacterCount = 150;
    public const int HutCount = 8;
    public const int MaxHutBodyCount = 5_376;

    public const int MaxTransformCount = 6 + NewtCount + AiCharacterCount + MaxHutBodyCount;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public const float FloorSize = 240f;
    public const float WallThickness = 200f;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "sponsor",
        "sponsor",
        BufferCapacity,
        static (config, transport) => new SponsorDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly CharacterControllers _characterControllers = new();
    private readonly SponsorNewt[] _newts = new SponsorNewt[NewtCount];
    private readonly SponsorCharacterAI[] _characterAIs = new SponsorCharacterAI[AiCharacterCount];
    private readonly Random _random = new(6);

    private readonly Vector2 _newtArenaMin = new(-100);
    private readonly Vector2 _newtArenaMax = new(100);
    private readonly StaticHandle _overlordHandle;

    private double _simulationTime;
    private long _seq;

    public SponsorDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new CharacterNarrowphaseCallbacks(_characterControllers),
            new DemoPoseIntegratorCallbacks(new Vector3(0, -10, 0)),
            new SolveDescription(8, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        // Newts (upstream mirrors the mesh with a negative X/Z scale).
        var newtMesh = ObjMeshParser.CreateMesh(ObjMeshParser.Parse(DemoContent.NewtObjText), new Vector3(-10, 10, -10), _bufferPool);
        var newtShape = _simulation.Shapes.Add(newtMesh);
        for (var i = 0; i < NewtCount; ++i)
        {
            _newts[i] = new SponsorNewt(_simulation, newtShape, 0, _newtArenaMin, _newtArenaMax, _random);
            _poses.AddDynamic(NewtRenderIdBase + i, _newts[i].BodyHandle, Vector3.One);
        }

        var floorPosition = new Vector3(0, -10f, 0);
        _simulation.Statics.Add(new StaticDescription(floorPosition, _simulation.Shapes.Add(new Box(FloorSize, 20, FloorSize))));
        _poses.AddStatic(FloorRenderId, floorPosition, Quaternion.Identity, new Vector3(FloorSize, 20f, FloorSize) * 2f);

        var walls = new (Vector3 Position, Vector3 Scale)[4];
        walls[0] = (new Vector3(FloorSize * -0.5f - WallThickness * 0.5f, -5, 0), new Vector3(WallThickness, 30, FloorSize + WallThickness * 2));
        walls[1] = (new Vector3(FloorSize * 0.5f + WallThickness * 0.5f, -5, 0), new Vector3(WallThickness, 30, FloorSize + WallThickness * 2));
        walls[2] = (new Vector3(0, -5, FloorSize * -0.5f - WallThickness * 0.5f), new Vector3(FloorSize, 30, WallThickness));
        walls[3] = (new Vector3(0, -5, FloorSize * 0.5f + WallThickness * 0.5f), new Vector3(FloorSize, 30, WallThickness));
        for (var i = 0; i < walls.Length; ++i)
        {
            _simulation.Statics.Add(new StaticDescription(walls[i].Position, _simulation.Shapes.Add(new Box(walls[i].Scale.X * 0.5f, walls[i].Scale.Y * 0.5f, walls[i].Scale.Z * 0.5f))));
            _poses.AddStatic(WallRenderIdBase + i, walls[i].Position, Quaternion.Identity, walls[i].Scale);
        }

        // AI characters flee the newts through the hut field.
        var characterCollidable = new CollidableDescription(_simulation.Shapes.Add(new Capsule(0.5f, 1f)), 0.1f);
        for (var i = 0; i < AiCharacterCount; ++i)
        {
            var position2D = _newtArenaMin + (_newtArenaMax - _newtArenaMin) * new Vector2(_random.NextSingle(), _random.NextSingle());
            var targetPosition = 0.5f * (_newtArenaMin + (_newtArenaMax - _newtArenaMin) * new Vector2(_random.NextSingle(), _random.NextSingle()));
            _characterAIs[i] = new SponsorCharacterAI(_characterControllers, characterCollidable, new Vector3(position2D.X, 5, position2D.Y), targetPosition);
            _poses.AddDynamic(CharacterRenderIdBase + i, _characterAIs[i].BodyHandle, Vector3.One);
        }

        // Hut rings (reduced count; the upstream calls the public ColosseumDemo.CreateRing helper).
        var hutBoxShape = new Box(0.4f, 2, 3);
        var obstacleDescription = BodyDescription.CreateDynamic(new Vector3(), hutBoxShape.ComputeInertia(20), _simulation.Shapes.Add(hutBoxShape), 1e-2f);
        var hutScale = new Vector3(hutBoxShape.HalfWidth, hutBoxShape.HalfHeight, hutBoxShape.HalfLength) * 2f;
        for (var i = 0; i < HutCount; ++i)
        {
            var position2D = _newtArenaMin + (_newtArenaMax - _newtArenaMin) * new Vector2(_random.NextSingle(), _random.NextSingle());
            var handles = CreateRing(_simulation, new Vector3(position2D.X, 0, position2D.Y), hutBoxShape, obstacleDescription, 5, 2, _random.Next(1, 5));
            for (var j = 0; j < handles.Count; ++j)
            {
                _poses.AddDynamic(HutRenderIdBase + j, handles[j], hutScale);
            }
        }

        // Overlord newt watching over the arena.
        var overlordMesh = newtMesh;
        overlordMesh.Scale = new Vector3(60, 60, 60);
        var overlordPosition = new Vector3(0, 10, -FloorSize * 0.5f - 70);
        _overlordHandle = _simulation.Statics.Add(new StaticDescription(overlordPosition, _simulation.Shapes.Add(overlordMesh)));
        _poses.AddStatic(OverlordRenderId, overlordPosition, Quaternion.Identity, new Vector3(60, 60, 60));
    }

    /// <summary>Test probe: number of dynamic hut boxes emitted with the signal.</summary>
    internal int HutBodyCount { get; private set; }

    /// <summary>Lifetime handle of the overlord newt static (kept for parity with the upstream fixture).</summary>
    internal StaticHandle OverlordHandle => _overlordHandle;

    private static List<BodyHandle> CreateRing(Simulation simulation, Vector3 position, Box ringBoxShape, BodyDescription bodyDescription, float radius, int heightPerPlatformLevel, int platformLevels)
    {
        var handles = new List<BodyHandle>(256);
        for (var platformIndex = 0; platformIndex < platformLevels; ++platformIndex)
        {
            var wallOffset = ringBoxShape.HalfLength - ringBoxShape.HalfWidth;
            CreateRingWall(simulation, position, ringBoxShape, bodyDescription, heightPerPlatformLevel, radius + wallOffset, handles);
            CreateRingWall(simulation, position, ringBoxShape, bodyDescription, heightPerPlatformLevel, radius - wallOffset, handles);
            CreateRingPlatform(simulation, position + new Vector3(0, heightPerPlatformLevel * ringBoxShape.Height, 0), ringBoxShape, bodyDescription, radius, handles);
            position.Y += heightPerPlatformLevel * ringBoxShape.Height + ringBoxShape.Width;
        }

        return handles;
    }

    private static void CreateRingWall(Simulation simulation, Vector3 position, Box ringBoxShape, BodyDescription bodyDescription, int height, float radius, List<BodyHandle> handles)
    {
        var circumference = MathF.PI * 2 * radius;
        var boxCountPerRing = (int)(0.9f * circumference / ringBoxShape.Length);
        var increment = MathHelper.TwoPi / boxCountPerRing;
        for (var ringIndex = 0; ringIndex < height; ringIndex++)
        {
            for (var i = 0; i < boxCountPerRing; i++)
            {
                var angle = ((ringIndex & 1) == 0 ? i + 0.5f : i) * increment;
                bodyDescription.Pose = (position + new Vector3(-MathF.Cos(angle) * radius, (ringIndex + 0.5f) * ringBoxShape.Height, MathF.Sin(angle) * radius),
                    QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, angle));
                handles.Add(simulation.Bodies.Add(bodyDescription));
            }
        }
    }

    private static void CreateRingPlatform(Simulation simulation, Vector3 position, Box ringBoxShape, BodyDescription bodyDescription, float radius, List<BodyHandle> handles)
    {
        var innerCircumference = MathF.PI * 2 * (radius - ringBoxShape.HalfLength);
        var boxCount = (int)(0.95f * innerCircumference / ringBoxShape.Height);
        var increment = MathHelper.TwoPi / boxCount;
        for (var i = 0; i < boxCount; i++)
        {
            var angle = i * increment;
            bodyDescription.Pose = (position + new Vector3(-MathF.Cos(angle) * radius, ringBoxShape.HalfWidth, MathF.Sin(angle) * radius),
                QuaternionEx.Concatenate(QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * 0.5f), QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, angle + MathF.PI * 0.5f)));
            handles.Add(simulation.Bodies.Add(bodyDescription));
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

            for (var i = 0; i < _newts.Length; ++i)
            {
                _newts[i].Update(_simulation, _simulationTime, 0, _newtArenaMin, _newtArenaMax, _random, 1f / (float)deltaSeconds);
            }

            for (var i = 0; i < _characterAIs.Length; ++i)
            {
                _characterAIs[i].Update(_characterControllers, _simulation, _newts, _random);
            }

            _simulationTime += deltaSeconds;

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
        if (HutBodyCount == 0) HutBodyCount = states.Count - (6 + NewtCount + AiCharacterCount);
        return new Transform3DRenderSignal(_seq, states.Count, tickMs, states);
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _bufferPool.Clear();
        World.Destroy(_world);
    }
}
