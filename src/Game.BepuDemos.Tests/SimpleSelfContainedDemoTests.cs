using DemoEngine.ECS;
using Game.BepuDemos;
using Game.BepuDemos.Demos;
using Xunit;

namespace Game.BepuDemos.Tests;

/// <summary>
///     Behavioral tests for the ported <see cref="SimpleSelfContainedDemo"/>: Bepu-authoritative
///     sphere on the static floor, ECS-only orbit markers, verb handling and determinism.
/// </summary>
public class SimpleSelfContainedDemoTests
{
    private static Transform3DRenderSignal Step(SimpleSelfContainedDemo sim, CapturingRenderTransport<Transform3DRenderSignal> transport, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            sim.Step(SimpleSelfContainedDemo.TickIntervalSeconds);
        }

        Assert.NotNull(transport.Last);
        return transport.Last!;
    }

    private static Transform3DState StateOf(Transform3DRenderSignal signal, int renderId) =>
        Assert.Single(signal.States, state => state.Id == renderId);

    [Fact]
    public void Sphere_FallsAndRestsOnStaticFloor()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);

        // Spawn height is 5; the first step already integrates gravity (assert the drop is small).
        Assert.True(sim.TryGetPosition(SimpleSelfContainedDemo.BallRenderIdBase, out var spawn));
        Assert.Equal(5f, spawn.Y, 3);

        var first = Step(sim, transport, 1);
        var ballStart = StateOf(first, SimpleSelfContainedDemo.BallRenderIdBase);
        Assert.InRange(ballStart.Y, 4.9d, 5d);

        // Gravity (DemoWorldConfig.Default: -9.81) plus contact spring settling.
        var settled = Step(sim, transport, 240);
        var ballEnd = StateOf(settled, SimpleSelfContainedDemo.BallRenderIdBase);
        Assert.True(ballEnd.Y < ballStart.Y, $"ball did not fall: {ballStart.Y} -> {ballEnd.Y}");
        Assert.InRange(ballEnd.Y, 1.0d, 2.5d);
    }

    [Fact]
    public void StaticFloor_IsEmittedWithFullExtentScale()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);
        var signal = Step(sim, transport, 1);

        var floor = StateOf(signal, SimpleSelfContainedDemo.FloorRenderId);
        Assert.Equal(SimpleSelfContainedDemo.FloorHalfExtent * 2d, floor.Sx, 3);
        Assert.Equal(SimpleSelfContainedDemo.FloorThickness, floor.Sy, 3);
        Assert.Equal(1d, floor.Qw, 3);
    }

    [Fact]
    public void OrbitMarkers_AreEcsDriven()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);

        var before = StateOf(Step(sim, transport, 1), SimpleSelfContainedDemo.MarkerRenderIdBase);
        var after = StateOf(Step(sim, transport, 120), SimpleSelfContainedDemo.MarkerRenderIdBase);

        Assert.True(sim.TryGetPosition(SimpleSelfContainedDemo.MarkerRenderIdBase, out var position));
        Assert.Equal(SimpleSelfContainedDemo.MarkerCount, 6);
        Assert.False(sim.TryGetPhysicsBody(SimpleSelfContainedDemo.MarkerRenderIdBase, out _));
        Assert.True(Math.Abs(before.X - after.X) > 0.5d || Math.Abs(before.Z - after.Z) > 0.5d,
            "orbit markers did not move — [Query] system glue may not be generated");
        Assert.Equal(position.X, after.X, 3);
    }

    [Fact]
    public void SpawnBall_RespectsCap_AndResetRestoresFixture()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);

        Assert.Equal(1, sim.BallCount);
        Assert.True(sim.SpawnBall());
        Assert.Equal(2, sim.BallCount);

        sim.Reset();
        Assert.Equal(1, sim.BallCount);
        Assert.True(sim.TryGetPosition(SimpleSelfContainedDemo.BallRenderIdBase, out var position));
        Assert.Equal(5f, position.Y, 3);
    }

    [Fact]
    public void Determinism_TwoRunsProduceIdenticalSignals()
    {
        var first = new CapturingRenderTransport<Transform3DRenderSignal>();
        var second = new CapturingRenderTransport<Transform3DRenderSignal>();

        using (var a = new SimpleSelfContainedDemo(null, first))
        using (var b = new SimpleSelfContainedDemo(null, second))
        {
            Step(a, first, 180);
            Step(b, second, 180);
        }

        var left = first.Last!;
        var right = second.Last!;
        Assert.Equal(left.States.Count, right.States.Count);
        for (var i = 0; i < left.States.Count; i++)
        {
            Assert.Equal(left.States[i].Id, right.States[i].Id);
            Assert.Equal(left.States[i].X, right.States[i].X, 12);
            Assert.Equal(left.States[i].Y, right.States[i].Y, 12);
            Assert.Equal(left.States[i].Z, right.States[i].Z, 12);
            Assert.Equal(left.States[i].Qw, right.States[i].Qw, 12);
        }
    }

    [Fact]
    public void Signals_ContainNoNaNOrInfinity()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);
        var signal = Step(sim, transport, 600);

        foreach (var state in signal.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsInfinity(state.X));
            Assert.False(double.IsNaN(state.Y) || double.IsInfinity(state.Y));
            Assert.False(double.IsNaN(state.Z) || double.IsInfinity(state.Z));
            Assert.False(double.IsNaN(state.Qw) || double.IsInfinity(state.Qw));
        }
    }

    [Fact]
    public void SkillVerbs_AreWired()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var sim = new SimpleSelfContainedDemo(null, transport);

        Assert.True(sim.TryCommand("spawn-ball"));
        Assert.Equal(2, sim.BallCount);
        Assert.False(sim.TryCommand("unknown"));
    }
}
