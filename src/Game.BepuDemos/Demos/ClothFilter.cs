using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Collision filter for a cloth-lattice node, packing its grid indices plus the cloth
///     instance id. Ported from the upstream BepuPhysics2 <c>ClothDemo</c> (Apache-2.0, Ross
///     Nordby); used by the dancer dress in <c>DancerDemo</c>.
/// </summary>
public struct ClothCollisionFilter
{
    private ushort x;
    private ushort y;
    private int instanceId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ClothCollisionFilter(int x, int y, int instanceId)
    {
        const int max = 1 << 16;
        Debug.Assert(x >= 0 && x < max && y >= 0 && y < max, "This filter packs local indices, so their range is limited.");
        this.x = (ushort)x;
        this.y = (ushort)y;
        this.instanceId = instanceId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Test(ClothCollisionFilter a, ClothCollisionFilter b, int minimumDistance)
    {
        if (a.instanceId != b.instanceId) return true;
        // Disallow collisions between vertices which are near each other. Distance is max(|dx|, |dy|).
        var differenceX = a.x - b.x;
        if (differenceX < -minimumDistance || differenceX > minimumDistance) return true;
        var differenceY = a.y - b.y;
        if (differenceY < -minimumDistance || differenceY > minimumDistance) return true;
        return false;
    }
}

/// <summary>Narrow phase callbacks that prune self collisions between nearby cloth nodes.</summary>
public struct ClothCallbacks : INarrowPhaseCallbacks, IDancerNarrowPhaseCallbacks<ClothCallbacks, ClothCollisionFilter>
{
    public CollidableProperty<ClothCollisionFilter> Filters;
    public PairMaterialProperties Material;

    /// <summary>Minimum index distance in cloth nodes required for two nodes to collide.</summary>
    public int MinimumDistanceForSelfCollisions;

    public ClothCallbacks(CollidableProperty<ClothCollisionFilter> filters, PairMaterialProperties material, int minimumDistanceForSelfCollisions = 3)
    {
        Filters = filters;
        Material = material;
        MinimumDistanceForSelfCollisions = minimumDistanceForSelfCollisions;
    }

    public ClothCallbacks(CollidableProperty<ClothCollisionFilter> filters, int minimumDistanceForSelfCollisions = 3)
        : this(filters, new PairMaterialProperties
        {
            SpringSettings = new SpringSettings(30, 1),
            FrictionCoefficient = 0.25f,
            MaximumRecoveryVelocity = 2f,
        }, minimumDistanceForSelfCollisions)
    {
    }

    public static ClothCallbacks Create(
        CollidableProperty<ClothCollisionFilter> filters, PairMaterialProperties pairMaterialProperties, int minimumDistanceForSelfCollisions) =>
        new(filters, pairMaterialProperties, minimumDistanceForSelfCollisions);

    public void Initialize(Simulation simulation)
    {
        Filters.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility != CollidableMobility.Static && b.Mobility != CollidableMobility.Static)
        {
            return ClothCollisionFilter.Test(Filters[a.BodyHandle], Filters[b.BodyHandle], MinimumDistanceForSelfCollisions);
        }

        return a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
        where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial = Material;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

    public void Dispose()
    {
        Filters.Dispose();
    }
}
