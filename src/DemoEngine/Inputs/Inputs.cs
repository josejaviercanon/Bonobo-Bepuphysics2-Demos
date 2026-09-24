namespace DemoEngine.Inputs;

/// <summary>
///     Source of truth for the input ring ABI. Mirrors
///     <c>Game.Engine.Inputs.InputRingLayout</c> 1:1; pinned by unit tests.
/// </summary>
public static class InputRingLayout
{
    /// <summary>Scalar elements per record (slot 0 = packet type id, slots 1..7 = payload).</summary>
    public const int SlotSize = 8;

    /// <summary>Fixed record capacity of the single-producer/single-consumer ring.</summary>
    public const int QueueCapacity = 100;
}

/// <summary>
///     Packet type ids. Mirrors the engine's <c>[WasmInputPacket(id)]</c> values:
///     1 = <see cref="ClickMoveInput"/>, 2 = <see cref="FireBallInput"/>,
///     3 = <see cref="SceneLoadedInput"/>. Ids 4..6 are the P3 player-intent extension
///     (character/vehicle/tank control) documented in docs/compat-review.md. Slot layout:
///     slot 0 = id, slots 1..N = fields in declaration order (mirrored by the TypeScript
///     producer in <c>src/BepuDemos.UI/src/inputRing.ts</c>).
/// </summary>
public static class InputPacketIds
{
    public const double ClickMove = 1d;
    public const double FireBall = 2d;
    public const double SceneLoaded = 3d;
    public const double CharacterMove = 4d;
    public const double VehicleControl = 5d;
    public const double TankControl = 6d;
}

