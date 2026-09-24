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
///     Port of the upstream <c>Tanks/TankDemo</c> (BepuPhysics2, Apache-2.0, Ross Nordby): a
///     player tank plus AI tanks duelling with CCD projectiles on a flattened-center heightfield,
///     built from body/turret/barrel parts, two five-wheel treads with suspension servos and
///     twist-servo aiming.
///
///     Test-bed deviations (documented in docs/compat-review.md): AI tank count reduced from 100
///     to <see cref="AiTankCount"/> and the terrain from 257×257×3 to 129×129×6 (same world
///     extent, deformer duplicated client side); player input arrives through the pinned ring
///     (<see cref="ITankControlSink"/>, keyboard-driven turret aim rates instead of the mouse
///     camera ray); the C-key player/AI toggle is dropped; explosion visuals are dropped (impact
///     effects need a record type this ABI does not have) while the exploding tank still falls
///     apart physically; projectile render records use the Spawned/removed lifecycle path.
///
///     Render-id ranges: 1000 + tankIndex·16 + child = tank part records (tankIndex 0 = player;
///     child 0 body, 1 turret, 2 barrel, 3..12 wheels); 100000+ = landmark buildings;
///     200000+ = live projectiles.
/// </summary>
public sealed class TankDemo : IDemoSimulation, ITankControlSink
{
    public const int TankRenderIdBase = 1_000;
    public const int TankRenderIdStride = 16;
    public const int BodyChildOffset = 0;
    public const int TurretChildOffset = 1;
    public const int BarrelChildOffset = 2;
    public const int WheelChildOffsetBase = 3;
    public const int PlayerTankIndex = 0;
    public const int AiTankCount = 32;
    public const int BuildingRenderIdBase = 100_000;
    public const int ProjectileRenderIdBase = 200_000;
    public const int BuildingCount = 25;
    public const int MaxProjectiles = 256;

    public const int PlaneWidth = 129;
    public const float TerrainScale = 6f;

    public const int MaxTransformCount =
        (1 + AiTankCount) * TankRenderIdStride + BuildingCount + MaxProjectiles;

    public const int BufferCapacity =
        SignalBuffer.HeaderLength + MaxTransformCount * SignalBufferLayout.Transform3DStride;

    public static DemoModule<Transform3DRenderSignal> CreateModule() => new(
        "tank",
        "tank",
        BufferCapacity,
        static (config, transport) => new TankDemo(config, transport),
        SignalBufferEncoders.ElementLength,
        SignalBufferEncoders.Encode);

    private readonly World _world;
    private readonly Simulation _simulation;
    private readonly BufferPool _bufferPool = new();
    private readonly IRenderTransport<Transform3DRenderSignal> _renderTransport;
    private readonly object _sync = new();

    private readonly DemoPoseSet _poses;
    private readonly CollidableProperty<TankDemoBodyProperties> _bodyProperties = new();
    private readonly ProjectileImpactTracker _impactTracker = new();

    private readonly TankController _playerController;
    private readonly AITank[] _aiTanks = new AITank[AiTankCount];
    private readonly Dictionary<int, int> _projectileRenderIdByBody = new(MaxProjectiles);

    private readonly Vector2 _playAreaMin;
    private readonly Vector2 _playAreaMax;
    private readonly Random _random = new(5);

    private double _inputMove;
    private double _inputTurn;
    private double _inputAimHorizontal;
    private double _inputAimVertical;
    private bool _inputFire;
    private bool _inputZoom;
    private bool _inputBrake;

    private float _aimYaw;
    private float _aimPitch;
    private int _aiTankCount = AiTankCount;
    private int _projectileCount;
    private int _nextProjectileRenderId;
    private long _frameIndex;
    private long _lastPlayerShotFrame = -1000;
    private long _seq;

