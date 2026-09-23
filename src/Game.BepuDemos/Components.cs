using Bonobo.ECS.SourceGenerators.Aot;
using Bonobo.Bepuphysics2;

namespace Game.BepuDemos;

/// <summary>3D world-space position of an entity. Feeds <c>Transform3DState.X/Y/Z</c>.</summary>
[Component]
public struct Position3
{
    public float X;
    public float Y;
    public float Z;

    public Position3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>
///     3D rotation as a unit quaternion (x, y, z, w). Component order matches
///     <c>Transform3DState</c>, BepuPhysics2 <c>RigidPose.Orientation</c> and Babylon.js
///     <c>Quaternion</c>.
/// </summary>
[Component]
public struct Rotation3
{
    public float Qx;
    public float Qy;
    public float Qz;
    public float Qw;

    public Rotation3(float qx, float qy, float qz, float qw)
    {
        Qx = qx;
        Qy = qy;
        Qz = qz;
        Qw = qw;
    }

    public static readonly Rotation3 Identity = new(0f, 0f, 0f, 1f);
}

/// <summary>Uniform/non-uniform scale along the three world axes.</summary>
[Component]
public struct Scale3
{
    public float X;
    public float Y;
    public float Z;

    public Scale3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static readonly Scale3 One = new(1f, 1f, 1f);
}

/// <summary>
///     Stable numeric id mapping an entity to a client-side (Babylon.js) render record. The raw
///     ECS <c>Entity</c> handle is not a presentation concept; ids are never reused within a
///     scene run.
/// </summary>
[Component]
public struct RenderId
{
    public int Id;

    public RenderId(int id) => Id = id;
}

/// <summary>
///     Per-entity lifecycle state, reset to <c>Active</c> after the tick that emitted it.
///     Destroyed entities are emitted one final time with the Destroyed flag so the Babylon
///     side can dispose the mesh.
/// </summary>
[Component]
public struct RenderLifecycle3
{
    public double State;
}

/// <summary>
///     BepuPhysics2 body handle owned by an ECS entity (<c>ECS_PHYSICS_MAPPING</c>). Bepu stays
///     authoritative for the pose; the simulation mirrors it into <see cref="Position3"/> /
///     <see cref="Rotation3"/> after every null-dispatcher <c>Simulation.Timestep</c>.
/// </summary>
[Component]
public struct PhysicsBody
{
    public BodyHandle Handle;

    public PhysicsBody(BodyHandle handle) => Handle = handle;
}

/// <summary>
///     Deterministic angular velocity (axis-scaled rotation vector, rad/s) for ECS-driven spin
///     systems. Presentation reads the quaternion; the component only feeds the system.
/// </summary>
[Component]
public struct Spin3
{
    public float X;
    public float Y;
    public float Z;

    public Spin3(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
}

/// <summary>
///     Deterministic circular orbit of an ECS-only marker (center at y = CenterY, radius,
///     angular speed and current phase). Consumed by the generated <c>OrbitSystem</c> query.
/// </summary>
[Component]
public struct OrbitState
{
    public float Radius;
    public float CenterY;
    public float Speed;
    public float Angle;

    public OrbitState(float radius, float centerY, float speed, float angle)
    {
        Radius = radius;
        CenterY = centerY;
        Speed = speed;
        Angle = angle;
    }
}

/// <summary>Lifecycle flag values carried in the 12th scalar of <c>Transform3DState</c>.</summary>
public static class EntityLifecycle3
{
    public const double Active = 0d;
    public const double Spawned = 1d;
    public const double Destroyed = 3d;
}
