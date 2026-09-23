using Bonobo.ECS.Core;
using Bonobo.ECS.Systems;

namespace Game.BepuDemos.Demos;

/// <summary>
///     ECS-authoritative orbit: advances the marker phase and writes its world position.
///     The per-entity <c>[Query]</c> method is called by the Bonobo.ECS source generator
///     (<c>Bonobo.ECS.SourceGenerators</c>) when <c>Group&lt;double&gt;.Update</c> runs — no
///     reflection, AOT-safe.
/// </summary>
public partial class OrbitSystem : BaseSystem<World, double>
{
    public OrbitSystem(World world) : base(world)
    {
    }

    [Query]
    public void Orbit([Data] double dt, ref Position3 position, ref OrbitState orbit)
    {
        orbit.Angle += (float)(dt * orbit.Speed);
        position.X = MathF.Cos(orbit.Angle) * orbit.Radius;
        position.Y = orbit.CenterY;
        position.Z = MathF.Sin(orbit.Angle) * orbit.Radius;
    }
}
