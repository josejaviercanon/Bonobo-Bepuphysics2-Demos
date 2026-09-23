using DemoEngine.Simulations;
using Game.BepuDemos.Demos;

namespace Game.BepuDemos;

/// <summary>
///     Hand-written AOT glue registering every demo module (the engine uses the
///     <c>Game.Engine.Generators</c> source generator; this repo keeps the call site shape
///     identical while staying self-contained). Direct static calls only — no reflection,
///     no assembly scanning, so the AOT trimmer keeps every simulation reachable from the
///     host entry point.
/// </summary>
public static class BepuDemosModules
{
    public static DemoRegistry AddGameBepuDemosModules(this DemoRegistry registry)
    {
        registry.AddModule(SimpleSelfContainedDemo.CreateModule());
        registry.AddModule(PyramidDemo.CreateModule());
        registry.AddModule(BouncinessDemo.CreateModule());
        registry.AddModule(PlanetDemo.CreateModule());
        registry.AddModule(FrictionDemo.CreateModule());
        registry.AddModule(PerBodyGravityDemo.CreateModule());
        registry.AddModule(ColosseumDemo.CreateModule());
        registry.AddModule(ContinuousCollisionDetectionDemo.CreateModule());
        registry.AddModule(SubsteppingDemo.CreateModule());
        registry.AddModule(CompoundDemo.CreateModule());
        registry.AddModule(ContactEventsDemo.CreateModule());
        registry.AddModule(CollisionTrackingDemo.CreateModule());
        registry.AddModule(CustomVoxelCollidableDemo.CreateModule());
        registry.AddModule(RopeStabilityDemo.CreateModule());
        registry.AddModule(RopeTwistDemo.CreateModule());
        registry.AddModule(ChainFountainDemo.CreateModule());
        registry.AddModule(BlockChainDemo.CreateModule());
        registry.AddModule(RagdollTubeDemo.CreateModule());
        registry.AddModule(DancerDemo.CreateModule());
        registry.AddModule(PlumpDancerDemo.CreateModule());
        return registry;
    }
}