/// <summary>
///     Player intent: steer the entity with the given render id toward a world-space point.
///     Mirrors <c>Game.Engine.Inputs.ClickMoveInput</c>.
/// </summary>
public readonly struct ClickMoveInput
{
    public readonly double TargetEntityId;
    public readonly double X;
    public readonly double Y;
    public readonly double Z;

    public ClickMoveInput(double targetEntityId, double x, double y, double z)
    {
        TargetEntityId = targetEntityId;
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>
///     Player intent: launch a projectile from a world-space origin along a direction.
///     Mirrors <c>Game.Engine.Inputs.FireBallInput</c>.
/// </summary>
public readonly struct FireBallInput
{
    public readonly double OriginX;
    public readonly double OriginY;
    public readonly double OriginZ;
    public readonly double DirectionX;
    public readonly double DirectionY;
    public readonly double DirectionZ;

    public FireBallInput(
        double originX, double originY, double originZ,
        double directionX, double directionY, double directionZ)
    {
        OriginX = originX;
        OriginY = originY;
        OriginZ = originZ;
        DirectionX = directionX;
        DirectionY = directionY;
        DirectionZ = directionZ;
    }
}

/// <summary>Presentation notice: scene switch finished (Ok = 1) or failed (Ok = 0).</summary>
public readonly struct SceneLoadedInput
{
    public readonly double Ok;

    public SceneLoadedInput(double ok) => Ok = ok;
}

/// <summary>
///     Player intent for a character controller: world-space horizontal movement direction
///     (already camera-relative, magnitude 0..1) plus jump/sprint buttons. Mirrors the
///     upstream <c>CharacterInput</c> target-velocity contract.
/// </summary>
public readonly struct CharacterMoveInput
{
    public readonly double MoveX;
    public readonly double MoveZ;
    public readonly double Jump;
    public readonly double Sprint;

    public CharacterMoveInput(double moveX, double moveZ, double jump, double sprint)
    {
        MoveX = moveX;
        MoveZ = moveZ;
        Jump = jump;
        Sprint = sprint;
    }
}

/// <summary>Player intent for a wheeled vehicle: throttle/steer in -1..1 plus zoom/brake buttons.</summary>
public readonly struct VehicleControlInput
{
    public readonly double Throttle;
    public readonly double Steer;
    public readonly double Zoom;
    public readonly double Brake;

    public VehicleControlInput(double throttle, double steer, double zoom, double brake)
    {
        Throttle = throttle;
        Steer = steer;
        Zoom = zoom;
        Brake = brake;
    }
}

/// <summary>
///     Player intent for a tank: forward/turn in -1..1, turret aim rates in -1..1 plus
///     fire/zoom/brake buttons (7 payload slots, the full ring record).
/// </summary>
public readonly struct TankControlInput
{
    public readonly double Move;
    public readonly double Turn;
    public readonly double AimHorizontal;
    public readonly double AimVertical;
    public readonly double Fire;
    public readonly double Zoom;
    public readonly double Brake;

    public TankControlInput(
        double move, double turn, double aimHorizontal, double aimVertical,
        double fire, double zoom, double brake)
    {
        Move = move;
        Turn = turn;
        AimHorizontal = aimHorizontal;
        AimVertical = aimVertical;
        Fire = fire;
        Zoom = zoom;
        Brake = brake;
    }
}

/// <summary>Routing surface for decoded input-ring packets (mirrors the engine sink interfaces).</summary>
public interface IClickMoveSink
{
    void OnClickMove(in ClickMoveInput input);
}

public interface IFireBallSink
{
    void OnFireBall(in FireBallInput input);
}

public interface ISceneLoadedSink
{
    void OnSceneLoaded(in SceneLoadedInput input);
}

public interface ICharacterMoveSink
{
    void OnCharacterMove(in CharacterMoveInput input);
}

public interface IVehicleControlSink
{
    void OnVehicleControl(in VehicleControlInput input);
}

public interface ITankControlSink
{
    void OnTankControl(in TankControlInput input);
}

/// <summary>
///     Hand-written decoder for the input ring (the engine generates this from
///     <c>[WasmInputPacket]</c> attributes; this repo has no source generator by design).
///     Record layout is fixed: slot 0 = packet id, slots 1..7 = payload doubles.
/// </summary>
public static class InputDispatcher
{
    /// <summary>Decodes one 8-slot record and routes it to the matching sink. Returns true when handled.</summary>
    public static bool Dispatch(ReadOnlySpan<double> record, object? sink)
    {
        if (record.Length < InputRingLayout.SlotSize) return false;

        switch (record[0])
        {
            case InputPacketIds.ClickMove:
                if (sink is IClickMoveSink clickMove)
                {
                    clickMove.OnClickMove(new ClickMoveInput(record[1], record[2], record[3], record[4]));
                    return true;
                }

                return false;

            case InputPacketIds.FireBall:
                if (sink is IFireBallSink fireBall)
                {
                    fireBall.OnFireBall(new FireBallInput(
                        record[1], record[2], record[3], record[4], record[5], record[6]));
                    return true;
                }

                return false;

            case InputPacketIds.SceneLoaded:
                if (sink is ISceneLoadedSink sceneLoaded)
                {
                    sceneLoaded.OnSceneLoaded(new SceneLoadedInput(record[1]));
                    return true;
                }

                return false;

            case InputPacketIds.CharacterMove:
                if (sink is ICharacterMoveSink characterMove)
                {
                    characterMove.OnCharacterMove(new CharacterMoveInput(
                        record[1], record[2], record[3], record[4]));
                    return true;
                }

                return false;

            case InputPacketIds.VehicleControl:
                if (sink is IVehicleControlSink vehicleControl)
                {
                    vehicleControl.OnVehicleControl(new VehicleControlInput(
                        record[1], record[2], record[3], record[4]));
                    return true;
                }

                return false;

            case InputPacketIds.TankControl:
                if (sink is ITankControlSink tankControl)
                {
                    tankControl.OnTankControl(new TankControlInput(
                        record[1], record[2], record[3], record[4], record[5], record[6], record[7]));
                    return true;
                }

                return false;

            default:
                return false;
        }
    }
}
