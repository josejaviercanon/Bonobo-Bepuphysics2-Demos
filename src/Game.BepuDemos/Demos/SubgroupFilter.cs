using System.Runtime.CompilerServices;
using Bonobo.Bepuphysics2;
using Bonobo.Bepuphysics2.Collidables;
using Bonobo.Bepuphysics2.CollisionDetection;
using Bonobo.Bepuphysics2.Constraints;

namespace Game.BepuDemos.Demos;

/// <summary>
///     Bit masks which control whether different members of a group of objects can collide with
///     each other. Ported from the upstream BepuPhysics2 <c>RagdollDemo</c> (Apache-2.0, Ross
///     Nordby). Shared by <c>RagdollTubeDemo</c> and the dancer demos (source dancer skeleton).
/// </summary>
public struct SubgroupCollisionFilter
{
    /// <summary>A mask of 16 bits, each set bit representing a collision group that an object belongs to.</summary>
    public ushort SubgroupMembership;

    /// <summary>A mask of 16 bits, each set bit representing a collision group that an object can interact with.</summary>
    public ushort CollidableSubgroups;

    /// <summary>Id of the owner of the object. Objects belonging to different groups always collide.</summary>
    public int GroupId;

    /// <summary>Initializes a collision filter that collides with everything in the group.</summary>
    public SubgroupCollisionFilter(int groupId)
    {
        GroupId = groupId;
        SubgroupMembership = ushort.MaxValue;
        CollidableSubgroups = ushort.MaxValue;
    }

    /// <summary>
    ///     Initializes a collision filter that belongs to one specific subgroup and can collide
    ///     with any other subgroup.
    /// </summary>
    public SubgroupCollisionFilter(int groupId, int subgroupId)
    {
        GroupId = groupId;
        SubgroupMembership = (ushort)(1 << subgroupId);
        CollidableSubgroups = ushort.MaxValue;
    }

    /// <summary>Disables a collision between this filter and the specified subgroup.</summary>
    public void DisableCollision(int subgroupId)
    {
        CollidableSubgroups ^= (ushort)(1 << subgroupId);
    }

    /// <summary>
    ///     Modifies the interactable subgroups such that filterB does not interact with the
    ///     subgroups defined by filter a and vice versa.
    /// </summary>
    public static void DisableCollision(ref SubgroupCollisionFilter filterA, ref SubgroupCollisionFilter filterB)
    {
        filterA.CollidableSubgroups &= (ushort)~filterB.SubgroupMembership;
        filterB.CollidableSubgroups &= (ushort)~filterA.SubgroupMembership;
    }

    /// <summary>Checks if the filters can collide.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool AllowCollision(in SubgroupCollisionFilter a, in SubgroupCollisionFilter b)
    {
        return a.GroupId != b.GroupId || (a.CollidableSubgroups & b.SubgroupMembership) > 0;
    }
}

/// <summary>Narrow phase callbacks that prune out collisions between members of a group of objects.</summary>
public struct SubgroupFilteredCallbacks : INarrowPhaseCallbacks
{
    public CollidableProperty<SubgroupCollisionFilter> CollisionFilters;
    public PairMaterialProperties Material;

    public SubgroupFilteredCallbacks(CollidableProperty<SubgroupCollisionFilter> filters)
    {
        CollisionFilters = filters;
        Material = new PairMaterialProperties(1, 2, new SpringSettings(30, 1));
    }

    public SubgroupFilteredCallbacks(CollidableProperty<SubgroupCollisionFilter> filters, PairMaterialProperties material)
    {
        CollisionFilters = filters;
        Material = material;
    }

    public void Initialize(Simulation simulation)
    {
        CollisionFilters.Initialize(simulation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
    {
        // It's impossible for two statics to collide, and pairs are sorted such that bodies always come before statics.
        if (b.Mobility != CollidableMobility.Static)
        {
            return SubgroupCollisionFilter.AllowCollision(CollisionFilters[a.BodyHandle], CollisionFilters[b.BodyHandle]);
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
        CollisionFilters.Dispose();
    }
}
