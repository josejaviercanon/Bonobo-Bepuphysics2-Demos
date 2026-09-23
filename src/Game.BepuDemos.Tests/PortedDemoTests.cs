using DemoEngine.ECS;
using DemoEngine.Simulations;
using Game.BepuDemos.Demos;
using Xunit;

namespace Game.BepuDemos.Tests;

/// <summary>
///     Behavioral tests for the ported group-A demos: upstream geometry/behavior preserved
///     (with the documented grid reductions), deterministic solves, no NaN, and the
///     ECS+Bepu mirror reaching the render signal.
/// </summary>
public class PortedDemoTests
{
    private static Transform3DRenderSignal Run(
        IDemoSimulation simulation, CapturingRenderTransport<Transform3DRenderSignal> transport, int steps)
    {
        for (var i = 0; i < steps; i++) simulation.Step(1.0 / 60.0);
        return transport.Last ?? throw new InvalidOperationException("no signal pushed");
    }

    // ---- Pyramid ----------------------------------------------------------

    [Fact]
    public void Pyramid_EmitsFloorPlusEveryBox()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PyramidDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.Equal(PyramidDemo.PyramidCount * PyramidDemo.BoxesPerPyramid, demo.BoxCount);
        Assert.Equal(1 + PyramidDemo.PyramidCount * PyramidDemo.BoxesPerPyramid, signal.States.Count);
        Assert.Equal(PyramidDemo.FloorRenderId, signal.States[0].Id);
        Assert.Equal(PyramidDemo.FloorRenderScale, signal.States[0].Sx, 3);
    }

    [Fact]
    public void Pyramid_SucceedsProjectile_WithReservedRenderId()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PyramidDemo(null, transport);

        Assert.True(demo.TryCommand("shoot"));
        Assert.Equal(1, demo.ProjectileCount);

        demo.Step(1.0 / 60.0);
        var projectile = Assert.Single(transport.Last!.States, s => s.Id >= PyramidDemo.BallRenderIdBase);
        Assert.InRange(projectile.Sx, 1d, 11d);

        Assert.True(demo.TryCommand("clear-projectiles"));
        Assert.Equal(0, demo.ProjectileCount);
    }

    // ---- Bounciness -------------------------------------------------------

    [Fact]
    public void Bounciness_EmitsFullGrid_AndSpheresFall()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new BouncinessDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var first = transport.Last!;
        Assert.Equal(1 + BouncinessDemo.BallCount, first.States.Count);
        Assert.Equal(BouncinessDemo.BallCount, demo.BallEntityCount);

        var startMean = MeanHeight(first);
        for (var i = 1; i < 90; i++) demo.Step(1.0 / 60.0);
        var laterMean = MeanHeight(transport.Last!);

        Assert.True(laterMean < startMean, $"grid did not fall: {startMean} -> {laterMean}");
    }

    [Fact]
    public void Bounciness_ResetRebuildsGrid()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new BouncinessDemo(null, transport);

        for (var i = 0; i < 30; i++) demo.Step(1.0 / 60.0);
        var fallen = MeanHeight(transport.Last!);

        Assert.True(demo.TryCommand("reset"));
        demo.Step(1.0 / 60.0);
        var reset = MeanHeight(transport.Last!);

        Assert.True(reset > fallen, $"reset did not restore heights: {fallen} -> {reset}");
        Assert.Equal(BouncinessDemo.BallCount, demo.BallEntityCount);
    }

    private static double MeanHeight(Transform3DRenderSignal signal)
    {
        double sum = 0;
        var count = 0;
        foreach (var state in signal.States)
        {
            if (state.Id < BouncinessDemo.BallRenderIdBase) continue;
            sum += state.Y;
            count++;
        }

        return count > 0 ? sum / count : 0d;
    }

    // ---- Planet -----------------------------------------------------------

    [Fact]
    public void Planet_EmitsPlanetPlusOrbiters_AndTheyMove()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PlanetDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var first = transport.Last!;
        Assert.Equal(1 + PlanetDemo.BallCount, first.States.Count);
        Assert.Equal(PlanetDemo.PlanetRenderId, first.States[0].Id);
        Assert.Equal(PlanetDemo.PlanetRadius * 2d, first.States[0].Sx, 3);

        var firstOrbiter = first.States[1];
        for (var i = 1; i < 60; i++) demo.Step(1.0 / 60.0);
        var laterOrbiter = transport.Last!.States[1];

        Assert.True(
            Math.Abs(laterOrbiter.X - firstOrbiter.X) > 0.5d ||
            Math.Abs(laterOrbiter.Z - firstOrbiter.Z) > 0.5d,
            "orbiters did not move");
    }

    [Fact]
    public void Planet_HasNoNaNOrInfinity()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PlanetDemo(null, transport);
        var signal = Run(demo, transport, 120);

        foreach (var state in signal.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsInfinity(state.X));
            Assert.False(double.IsNaN(state.Y) || double.IsInfinity(state.Y));
            Assert.False(double.IsNaN(state.Z) || double.IsInfinity(state.Z));
        }
    }

    // ---- Determinism across the ported set ---------------------------------

    [Theory]
    [InlineData("simple-self-contained")]
    [InlineData("pyramid")]
    [InlineData("bounciness")]
    [InlineData("planet")]
    public void PortedSet_DeterministicAcrossRuns(string gameKey)
    {
        var first = new CapturingRenderTransport<Transform3DRenderSignal>();
        var second = new CapturingRenderTransport<Transform3DRenderSignal>();

        RunKey(gameKey, first, 60);
        RunKey(gameKey, second, 60);

        var a = first.Last!;
        var b = second.Last!;
        Assert.Equal(a.States.Count, b.States.Count);
        for (var i = 0; i < a.States.Count; i++)
        {
            Assert.Equal(a.States[i].Id, b.States[i].Id);
            Assert.Equal(a.States[i].X, b.States[i].X, 10);
            Assert.Equal(a.States[i].Y, b.States[i].Y, 10);
            Assert.Equal(a.States[i].Z, b.States[i].Z, 10);
        }

        static void RunKey(string key, CapturingRenderTransport<Transform3DRenderSignal> transport, int steps)
        {
            using var simulation = Create(key, transport);
            for (var i = 0; i < steps; i++) simulation.Step(1.0 / 60.0);
        }
    }

    private static IDemoSimulation Create(string gameKey, CapturingRenderTransport<Transform3DRenderSignal> transport) =>
        gameKey switch
        {
            "simple-self-contained" => new SimpleSelfContainedDemo(null, transport),
            "pyramid" => new PyramidDemo(null, transport),
            "bounciness" => new BouncinessDemo(null, transport),
            "planet" => new PlanetDemo(null, transport),
            _ => throw new ArgumentOutOfRangeException(nameof(gameKey), gameKey, "unknown demo key"),
        };
}