    public TankDemo(
        DemoWorldConfig? config = null,
        IRenderTransport<Transform3DRenderSignal>? renderTransport = null)
    {
        _renderTransport = renderTransport ?? new CapturingRenderTransport<Transform3DRenderSignal>();

        _world = World.Create();
        _simulation = Simulation.Create(
            _bufferPool,
            new TankCallbacks { Properties = _bodyProperties, Tracker = _impactTracker },
            new DemoPoseIntegratorCallbacks(new Vector3(0f, -10f, 0f)),
            new SolveDescription(6, 1));
        _poses = new DemoPoseSet(_world, _simulation);

        var wheelShape = new Cylinder(0.4f, 0.18f);
        var wheelInertia = wheelShape.ComputeInertia(0.25f);
        var wheelShapeIndex = _simulation.Shapes.Add(wheelShape);

        var projectileShape = new Sphere(0.1f);
        var projectileInertia = projectileShape.ComputeInertia(0.2f);
        var tankDescription = new TankDescription
        {
            Body = TankPartDescription.Create(10, new Box(4f, 1, 5), RigidPose.Identity, 0.5f, _simulation.Shapes),
            Turret = TankPartDescription.Create(1, new Box(1.5f, 0.7f, 2f), new RigidPose(new Vector3(0, 0.85f, 0.4f)), 0.5f, _simulation.Shapes),
            Barrel = TankPartDescription.Create(0.5f, new Box(0.2f, 0.2f, 3f), new RigidPose(new Vector3(0, 0.85f, 0.4f - 1f - 1.5f)), 0.5f, _simulation.Shapes),
            TurretAnchor = new Vector3(0f, 0.5f, 0.4f),
            BarrelAnchor = new Vector3(0, 0.5f + 0.35f, 0.4f - 1f),
            TurretBasis = Quaternion.Identity,
            TurretServo = new ServoSettings(1f, 0f, 40f),
            TurretSpring = new SpringSettings(10f, 1f),
            BarrelServo = new ServoSettings(1f, 0f, 40f),
            BarrelSpring = new SpringSettings(10f, 1f),

            ProjectileShape = _simulation.Shapes.Add(projectileShape),
            ProjectileSpeed = 100f,
            BarrelLocalProjectileSpawn = new Vector3(0, 0, -1.5f),
            ProjectileInertia = projectileInertia,

            LeftTreadOffset = new Vector3(-1.9f, 0f, 0),
            RightTreadOffset = new Vector3(1.9f, 0f, 0),
            SuspensionLength = 1f,
            SuspensionSettings = new SpringSettings(2.5f, 1.5f),
            WheelShape = wheelShapeIndex,
            WheelInertia = wheelInertia,
            WheelFriction = 1f,
            TreadSpacing = 1f,
            WheelCountPerTread = 5,
            WheelOrientation = QuaternionEx.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI * -0.5f),
        };

        _playerController = new TankController(
            Tank.Create(_simulation, _bodyProperties, new RigidPose(new Vector3(0, 10, 0), Quaternion.Identity), tankDescription),
            20, 5, 2, 1, 3.5f);
        RegisterTankRecords(PlayerTankIndex, _playerController.Tank);

        var terrainPosition = new Vector2(1 - PlaneWidth, 1 - PlaneWidth) * TerrainScale * 0.5f;

        // Landmark buildings.
        var landmarkMin = new Vector3(PlaneWidth * TerrainScale * -0.45f, 0, PlaneWidth * TerrainScale * -0.45f);
        var landmarkMax = new Vector3(PlaneWidth * TerrainScale * 0.45f, 0, PlaneWidth * TerrainScale * 0.45f);
        var landmarkSpan = landmarkMax - landmarkMin;
        for (var j = 0; j < BuildingCount; ++j)
        {
            var buildingShape = new Box(10 + _random.NextSingle() * 10, 20 + _random.NextSingle() * 20, 10 + _random.NextSingle() * 10);
            var position = landmarkMin + landmarkSpan * new Vector3(_random.NextSingle(), _random.NextSingle(), _random.NextSingle());
            position.Y += buildingShape.HalfHeight - 4f + GetHeightForPosition(position.X, position.Z);
            var orientation = QuaternionEx.CreateFromAxisAngle(Vector3.UnitY, _random.NextSingle() * MathF.PI);
            _simulation.Statics.Add(new StaticDescription(position, orientation, _simulation.Shapes.Add(buildingShape)));
            _poses.AddStatic(
                BuildingRenderIdBase + j,
                position,
                orientation,
                new Vector3(buildingShape.HalfWidth, buildingShape.HalfHeight, buildingShape.HalfLength) * 2f);
        }

