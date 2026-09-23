using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;
using Bonobo.BepuUtilities;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Collision filter for a deformable (voxel-lattice) node, packing its grid indices plus the
///     instance id (10 bits per axis). Ported from the upstream BepuPhysics2 <c>NewtDemo</c>
///     (Apache-2.0, Ross Nordby); used by the dancer fat suit in <c>PlumpDancerDemo</c>.
/// </summary>
public struct DeformableCollisionFilter
{
    private int localIndices;
    private int instanceId;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public DeformableCollisionFilter(int x, int y, int z, int instanceId)
    {
        const int max = 1 << 10;
        Debug.Assert(x >= 0 && x < max && y >= 0 && y < max && z >= 0 && z < max, "This filter packs local indices, so their range is limited.");
        localIndices = x | (y << 10) | (z << 20);
        this.instanceId = instanceId;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Test(in DeformableCollisionFilter a, in DeformableCollisionFilter b)
    {
        if (a.instanceId != b.instanceId) return true;
        // Disallow collisions between vertices which are near each other. Distance is max(|dx|, |dy|, |dz|).
        const int minimumDistance = 3;
        const int mask = (1 << 10) - 1;
        var ax = a.localIndices & mask;
        var bx = b.localIndices & mask;
        var differenceX = ax - bx;
        if (differenceX < -minimumDistance || differenceX > minimumDistance) return true;
        var ay = (a.localIndices >> 10) & mask;
        var by = (b.localIndices >> 10) & mask;
        var differenceY = ay - by;
        if (differenceY < -minimumDistance || differenceY > minimumDistance) return true;
        var az = (a.localIndices >> 20) & mask;
        var bz = (b.localIndices >> 20) & mask;
        var differenceZ = az - bz;
        if (differenceZ < -minimumDistance || differenceZ > minimumDistance) return true;
        return false;
    }
}

/// <summary>Narrow phase callbacks that prune self collisions between nearby deformable nodes.</summary>
public struct DeformableCallbacks : INarrowPhaseCallbacks, IDancerNarrowPhaseCallbacks<DeformableCallbacks, DeformableCollisionFilter>
{
    public CollidableProperty<DeformableCollisionFilter> Filters;
    public PairMaterialProperties Material;

    /// <summary>Minimum index distance in deformable nodes required for two nodes to collide.</summary>
    public int MinimumDistanceForSelfCollisions;

    public DeformableCallbacks(CollidableProperty<DeformableCollisionFilter> filters, PairMaterialProperties material, int minimumDistanceForSelfCollisions = 3)
    {
        Filters = filters;
        Material = material;
        MinimumDistanceForSelfCollisions = minimumDistanceForSelfCollisions;
    }

    public DeformableCallbacks(CollidableProperty<DeformableCollisionFilter> filters, int minimumDistanceForSelfCollisions = 3)
        : this(filters, new PairMaterialProperties(1, 2, new SpringSettings(30, 1)), minimumDistanceForSelfCollisions)
    {
    }

    public static DeformableCallbacks Create(
        CollidableProperty<DeformableCollisionFilter> filters, PairMaterialProperties pairMaterialProperties, int minimumDistanceForSelfCollisions) =>
        new(filters, pairMaterialProperties, minimumDistanceForSelfCollisions);

    public void Initialize(Simulation simulation)
    {
        Filters.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        if (a.Mobility == CollidableMobility.Dynamic && b.Mobility == CollidableMobility.Dynamic)
        {
            return DeformableCollisionFilter.Test(Filters[a.BodyHandle], Filters[b.BodyHandle]);
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
