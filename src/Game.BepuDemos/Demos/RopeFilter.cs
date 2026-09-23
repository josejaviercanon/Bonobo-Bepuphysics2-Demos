using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Filter for a body in a rope, used by the <see cref="RopeNarrowPhaseCallbacks"/>.
///     Ported from the upstream BepuPhysics2 <c>RopeTwistDemo</c> (Apache-2.0, Ross Nordby).
/// </summary>
public struct RopeFilter
{
    public short RopeIndex;
    public short IndexInRope;
}

/// <summary>
///     Narrow phase callbacks that include collision filters designed for ropes: adjacent bodies
///     in a rope do not collide with each other. Shared by <c>RopeTwistDemo</c> and
///     <c>ChainFountainDemo</c>.
/// </summary>
public struct RopeNarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public CollidableProperty<RopeFilter> Filters;
    public PairMaterialProperties Material;
    public int MinimumDistanceForCollisions;

    public RopeNarrowPhaseCallbacks(
        CollidableProperty<RopeFilter> filters, PairMaterialProperties contactMaterial, int minimumDistanceForCollisions = 3)
    {
        Filters = filters;
        Material = contactMaterial;
        MinimumDistanceForCollisions = minimumDistanceForCollisions;
    }

    public RopeNarrowPhaseCallbacks(CollidableProperty<RopeFilter> filters, int minimumDistanceForCollisions = 3)
        : this(filters, new PairMaterialProperties(1, 2, new SpringSettings(30, 1)), minimumDistanceForCollisions)
    {
    }

    public void Initialize(Simulation simulation)
    {
        Filters.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        var aFilter = Filters[a];
        var bFilter = Filters[b];
        return (aFilter.RopeIndex != bFilter.RopeIndex ||
                Math.Abs(aFilter.IndexInRope - bFilter.IndexInRope) > MinimumDistanceForCollisions) &&
               (a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic);
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
