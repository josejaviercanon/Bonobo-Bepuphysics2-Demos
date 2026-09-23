using DemoEngine.ECS;
using DemoEngine.Inputs;
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

    private static Transform3DState StateById(Transform3DRenderSignal signal, int renderId)
    {
        foreach (var state in signal.States)
        {
            if (state.Id == renderId) return state;
        }

        throw new InvalidOperationException($"render id {renderId} not found");
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

    // ---- Friction ---------------------------------------------------------

    [Fact]
    public void Friction_EmitsFullLine_AndFrictionSlowsBoxes()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new FrictionDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var first = transport.Last!;
        Assert.Equal(1 + FrictionDemo.BoxCount, first.States.Count);
        Assert.Equal(FrictionDemo.FloorRenderId, StateById(first, FrictionDemo.FloorRenderId).Id);

        for (var i = 1; i < 150; i++) demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        var lowFrictionX = StateById(signal, FrictionDemo.BoxRenderIdBase).X;
        var highFrictionX = StateById(signal, FrictionDemo.BoxRenderIdBase + FrictionDemo.BoxCount - 1).X;

        Assert.True(lowFrictionX > highFrictionX + 1d, $"friction sweep did not separate slides: {lowFrictionX} vs {highFrictionX}");
    }

    [Fact]
    public void Friction_ResetRebuildsLine()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new FrictionDemo(null, transport);

        for (var i = 0; i < 60; i++) demo.Step(1.0 / 60.0);
        Assert.True(demo.TryCommand("reset"));
        demo.Step(1.0 / 60.0);

        var signal = transport.Last!;
        Assert.Equal(1 + FrictionDemo.BoxCount, signal.States.Count);
        Assert.InRange(StateById(signal, FrictionDemo.BoxRenderIdBase).X, -80.5d, -79.0d);
    }

    // ---- Per-body gravity --------------------------------------------------

    [Fact]
    public void PerBodyGravity_SpheresFallSlowerThanBoxes()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PerBodyGravityDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(1 + PerBodyGravityDemo.BodyCount, transport.Last!.States.Count);

        for (var i = 1; i < 120; i++) demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        double sphereSum = 0, boxSum = 0;
        int sphereCount = 0, boxCount = 0;
        foreach (var state in signal.States)
        {
            if (state.Id < PerBodyGravityDemo.BodyRenderIdBase) continue;
            var kind = PerBodyGravityDemo.ShapeKindOf(state.Id);
            if (kind == PerBodyGravityDemo.ShapeKindSphere)
            {
                sphereSum += state.Y;
                sphereCount++;
            }
            else if (kind == PerBodyGravityDemo.ShapeKindBox)
            {
                boxSum += state.Y;
                boxCount++;
            }
        }

        Assert.True(sphereCount > 0 && boxCount > 0);
        Assert.True(sphereSum / sphereCount > boxSum / boxCount + 1d,
            $"per-body gravity did not separate shapes: spheres {sphereSum / sphereCount}, boxes {boxSum / boxCount}");
    }

    // ---- Colosseum ---------------------------------------------------------

    [Fact]
    public void Colosseum_EmitsRings_AndShootsProjectiles()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new ColosseumDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        Assert.Equal(1 + demo.BoxCount, signal.States.Count);
        Assert.InRange(demo.BoxCount, 1500, 2200);

        Assert.True(demo.TryCommand("shoot-big"));
        Assert.Equal(1, demo.ProjectileCount);
        demo.OnFireBall(new FireBallInput(0, 40, -90, 0, 0, 1));
        Assert.Equal(2, demo.ProjectileCount);

        demo.Step(1.0 / 60.0);
        Assert.Equal(3 + demo.BoxCount, transport.Last!.States.Count);
    }

    // ---- Continuous collision detection ------------------------------------

    [Fact]
    public void ContinuousCollisionDetection_GridsComeToRest_AndSpinnersRotate()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new ContinuousCollisionDetectionDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(1 + ContinuousCollisionDetectionDemo.BoxCount + ContinuousCollisionDetectionDemo.SpinnerBodyCount,
            transport.Last!.States.Count);
        Assert.Equal(8, ContinuousCollisionDetectionDemo.SpinnerBodyCount);

        for (var i = 1; i < 60; i++) demo.Step(1.0 / 60.0);
        var bladeId = ContinuousCollisionDetectionDemo.SpinnerRenderIdBase + 1;
        var bladeBefore = StateById(transport.Last!, bladeId);

        for (var i = 60; i < 180; i++) demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        var bladeAfter = StateById(signal, bladeId);

        // The motorized blade keeps rotating.
        var rotationDelta =
            Math.Abs(bladeAfter.Qx - bladeBefore.Qx) + Math.Abs(bladeAfter.Qy - bladeBefore.Qy) +
            Math.Abs(bladeAfter.Qz - bladeBefore.Qz) + Math.Abs(bladeAfter.Qw - bladeBefore.Qw);
        Assert.True(rotationDelta > 0.05d, $"spinner blade did not rotate: {rotationDelta}");

        // Every falling box settles on the ground (top at y = 0) and stays finite.
        foreach (var state in signal.States)
        {
            if (state.Id < ContinuousCollisionDetectionDemo.BoxRenderIdBase) continue;
            if (state.Id >= ContinuousCollisionDetectionDemo.BoxRenderIdBase + ContinuousCollisionDetectionDemo.BoxCount) continue;
            Assert.InRange(state.Y, -3d, 3d);
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- Substepping -------------------------------------------------------

    [Fact]
    public void Substepping_EmitsFixture_AndSolverVerbsMutateCounts()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new SubsteppingDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(SubsteppingDemo.MaxRecords, transport.Last!.States.Count);
        Assert.Equal(48, demo.SubstepCount);
        Assert.Equal(2, demo.VelocityIterationCount);

        Assert.True(demo.TryCommand("substeps-less"));
        Assert.Equal(36, demo.SubstepCount);
        Assert.True(demo.TryCommand("iters-more"));
        Assert.Equal(3, demo.VelocityIterationCount);
        Assert.False(demo.TryCommand("unknown-verb"));
    }

    // ---- Compound ----------------------------------------------------------

    [Fact]
    public void Compound_EmitsEveryChild_WithDistinctRenderIds()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new CompoundDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.Equal(31, demo.CompoundBodyCount);
        Assert.Equal(1156, signal.States.Count);
        Assert.Contains(signal.States, s => s.Id == CompoundDemo.PlaneRenderId);
        Assert.Contains(signal.States, s => s.Id == CompoundDemo.StaticSphereRenderId);
        Assert.Contains(signal.States, s => s.Id >= CompoundDemo.SphereChildRenderIdBase && s.Id < CompoundDemo.CapsuleChildRenderIdBase);

        var ids = new HashSet<int>();
        foreach (var state in signal.States)
        {
            Assert.True(ids.Add(state.Id), $"duplicate render id {state.Id}");
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- Contact events / collision tracking --------------------------------

    [Fact]
    public void ContactEvents_SpawnsParticlesOnNewContacts()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new ContactEventsDemo(null, transport);

        Assert.Equal(2, demo.ListenedBodyCount);
        Assert.Equal(0, demo.ParticleCount);

        for (var i = 0; i < 180; i++) demo.Step(1.0 / 60.0);

        Assert.True(demo.SpawnedParticleCount > 0, "no contact particles spawned");
        Assert.Equal(4 + demo.ParticleCount, transport.Last!.States.Count);

        // The drop verb teleports the bodies back up for a fresh burst.
        Assert.True(demo.TryCommand("drop"));
        Assert.False(demo.TryCommand("unknown-verb"));
    }

    [Fact]
    public void CollisionTracking_TracksPairs_AndSpawnsParticles()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new CollisionTrackingDemo(null, transport);

        Assert.Equal(2, demo.TrackedCount);

        for (var i = 0; i < 180; i++) demo.Step(1.0 / 60.0);

        Assert.True(demo.SpawnedParticleCount > 0, "no tracked-contact particles spawned");
        Assert.Equal(4 + demo.ParticleCount, transport.Last!.States.Count);

        // The drop verb teleports the bodies back up for a fresh burst.
        Assert.True(demo.TryCommand("drop"));
        Assert.False(demo.TryCommand("unknown-verb"));
    }

    // ---- Custom voxel collidable -------------------------------------------

    [Fact]
    public void CustomVoxel_EmitsVoxelsPlusFallingBoxes_NoNaN()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new CustomVoxelCollidableDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.InRange(demo.VoxelCount, 1, CustomVoxelCollidableDemo.MaxVoxels);
        Assert.Equal(1 + CustomVoxelCollidableDemo.BoxCount + demo.VoxelCount, signal.States.Count);

        for (var i = 1; i < 60; i++) demo.Step(1.0 / 60.0);
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- P2b constraints: ropes / chains ------------------------------------

    [Fact]
    public void BlockChain_EmitsChains_AndIcoSpawnsCoins()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new BlockChainDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        Assert.Equal(1 + BlockChainDemo.ForkCount * BlockChainDemo.BlocksPerChain, signal.States.Count);
        Assert.Equal(BlockChainDemo.ForkCount * BlockChainDemo.BlocksPerChain, demo.BlockCount);

        Assert.True(demo.TryCommand("ico"));
        Assert.Equal(BlockChainDemo.CoinCount, demo.CoinCountSpawned);
        demo.Step(1.0 / 60.0);
        Assert.Equal(
            1 + BlockChainDemo.ForkCount * BlockChainDemo.BlocksPerChain + BlockChainDemo.CoinCount,
            transport.Last!.States.Count);

        // The second ICO replaces the first batch (fixed-capacity signal).
        Assert.True(demo.TryCommand("ico"));
        demo.Step(1.0 / 60.0);
        Assert.Equal(
            1 + BlockChainDemo.ForkCount * BlockChainDemo.BlocksPerChain + BlockChainDemo.CoinCount,
            transport.Last!.States.Count);
        Assert.False(demo.TryCommand("unknown-verb"));

        Assert.True(demo.TryCommand("reset"));
        Assert.Equal(0, demo.CoinCountSpawned);
        demo.Step(1.0 / 60.0);
        Assert.Equal(1 + BlockChainDemo.ForkCount * BlockChainDemo.BlocksPerChain, transport.Last!.States.Count);
    }

    [Fact]
    public void RopeStability_EmitsAllConfigs_NoNaN()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new RopeStabilityDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(RopeStabilityDemo.MaxRecords, transport.Last!.States.Count);
        Assert.Equal(RopeStabilityDemo.MaxRecords, demo.RecordCount);

        for (var i = 1; i < 120; i++) demo.Step(1.0 / 60.0);
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
            Assert.False(double.IsNaN(state.Qx) || double.IsNaN(state.Qy) || double.IsNaN(state.Qz) || double.IsNaN(state.Qw));
        }
    }

    [Fact]
    public void RopeTwist_EmitsTwoRopesPlusBall_NoNaN()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new RopeTwistDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(RopeTwistDemo.MaxRecords, transport.Last!.States.Count);

        for (var i = 1; i < 60; i++) demo.Step(1.0 / 60.0);
        var ball = StateById(transport.Last!, RopeTwistDemo.WreckingBallRenderId);
        Assert.False(double.IsNaN(ball.X) || double.IsNaN(ball.Y) || double.IsNaN(ball.Z));
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    [Fact]
    public void ChainFountain_EmitsCoil_AndBeadsMove()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new ChainFountainDemo(null, transport);

        demo.Step(1.0 / 60.0);
        Assert.Equal(ChainFountainDemo.MaxRecords, transport.Last!.States.Count);

        var firstBead = StateById(transport.Last!, ChainFountainDemo.BeadRenderIdBase + ChainFountainDemo.BeadCount - 1);
        for (var i = 1; i < 60; i++) demo.Step(1.0 / 60.0);
        var laterBead = StateById(transport.Last!, ChainFountainDemo.BeadRenderIdBase + ChainFountainDemo.BeadCount - 1);

        Assert.True(
            Math.Abs(laterBead.X - firstBead.X) > 0.05d || Math.Abs(laterBead.Z - firstBead.Z) > 0.05d,
            "chain tip did not move");
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    [Fact]
    public void RagdollTube_EmitsTubeAndEveryRagdoll_WithFinitePoses()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new RagdollTubeDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        Assert.Equal(RagdollTubeDemo.MaxRecords, signal.States.Count);
        var ragdollRecords = 0;
        foreach (var state in signal.States)
        {
            if (state.Id >= RagdollTubeDemo.RagdollRenderIdBase) ragdollRecords++;
        }

        Assert.Equal(RagdollTubeDemo.RagdollCount * RagdollTubeDemo.RagdollRenderIdStride, ragdollRecords);
        Assert.True(demo.ConstraintCount > 0);

        for (var i = 1; i < 30; i++) demo.Step(1.0 / 60.0);
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- P2b constraints: dancers -------------------------------------------

    [Fact]
    public void Dancer_EmitsMainAndBackgroundDancers_WithDresses()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new DancerDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        Assert.Equal(DancerDemo.DancerCount, demo.DancerCountActual);
        Assert.True(demo.DressNodeCount > 0, "no dress nodes created");
        Assert.Equal(demo.RecordCount, signal.States.Count);

        var hips = StateById(signal, DancerDemo.MainDancerRenderIdBase + 8);
        for (var i = 1; i < 30; i++) demo.Step(1.0 / 60.0);
        var laterHips = StateById(transport.Last!, DancerDemo.MainDancerRenderIdBase + 8);
        Assert.True(
            Math.Abs(laterHips.Z - hips.Z) > 1e-3d || Math.Abs(laterHips.Y - hips.Y) > 1e-3d,
            "the main dancer did not move");

        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    [Fact]
    public void PlumpDancer_EmitsMainAndBackgroundDancers_WithSuitNodes()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new PlumpDancerDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;
        Assert.Equal(PlumpDancerDemo.DancerCount, demo.DancerCountActual);
        Assert.True(demo.SuitNodeCount > 0, "no fat-suit nodes created");
        Assert.Equal(demo.RecordCount, signal.States.Count);
        Assert.True(demo.ConstraintCount > 0);

        for (var i = 1; i < 30; i++) demo.Step(1.0 / 60.0);
        foreach (var state in transport.Last!.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- Ray casting (P2c) -------------------------------------------------

    [Fact]
    public void RayCasting_EmitsLineSegments_AndHitsInsideCloud()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new RayCastingDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.Equal(1 + RayCastingDemo.GridCount, signal.States.Count);
        Assert.True(signal.LineCount >= RayCastingDemo.RandomRayCount,
            $"expected at least one segment per ray, got {signal.LineCount}");
        Assert.True(signal.LineCount <= RayCastingDemo.MaxLineCount);

        // The random source starts inside the collidable cloud, so some rays must hit: hit
        // segments shade green / normals are yellow, misses stay dark red (G == 0).
        Assert.Contains(signal.Lines!, line => line.G > 0d);
    }

    [Fact]
    public void RayCasting_SourceCommands_SwitchRayCounts()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new RayCastingDemo(null, transport);

        Assert.True(demo.TryCommand("source-frustum"));
        demo.Step(1.0 / 60.0);
        var frustum = transport.Last!;
        Assert.InRange(frustum.LineCount, RayCastingDemo.FrustumRayCount, RayCastingDemo.MaxLineCount);

        Assert.True(demo.TryCommand("reset-rotation"));
        Assert.False(demo.TryCommand("not-a-verb"));
    }

    // ---- Sweep (P2c) -------------------------------------------------------

    [Fact]
    public void Sweep_EmitsGridGhostTrailsAndImpactLines()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new SweepDemo(null, transport);

        demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.Equal(1 + SweepDemo.GridCount + SweepDemo.GhostCount, signal.States.Count);
        var ghosts = signal.States.Count(s => s.Id >= SweepDemo.SweepHitGhostRenderIdBase && s.Id < SweepDemo.GridCapsuleRenderIdBase);
        Assert.Equal(SweepDemo.GhostCount, ghosts);
        Assert.True(signal.LineCount <= SweepDemo.MaxLineCount);
    }

    // ---- Collision query (P2c) ---------------------------------------------

    [Fact]
    public void CollisionQuery_RoutesEveryQueryToTouchedOrUntouched()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new CollisionQueryDemo(null, transport);

        // The falling boxes start 45+ units up and pass through the query grid around step 180.
        for (var i = 0; i < 200; i++) demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        Assert.Equal(CollisionQueryDemo.MaxTransformCount, signal.States.Count);
        var touched = signal.States.Count(s => s.Id >= CollisionQueryDemo.TouchedQueryRenderIdBase && s.Id < CollisionQueryDemo.UntouchedQueryRenderIdBase);
        var untouched = signal.States.Count(s => s.Id >= CollisionQueryDemo.UntouchedQueryRenderIdBase);
        Assert.Equal(CollisionQueryDemo.QueryCount, touched + untouched);

        // Boxes falling through the query grid must register positive-depth contacts.
        Assert.True(touched > 0, "no query reported a contact while boxes fell through the grid");
    }

    // ---- Solver contact enumeration (P2c) ----------------------------------

    [Fact]
    public void SolverContactEnumeration_EmitsContactVisuals_AfterPyramidLands()
    {
        var transport = new CapturingRenderTransport<Transform3DRenderSignal>();
        using var demo = new SolverContactEnumerationDemo(null, transport);

        for (var i = 0; i < 120; i++) demo.Step(1.0 / 60.0);
        var signal = transport.Last!;

        var green = signal.States.Count(s => s.Id >= SolverContactEnumerationDemo.GreenContactRenderIdBase
                                             && s.Id < SolverContactEnumerationDemo.BlueContactRenderIdBase);
        var blue = signal.States.Count(s => s.Id >= SolverContactEnumerationDemo.BlueContactRenderIdBase);
        Assert.True(green > 0, "no touching contacts extracted from the sensor");
        Assert.Equal(2 + SolverContactEnumerationDemo.PyramidCount + green + blue, signal.States.Count);

        foreach (var state in signal.States)
        {
            Assert.False(double.IsNaN(state.X) || double.IsNaN(state.Y) || double.IsNaN(state.Z));
        }
    }

    // ---- Determinism across the ported set ---------------------------------

    [Theory]
    [InlineData("simple-self-contained")]
    [InlineData("pyramid")]
    [InlineData("bounciness")]
    [InlineData("planet")]
    [InlineData("friction")]
    [InlineData("per-body-gravity")]
    [InlineData("colosseum")]
    [InlineData("continuous-collision-detection")]
    [InlineData("substepping")]
    [InlineData("compound")]
    [InlineData("contact-events")]
    [InlineData("collision-tracking")]
    [InlineData("custom-voxel-collidable")]
    [InlineData("rope-stability")]
    [InlineData("rope-twist")]
    [InlineData("chain-fountain")]
    [InlineData("block-chain")]
    [InlineData("ragdoll-tube")]
    [InlineData("dancer")]
    [InlineData("plump-dancer")]
    [InlineData("ray-casting")]
    [InlineData("sweep")]
    [InlineData("collision-query")]
    [InlineData("solver-contact-enumeration")]
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
            "friction" => new FrictionDemo(null, transport),
            "per-body-gravity" => new PerBodyGravityDemo(null, transport),
            "colosseum" => new ColosseumDemo(null, transport),
            "continuous-collision-detection" => new ContinuousCollisionDetectionDemo(null, transport),
            "substepping" => new SubsteppingDemo(null, transport),
            "compound" => new CompoundDemo(null, transport),
            "contact-events" => new ContactEventsDemo(null, transport),
            "collision-tracking" => new CollisionTrackingDemo(null, transport),
            "custom-voxel-collidable" => new CustomVoxelCollidableDemo(null, transport),
            "rope-stability" => new RopeStabilityDemo(null, transport),
            "rope-twist" => new RopeTwistDemo(null, transport),
            "chain-fountain" => new ChainFountainDemo(null, transport),
            "block-chain" => new BlockChainDemo(null, transport),
            "ragdoll-tube" => new RagdollTubeDemo(null, transport),
            "dancer" => new DancerDemo(null, transport),
            "plump-dancer" => new PlumpDancerDemo(null, transport),
            "ray-casting" => new RayCastingDemo(null, transport),
            "sweep" => new SweepDemo(null, transport),
            "collision-query" => new CollisionQueryDemo(null, transport),
            "solver-contact-enumeration" => new SolverContactEnumerationDemo(null, transport),
            _ => throw new ArgumentOutOfRangeException(nameof(gameKey), gameKey, "unknown demo key"),
        };
}
