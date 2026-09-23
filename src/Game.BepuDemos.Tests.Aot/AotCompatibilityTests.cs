using DemoEngine.ECS;
using DemoEngine.Simulations;
using Game.BepuDemos;
using Game.BepuDemos.Demos;

namespace Game.BepuDemos.Tests.Aot;

/// <summary>
///     AOT/trim pattern checks over the demo closure: the module registry is built from
///     direct static calls, the Bonobo.ECS source generator emitted its AOT component
///     registry, and a full host connect + fixed-step run works without any reflection on the
///     hot path. The authoritative check is the Release Native AOT publish of
///     DemoHost.WinApp, which compiles this closure.
/// </summary>
public class AotCompatibilityTests
{
    [Test]
    public async Task ModuleRegistry_IsPopulatedByStaticGlue()
    {
        var registry = new DemoRegistry().AddGameBepuDemosModules();

        await Assert.That(registry.GameKeys.Count).IsGreaterThan(0);
        await Assert.That(registry.GameKeys).Contains("simple-self-contained");
    }

    [Test]
    public async Task ComponentRegistry_SourceGeneratorEmittedRegistration()
    {
        // The Bonobo.ECS.SourceGenerators.Aot generator emits an assembly-scoped
        // registration module (ModuleInitializer) that registers every [Component].
        var assembly = typeof(SimpleSelfContainedDemo).Assembly;
        var registryType = assembly.GetTypes()
            .FirstOrDefault(type => (type.FullName ?? string.Empty).Contains("GeneratedComponentRegistry"));

        await Assert.That(registryType).IsNotNull();
        var initializer = registryType!.GetMethod(
            "Initialize",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        await Assert.That(initializer).IsNotNull();
    }

    [Test]
    public async Task HostConnectAndFixedStepRun_Succeeds()
    {
        var host = new SimulationHost(static (_, _, _) => { }, new DemoRegistry().AddGameBepuDemosModules());
        try
        {
            host.Connect("simple-self-contained");
            await Assert.That(host.ActiveSimulation).IsNotNull();

            var steps = 0;
            for (var i = 0; i < 120; i++)
            {
                steps += host.Tick(SimulationHost.FixedStepSeconds);
            }

            await Assert.That(steps).IsGreaterThan(0);
            await Assert.That(host.ReadGlobalClockForTest(SignalBuffer.GlobalClockStepCount)).IsGreaterThan(0d);
        }
        finally
        {
            host.Dispose();
        }
    }
}
