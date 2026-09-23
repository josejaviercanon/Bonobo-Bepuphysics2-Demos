using DemoEngine.Config;
using DemoEngine.ECS;
using DemoEngine.Inputs;
using DemoEngine.Simulations;
using Game.BepuDemos;
using Game.BepuDemos.Demos;
using Xunit;

namespace Game.BepuDemos.Tests;

/// <summary>
///     Host-level tests: module registration via the hand-written AOT glue, fixed-step
///     accumulator, pause behavior, input-ring routing and the globals clock block.
/// </summary>
public class SimulationHostTests
{
    private static SimulationHost CreateHost(out List<(string Event, int Count)> commits)
    {
        var seen = commits = new List<(string, int)>();
        var host = new SimulationHost(
            (eventName, _, elementCount) => seen.Add((eventName, elementCount)),
            new DemoRegistry().AddGameBepuDemosModules());
        return host;
    }

    [Fact]
    public void GlueRegistersEveryDemoWithUniqueKeys()
    {
        var registry = new DemoRegistry().AddGameBepuDemosModules();
        Assert.Contains("simple-self-contained", registry.GameKeys);
        Assert.Equal(registry.GameKeys.Count, registry.GameKeys.Distinct().Count());

        // Duplicate registration must fail fast (mirrors EngineBuilder).
        Assert.Throws<InvalidOperationException>(() =>
            registry.AddModule(SimpleSelfContainedDemo.CreateModule()));
    }

    [Fact]
    public void Connect_CreatesActiveSimulation_AndUnknownKeyIsIdle()
    {
        using var host = CreateHost(out _);

        host.Connect("does-not-exist");
        Assert.Null(host.ActiveSimulation);

        host.Connect("simple-self-contained");
        Assert.IsType<SimpleSelfContainedDemo>(host.ActiveSimulation);
    }

    [Fact]
    public void Connect_ReleasesThePreviousPinnedBuffer_AndMenuKeyIdles()
    {
        using var host = CreateHost(out _);
        host.Connect("simple-self-contained");
        host.Tick(1.0 / 60.0);
        Assert.NotNull(host.ActiveSimulation);
        Assert.Equal(0, host.DisposedSignalBufferCount);

        // The menu key is unknown by design: the sim stops and its pinned GCHandle is freed.
        host.Connect("menu");
        Assert.Null(host.ActiveSimulation);
        Assert.Equal(1, host.DisposedSignalBufferCount);

        // A fresh connect allocates a new buffer; switching away releases it again.
        host.Connect("pyramid");
        Assert.NotNull(host.ActiveSimulation);
        Assert.Equal(1, host.DisposedSignalBufferCount);
        host.Connect("simple-self-contained");
        Assert.Equal(2, host.DisposedSignalBufferCount);
    }

    [Fact]
    public void Tick_RunsCappedFixedSteps_AndPublishesGlobalsClock()
    {
        using var host = CreateHost(out var commits);
        host.Connect("simple-self-contained");

        // 1/60 s of delta -> exactly one fixed step.
        var steps = host.Tick(1.0 / 60.0);
        Assert.Equal(1, steps);

        // A huge delta is clamped to MaxStepsPerTick (catch-up cap).
        var capped = host.Tick(10d);
        Assert.Equal(SimulationHost.MaxStepsPerTick, capped);

        Assert.Contains(commits, c => c.Event == SimulationHost.GlobalsEventName && c.Count == 8);
    }

    [Fact]
    public void Paused_HostExecutesNoSteps()
    {
        using var host = CreateHost(out _);
        host.Connect("simple-self-contained");
        host.SetPaused(true);

        Assert.Equal(0, host.Tick(1d));
        Assert.True(host.IsPaused);

        host.SetPaused(false);
        Assert.Equal(1, host.Tick(1.0 / 60.0));
    }

    [Fact]
    public void FireBallPacket_IsRoutedIntoTheActiveSimulation()
    {
        using var host = CreateHost(out _);
        host.Connect("simple-self-contained");

        var sim = Assert.IsType<SimpleSelfContainedDemo>(host.ActiveSimulation);
        Assert.Equal(1, sim.BallCount);

        var record = new double[InputRingLayout.SlotSize];
        record[0] = InputPacketIds.FireBall;
        record[1] = 0d;
        record[2] = 5d;
        record[3] = 0d;
        record[4] = 1d;   // direction +x

        // Pinned ring path (drained inside Tick).
        host.PushInputRecordForTest(record);
        host.Tick(1.0 / 60.0);

        Assert.Equal(2, sim.BallCount);
    }

    [Fact]
    public void SendCommand_ReachesTheActiveModulesVerbs()
    {
        using var host = CreateHost(out _);
        host.Connect("simple-self-contained");
        var sim = Assert.IsType<SimpleSelfContainedDemo>(host.ActiveSimulation);

        Assert.True(host.SendCommand("simple-self-contained", "spawn-ball"));
        Assert.Equal(2, sim.BallCount);
        Assert.False(host.SendCommand("other-game", "spawn-ball"));
    }

    [Fact]
    public void GlobalsClock_CarriesProcessedInputCounter()
    {
        using var host = CreateHost(out _);
        host.Connect("simple-self-contained");

        // FireBall is the packet the fixture sim accepts (IClickMoveSink is not implemented).
        var record = new double[InputRingLayout.SlotSize];
        record[0] = InputPacketIds.FireBall;
        record[4] = 1d;   // direction +x
        host.ProcessInputRecord(record);
        host.Tick(1.0 / 60.0);

        Assert.Equal(1d, host.ReadGlobalClockForTest(SignalBuffer.GlobalClockReserved0));
        Assert.Equal(0d, host.ReadGlobalClockForTest(SignalBuffer.GlobalClockReserved1));
    }

    [Fact]
    public void TryGetSignalInfo_ExposesStableGlobalsPointer()
    {
        using var host = CreateHost(out _);
        Assert.True(host.TryGetSignalInfo(SimulationHost.GlobalsEventName, out var pointer, out var capacity));
        Assert.NotEqual(0, pointer);
        Assert.Equal(SimulationHost.GlobalClockCapacity, capacity);
        Assert.False(host.TryGetSignalInfo("not-a-signal", out _, out _));

        Assert.True(host.TryGetInputInfo(out var dataPtr, out var headPtr, out var records));
        Assert.NotEqual(0, dataPtr);
        Assert.NotEqual(0, headPtr);
        Assert.Equal(InputRingLayout.QueueCapacity, records);
    }
}