        var planeMesh = DemoMeshHelper.CreateDeformedPlane(PlaneWidth, PlaneWidth,
            (vX, vY) =>
            {
                var position2D = new Vector2(vX, vY) * TerrainScale + terrainPosition;
                return new Vector3(position2D.X, GetHeightForPosition(position2D.X, position2D.Y), position2D.Y);
            }, new Vector3(1, 1, 1), _bufferPool);
        _simulation.Statics.Add(new StaticDescription(new Vector3(0, 0, 0), _simulation.Shapes.Add(planeMesh)));

        // Create the AI tanks.
        _playAreaMin = new Vector2(landmarkMin.X, landmarkMin.Z);
        _playAreaMax = new Vector2(landmarkMax.X, landmarkMax.Z);
        var playAreaSpan = _playAreaMax - _playAreaMin;
        for (var i = 0; i < AiTankCount; ++i)
        {
            var horizontalPosition = _playAreaMin + new Vector2(_random.NextSingle(), _random.NextSingle()) * playAreaSpan;
            _aiTanks[i] = new AITank
            {
                Controller = new TankController(
                    Tank.Create(
                        _simulation, _bodyProperties,
                        new RigidPose(
                            new Vector3(horizontalPosition.X, 10, horizontalPosition.Y),
                            QuaternionEx.CreateFromAxisAngle(new Vector3(0, 1, 0), _random.NextSingle() * 0.1f)),
                        tankDescription),
                    20, 5, 2, 1, 3.5f),
                HitPoints = 5,
            };
            RegisterTankRecords(i + 1, _aiTanks[i].Controller.Tank);
        }
    }

    /// <summary>Routes one decoded tank-control packet (generated dispatcher → active simulation).</summary>
    public void OnTankControl(in TankControlInput input)
    {
        lock (_sync)
        {
            _inputMove = input.Move;
            _inputTurn = input.Turn;
            _inputAimHorizontal = input.AimHorizontal;
            _inputAimVertical = input.AimVertical;
            _inputFire = input.Fire > 0.5d;
            _inputZoom = input.Zoom > 0.5d;
            _inputBrake = input.Brake > 0.5d;
        }
    }

    public void Step(double deltaSeconds)
    {
        Transform3DRenderSignal signal;
        lock (_sync)
        {
            var stopwatch = Stopwatch.StartNew();
            UpdatePlayerLocked((float)deltaSeconds);
            for (var i = 0; i < _aiTankCount; ++i)
            {
                if (_aiTanks[i].Update(
                        _simulation, _bodyProperties, _random, _frameIndex, _playAreaMin, _playAreaMax, i,
                        _aiTanks, _aiTankCount, out var firedProjectile))
                {
                    ++_projectileCount;
                    if (_projectileRenderIdByBody.Count < MaxProjectiles) RegisterProjectileRecord(firedProjectile);
                }
            }

            _simulation.Timestep((float)deltaSeconds);
            stopwatch.Stop();

            ProcessProjectileImpactsLocked();
            ++_frameIndex;
            _poses.Sync();
            signal = BuildSignalLocked(stopwatch.Elapsed.TotalMilliseconds);
        }

        _renderTransport.Push(signal);
    }

    private void UpdatePlayerLocked(float dt)
    {
        // Turret aim rates (keyboard replacement for the upstream mouse-camera aim).
        _aimYaw = Math.Clamp(_aimYaw + (float)_inputAimHorizontal * dt * 2f, -MathF.PI * 0.75f, MathF.PI * 0.75f);
        _aimPitch = Math.Clamp(_aimPitch + (float)_inputAimVertical * dt * 1.5f, -0.6f, 0.6f);

        var body = _simulation.Bodies[_playerController.Tank.Body];
        Matrix3x3.CreateFromQuaternion(body.Pose.Orientation, out var orientation);
        var forward = new Vector3(orientation.Z.X, 0f, orientation.Z.Z);
        if (forward.LengthSquared() < 1e-8f) forward = new Vector3(0, 0, 1);
        forward = Vector3.Normalize(forward);
        Matrix3x3.CreateFromAxisAngle(Vector3.UnitY, _aimYaw, out var yawRotation);
        Matrix3x3.Transform(forward, yawRotation, out var aimDirection);
        aimDirection.Y = _aimPitch;
        aimDirection = Vector3.Normalize(aimDirection);

        var (leftTrack, rightTrack) = ComputePlayerTreads();
        _playerController.UpdateMovementAndAim(_simulation, leftTrack, rightTrack, _inputZoom, _inputBrake, _inputBrake, aimDirection);

        if (_inputFire && _frameIndex > _lastPlayerShotFrame + 60)
        {
            var projectileHandle = _playerController.Tank.Fire(_simulation, _bodyProperties);
            ++_projectileCount;
            if (_projectileRenderIdByBody.Count < MaxProjectiles) RegisterProjectileRecord(projectileHandle);
            _lastPlayerShotFrame = _frameIndex;
        }
    }

    /// <summary>Keyboard-compatible tread mapping (upstream WASD logic, fed by the input ring).</summary>
    private (float left, float right) ComputePlayerTreads()
    {
        var left = _inputTurn > 0.5d;
        var right = _inputTurn < -0.5d;
        float leftTrack = 0, rightTrack = 0;
        if (_inputMove > 0.5d)
        {
            if (left == right)
            {
                leftTrack = 1f;
                rightTrack = 1f;
            }
            else if (left)
            {
                leftTrack = 0.5f;
                rightTrack = 1f;
            }
            else
            {
                leftTrack = 1f;
                rightTrack = 0.5f;
            }
        }
        else if (_inputMove < -0.5d)
        {
            if (left == right)
            {
                leftTrack = -1f;
                rightTrack = -1f;
            }
            else if (left)
            {
                leftTrack = -0.5f;
                rightTrack = -1f;
            }
            else
            {
                leftTrack = -1f;
                rightTrack = -0.5f;
            }
        }
        else
        {
            // Not trying to move. Turn?
            if (left && !right)
            {
                leftTrack = -1f;
                rightTrack = 1f;
            }
            else if (right && !left)
            {
                leftTrack = 1f;
                rightTrack = -1f;
            }
        }

        return (leftTrack, rightTrack);
    }

    private void ProcessProjectileImpactsLocked()
    {
        for (var i = 0; i < _impactTracker.Count; ++i)
        {
            ref var impact = ref _impactTracker.Impacts[i];
            if (_projectileRenderIdByBody.TryGetValue(impact.ProjectileHandle.Value, out var renderId))
            {
                _poses.Remove(renderId, true);
                _projectileRenderIdByBody.Remove(impact.ProjectileHandle.Value);
                if (_projectileCount > 0) --_projectileCount;
            }

            if (impact.ImpactedTankBodyHandle.Value >= 0)
            {
                for (var aiIndex = 0; aiIndex < _aiTankCount; ++aiIndex)
                {
                    if (_aiTanks[aiIndex].Controller.Tank.Body.Value != impact.ImpactedTankBodyHandle.Value) continue;

                    if (--_aiTanks[aiIndex].HitPoints > 0) break;

                    _aiTanks[aiIndex].Controller.Tank.Explode(_simulation, _bodyProperties);
                    // Swap-remove (upstream FastRemoveAt); the records stay so the wreck keeps rendering.
                    _aiTanks[aiIndex] = _aiTanks[--_aiTankCount];
                    break;
                }
            }
        }

        _impactTracker.Clear();
    }

    private void RegisterProjectileRecord(BodyHandle handle)
    {
        // Caller guarantees a free slot: at most MaxProjectiles live records exist and the id
        // window is exactly that size, so wrapping ids cannot collide with a live projectile.
        var renderId = ProjectileRenderIdBase + _nextProjectileRenderId;
        _nextProjectileRenderId = (_nextProjectileRenderId + 1) % MaxProjectiles;
        _poses.AddDynamic(renderId, handle, new Vector3(0.2f, 0.2f, 0.2f));
        _projectileRenderIdByBody[handle.Value] = renderId;
    }

    private void RegisterTankRecords(int tankIndex, in Tank tank)
    {
        var baseId = TankRenderIdBase + tankIndex * TankRenderIdStride;
        _poses.AddDynamic(baseId + BodyChildOffset, tank.Body, new Vector3(8f, 2f, 10f));
        _poses.AddDynamic(baseId + TurretChildOffset, tank.Turret, new Vector3(3f, 1.4f, 4f));
        _poses.AddDynamic(baseId + BarrelChildOffset, tank.Barrel, new Vector3(0.4f, 0.4f, 6f));
        for (var i = 0; i < tank.WheelHandles.Length; ++i)
        {
            _poses.AddDynamic(baseId + WheelChildOffsetBase + i, tank.WheelHandles[i], new Vector3(0.8f, 0.36f, 0.8f));
        }
    }

    private float GetHeightForPosition(float x, float y)
    {
        const float inverseTerrainScale = 1f / TerrainScale;
        var terrainPosition = new Vector2(1 - PlaneWidth, 1 - PlaneWidth) * TerrainScale * 0.5f;
        var normalizedX = (x - terrainPosition.X) * inverseTerrainScale;
        var normalizedY = (y - terrainPosition.Y) * inverseTerrainScale;
        var octave0 = (MathF.Sin((normalizedX + 5f) * 0.05f) + MathF.Sin((normalizedY + 11) * 0.05f)) * 3.8f;
        var octave1 = (MathF.Sin((normalizedX + 17) * 0.15f) + MathF.Sin((normalizedY + 47) * 0.15f)) * 1.5f;
        var octave2 = (MathF.Sin((normalizedX + 37) * 0.35f) + MathF.Sin((normalizedY + 93) * 0.35f)) * 0.5f;
        var octave3 = (MathF.Sin((normalizedX + 53) * 0.65f) + MathF.Sin((normalizedY + 131) * 0.65f)) * 0.3f;
        var octave4 = (MathF.Sin((normalizedX + 67) * 1.50f) + MathF.Sin((normalizedY + 13) * 1.5f)) * 0.1525f;
        var distanceToEdge = PlaneWidth / 2 - Math.Max(Math.Abs(normalizedX - PlaneWidth / 2), Math.Abs(normalizedY - PlaneWidth / 2));
        // Flatten an area in the middle.
        var offsetX = PlaneWidth * 0.5f - normalizedX;
        var offsetY = PlaneWidth * 0.5f - normalizedY;
        var distanceToCenterSquared = offsetX * offsetX + offsetY * offsetY;
        const float centerCircleSize = 30f;
        const float fadeoutBoundary = 50f;
        var outsideWeight = MathF.Min(1f, MathF.Max(0, distanceToCenterSquared - centerCircleSize * centerCircleSize) / (fadeoutBoundary * fadeoutBoundary - centerCircleSize * centerCircleSize));
        var edgeRamp = 25f / (5 * distanceToEdge + 1);
        return outsideWeight * (octave0 + octave1 + octave2 + octave3 + octave4 + edgeRamp);
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
